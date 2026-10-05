using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>Outcome of an isolated draft composition, never exposed as production availability.</summary>
public enum DraftCompositionResult
{
    /// <summary>Both components were stored together.</summary>
    Stored,
    /// <summary>An existing exact Full draft already matches the current verified profile.</summary>
    AlreadyCurrent,
    /// <summary>Host-derived media identity changed.</summary>
    MediaChanged,
    /// <summary>A source or target analysis changed while workers were running.</summary>
    AnalysisChanged,
    /// <summary>Capacity cannot accommodate the new document while preserving the old source.</summary>
    StorageLimitExceeded
}

/// <summary>
/// Private, non-resumable staging for fresh audio/video Full jobs. A host resolver supplies
/// authoritative identity at start and immediately before commit. No DI or production entry point.
/// </summary>
public sealed class DraftAnalysisCompositionSession : IAsyncDisposable
{
    private readonly IAnalysisRepository _repository;
    private readonly AnalysisWriteCoordinator _writes;
    private readonly Func<CancellationToken, Task<MediaFingerprint?>> _resolveMedia;
    private readonly MediaFingerprint _media;
    private readonly AnalysisKey? _sourceKey;
    private readonly string? _sourceEtag;
    private readonly AnalysisKey _targetKey;
    private readonly string? _targetEtag;
    private readonly List<IDisposable> _pins;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly Dictionary<string, string> _staged = new(StringComparer.Ordinal);
    private bool _finished;

    private DraftAnalysisCompositionSession(
        IAnalysisRepository repository, AnalysisWriteCoordinator writes,
        Func<CancellationToken, Task<MediaFingerprint?>> resolveMedia,
        MediaFingerprint media, DraftArtifactContext context, AnalysisKey? sourceKey,
        string? sourceEtag, AnalysisKey targetKey, string? targetEtag, List<IDisposable> pins)
    {
        _repository = repository;
        _writes = writes;
        _resolveMedia = resolveMedia;
        _media = media;
        Context = context;
        _sourceKey = sourceKey;
        _sourceEtag = sourceEtag;
        _targetKey = targetKey;
        _targetEtag = targetEtag;
        _pins = pins;
        _directory = Path.Combine(Path.GetTempPath(), "aether-draft-composition-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
        }
        else
        {
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Verified context required by both component jobs.</summary>
    public DraftArtifactContext Context { get; }

    /// <summary>Captured actual ETags for offline job snapshots. Not a local-stat identity claim.</summary>
    public JsonElement Snapshot
    {
        get
        {
            var snapshot = new JsonObject
            {
                ["itemId"] = _media.ItemId.ToString(),
                ["mediaSourceId"] = _media.MediaSourceId,
                ["sourceFingerprint"] = _media.Fingerprint,
                ["sourceAnalysis"] = Reference(_sourceKey, _sourceEtag),
                ["targetAnalysis"] = Reference(_targetEtag is null ? null : _targetKey, _targetEtag)
            };
            return JsonSerializer.SerializeToElement(snapshot);
        }
    }

    /// <summary>
    /// Captures host identity and actual repository ETags under the shared write lease, then pins
    /// existing records. resolveMedia must query the host source, never a worker artifact snapshot.
    /// </summary>
    public static async Task<DraftAnalysisCompositionSession> BeginAsync(
        IAnalysisRepository repository, AnalysisWriteCoordinator writes,
        Func<CancellationToken, Task<MediaFingerprint?>> resolveMedia,
        Guid itemId, string mediaSourceId, AnalysisKey? sourceKey,
        int? ffmpegStreamIndex, int? verifiedJellyfinStreamIndex,
        string expectedProducerRevision, int maximumDocumentBytes, CancellationToken cancellationToken,
        string targetVersion = DraftAnalysisMasterBuilder.AlgorithmVersion)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(resolveMedia);
        if (targetVersion is not ("1.2.0-draft" or "1.2.0")
            || maximumDocumentBytes is <= 0 or > 50 * 1024 * 1024 || ffmpegStreamIndex is < 0
            || verifiedJellyfinStreamIndex is < 0 || (ffmpegStreamIndex is null && verifiedJellyfinStreamIndex is not null))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDocumentBytes));
        }

        using var lease = await writes.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var media = await resolveMedia(cancellationToken).ConfigureAwait(false);
        if (media is null || media.ItemId != itemId || media.MediaSourceId != mediaSourceId
            || media.FingerprintQuality != "strong" || media.DurationMs <= 0
            || media.Fingerprint == "sha256:" + new string('0', 64))
        {
            throw new InvalidDataException("composition-unverified-host-source");
        }

        var target = new AnalysisKey(itemId, mediaSourceId, "aether-visual", targetVersion);
        if (sourceKey is { } source && (source.ItemId != itemId || source.MediaSourceId != mediaSourceId
            || source.AlgorithmId != "aether-visual" || source.AlgorithmVersion is not ("1.0.0" or "1.1.0" or "1.2.0-draft")))
        {
            throw new InvalidDataException("composition-source-key");
        }

        var keys = sourceKey is { } key ? new[] { key, target }.Distinct().ToArray() : [target];
        var metadata = await repository.GetMetadataAsync(keys, cancellationToken).ConfigureAwait(false);
        if (sourceKey is { } required && (!metadata.TryGetValue(required, out var found) || found.MediaFingerprint != media.Fingerprint))
        {
            throw new InvalidDataException("composition-source-missing-or-stale");
        }

        var pins = new List<IDisposable>();
        try
        {
            foreach (var existingKey in keys.Where(metadata.ContainsKey))
            {
                pins.Add(writes.Protect(existingKey));
            }

            return new DraftAnalysisCompositionSession(repository, writes, resolveMedia, media,
                new DraftArtifactContext(itemId, mediaSourceId, media.Fingerprint, media.DurationMs,
                    ffmpegStreamIndex, verifiedJellyfinStreamIndex, expectedProducerRevision, maximumDocumentBytes, targetVersion), sourceKey,
                sourceKey is { } selected ? metadata[selected].Etag : null,
                target, metadata.GetValueOrDefault(target)?.Etag, pins);
        }
        catch
        {
            foreach (var pin in pins)
            {
                pin.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Bounds the input stream before parsing and privately stages one validated component.
    /// Failure or cancellation aborts this session and releases all staged data and source pins.
    /// </summary>
    public async Task StageAsync(string mode, Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (mode is not ("audio-only" or "video-only") || _staged.ContainsKey(mode))
            {
                throw new InvalidDataException("composition-duplicate-or-unknown-mode");
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int count;
            while ((count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > Context.MaximumDocumentBytes)
                {
                    throw new InvalidDataException("composition-component-budget");
                }

                buffer.Write(chunk, 0, count);
            }

            var bytes = buffer.ToArray();
            JsonElement artifact;
            try
            {
                artifact = DraftAnalysisArtifactValidator.Validate(bytes, Context, mode);
            }
            catch (InvalidDataException exception)
            {
                exception.Data[DraftArtifactDiagnostics.ModeKey] = mode;
                exception.Data[DraftArtifactDiagnostics.DataKey] =
                    DraftArtifactDiagnostics.Describe(bytes, Context.DurationMs, mode);
                throw;
            }

            VerifySnapshot(artifact.GetProperty("snapshot"));
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(_directory, mode + ".json");
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            _staged.Add(mode, path);
        }
        catch
        {
            Finish();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Builds and compresses outside the shared lease. Rechecks host identity and both ETags,
    /// then commits one complete record with the repository's atomic capacity transaction.
    /// Every terminal outcome cleans private staging and preserves any competing write.
    /// </summary>
    public async Task<DraftCompositionResult> CommitAsync(long maxStoredBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStoredBytes);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (_staged.Count != 2)
            {
                throw new InvalidDataException("composition-missing-component");
            }

            var audio = await File.ReadAllBytesAsync(_staged["audio-only"], cancellationToken).ConfigureAwait(false);
            var video = await File.ReadAllBytesAsync(_staged["video-only"], cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var bytes = DraftAnalysisMasterBuilder.Build(audio, video, Context, _media, now);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var record = new AnalysisRecord
            {
                ItemId = _media.ItemId,
                MediaSourceId = _media.MediaSourceId,
                AlgorithmId = "aether-visual",
                AlgorithmVersion = Context.TargetVersion,
                MediaFingerprint = _media.Fingerprint,
                FingerprintQuality = _media.FingerprintQuality,
                Etag = AnalysisRepresentationService.CreateEtag(bytes),
                CompressedDocument = CompressionCodec.Compress(bytes),
                UncompressedBytes = bytes.Length,
                FrameCount = root.GetProperty("frames").GetArrayLength(),
                SourceIntervalMs = root.GetProperty("sampling").GetProperty("intervalMs").GetInt32(),
                CreatedAt = root.GetProperty("createdAt").GetDateTimeOffset(),
                StoredAt = now,
                LastAccessedAt = now
            };
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = await _writes.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var current = await _resolveMedia(cancellationToken).ConfigureAwait(false);
            if (current != _media)
            {
                return DraftCompositionResult.MediaChanged;
            }

            var keys = _sourceKey is { } source ? new[] { source, _targetKey }.Distinct().ToArray() : [_targetKey];
            var metadata = await _repository.GetMetadataAsync(keys, cancellationToken).ConfigureAwait(false);
            if (metadata.GetValueOrDefault(_targetKey)?.Etag != _targetEtag
                || (_sourceKey is { } selected && metadata.GetValueOrDefault(selected)?.Etag != _sourceEtag))
            {
                return DraftCompositionResult.AnalysisChanged;
            }

            var result = await _repository.StoreBoundedAsync(new AnalysisStoreRequest(record,
                _targetEtag is null ? [] : [_targetEtag], _targetEtag is not null, maxStoredBytes, null, now),
                cancellationToken).ConfigureAwait(false);
            return result switch
            {
                AnalysisStoreResult.Created or AnalysisStoreResult.Replaced => DraftCompositionResult.Stored,
                AnalysisStoreResult.PreconditionFailed => DraftCompositionResult.AnalysisChanged,
                _ => DraftCompositionResult.StorageLimitExceeded
            };
        }
        finally
        {
            Finish();
            _gate.Release();
        }
    }

    /// <summary>Aborts unpublished work and releases pins. No persisted resume state is claimed.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Finish();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void VerifySnapshot(JsonElement snapshot)
    {
        var expected = Snapshot;
        foreach (var property in new[] { "sourceAnalysis", "targetAnalysis" })
        {
            if (!JsonElement.DeepEquals(expected.GetProperty(property), snapshot.GetProperty(property)))
            {
                throw new InvalidDataException("composition-snapshot-etag");
            }
        }
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static JsonObject? Reference(AnalysisKey? key, string? etag) => key is { } value
        ? new JsonObject { ["algorithm"] = new JsonObject { ["id"] = value.AlgorithmId, ["version"] = value.AlgorithmVersion }, ["etag"] = etag }
        : null;

    private void EnsureOpen()
    {
        if (_finished)
        {
            throw new InvalidOperationException("composition-session-finished");
        }
    }

    private void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            Directory.Delete(_directory, recursive: true);
            _staged.Clear();
        }
        finally
        {
            foreach (var pin in _pins)
            {
                pin.Dispose();
            }

            _pins.Clear();
        }
    }
}
