using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class AnalysisRepositoryTests
{
    [Fact]
    public async Task UpsertHonorsEtagPreconditionAndReportsStats()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-analysis-{Guid.NewGuid():N}.sqlite");
        try
        {
            var options = new DbContextOptionsBuilder<AnalysisDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            var factory = new TestContextFactory(options);
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            var repository = new AnalysisRepository(factory);
            var record = CreateRecord("\"etag-1\"");
            Assert.True(await repository.UpsertAsync(record, expectedEtag: null, CancellationToken.None));

            var replacement = CreateRecord("\"etag-2\"");
            Assert.False(await repository.UpsertAsync(replacement, "\"wrong\"", CancellationToken.None));
            Assert.True(await repository.UpsertAsync(replacement, "\"etag-1\"", CancellationToken.None));

            var stored = await repository.GetAsync(
                new AnalysisKey(record.ItemId, record.MediaSourceId, record.AlgorithmId, record.AlgorithmVersion),
                CancellationToken.None);
            var stats = await repository.GetStatsAsync(CancellationToken.None);

            Assert.Equal("\"etag-2\"", stored!.Etag);
            Assert.Equal(1, stats.RecordCount);
            Assert.Equal(replacement.CompressedDocument.Length, stats.StoredBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CleanupAppliesRetentionThenLruAndPersistsSummary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-analysis-{Guid.NewGuid():N}.sqlite");
        try
        {
            var options = new DbContextOptionsBuilder<AnalysisDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            var factory = new TestContextFactory(options);
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            var repository = new AnalysisRepository(factory);
            var now = DateTimeOffset.UtcNow;
            await repository.UpsertAsync(CreateRecord("source-expired", "\"expired\"", 3, now.AddDays(-30)), null, default);
            await repository.UpsertAsync(CreateRecord("source-lru", "\"lru\"", 5, now.AddDays(-2)), null, default);
            await repository.UpsertAsync(CreateRecord("source-keep", "\"keep\"", 7, now.AddDays(-1)), null, default);

            var result = await repository.CleanupAsync(
                new AnalysisCleanupRequest(now.AddDays(-7), 7, null, "test", now),
                default);
            var stats = await repository.GetStatsAsync(default);
            var summary = await repository.GetMaintenanceSummaryAsync(default);

            Assert.Equal(1, result.RetentionDeletedRecords);
            Assert.Equal(1, result.CapacityDeletedRecords);
            Assert.Equal(8, result.DeletedBytes);
            Assert.Equal(7, result.StoredBytesAfter);
            Assert.Equal(1, stats.RecordCount);
            Assert.Equal("test", summary!.LastReason);
            Assert.Equal(now.ToUnixTimeMilliseconds(), summary.LastCompletedAt!.Value.ToUnixTimeMilliseconds());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GetIsReadOnlyAndTouchAdvancesOldAccessTime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-analysis-{Guid.NewGuid():N}.sqlite");
        try
        {
            var options = new DbContextOptionsBuilder<AnalysisDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            var factory = new TestContextFactory(options);
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            var repository = new AnalysisRepository(factory);
            var oldAccess = DateTimeOffset.UtcNow.AddDays(-2);
            var record = CreateRecord("\"etag-touch\"", storedAt: oldAccess);
            var key = new AnalysisKey(
                record.ItemId,
                record.MediaSourceId,
                record.AlgorithmId,
                record.AlgorithmVersion);
            Assert.True(await repository.UpsertAsync(record, null, default));

            _ = await repository.GetAsync(key, default);
            await using (var context = await factory.CreateDbContextAsync())
            {
                var unchanged = await context.Analyses.AsNoTracking().SingleAsync();
                Assert.Equal(oldAccess.ToUnixTimeMilliseconds(), unchanged.LastAccessedAtUnixTimeMilliseconds);
            }

            await repository.TouchAsync(key, default);
            await using (var context = await factory.CreateDbContextAsync())
            {
                var touched = await context.Analyses.AsNoTracking().SingleAsync();
                Assert.True(touched.LastAccessedAt > oldAccess.AddHours(1));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BoundedStoreEvictsAndCommitsNewRecordTogether()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-analysis-{Guid.NewGuid():N}.sqlite");
        try
        {
            var options = new DbContextOptionsBuilder<AnalysisDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            var factory = new TestContextFactory(options);
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            var repository = new AnalysisRepository(factory);
            var now = DateTimeOffset.UtcNow;
            await repository.UpsertAsync(CreateRecord("source-old", "\"old\"", 5, now.AddDays(-2)), null, default);
            var incoming = CreateRecord("source-new", "\"new\"", 7, now);

            var result = await repository.StoreBoundedAsync(
                new AnalysisStoreRequest(incoming, [], false, 7, null, now),
                default);
            var stats = await repository.GetStatsAsync(default);

            Assert.Equal(AnalysisStoreResult.Created, result);
            Assert.Equal(1, stats.RecordCount);
            Assert.Equal(7, stats.StoredBytes);
            Assert.NotNull(await repository.GetAsync(
                new AnalysisKey(
                    incoming.ItemId,
                    incoming.MediaSourceId,
                    incoming.AlgorithmId,
                    incoming.AlgorithmVersion),
                default));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MetadataBatchReturnsOnlyRequestedLightweightRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-analysis-{Guid.NewGuid():N}.sqlite");
        try
        {
            var options = new DbContextOptionsBuilder<AnalysisDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            var factory = new TestContextFactory(options);
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            var repository = new AnalysisRepository(factory);
            var requested = CreateRecord("source-requested", "\"requested\"", 11);
            var unrelated = CreateRecord("source-unrelated", "\"unrelated\"", 17);
            await repository.UpsertAsync(requested, null, default);
            await repository.UpsertAsync(unrelated, null, default);
            var requestedKey = new AnalysisKey(
                requested.ItemId,
                requested.MediaSourceId,
                requested.AlgorithmId,
                requested.AlgorithmVersion);
            var missingKey = requestedKey with { MediaSourceId = "source-missing" };

            var metadata = await repository.GetMetadataAsync([requestedKey, missingKey], default);

            var result = Assert.Single(metadata);
            Assert.Equal(requestedKey, result.Key);
            Assert.Equal(11, result.Value.StoredBytes);
            Assert.Equal("\"requested\"", result.Value.Etag);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FailedBoundedStorePreconditionDoesNotEvictOtherRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-analysis-{Guid.NewGuid():N}.sqlite");
        try
        {
            var options = new DbContextOptionsBuilder<AnalysisDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            var factory = new TestContextFactory(options);
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            var repository = new AnalysisRepository(factory);
            var now = DateTimeOffset.UtcNow;
            var current = CreateRecord("source-current", "\"current\"", 5, now);
            await repository.UpsertAsync(current, null, default);
            await repository.UpsertAsync(CreateRecord("source-victim", "\"victim\"", 5, now.AddDays(-1)), null, default);
            var replacement = CreateRecord("source-current", "\"replacement\"", 5, now);

            var result = await repository.StoreBoundedAsync(
                new AnalysisStoreRequest(replacement, ["\"wrong\""], true, 5, null, now),
                default);
            var stats = await repository.GetStatsAsync(default);

            Assert.Equal(AnalysisStoreResult.PreconditionFailed, result);
            Assert.Equal(2, stats.RecordCount);
            Assert.Equal(10, stats.StoredBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MigrationAdoptsEnsureCreatedDevelopmentDatabaseWithoutDataLoss()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-analysis-{Guid.NewGuid():N}.sqlite");
        try
        {
            var options = new DbContextOptionsBuilder<AnalysisDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            await using (var developmentContext = new AnalysisDbContext(options))
            {
                await developmentContext.Database.EnsureCreatedAsync();
                developmentContext.Analyses.Add(CreateRecord("source-legacy", "\"legacy\""));
                await developmentContext.SaveChangesAsync();
            }

            await using (var migratedContext = new AnalysisDbContext(options))
            {
                await migratedContext.Database.MigrateAsync();
                Assert.Single(await migratedContext.Analyses.AsNoTracking().ToListAsync());
                Assert.Contains(
                    await migratedContext.Database.GetAppliedMigrationsAsync(),
                    value => value.EndsWith("_ProductionBaseline", StringComparison.Ordinal));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AnalysisRecord CreateRecord(
        string etag,
        int storedBytes = 3,
        DateTimeOffset? storedAt = null) => CreateRecord("source-1", etag, storedBytes, storedAt);

    private static AnalysisRecord CreateRecord(
        string mediaSourceId,
        string etag,
        int storedBytes = 3,
        DateTimeOffset? storedAt = null) => new()
        {
            ItemId = Guid.Parse("61fd9006-c087-4f95-822d-cbf1997d5e1a"),
            MediaSourceId = mediaSourceId,
            AlgorithmId = "aether-visual",
            AlgorithmVersion = "1.0.0",
            MediaFingerprint = "sha256:" + new string('a', 64),
            FingerprintQuality = "strong",
            Etag = etag,
            CompressedDocument = Enumerable.Repeat((byte)1, storedBytes).ToArray(),
            UncompressedBytes = storedBytes,
            FrameCount = 1,
            SourceIntervalMs = 250,
            CreatedAt = DateTimeOffset.UtcNow,
            StoredAt = storedAt ?? DateTimeOffset.UtcNow,
            LastAccessedAt = storedAt ?? DateTimeOffset.UtcNow
        };

    private sealed class TestContextFactory(DbContextOptions<AnalysisDbContext> options)
        : IDbContextFactory<AnalysisDbContext>
    {
        public AnalysisDbContext CreateDbContext() => new(options);

        public Task<AnalysisDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
