using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>
/// Der serverweite Katalog vermessener Geräteprofile (siehe <see cref="DeviceQualityProfile"/>).
///
/// Anders als das Regler-Preset hängt ein Profil an KEINEM Nutzer, sondern an der
/// Geräteklasse (Client, Treppe, Gerät) — wie das Sprachpaket. Die Tests decken deshalb vor
/// allem Last-Write-Wins ohne Historie und den zusammengesetzten Schlüssel ab.
/// </summary>
public sealed class DeviceQualityProfileRepositoryTests
{
    /// <summary>Kontext-Fabrik über eine echte SQLite-Datei, wie in VoiceRecordingRepositoryTests.</summary>
    private sealed class TestContextFactory(DbContextOptions<AnalysisDbContext> options)
        : IDbContextFactory<AnalysisDbContext>
    {
        public AnalysisDbContext CreateDbContext() => new(options);
    }

    private static async Task<(DeviceQualityProfileRepository Repository, string Path)> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-device-profiles-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<AnalysisDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var factory = new TestContextFactory(options);
        await using (var context = factory.CreateDbContext())
        {
            await context.Database.MigrateAsync();
        }

        return (new DeviceQualityProfileRepository(factory), path);
    }

    private const string Document = """{"schema":"aether.device-quality-profile","schemaVersion":1}""";

    [Fact]
    public async Task StoresAndReturnsAProfile()
    {
        var (repository, path) = await CreateAsync();
        try
        {
            await repository.UpsertAsync("tvos", "tvos-7", "AppleTV14,1", "AppleTV14,1", "schnell", "2026-08-28T05:41:42Z", 3, 5, Document, CancellationToken.None);

            var stored = await repository.GetAsync("tvos", "tvos-7", "AppleTV14,1", CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal("AppleTV14,1", stored.DeviceDescription);
            Assert.Equal("schnell", stored.Mode);
            Assert.Equal("2026-08-28T05:41:42Z", stored.FinishedAt);
            Assert.Equal(3, stored.EntryCount);
            Assert.Equal(5, stored.FallbackStartStepIndex);
            Assert.Equal(Document, stored.DocumentJson);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReplacesInsteadOfAccumulating()
    {
        // Eine neue Selbstvermessung ist per Definition aktueller als die vorige — der
        // Katalog kennt keine Historie, nur den letzten Stand (Last-Write-Wins).
        var (repository, path) = await CreateAsync();
        try
        {
            await repository.UpsertAsync("tvos", "tvos-7", "AppleTV14,1", "Erst", "schnell", "2026-01-01T00:00:00Z", 1, null, Document, CancellationToken.None);
            await repository.UpsertAsync("tvos", "tvos-7", "AppleTV14,1", "Neu", "vollstaendig", "2026-02-02T00:00:00Z", 4, 2, Document, CancellationToken.None);

            var list = await repository.ListAsync(CancellationToken.None);
            Assert.Single(list);
            Assert.Equal("Neu", list[0].DeviceDescription);
            Assert.Equal("vollstaendig", list[0].Mode);
            Assert.Equal(4, list[0].EntryCount);
            Assert.Equal(2, list[0].FallbackStartStepIndex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task KeepsDifferentDevicesUnderTheSameLadderSeparate()
    {
        // Der Schlüssel ist dreiteilig: gleiche Treppe, verschiedene Geräte dürfen sich
        // nicht überschreiben.
        var (repository, path) = await CreateAsync();
        try
        {
            await repository.UpsertAsync("tvos", "tvos-7", "AppleTV14,1", "Apple TV 4K (3. Gen)", "schnell", "2026-01-01T00:00:00Z", 1, null, Document, CancellationToken.None);
            await repository.UpsertAsync("tvos", "tvos-7", "AppleTV11,1", "Apple TV HD", "schnell", "2026-01-01T00:00:00Z", 1, null, Document, CancellationToken.None);

            var list = await repository.ListAsync(CancellationToken.None);
            Assert.Equal(2, list.Count);
            Assert.Contains(list, value => value.DeviceIdentifier == "AppleTV14,1");
            Assert.Contains(list, value => value.DeviceIdentifier == "AppleTV11,1");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ListsNewestWriteFirst()
    {
        var (repository, path) = await CreateAsync();
        try
        {
            await repository.UpsertAsync("web", "web-7", "web-11ffa05c", "Chrome", "schnell", "2026-01-01T00:00:00Z", 1, null, Document, CancellationToken.None);
            await repository.UpsertAsync("tvos", "tvos-7", "AppleTV14,1", "Apple TV 4K", "schnell", "2026-01-01T00:00:00Z", 1, null, Document, CancellationToken.None);

            var list = await repository.ListAsync(CancellationToken.None);
            Assert.Equal(["AppleTV14,1", "web-11ffa05c"], list.Select(value => value.DeviceIdentifier));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ListsWithoutLoadingTheDocument()
    {
        // Die Katalogübersicht holt der Client bei jedem Start. Sie darf nicht den
        // gesamten Profil-Text jedes Geräts durch die Leitung ziehen.
        var (repository, path) = await CreateAsync();
        try
        {
            await repository.UpsertAsync("tvos", "tvos-7", "AppleTV14,1", "Apple TV 4K", "schnell", "2026-01-01T00:00:00Z", 1, null, Document, CancellationToken.None);

            var list = await repository.ListAsync(CancellationToken.None);
            Assert.Single(list);
            Assert.Equal("Apple TV 4K", list[0].DeviceDescription);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReportsNothingForAnEmptyCatalog()
    {
        var (repository, path) = await CreateAsync();
        try
        {
            Assert.Empty(await repository.ListAsync(CancellationToken.None));
            Assert.Null(await repository.GetAsync("tvos", "tvos-7", "gibt-es-nicht", CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
