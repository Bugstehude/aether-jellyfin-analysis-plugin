using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>Gated experimental source-to-worker-to-repository integration. No API or routine caller.</summary>
public sealed class DraftServerAnalysisRunner(
    IDraftSourceResolver sources,
    IDraftWorkerProcessRunner worker,
    IAnalysisRepository repository,
    AnalysisWriteCoordinator writes,
    AnalysisWorkerExecutionGate executionGate,
    Func<DraftWorkerSettings> settingsProvider) : IDisposable
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    /// <summary>Cancels running experimental workers when the host shuts down.</summary>
    public void Dispose() => _lifetimeCancellation.Cancel();

    /// <summary>Runs two fresh Full components for one local source and atomically stores an isolated draft.</summary>
    public Task<DraftCompositionResult> AnalyzeAsync(
        Guid itemId, string mediaSourceId, CancellationToken cancellationToken, int? requestedFfmpegStreamIndex = null) =>
        AnalyzeCoreAsync(itemId, mediaSourceId, requestedFfmpegStreamIndex, recalculate: true, cancellationToken);

    /// <summary>
    /// Experimental routine semantics: keep a fully validated current target after fresh track
    /// probing, otherwise measure both components. No production scheduler/API calls this method.
    /// </summary>
    public Task<DraftCompositionResult> AnalyzeIfNeededAsync(
        Guid itemId, string mediaSourceId, CancellationToken cancellationToken, int? requestedFfmpegStreamIndex = null) =>
        AnalyzeCoreAsync(itemId, mediaSourceId, requestedFfmpegStreamIndex, recalculate: false, cancellationToken);

    private async Task<DraftCompositionResult> AnalyzeCoreAsync(
        Guid itemId, string mediaSourceId, int? requestedFfmpegStreamIndex, bool recalculate, CancellationToken cancellationToken)
    {
        var settings = settingsProvider();
        DraftWorkerProcessRunner.ValidateSettings(settings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        var operationToken = timeout.Token;
        timeout.CancelAfter(settings.Timeout ?? TimeSpan.FromMinutes(60));
        var pins = new List<IDisposable>();
        var directory = Path.Combine(Path.GetTempPath(), "aether-draft-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var workerLease = await executionGate.AcquireAsync(operationToken).ConfigureAwait(false);
            DraftHostSource? initial;
            AnalysisKey? sourceKey;
            string? sourceEtag;
            string? targetEtag;
            using (await writes.AcquireAsync(operationToken).ConfigureAwait(false))
            {
                initial = await sources.ResolveAsync(itemId, mediaSourceId, operationToken).ConfigureAwait(false);
                if (initial is null || initial.Media.ItemId != itemId
                    || !string.Equals(initial.Media.MediaSourceId, mediaSourceId, StringComparison.OrdinalIgnoreCase)
                    || initial.Media.FingerprintQuality != "strong" || initial.Media.DurationMs <= 0)
                {
                    return DraftCompositionResult.MediaChanged;
                }

                var keys = new[] { Key(initial, "1.1.0"), Key(initial, "1.0.0"), Key(initial, settings.TargetVersion) };
                var metadata = await repository.GetMetadataAsync(keys, operationToken).ConfigureAwait(false);
                sourceKey = keys.Take(2).Where(key => metadata.GetValueOrDefault(key)?.MediaFingerprint == initial.Media.Fingerprint)
                    .Select(key => (AnalysisKey?)key).FirstOrDefault();
                sourceEtag = sourceKey is { } source ? metadata[source].Etag : null;
                targetEtag = metadata.GetValueOrDefault(keys[2])?.Etag;
                foreach (var key in keys.Where(metadata.ContainsKey))
                {
                    pins.Add(writes.Protect(key));
                }
            }

            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            settings = await DraftWorkerProcessRunner.PinBundleAsync(settings, directory, operationToken).ConfigureAwait(false);
            var probe = await worker.ProbeAsync(initial, settings, operationToken).ConfigureAwait(false);
            var tracks = DraftSourceTrackMapper.Select(initial, probe, requestedFfmpegStreamIndex);

            async Task<MediaFingerprint?> ResolveUnchanged(CancellationToken token)
            {
                var current = await sources.ResolveAsync(initial.Media.ItemId, initial.Media.MediaSourceId, token).ConfigureAwait(false);
                return SameSource(initial, current) ? current!.Media : null;
            }

            if (await ResolveUnchanged(operationToken).ConfigureAwait(false) is null)
            {
                return DraftCompositionResult.MediaChanged;
            }

            DraftAnalysisCompositionSession session;
            try
            {
                session = await DraftAnalysisCompositionSession.BeginAsync(repository, writes, ResolveUnchanged,
                    initial.Media.ItemId, initial.Media.MediaSourceId, sourceKey, tracks.FfmpegStreamIndex, tracks.JellyfinStreamIndex,
                    settings.ProducerRevision, settings.MaximumDocumentBytes, operationToken, settings.TargetVersion).ConfigureAwait(false);
            }
            catch (InvalidDataException exception) when (exception.Message == "composition-source-missing-or-stale")
            {
                return DraftCompositionResult.AnalysisChanged;
            }
            catch (InvalidDataException exception) when (exception.Message == "composition-unverified-host-source")
            {
                return DraftCompositionResult.MediaChanged;
            }

            await using var sessionLease = session;
            if (ReadEtag(session.Snapshot, "sourceAnalysis") != sourceEtag
                || ReadEtag(session.Snapshot, "targetAnalysis") != targetEtag)
            {
                return DraftCompositionResult.AnalysisChanged;
            }

            if (!recalculate)
            {
                var targetKey = Key(initial, settings.TargetVersion);
                var target = await repository.GetAsync(targetKey, operationToken).ConfigureAwait(false);
                if (target?.Etag != targetEtag)
                {
                    return DraftCompositionResult.AnalysisChanged;
                }

                var plan = DraftAnalysisMigrationPlanner.Evaluate(target, session.Context, settings.Fps, settings.Width);
                if (plan.Action == DraftMigrationAction.KeepCurrent)
                {
                    operationToken.ThrowIfCancellationRequested();
                    using var writeLease = await writes.AcquireAsync(operationToken).ConfigureAwait(false);
                    if (await ResolveUnchanged(operationToken).ConfigureAwait(false) is null)
                    {
                        return DraftCompositionResult.MediaChanged;
                    }

                    var keys = sourceKey is { } selected ? new[] { selected, targetKey } : [targetKey];
                    var current = await repository.GetMetadataAsync(keys, operationToken).ConfigureAwait(false);
                    return current.GetValueOrDefault(targetKey)?.Etag == targetEtag
                        && (sourceKey is null || current.GetValueOrDefault(sourceKey.Value)?.Etag == sourceEtag)
                        ? DraftCompositionResult.AlreadyCurrent : DraftCompositionResult.AnalysisChanged;
                }
            }

            foreach (var mode in new[] { "audio-only", "video-only" })
            {
                if (await ResolveUnchanged(operationToken).ConfigureAwait(false) is null)
                {
                    return DraftCompositionResult.MediaChanged;
                }

                var path = await worker.ProduceAsync(initial, tracks, session.Snapshot, mode, directory, settings, operationToken).ConfigureAwait(false);
                if (!string.Equals(Path.GetFullPath(path), Path.Combine(directory, mode + ".json"), StringComparison.Ordinal)
                    || new FileInfo(path).LinkTarget is not null)
                {
                    throw new InvalidDataException("draft-worker-output-path");
                }

                await using var stream = File.OpenRead(path);
                await session.StageAsync(mode, stream, operationToken).ConfigureAwait(false);
            }

            return await session.CommitAsync(settings.MaximumStoredBytes, operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetimeCancellation.IsCancellationRequested)
        {
            throw new InvalidDataException("draft-job-timeout");
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            finally
            {
                foreach (var pin in pins)
                {
                    pin.Dispose();
                }
            }
        }
    }

    private static AnalysisKey Key(DraftHostSource source, string version) =>
        new(source.Media.ItemId, source.Media.MediaSourceId, "aether-visual", version);

    private static bool SameSource(DraftHostSource initial, DraftHostSource? current) =>
        current is not null && current.Media == initial.Media && current.InputPath == initial.InputPath
        && current.StreamIdentity == initial.StreamIdentity && current.DefaultAudioStreamIndex == initial.DefaultAudioStreamIndex
        && current.Streams.SequenceEqual(initial.Streams);

    private static string? ReadEtag(JsonElement snapshot, string name) =>
        snapshot.GetProperty(name).ValueKind == JsonValueKind.Null ? null : snapshot.GetProperty(name).GetProperty("etag").GetString();
}
