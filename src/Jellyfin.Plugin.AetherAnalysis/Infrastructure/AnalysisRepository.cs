using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>EF Core implementation of plugin-owned analysis storage.</summary>
public sealed class AnalysisRepository(IDbContextFactory<AnalysisDbContext> contextFactory)
    : IAnalysisRepository
{
    private const int CleanupBatchSize = 256;
    private static readonly TimeSpan AccessWriteInterval = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public async Task<AnalysisRecord?> GetAsync(AnalysisKey key, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await FindAsync(context, key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TouchAsync(AnalysisKey key, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var threshold = (now - AccessWriteInterval).ToUnixTimeMilliseconds();
        await context.Analyses
            .Where(value => value.ItemId == key.ItemId
                && value.MediaSourceId == key.MediaSourceId
                && value.AlgorithmId == key.AlgorithmId
                && value.AlgorithmVersion == key.AlgorithmVersion
                && value.LastAccessedAtUnixTimeMilliseconds <= threshold)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(
                    value => value.LastAccessedAtUnixTimeMilliseconds,
                    now.ToUnixTimeMilliseconds()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UpsertAsync(
        AnalysisRecord record,
        string? expectedEtag,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var key = new AnalysisKey(
            record.ItemId,
            record.MediaSourceId,
            record.AlgorithmId,
            record.AlgorithmVersion);
        var existing = await FindAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (expectedEtag is not null && !string.Equals(existing?.Etag, expectedEtag, StringComparison.Ordinal))
        {
            return false;
        }

        if (existing is null)
        {
            context.Analyses.Add(record);
        }
        else
        {
            context.Analyses.Update(record);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<AnalysisStoreResult> StoreBoundedAsync(
        AnalysisStoreRequest request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var record = request.Record;
        var key = new AnalysisKey(record.ItemId, record.MediaSourceId, record.AlgorithmId, record.AlgorithmVersion);
        var existing = await FindAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (request.RequireEtagMatch
            && (existing is null || !request.ExpectedEtags.Any(value =>
                value == "*" || string.Equals(value, existing.Etag, StringComparison.Ordinal))))
        {
            return AnalysisStoreResult.PreconditionFailed;
        }

        if (record.CompressedDocument.LongLength > request.MaxStoredBytes)
        {
            return AnalysisStoreResult.StorageLimitExceeded;
        }

        var capacityTarget = request.MaxStoredBytes
            - record.CompressedDocument.LongLength
            + (existing?.CompressedDocument.LongLength ?? 0);
        await CleanupCoreAsync(
            context,
            new AnalysisCleanupRequest(request.RetentionCutoff, capacityTarget, key, "upload", request.CompletedAt),
            cancellationToken).ConfigureAwait(false);
        var storedBytes = await GetStoredBytesAsync(context, cancellationToken).ConfigureAwait(false);
        var projectedBytes = storedBytes - (existing?.CompressedDocument.LongLength ?? 0) + record.CompressedDocument.LongLength;
        if (projectedBytes > request.MaxStoredBytes)
        {
            return AnalysisStoreResult.StorageLimitExceeded;
        }

        if (existing is null)
        {
            context.Analyses.Add(record);
        }
        else
        {
            context.Analyses.Update(record);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return existing is null ? AnalysisStoreResult.Created : AnalysisStoreResult.Replaced;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(AnalysisKey key, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var record = await FindAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return false;
        }

        context.Analyses.Remove(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<AnalysisStorageStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var recordCount = await context.Analyses.CountAsync(cancellationToken).ConfigureAwait(false);
        var storedBytes = await context.Analyses
            .Select(value => (long)value.CompressedDocument.Length)
            .SumAsync(cancellationToken)
            .ConfigureAwait(false);
        var oldestUnixTime = await context.Analyses
            .Select(value => (long?)value.StoredAtUnixTimeMilliseconds)
            .MinAsync(cancellationToken)
            .ConfigureAwait(false);
        var oldest = oldestUnixTime.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(oldestUnixTime.Value)
            : (DateTimeOffset?)null;
        return new AnalysisStorageStats(recordCount, storedBytes, oldest);
    }

    /// <inheritdoc />
    public async Task<AnalysisCleanupResult> CleanupAsync(
        AnalysisCleanupRequest request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = await CleanupCoreAsync(context, request, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<AnalysisCleanupResult> CleanupCoreAsync(
        AnalysisDbContext context,
        AnalysisCleanupRequest request,
        CancellationToken cancellationToken)
    {
        var retentionDeleted = 0;
        var capacityDeleted = 0;
        var deletedBytes = 0L;

        if (request.RetentionCutoff.HasValue)
        {
            var cutoff = request.RetentionCutoff.Value.ToUnixTimeMilliseconds();
            while (true)
            {
                var expired = await SelectCandidates(CandidateQuery(context, request.ExcludedKey)
                        .Where(value => value.StoredAtUnixTimeMilliseconds < cutoff)
                        .OrderBy(value => value.StoredAtUnixTimeMilliseconds), cancellationToken)
                    .ConfigureAwait(false);
                if (expired.Count == 0) break;
                await DeleteCandidatesAsync(context, expired, cancellationToken).ConfigureAwait(false);
                retentionDeleted += expired.Count;
                deletedBytes += expired.Sum(value => (long)value.StoredBytes);
            }
        }

        var storedBytes = await GetStoredBytesAsync(context, cancellationToken).ConfigureAwait(false);
        if (request.TargetStoredBytes.HasValue && storedBytes > request.TargetStoredBytes.Value)
        {
            while (storedBytes > request.TargetStoredBytes.Value)
            {
                var candidates = await SelectCandidates(CandidateQuery(context, request.ExcludedKey)
                        .OrderBy(value => value.LastAccessedAtUnixTimeMilliseconds)
                        .ThenBy(value => value.StoredAtUnixTimeMilliseconds), cancellationToken)
                    .ConfigureAwait(false);
                if (candidates.Count == 0) break;
                var selected = new List<CleanupCandidate>();
                foreach (var candidate in candidates)
                {
                    if (storedBytes <= request.TargetStoredBytes.Value) break;
                    selected.Add(candidate);
                    storedBytes -= candidate.StoredBytes;
                }

                await DeleteCandidatesAsync(context, selected, cancellationToken).ConfigureAwait(false);
                capacityDeleted += selected.Count;
                deletedBytes += selected.Sum(value => (long)value.StoredBytes);
            }
        }

        var state = await context.MaintenanceStates.SingleOrDefaultAsync(value => value.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            state = new AnalysisMaintenanceState { LastReason = request.Reason };
            context.MaintenanceStates.Add(state);
        }

        state.LastCompletedAt = request.CompletedAt;
        state.LastReason = request.Reason;
        state.LastRetentionDeletedRecords = retentionDeleted;
        state.LastCapacityDeletedRecords = capacityDeleted;
        state.LastDeletedBytes = deletedBytes;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var storedBytesAfter = await GetStoredBytesAsync(context, cancellationToken).ConfigureAwait(false);
        return new AnalysisCleanupResult(retentionDeleted, capacityDeleted, deletedBytes, storedBytesAfter);
    }

    /// <inheritdoc />
    public async Task<AnalysisMaintenanceSummary?> GetMaintenanceSummaryAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.MaintenanceStates
            .AsNoTracking()
            .Where(value => value.Id == 1)
            .Select(value => new AnalysisMaintenanceSummary(
                value.LastCompletedAt,
                value.LastReason,
                value.LastRetentionDeletedRecords,
                value.LastCapacityDeletedRecords,
                value.LastDeletedBytes))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static IQueryable<AnalysisRecord> CandidateQuery(
        AnalysisDbContext context,
        AnalysisKey? excludedKey)
    {
        var query = context.Analyses.AsNoTracking();
        if (excludedKey.HasValue)
        {
            var key = excludedKey.Value;
            query = query.Where(value => value.ItemId != key.ItemId
                || value.MediaSourceId != key.MediaSourceId
                || value.AlgorithmId != key.AlgorithmId
                || value.AlgorithmVersion != key.AlgorithmVersion);
        }
        return query;
    }

    private static async Task DeleteCandidatesAsync(
        AnalysisDbContext context,
        IReadOnlyCollection<CleanupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            context.Analyses.Remove(new AnalysisRecord
            {
                ItemId = candidate.ItemId,
                MediaSourceId = candidate.MediaSourceId,
                AlgorithmId = candidate.AlgorithmId,
                AlgorithmVersion = candidate.AlgorithmVersion,
                MediaFingerprint = string.Empty,
                FingerprintQuality = string.Empty,
                Etag = string.Empty,
                CompressedDocument = [],
                UncompressedBytes = 0,
                FrameCount = 0,
                SourceIntervalMs = 0
            });
        }

        if (candidates.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<List<CleanupCandidate>> SelectCandidates(
        IOrderedQueryable<AnalysisRecord> query,
        CancellationToken cancellationToken) => query
        .Take(CleanupBatchSize)
        .Select(value => new CleanupCandidate(
            value.ItemId,
            value.MediaSourceId,
            value.AlgorithmId,
            value.AlgorithmVersion,
            value.CompressedDocument.Length,
            value.StoredAtUnixTimeMilliseconds,
            value.LastAccessedAtUnixTimeMilliseconds))
        .ToListAsync(cancellationToken);

    private static Task<long> GetStoredBytesAsync(
        AnalysisDbContext context,
        CancellationToken cancellationToken) => context.Analyses
        .Select(value => (long)value.CompressedDocument.Length)
        .SumAsync(cancellationToken);

    private static Task<AnalysisRecord?> FindAsync(
        AnalysisDbContext context,
        AnalysisKey key,
        CancellationToken cancellationToken)
    {
        return context.Analyses.AsNoTracking().SingleOrDefaultAsync(
            value => value.ItemId == key.ItemId
                && value.MediaSourceId == key.MediaSourceId
                && value.AlgorithmId == key.AlgorithmId
                && value.AlgorithmVersion == key.AlgorithmVersion,
            cancellationToken);
    }

    private sealed record CleanupCandidate(
        Guid ItemId,
        string MediaSourceId,
        string AlgorithmId,
        string AlgorithmVersion,
        int StoredBytes,
        long StoredAtUnixTimeMilliseconds,
        long LastAccessedAtUnixTimeMilliseconds);
}
