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
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private readonly ConcurrentDictionary<Guid, AnalysisJobStatus> _status = new();

    /// <summary>Queues an item for analysis; a no-op if it is already queued or running.</summary>
    public AnalysisJobStatus Enqueue(Guid itemId)
    {
        var queued = new AnalysisJobStatus(AnalysisJobState.Queued, 0, DateTimeOffset.UtcNow, null);
        var status = _status.AddOrUpdate(
            itemId,
            queued,
            (_, existing) => existing.State is AnalysisJobState.Queued or AnalysisJobState.Running
                ? existing
                : queued);

        if (ReferenceEquals(status, queued))
        {
            _channel.Writer.TryWrite(itemId);
        }

        return status;
    }

    /// <summary>Gets the latest known status for an item, or null if never requested.</summary>
    public AnalysisJobStatus? GetStatus(Guid itemId) =>
        _status.TryGetValue(itemId, out var status) ? status : null;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var itemId in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            _status[itemId] = new AnalysisJobStatus(AnalysisJobState.Running, 0, DateTimeOffset.UtcNow, null);
            var progress = new Progress<double>(fraction =>
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
