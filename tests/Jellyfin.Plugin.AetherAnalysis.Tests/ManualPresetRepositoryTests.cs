using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>
/// Der je-Nutzer-Vorrat der Regler-Panel-Presets (ADR-026-Nachtrag "Eigene
/// Erlebnisse"). Anders als das Sprachpaket ist ein Preset PERSÖNLICH — die
/// Tests decken deshalb vor allem die Trennung zwischen Nutzern ab.
/// </summary>
public sealed class ManualPresetRepositoryTests
{
    /// <summary>Kontext-Fabrik über eine echte SQLite-Datei, wie in VoiceRecordingRepositoryTests.</summary>
    private sealed class TestContextFactory(DbContextOptions<AnalysisDbContext> options)
        : IDbContextFactory<AnalysisDbContext>
    {
        public AnalysisDbContext CreateDbContext() => new(options);
    }

    private static async Task<(ManualPresetRepository Repository, string Path)> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-presets-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<AnalysisDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var factory = new TestContextFactory(options);
        await using (var context = factory.CreateDbContext())
        {
            await context.Database.MigrateAsync();
        }

        return (new ManualPresetRepository(factory), path);
    }

    private const string Snapshot = """{"knobs":{"grain":0.4},"rimColor":[1,0.5,0]}""";

    [Fact]
    public async Task StoresAndListsAPreset()
    {
        var (repository, path) = await CreateAsync();
        try
        {
            var userId = Guid.NewGuid();
            await repository.UpsertAsync(userId, "preset-1", "Mein Preset", Snapshot, CancellationToken.None);

            var stored = await repository.ListAsync(userId, CancellationToken.None);
            Assert.Single(stored);
            Assert.Equal("preset-1", stored[0].Id);
            Assert.Equal("Mein Preset", stored[0].Name);
            Assert.Equal(Snapshot, stored[0].SnapshotJson);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReplacesInsteadOfAccumulating()
    {
        var (repository, path) = await CreateAsync();
        try
        {
            var userId = Guid.NewGuid();
            await repository.UpsertAsync(userId, "preset-1", "Erst", Snapshot, CancellationToken.None);
            await repository.UpsertAsync(userId, "preset-1", "Umbenannt", Snapshot, CancellationToken.None);

            var stored = await repository.ListAsync(userId, CancellationToken.None);
            Assert.Single(stored);
            Assert.Equal("Umbenannt", stored[0].Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NeverSeesAnotherUsersPresets()
    {
        // Der Kern der Sache: anders als das Sprachpaket ist ein Preset
        // persönlich. Zwei Nutzer dürfen dieselbe Kennung vergeben, ohne dass
        // sich ihre Vorräte berühren.
        var (repository, path) = await CreateAsync();
        try
        {
            var userA = Guid.NewGuid();
            var userB = Guid.NewGuid();
            await repository.UpsertAsync(userA, "shared-id", "Von A", Snapshot, CancellationToken.None);
            await repository.UpsertAsync(userB, "shared-id", "Von B", Snapshot, CancellationToken.None);

            var listA = await repository.ListAsync(userA, CancellationToken.None);
            var listB = await repository.ListAsync(userB, CancellationToken.None);

            Assert.Single(listA);
            Assert.Equal("Von A", listA[0].Name);
            Assert.Single(listB);
            Assert.Equal("Von B", listB[0].Name);

            Assert.True(await repository.DeleteAsync(userA, "shared-id", CancellationToken.None));
            Assert.Empty(await repository.ListAsync(userA, CancellationToken.None));
            Assert.Single(await repository.ListAsync(userB, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeletesIdempotently()
    {
        var (repository, path) = await CreateAsync();
        try
        {
            var userId = Guid.NewGuid();
            await repository.UpsertAsync(userId, "preset-1", "Preset", Snapshot, CancellationToken.None);

            Assert.True(await repository.DeleteAsync(userId, "preset-1", CancellationToken.None));
            Assert.False(await repository.DeleteAsync(userId, "preset-1", CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReportsNothingForAnEmptyVorrat()
    {
        var (repository, path) = await CreateAsync();
        try
        {
            var userId = Guid.NewGuid();
            Assert.Empty(await repository.ListAsync(userId, CancellationToken.None));
            Assert.Equal(0, await repository.CountAsync(userId, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
