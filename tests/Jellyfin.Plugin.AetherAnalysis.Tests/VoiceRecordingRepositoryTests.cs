using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>
/// Der serverweite Vorrat der eingesprochenen Zeilen.
///
/// Er hängt bewusst an KEINEM Item: dieselben Sätze passen zu jedem Film.
/// Gebraucht wird er, weil die Aufnahmen vorher nur in der IndexedDB des
/// Browsers lagen — also je Gerät und je Adresse getrennt. Wer am Rechner
/// einspricht, hatte auf der Quest nichts.
/// </summary>
public sealed class VoiceRecordingRepositoryTests
{
    /// <summary>Kontext-Fabrik über eine echte SQLite-Datei, wie in AnalysisRepositoryTests.</summary>
    private sealed class TestContextFactory(DbContextOptions<AnalysisDbContext> options)
        : IDbContextFactory<AnalysisDbContext>
    {
        public AnalysisDbContext CreateDbContext() => new(options);
    }

    private static async Task<(VoiceRecordingRepository Repository, string Path)> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-voice-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<AnalysisDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var factory = new TestContextFactory(options);
        await using (var context = factory.CreateDbContext())
        {
            await context.Database.MigrateAsync();
        }

        return (new VoiceRecordingRepository(factory), path);
    }

    [Fact]
    public async Task StoresAndReturnsARecording()
    {
        var (repository, path) = await CreateAsync();
        try
        {
            await repository.UpsertAsync("fen-3", "audio/mpeg", [1, 2, 3, 4], CancellationToken.None);

            var stored = await repository.GetAsync("fen-3", CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal("audio/mpeg", stored.ContentType);
            Assert.Equal([1, 2, 3, 4], stored.Content);
            Assert.Equal(4, stored.ContentLength);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReplacesInsteadOfAccumulating()
    {
        // Wer eine Zeile neu einspricht, will sie ERSETZEN. Zwei Aufnahmen für
        // denselben Satz wären ein Zustand, den niemand auflösen kann.
        var (repository, path) = await CreateAsync();
        try
        {
            await repository.UpsertAsync("fen-3", "audio/mpeg", [1, 2, 3], CancellationToken.None);
            await repository.UpsertAsync("fen-3", "audio/wav", [9, 9, 9, 9, 9], CancellationToken.None);

            var list = await repository.ListAsync(CancellationToken.None);
            Assert.Single(list);
            Assert.Equal("audio/wav", list[0].ContentType);
            Assert.Equal(5, list[0].Bytes);
            Assert.Equal(5, await repository.TotalBytesAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ListsWithoutLoadingTheAudio()
    {
        // Die Übersicht holt der Client bei jedem Start. Sie darf nicht die
        // gesamten Audiodaten durch die Leitung ziehen.
        var (repository, path) = await CreateAsync();
        try
        {
            await repository.UpsertAsync("ein-1", "audio/mpeg", new byte[512], CancellationToken.None);
            await repository.UpsertAsync("fen-7", "audio/mpeg", new byte[1024], CancellationToken.None);

            var list = await repository.ListAsync(CancellationToken.None);

            Assert.Equal(["ein-1", "fen-7"], list.Select(item => item.LineId));
            Assert.Equal([512, 1024], list.Select(item => item.Bytes));
            Assert.Equal(1536, await repository.TotalBytesAsync(CancellationToken.None));
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
            await repository.UpsertAsync("fen-3", "audio/mpeg", [1], CancellationToken.None);

            Assert.True(await repository.DeleteAsync("fen-3", CancellationToken.None));
            Assert.False(await repository.DeleteAsync("fen-3", CancellationToken.None));
            Assert.Null(await repository.GetAsync("fen-3", CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReportsNothingForAnEmptyPack()
    {
        var (repository, path) = await CreateAsync();
        try
        {
            Assert.Empty(await repository.ListAsync(CancellationToken.None));
            Assert.Equal(0, await repository.TotalBytesAsync(CancellationToken.None));
            Assert.Null(await repository.GetAsync("gibt-es-nicht", CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
