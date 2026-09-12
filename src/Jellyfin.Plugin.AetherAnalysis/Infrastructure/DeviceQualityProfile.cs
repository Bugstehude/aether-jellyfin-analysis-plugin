using System.ComponentModel.DataAnnotations.Schema;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>
/// Ein vermessenes Geräteprofil im Vertrag <c>aether.device-quality-profile</c> v1
/// (siehe <c>contracts/schemas/device-quality-profile-v1.schema.json</c>), serverweit
/// abgelegt.
///
/// ## Warum serverweit statt je Nutzer (wie <see cref="ManualPreset"/>)
/// Das Profil hängt an der GERÄTEKLASSE (<see cref="DeviceIdentifier"/>, z. B.
/// "AppleTV14,1" oder ein Web-Fingerabdruck), nicht am anmeldenden Menschen: zwei
/// Nutzer an derselben Apple-TV-Generation sollen dasselbe Messergebnis lesen, statt
/// es je Konto neu zu vermessen. Das entspricht dem Sprachpaket
/// (<see cref="VoiceRecording"/>) und der Reise-Tonspur, nicht den Presets.
///
/// ## Warum Last-Write-Wins ohne Historie
/// Eine neue Selbstvermessung ist per Definition aktueller als die vorige — der Client
/// misst ja gerade deshalb neu, weil sich Firmware, Bauform oder Ausgabeauflösung
/// geändert haben könnte. Eine Historie mitzuführen würde nur Ballast anhäufen, den
/// niemand ausliest; der Schlüssel (Client, Treppe, Gerät) hat ohnehin keine Vorgänger-
/// Beziehung zu sich selbst.
///
/// ## Warum DocumentJson statt vollständig relationaler Felder
/// Der Vertrag (Erlebnis-Einträge, Policy-Felder, Beweisdaten je Stufe) wächst mit der
/// Zeit, genau wie beim Regler-Preset (<see cref="ManualPreset"/>). Die wenigen
/// zusätzlichen Spalten (<see cref="DeviceDescription"/>, <see cref="Mode"/>,
/// <see cref="FinishedAt"/>, <see cref="EntryCount"/>,
/// <see cref="FallbackStartStepIndex"/>) sind reine Lesebeschleunigung für die
/// Katalog-Übersicht — sie doppeln Felder aus dem JSON, statt es zu ersetzen, damit ein
/// GET auf den Einzeleintrag den Körper byteidentisch zurückgeben kann.
/// </summary>
public sealed class DeviceQualityProfile
{
    /// <summary>Client-Familie (<c>tvos</c> oder <c>web</c>), Teil des Primärschlüssels.</summary>
    public required string Client { get; set; }

    /// <summary>Kennung der Qualitätstreppe (z. B. <c>tvos-7</c>), Teil des Primärschlüssels.</summary>
    public required string Ladder { get; set; }

    /// <summary>Geräteklassen-Kennung (z. B. <c>AppleTV14,1</c>), Teil des Primärschlüssels.</summary>
    public required string DeviceIdentifier { get; set; }

    /// <summary>Gespiegeltes <c>device.description</c>, für die Katalog-Übersicht ohne Dokument-Zugriff.</summary>
    public required string DeviceDescription { get; set; }

    /// <summary>Gespiegeltes <c>run.mode</c> (<c>schnell</c>, <c>vollstaendig</c> oder <c>referenz</c>).</summary>
    public required string Mode { get; set; }

    /// <summary>
    /// Gespiegeltes <c>run.finishedAt</c>, unverändert als Zeichenkette. Der Client liefert
    /// bereits ISO-8601; ein Parsen hier böte nur eine weitere Fehlerquelle für einen Wert,
    /// den der Server ausschließlich anzeigt, nie selbst auswertet oder danach sortiert.
    /// </summary>
    public required string FinishedAt { get; set; }

    /// <summary>Länge von <c>entries</c>, für die Katalog-Übersicht ohne Dokument-Zugriff.</summary>
    public int EntryCount { get; set; }

    /// <summary>Gespiegeltes <c>fallbackStartStepIndex</c>, falls im Dokument vorhanden.</summary>
    public int? FallbackStartStepIndex { get; set; }

    /// <summary>Der Profil-Körper, unverändert wie empfangen — für ein byteidentisches GET.</summary>
    public required string DocumentJson { get; set; }

    /// <summary>Zeitpunkt der letzten Ablage, SQLite-sortierbar.</summary>
    public long StoredAtUnixTimeMilliseconds { get; set; }

    /// <summary>Zeitpunkt der letzten Ablage.</summary>
    [NotMapped]
    public DateTimeOffset StoredAt
    {
        get => DateTimeOffset.FromUnixTimeMilliseconds(StoredAtUnixTimeMilliseconds);
        set => StoredAtUnixTimeMilliseconds = value.ToUnixTimeMilliseconds();
    }
}
