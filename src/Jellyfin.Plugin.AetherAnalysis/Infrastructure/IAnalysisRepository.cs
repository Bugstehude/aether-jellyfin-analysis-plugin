namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Stable storage boundary for analysis records.</summary>
public interface IAnalysisRepository
{
    /// <summary>Gets a record without changing LRU state.</summary>
    Task<AnalysisRecord?> GetAsync(AnalysisKey key, CancellationToken cancellationToken);

    /// <summary>Marks a successfully served record as accessed with write damping.</summary>
    Task TouchAsync(AnalysisKey key, CancellationToken cancellationToken);

    /// <summary>Gets lightweight metadata for a bounded set without loading analysis blobs.</summary>
    Task<IReadOnlyDictionary<AnalysisKey, AnalysisRecordMetadata>> GetMetadataAsync(
        IReadOnlyCollection<AnalysisKey> keys,
        CancellationToken cancellationToken);

    /// <summary>Creates or atomically replaces a record.</summary>
    Task<bool> UpsertAsync(AnalysisRecord record, string? expectedEtag, CancellationToken cancellationToken);

    /// <summary>Atomically makes room and stores a record without evicting data on a failed upload.</summary>
    Task<AnalysisStoreResult> StoreBoundedAsync(
        AnalysisStoreRequest request,
        CancellationToken cancellationToken);

    /// <summary>Deletes a record idempotently.</summary>
    Task<bool> DeleteAsync(AnalysisKey key, CancellationToken cancellationToken);

    /// <summary>Gets aggregate storage statistics.</summary>
    Task<AnalysisStorageStats> GetStatsAsync(CancellationToken cancellationToken);

    /// <summary>Applies retention and least-recently-used capacity cleanup.</summary>
    Task<AnalysisCleanupResult> CleanupAsync(AnalysisCleanupRequest request, CancellationToken cancellationToken);

    /// <summary>Gets the latest successful maintenance summary.</summary>
    Task<AnalysisMaintenanceSummary?> GetMaintenanceSummaryAsync(CancellationToken cancellationToken);
}

/// <summary>Canonical record key.</summary>
public readonly record struct AnalysisKey(
    Guid ItemId,
    string MediaSourceId,
    string AlgorithmId,
    string AlgorithmVersion);

/// <summary>Aggregate storage statistics.</summary>
public sealed record AnalysisStorageStats(
    int RecordCount,
    long StoredBytes,
    DateTimeOffset? OldestRecordAt);

/// <summary>Lightweight analysis metadata used by batch status queries.</summary>
public sealed record AnalysisRecordMetadata(
    AnalysisKey Key,
    string MediaFingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    int FrameCount,
    int StoredBytes,
    string Etag);

/// <summary>One atomic bounded-store request.</summary>
public sealed record AnalysisStoreRequest(
    AnalysisRecord Record,
    IReadOnlyList<string> ExpectedEtags,
    bool RequireEtagMatch,
    long MaxStoredBytes,
    DateTimeOffset? RetentionCutoff,
    DateTimeOffset CompletedAt);

/// <summary>Outcome of an atomic bounded store.</summary>
public enum AnalysisStoreResult
{
    /// <summary>A new analysis record was created.</summary>
    Created,

    /// <summary>An existing analysis record was atomically replaced.</summary>
    Replaced,

    /// <summary>The supplied optimistic concurrency condition did not match.</summary>
    PreconditionFailed,

    /// <summary>The record could not be stored within the configured capacity.</summary>
    StorageLimitExceeded
}

/// <summary>Bounded cleanup request. The excluded key is preserved during an in-flight replacement.</summary>
public sealed record AnalysisCleanupRequest(
    DateTimeOffset? RetentionCutoff,
    long? TargetStoredBytes,
    AnalysisKey? ExcludedKey,
    string Reason,
    DateTimeOffset CompletedAt);

/// <summary>Cleanup outcome suitable for logging and the administrator status endpoint.</summary>
public sealed record AnalysisCleanupResult(
    int RetentionDeletedRecords,
    int CapacityDeletedRecords,
    long DeletedBytes,
    long StoredBytesAfter);

/// <summary>Persistent summary of the latest successful cleanup.</summary>
public sealed record AnalysisMaintenanceSummary(
    DateTimeOffset? LastCompletedAt,
    string LastReason,
    int LastRetentionDeletedRecords,
    int LastCapacityDeletedRecords,
    long LastDeletedBytes);
