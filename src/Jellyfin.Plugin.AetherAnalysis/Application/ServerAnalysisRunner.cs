using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AetherAnalysis.Configuration;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>
/// Server-side analysis orchestrator: resolves a local media file, runs the shared
/// perception-engine worker, and stores the result directly through the repository —
/// the same validate → fingerprint-match → build-master → bounded-store path the
/// HTTP <c>PutAnalysis</c> handler performs, but in-process with no auth round-trip.
/// A single gate serializes whole analyses so the weak server never runs two at once.
/// </summary>
public sealed class ServerAnalysisRunner(
    ILibraryManager libraryManager,
    IAnalysisRepository repository,
    AnalysisDocumentValidator validator,
    MediaFingerprintService fingerprintService,
    AnalysisRepresentationService representationService,
    ServerAnalysisWorkerRunner worker,
    ILogger<ServerAnalysisRunner> logger) : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Enumerates the video items eligible for server-side analysis per configuration.</summary>
    public IReadOnlyList<BaseItem> SelectItems()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            IsVirtualItem = false
        };

        var libraryIds = ParseGuids(Configuration.AnalysisLibraryIds);
        if (libraryIds.Length > 0)
        {
            query.AncestorIds = libraryIds;
        }

        return libraryManager.GetItemList(query);
    }

    /// <summary>
    /// Analyzes every eligible item that lacks a current analysis, reporting 0..100 percent.
    /// Shared by the scheduled task and the post-scan hook; already-current items are cheap
    /// (a metadata lookup only, no worker run).
    /// </summary>
    public async Task AnalyzePendingAsync(IProgress<double>? percentProgress, CancellationToken cancellationToken)
    {
        var items = SelectItems();
        if (items.Count == 0)
        {
            percentProgress?.Report(100);
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            try
            {
                if (await ItemNeedsAnalysisAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    await AnalyzeItemAsync(item.Id, null, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AETHER analysis failed for item {ItemId}", item.Id);
            }

            percentProgress?.Report((i + 1) * 100.0 / items.Count);
        }
    }

    /// <summary>True when the item has at least one local source lacking a current stored analysis.</summary>
    public async Task<bool> ItemNeedsAnalysisAsync(BaseItem item, CancellationToken cancellationToken)
    {
        foreach (var mediaSourceId in LocalSourceIds(item))
        {
            if (await NeedsAnalysisAsync(item, mediaSourceId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Analyzes every local source of one item, skipping sources already current.</summary>
    public async Task<ItemAnalysisResult> AnalyzeItemAsync(
        Guid itemId,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var item = libraryManager.GetItemById<BaseItem>(itemId);
        if (item is null)
        {
            return new ItemAnalysisResult(itemId, []);
        }

        var sources = LocalSources(item).ToArray();
        var outcomes = new List<SourceAnalysisOutcome>(sources.Length);
        for (var i = 0; i < sources.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[i];
            var index = i;
            var sourceProgress = progress is null
                ? null
                : new Progress<double>(fraction =>
                    progress.Report((index + Math.Clamp(fraction, 0, 1)) / sources.Length));
            outcomes.Add(await AnalyzeSourceAsync(item, source, sourceProgress, cancellationToken).ConfigureAwait(false));
        }

        progress?.Report(1.0);
        return new ItemAnalysisResult(itemId, outcomes);
    }

    private async Task<SourceAnalysisOutcome> AnalyzeSourceAsync(
        BaseItem item,
        MediaSourceInfo source,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var mediaSourceId = source.Id;
        var media = fingerprintService.Create(item, mediaSourceId);
        if (media is null || string.IsNullOrWhiteSpace(source.Path))
        {
            return new SourceAnalysisOutcome(mediaSourceId, SourceAnalysisStatus.Skipped, "no-local-source");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string documentJson;
            try
            {
                var timeout = TimeSpan.FromMinutes(Math.Clamp(Configuration.AnalysisTimeoutMinutes, 1, 720));
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                documentJson = await worker.AnalyzeAsync(
                    source.Path,
                    Math.Clamp(Configuration.AnalysisFps, 1, 10),
                    Math.Clamp(Configuration.AnalysisMaxWidth, 16, 1920),
                    progress,
                    timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new SourceAnalysisOutcome(mediaSourceId, SourceAnalysisStatus.Failed, "worker-timeout");
            }
            catch (ServerAnalysisWorkerException exception)
            {
                logger.LogError(exception, "AETHER worker failed for item {ItemId} source {SourceId}", item.Id, mediaSourceId);
                return new SourceAnalysisOutcome(mediaSourceId, SourceAnalysisStatus.Failed, "worker-error");
            }

            // Overwrite the worker's placeholder fingerprint with the authoritative
            // server value, so the validator's fingerprint-match invariant holds.
            JsonObject node;
            try
            {
                node = JsonNode.Parse(documentJson)!.AsObject();
            }
            catch (JsonException exception)
            {
                logger.LogError(exception, "AETHER worker returned invalid JSON for item {ItemId}", item.Id);
                return new SourceAnalysisOutcome(mediaSourceId, SourceAnalysisStatus.Failed, "invalid-json");
            }

            node["mediaFingerprintAtStart"] = media.Fingerprint;
            var element = JsonSerializer.SerializeToElement(node, SerializerOptions);

            var validation = validator.Validate(element, EffectiveMaxUploadBytes);
            if (!validation.IsValid)
            {
                logger.LogWarning(
                    "AETHER server analysis rejected for item {ItemId}: {Error}",
                    item.Id,
                    validation.Error);
                return new SourceAnalysisOutcome(mediaSourceId, SourceAnalysisStatus.Failed, validation.Code);
            }

            // Re-check the media has not changed while the (minute-long) analysis ran.
            var mediaAfter = fingerprintService.Create(item, mediaSourceId);
            if (mediaAfter is null || !string.Equals(mediaAfter.Fingerprint, media.Fingerprint, StringComparison.Ordinal))
            {
                return new SourceAnalysisOutcome(mediaSourceId, SourceAnalysisStatus.Skipped, "media-changed");
            }

            var storedAt = DateTimeOffset.UtcNow;
            var master = representationService.BuildMaster(
                validation.Json!,
                mediaAfter,
                AetherAlgorithm.Id,
                AetherAlgorithm.Version,
                storedAt);
            var etag = AnalysisRepresentationService.CreateEtag(master);
            var compressed = CompressionCodec.Compress(master);
            var now = DateTimeOffset.UtcNow;
            var record = new AnalysisRecord
            {
                ItemId = item.Id,
                MediaSourceId = mediaSourceId,
                AlgorithmId = AetherAlgorithm.Id,
                AlgorithmVersion = AetherAlgorithm.Version,
                MediaFingerprint = mediaAfter.Fingerprint,
                FingerprintQuality = mediaAfter.FingerprintQuality,
                Etag = etag,
                CompressedDocument = compressed,
                UncompressedBytes = master.Length,
                FrameCount = validation.Value!.Frames.Count,
                SourceIntervalMs = validation.Value.Sampling.IntervalMs,
                CreatedAt = validation.Value.CreatedAt,
                StoredAt = storedAt,
                LastAccessedAt = storedAt
            };
            var retentionCutoff = EffectiveRetentionDays > 0
                ? now.AddDays(-EffectiveRetentionDays)
                : (DateTimeOffset?)null;

            var result = await repository.StoreBoundedAsync(
                new AnalysisStoreRequest(record, [], false, EffectiveMaxStoredBytes, retentionCutoff, now),
                cancellationToken).ConfigureAwait(false);

            return result switch
            {
                AnalysisStoreResult.Created => new SourceAnalysisOutcome(mediaSourceId, SourceAnalysisStatus.Created, null),
                AnalysisStoreResult.Replaced => new SourceAnalysisOutcome(mediaSourceId, SourceAnalysisStatus.Replaced, null),
                AnalysisStoreResult.StorageLimitExceeded =>
                    new SourceAnalysisOutcome(mediaSourceId, SourceAnalysisStatus.Failed, "storage-limit-exceeded"),
                _ => new SourceAnalysisOutcome(mediaSourceId, SourceAnalysisStatus.Failed, "precondition-failed")
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> NeedsAnalysisAsync(BaseItem item, string mediaSourceId, CancellationToken cancellationToken)
    {
        var media = fingerprintService.Create(item, mediaSourceId);
        if (media is null)
        {
            return false;
        }

        var key = new AnalysisKey(item.Id, mediaSourceId, AetherAlgorithm.Id, AetherAlgorithm.Version);
        var record = await repository.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.MediaFingerprint, media.Fingerprint, StringComparison.Ordinal))
        {
            // No analysis yet, or the media changed → (re)analyze.
            return true;
        }

        // Upgrade rule: a stored analysis that was NOT produced by the server (a browser
        // precompute is visual-only) is replaced with the richer server analysis (visual +
        // audio, globally normalized). Once every record is server-produced this is a no-op.
        return !IsServerProduced(record);
    }

    private static bool IsServerProduced(AnalysisRecord record)
    {
        try
        {
            var master = CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes);
            using var document = JsonDocument.Parse(master);
            return document.RootElement.TryGetProperty("producer", out var producer)
                && producer.TryGetProperty("platform", out var platform)
                && string.Equals(platform.GetString(), "server", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            // Unreadable stored document → treat as replaceable so a clean analysis takes over.
            return false;
        }
    }

    private static IEnumerable<MediaSourceInfo> LocalSources(BaseItem item) =>
        item.GetMediaSources(enablePathSubstitution: false)
            .Where(source => !source.IsRemote
                && !string.IsNullOrWhiteSpace(source.Path)
                && File.Exists(source.Path));

    private static IEnumerable<string> LocalSourceIds(BaseItem item) =>
        LocalSources(item).Select(source => source.Id);

    private static Guid[] ParseGuids(string[]? values) => (values ?? [])
        .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty)
        .ToArray();

    private static PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static int EffectiveMaxUploadBytes => Math.Clamp(Configuration.MaxUploadBytes, 1, 50 * 1024 * 1024);

    private static int EffectiveRetentionDays => Math.Clamp(Configuration.RetentionDays, 0, 36500);

    private static long EffectiveMaxStoredBytes => Math.Clamp(
        Configuration.MaxStoredBytes,
        1024 * 1024,
        1024L * 1024 * 1024 * 1024);
}

/// <summary>Per-source analysis outcome.</summary>
public enum SourceAnalysisStatus
{
    /// <summary>A new analysis was stored.</summary>
    Created,

    /// <summary>An existing analysis was replaced.</summary>
    Replaced,

    /// <summary>The source was skipped (not local, or media changed during analysis).</summary>
    Skipped,

    /// <summary>Analysis failed (worker error, timeout, validation, or storage limit).</summary>
    Failed
}

/// <summary>Outcome for one media source.</summary>
public sealed record SourceAnalysisOutcome(string MediaSourceId, SourceAnalysisStatus Status, string? Detail);

/// <summary>Aggregated outcome for one item.</summary>
public sealed record ItemAnalysisResult(Guid ItemId, IReadOnlyList<SourceAnalysisOutcome> Sources)
{
    /// <summary>True when at least one source was created or replaced.</summary>
    public bool AnyStored => Sources.Any(s => s.Status is SourceAnalysisStatus.Created or SourceAnalysisStatus.Replaced);

    /// <summary>True when at least one source failed.</summary>
    public bool AnyFailed => Sources.Any(s => s.Status == SourceAnalysisStatus.Failed);
}
