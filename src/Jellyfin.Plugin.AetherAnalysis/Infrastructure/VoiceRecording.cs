using System.ComponentModel.DataAnnotations.Schema;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>
/// Eine eingesprochene Zeile der „Sitzung", serverweit abgelegt.
///
/// ## Warum das NICHT an einem Item hängt
/// Analysen gehören zu einer Mediendatei; eine eingesprochene Zeile gehört zu
/// niemandem. Dieselben Sätze passen zu jedem Film — sie einmal je Video
/// zuzuordnen wäre genau die Art Arbeit, an der so eine Funktion im Alltag
/// stirbt. Deshalb ein serverweiter Vorrat mit der Zeilen-Kennung als Schlüssel.
///
/// ## Warum überhaupt auf dem Server
/// Vorher lagen die Aufnahmen nur in der IndexedDB des Browsers — also je Gerät
/// und je Adresse getrennt. Wer am Rechner einspricht, hatte auf der Quest
/// nichts, und dort Dateien zuzuordnen ist zäh. Über den Server sind sie
/// einfach da.
/// </summary>
public sealed class VoiceRecording
{
    /// <summary>Zeilen-Kennung, klein geschrieben (z. B. <c>fen-3</c>).</summary>
    public required string LineId { get; set; }

    /// <summary>MIME-Typ der Aufnahme, wie vom Client gemeldet.</summary>
    public required string ContentType { get; set; }

    /// <summary>Die Audiodaten.</summary>
    public required byte[] Content { get; set; }

    /// <summary>Größe in Bytes — für die Gesamtgrenze, ohne die Daten zu laden.</summary>
    public int ContentLength { get; set; }

    /// <summary>Zeitpunkt der letzten Ablage, SQLite-sortierbar.</summary>
    public long UpdatedAtUnixTimeMilliseconds { get; set; }

    /// <summary>Zeitpunkt der letzten Ablage.</summary>
    [NotMapped]
    public DateTimeOffset UpdatedAt
    {
        get => DateTimeOffset.FromUnixTimeMilliseconds(UpdatedAtUnixTimeMilliseconds);
        set => UpdatedAtUnixTimeMilliseconds = value.ToUnixTimeMilliseconds();
    }
}
