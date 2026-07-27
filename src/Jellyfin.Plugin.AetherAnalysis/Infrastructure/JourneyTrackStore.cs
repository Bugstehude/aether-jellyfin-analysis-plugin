namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Was über die abgelegte Reise-Tonspur bekannt ist, ohne sie zu laden.</summary>
public sealed record JourneyTrackInfo(string ContentType, long Bytes, DateTimeOffset UpdatedAt);

/// <summary>
/// Die EINE lange Tonspur des Erlebnisses „Reise" (ADR-016).
///
/// <para>Bewusst als DATEI neben der Datenbank, nicht als Zeile darin. Der
/// Sprachvorrat liegt in SQLite, weil dort achtzehn Aufnahmen à wenige hundert
/// Kilobyte liegen und die Datenbank klein genug bleiben soll, um sie
/// mitzusichern. Eine durchgehende Aufnahme von zwanzig Minuten und mehr würde
/// genau das kaputt machen: jede Sicherung der Plugin-Datenbank trüge sie
/// fortan mit.</para>
///
/// <para>Aus demselben Grund wird beim Hochladen direkt in die Datei
/// geschrieben statt erst in den Arbeitsspeicher. Der Weg über einen
/// MemoryStream (wie bei den kurzen Zeilen) kostet bei 200 MiB das Doppelte
/// davon an RAM — auf einem Server, der nebenbei transkodiert, ist das kein
/// Detail.</para>
///
/// <para>Alles hier ist tolerant gegenüber einem fehlenden Verzeichnis oder
/// einer halb geschriebenen Datei: die Reise funktioniert ohne Server weiter,
/// nur eben ohne Abgleich zwischen Geräten.</para>
/// </summary>
public sealed class JourneyTrackStore
{
    /// <summary>
    /// Obergrenze der Spur. Großzügig, weil hier ganze Stücke liegen: 20 Minuten
    /// als MP3 sind rund 20 MiB, eine Stunde verlustfrei ein Vielfaches davon.
    /// Eine Grenze braucht es trotzdem — sonst füllt ein versehentlich gewähltes
    /// Videoformat die Serverplatte, und der Fehler fiele erst dort auf.
    /// </summary>
    public const long MaxTrackBytes = 256L * 1024 * 1024;

    private readonly string dataFolder;

    /// <summary>Legt den Speicher unter dem Datenverzeichnis des Plugins an.</summary>
    /// <param name="dataFolder">Datenverzeichnis des Plugins.</param>
    public JourneyTrackStore(string dataFolder)
    {
        this.dataFolder = dataFolder;
    }

    private string TrackPath => Path.Combine(dataFolder, "journey-track.bin");

    private string TypePath => Path.Combine(dataFolder, "journey-track.type");

    /// <summary>Was abgelegt ist, oder null. Wirft nie.</summary>
    public JourneyTrackInfo? GetInfo()
    {
        try
        {
            var file = new FileInfo(TrackPath);
            if (!file.Exists || file.Length == 0)
            {
                return null;
            }

            var contentType = File.Exists(TypePath) ? File.ReadAllText(TypePath).Trim() : string.Empty;
            return new JourneyTrackInfo(
                contentType.Length is > 0 and <= 64 ? contentType : "audio/mpeg",
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Öffnet die Spur zum Lesen, oder null.</summary>
    public Stream? OpenRead()
    {
        try
        {
            return File.Exists(TrackPath)
                ? new FileStream(TrackPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Schreibt die Spur. Erst in eine Nebendatei, dann umbenennen: bricht der
    /// Upload ab, bleibt die vorige Spur unversehrt stehen, statt dass ein
    /// halbes Stück die alte ersetzt.
    /// </summary>
    /// <returns>Die geschriebene Länge, oder null, wenn die Grenze überschritten wurde.</returns>
    public async Task<long?> WriteAsync(Stream body, string contentType, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataFolder);
        var temporary = TrackPath + ".part";
        long written = 0;
        try
        {
            await using (var target = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > MaxTrackBytes)
                    {
                        // Abbrechen, sobald die Grenze fällt — NICHT erst alles
                        // annehmen und dann ablehnen. Sonst hätte ein einziger
                        // Aufruf die Platte gefüllt, bevor die Prüfung greift.
                        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                        return null;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            if (written == 0)
            {
                return 0;
            }

            File.Move(temporary, TrackPath, overwrite: true);
            await File.WriteAllTextAsync(TypePath, contentType, cancellationToken).ConfigureAwait(false);
            return written;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>Verwirft die Spur; wirkt auch, wenn keine da ist.</summary>
    public void Delete()
    {
        TryDelete(TrackPath);
        TryDelete(TypePath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Aufräumen ist best-effort: eine liegengebliebene .part-Datei ist
            // ärgerlich, aber kein Grund, den Aufruf scheitern zu lassen.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
