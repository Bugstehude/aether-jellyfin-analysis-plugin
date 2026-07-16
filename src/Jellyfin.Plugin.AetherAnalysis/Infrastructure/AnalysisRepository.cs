using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>EF Core implementation of plugin-owned analysis storage.</summary>
public sealed class AnalysisRepository(IDbContextFactory<AnalysisDbContext> contextFactory)
    : IAnalysisRepository
{
    /// <inheritdoc />
    public async Task<AnalysisRecord?> GetAsync(AnalysisKey key, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var record = await FindAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        record.LastAccessedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return record;
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
            context.Entry(existing).CurrentValues.SetValues(record);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
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

    private static Task<AnalysisRecord?> FindAsync(
        AnalysisDbContext context,
        AnalysisKey key,
        CancellationToken cancellationToken)
    {
        return context.Analyses.SingleOrDefaultAsync(
            value => value.ItemId == key.ItemId
                && value.MediaSourceId == key.MediaSourceId
                && value.AlgorithmId == key.AlgorithmId
                && value.AlgorithmVersion == key.AlgorithmVersion,
            cancellationToken);
    }
}
