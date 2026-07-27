using Jellyfin.Plugin.AetherAnalysis.Infrastructure;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>
/// Die Ablage der Reise-Tonspur. Geprüft werden vor allem die Fälle, in denen
/// etwas SCHIEFGEHT — eine Ablage, die nur den geraden Weg kann, verliert im
/// Alltag Daten: der Upload bricht ab, die Grenze fällt, das Verzeichnis fehlt.
/// </summary>
public sealed class JourneyTrackStoreTests : IDisposable
{
    private readonly string folder = Path.Combine(
        Path.GetTempPath(), "aether-journey-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private JourneyTrackStore Store => new(folder);

    [Fact]
    public void ReportsNothingWhenNoTrackIsStored()
    {
        // Kein Verzeichnis, keine Datei — und trotzdem kein Fehler: die Reise
        // läuft ohne Server weiter, nur ohne Abgleich zwischen Geräten.
        Assert.Null(Store.GetInfo());
        Assert.Null(Store.OpenRead());
    }

    [Fact]
    public async Task WritesAndReadsBackTheExactBytes()
    {
        var payload = new byte[64 * 1024 + 7];
        Random.Shared.NextBytes(payload);

        var written = await Store.WriteAsync(new MemoryStream(payload), "audio/mpeg", CancellationToken.None);

        Assert.Equal(payload.Length, written);
        var info = Store.GetInfo();
        Assert.NotNull(info);
        Assert.Equal("audio/mpeg", info!.ContentType);
        Assert.Equal(payload.Length, info.Bytes);

        await using var stream = Store.OpenRead();
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        await stream!.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task ReplacesAnExistingTrack()
    {
        await Store.WriteAsync(new MemoryStream(new byte[1000]), "audio/mpeg", CancellationToken.None);
        await Store.WriteAsync(new MemoryStream(new byte[25]), "audio/ogg", CancellationToken.None);

        var info = Store.GetInfo();
        Assert.Equal(25, info!.Bytes);
        Assert.Equal("audio/ogg", info.ContentType);
    }

    [Fact]
    public async Task KeepsThePreviousTrackWhenTheNewOneExceedsTheLimit()
    {
        // Der wichtige Teil ist der zweite Halbsatz. Eine Ablage, die erst
        // loeschte und dann am Limit scheiterte, haette die vorhandene Aufnahme
        // gegen nichts eingetauscht — und der Verlust faellt erst beim naechsten
        // Abspielen auf, wenn niemand mehr weiss, warum.
        await Store.WriteAsync(new MemoryStream(new byte[512]), "audio/mpeg", CancellationToken.None);

        var tooLarge = new EndlessStream();
        var result = await Store.WriteAsync(tooLarge, "audio/mpeg", CancellationToken.None);

        Assert.Null(result);
        var info = Store.GetInfo();
        Assert.NotNull(info);
        Assert.Equal(512, info!.Bytes);
    }

    [Fact]
    public async Task LeavesNoPartialFilesBehind()
    {
        // Die .part-Datei muss weg sein, egal wie der Aufruf ausging — sonst
        // sammelt sich auf dem Server bei jedem abgebrochenen Upload ein
        // weiteres Bruchstueck an, und niemand raeumt es je auf.
        await Store.WriteAsync(new EndlessStream(), "audio/mpeg", CancellationToken.None);

        Assert.Empty(Directory.GetFiles(folder, "*.part"));
    }

    [Fact]
    public async Task StoresNothingForAnEmptyBody()
    {
        var written = await Store.WriteAsync(new MemoryStream([]), "audio/mpeg", CancellationToken.None);

        Assert.Equal(0, written);
        Assert.Null(Store.GetInfo());
    }

    [Fact]
    public async Task DeleteIsRepeatable()
    {
        await Store.WriteAsync(new MemoryStream(new byte[10]), "audio/mpeg", CancellationToken.None);
        Store.Delete();
        Store.Delete();

        Assert.Null(Store.GetInfo());
    }

    [Fact]
    public void LimitFitsAWholePieceButNotAWholeDisk()
    {
        // Zwanzig Minuten MP3 sind rund 20 MiB; ohne genug Luft nach oben waere
        // der Endpunkt fuer seinen Zweck unbrauchbar. Nach oben trotzdem
        // begrenzt, sonst fuellt ein versehentlich gewaehltes Video die Platte.
        Assert.True(JourneyTrackStore.MaxTrackBytes >= 100L * 1024 * 1024);
        Assert.True(JourneyTrackStore.MaxTrackBytes <= 1024L * 1024 * 1024);
    }

    /// <summary>Liefert endlos Nullbytes — für den Fall „über der Grenze".</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => count;

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
