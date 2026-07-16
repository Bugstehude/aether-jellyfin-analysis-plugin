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
                await context.Database.EnsureCreatedAsync();
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

    private static AnalysisRecord CreateRecord(string etag) => new()
    {
        ItemId = Guid.Parse("61fd9006-c087-4f95-822d-cbf1997d5e1a"),
        MediaSourceId = "source-1",
        AlgorithmId = "aether-visual",
        AlgorithmVersion = "1.0.0",
        MediaFingerprint = "sha256:" + new string('a', 64),
        FingerprintQuality = "strong",
        Etag = etag,
        CompressedDocument = [1, 2, 3],
        UncompressedBytes = 3,
        FrameCount = 1,
        SourceIntervalMs = 250,
        CreatedAt = DateTimeOffset.UtcNow,
        StoredAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow
    };

    private sealed class TestContextFactory(DbContextOptions<AnalysisDbContext> options)
        : IDbContextFactory<AnalysisDbContext>
    {
        public AnalysisDbContext CreateDbContext() => new(options);

        public Task<AnalysisDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
