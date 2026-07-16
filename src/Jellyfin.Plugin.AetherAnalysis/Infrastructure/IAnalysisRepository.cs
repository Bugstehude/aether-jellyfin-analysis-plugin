namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Stable storage boundary for analysis records.</summary>
public interface IAnalysisRepository
{
    /// <summary>Gets a record and updates its access time.</summary>
    Task<AnalysisRecord?> GetAsync(AnalysisKey key, CancellationToken cancellationToken);

    /// <summary>Creates or atomically replaces a record.</summary>
    Task<bool> UpsertAsync(AnalysisRecord record, string? expectedEtag, CancellationToken cancellationToken);

    /// <summary>Deletes a record idempotently.</summary>
    Task<bool> DeleteAsync(AnalysisKey key, CancellationToken cancellationToken);

    /// <summary>Gets aggregate storage statistics.</summary>
    Task<AnalysisStorageStats> GetStatsAsync(CancellationToken cancellationToken);
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
