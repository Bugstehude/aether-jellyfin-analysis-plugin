using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>
/// Serial background queue for ad-hoc analysis requests (the AETHER "Server-Analyse"
/// button). One item is analyzed at a time; per-item status is tracked so the client
/// can poll progress. The scheduled task and post-scan hook drive the shared
/// <see cref="ServerAnalysisRunner"/> directly — its internal gate keeps everything serial.
/// </summary>
public sealed class AnalysisJobDispatcher(
    ServerAnalysisRunner runner,
    ILogger<AnalysisJobDispatcher> logger) : BackgroundService
{
    /// <summary>Upper bound of items waiting for analysis; further requests are rejected.</summary>
    private const int QueueCapacity = 64;

    /// <summary>Finished status entries older than this are dropped so the table cannot grow without bound.</summary>
    private static readonly TimeSpan FinishedStatusRetention = TimeSpan.FromHours(1);

    // Begrenzt: eine unbegrenzte Warteschlange liesse jeden Aufrufer mit Upload-Recht
    // beliebig viele (jeweils minuten- bis stundenlange) ffmpeg-Läufe aufstauen.
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        // Wait, NICHT DropWrite: bei DropWrite verwirft der Kanal den Auftrag
        // still und `TryWrite` meldet trotzdem Erfolg — der Ablehnungspfad
        // liefe nie an, das Item bliebe dauerhaft als "queued" stehen und ein
        // erneuter Versuch käme wegen der Dedupe-Regel nie mehr durch. Mit Wait
        // schlägt `TryWrite` bei vollem Kanal sofort fehl (es blockiert nicht,
        // das täte nur `WriteAsync`), und der Aufrufer bekommt sein 429.
        FullMode = BoundedChannelFullMode.Wait
    });

    private readonly ConcurrentDictionary<Guid, AnalysisJobStatus> _status = new();

    /// <summary>
    /// Queues an item for analysis; a no-op if it is already queued or running.
    /// Returns <c>null</c> when the queue is full and the request was rejected.
    /// </summary>
    public AnalysisJobStatus? Enqueue(Guid itemId)
    {
        var queued = new AnalysisJobStatus(AnalysisJobState.Queued, 0, DateTimeOffset.UtcNow, null);
        var status = _status.AddOrUpdate(
            itemId,
            queued,
            (_, existing) => existing.State is AnalysisJobState.Queued or AnalysisJobState.Running
                ? existing
                : queued);

        if (!ReferenceEquals(status, queued))
        {
            return status;
        }

        if (_channel.Writer.TryWrite(itemId))
        {
            return status;
        }

        // Nicht aufgenommen — den Queued-Eintrag zurücknehmen, sonst bliebe das Item
        // dauerhaft als "queued" stehen, ohne dass je jemand daran arbeitet.
        _status.TryRemove(new KeyValuePair<Guid, AnalysisJobStatus>(itemId, queued));
        logger.LogWarning("AETHER analysis queue is full ({Capacity}); rejected item {ItemId}", QueueCapacity, itemId);
        return null;
    }

    /// <summary>Gets the latest known status for an item, or null if never requested.</summary>
    public AnalysisJobStatus? GetStatus(Guid itemId) =>
        _status.TryGetValue(itemId, out var status) ? status : null;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var itemId in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            PruneFinishedStatus();
            _status[itemId] = new AnalysisJobStatus(AnalysisJobState.Running, 0, DateTimeOffset.UtcNow, null);
            // MUSS synchron berichten, nicht `System.Progress<T>`: dessen `Report` liefert
            // seinen Callback über den zur Konstruktionszeit aktiven SynchronizationContext
            // (hier keiner vorhanden -> ThreadPool) NACHTRÄGLICH aus. Der letzte
            // `progress.Report(1.0)` in AnalyzeItemAsync konnte dadurch NACH der finalen
            // Completed-Zuweisung unten laufen und sie zurück auf Running/progress=1
            // überschreiben -- der Job blieb dann für immer "running" stehen, obwohl er
            // längst fertig war (das Statusfeld, nicht der Job selbst, hing).
            var progress = new SynchronousProgress<double>(fraction =>
                _status[itemId] = new AnalysisJobStatus(
                    AnalysisJobState.Running,
                    Math.Clamp(fraction, 0, 1),
                    DateTimeOffset.UtcNow,
                    null));

            try
            {
                var result = await runner.AnalyzeItemAsync(itemId, progress, stoppingToken).ConfigureAwait(false);
                var detail = result.Sources.Count == 0
                    ? "no-local-source"
                    : (result.AnyStored ? "stored" : (result.AnyFailed ? result.Sources.First(s => s.Status == SourceAnalysisStatus.Failed).Detail : "already-current"));
                var state = result.AnyFailed && !result.AnyStored ? AnalysisJobState.Failed : AnalysisJobState.Completed;
                _status[itemId] = new AnalysisJobStatus(state, 1, DateTimeOffset.UtcNow, detail);
                logger.LogInformation("AETHER analysis for item {ItemId} finished: {Detail}", itemId, detail);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _status[itemId] = new AnalysisJobStatus(AnalysisJobState.Failed, 1, DateTimeOffset.UtcNow, "error");
                logger.LogError(exception, "AETHER analysis job for item {ItemId} failed", itemId);
            }
        }
    }

    /// <summary>Drops long-finished entries so the status table stays bounded.</summary>
    private void PruneFinishedStatus()
    {
        var cutoff = DateTimeOffset.UtcNow - FinishedStatusRetention;
        foreach (var entry in _status)
        {
            if (entry.Value.State is AnalysisJobState.Completed or AnalysisJobState.Failed
                && entry.Value.UpdatedAt < cutoff)
            {
                _status.TryRemove(entry);
            }
        }
    }
}

/// <summary>Lifecycle state of a queued analysis job.</summary>
public enum AnalysisJobState
{
    /// <summary>Waiting in the queue.</summary>
    Queued,

    /// <summary>Currently analyzing.</summary>
    Running,

    /// <summary>Finished (stored, or already current / no local source).</summary>
    Completed,

    /// <summary>Finished with a failure.</summary>
    Failed
}

/// <summary>Immutable snapshot of a job's progress for the status endpoint.</summary>
public sealed record AnalysisJobStatus(
    AnalysisJobState State,
    double Progress,
    DateTimeOffset UpdatedAt,
    string? Detail);

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its callback inline instead of marshaling it
/// through a captured <see cref="SynchronizationContext"/> (or the thread pool) the way
/// <see cref="Progress{T}"/> does. That deferred delivery is exactly what let a job's final
/// `Report(1.0)` land after the dispatcher had already written its Completed status, silently
/// reverting it back to Running forever -- see the comment at its call site.
/// </summary>
public sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
{
    /// <inheritdoc />
    public void Report(T value) => callback(value);
}
