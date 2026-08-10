using System.ComponentModel.DataAnnotations.Schema;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>
/// Ein benanntes Preset des manuellen Regler-Panels (ADR-026-Nachtrag "Eigene
/// Erlebnisse"), serverseitig gespiegelt.
///
/// ## Warum je Jellyfin-Nutzer
/// Anders als das Sprachpaket (<see cref="VoiceRecording"/>, serverweit) ist
/// ein Regler-Preset persönlich — die Werte eines anderen Nutzers hier zu
/// sehen wäre keine Bequemlichkeit, sondern ein Datenleck. Deshalb der erste
/// Fremdschlüssel-artige Spalte dieses Plugins: <see cref="UserId"/> ist Teil
/// des Primärschlüssels, nicht nur ein Filter.
///
/// ## Warum die Kennung vom Client kommt
/// Der lokale Vorrat (`manual-presets-store.ts`) vergibt die Kennung schon
/// beim Anlegen, damit ein Preset über Last-Write-Wins-Merge hinweg sein
/// Gegenstück wiederfindet. Der Server übernimmt diese Kennung unverändert,
/// statt eine eigene zu vergeben — sonst bräuchte jede Sync-Runde eine
/// Übersetzungstabelle.
///
/// ## Warum die Werte als JSON-Zeichenkette statt relational
/// Der Reglersatz wächst mit der Zeit (aktuell zweiundzwanzig plus Saumfarbe).
/// Eine relationale Abbildung bräuchte bei jedem neuen Regler eine Migration;
/// eine JSON-Spalte nicht.
/// </summary>
public sealed class ManualPreset
{
    /// <summary>Jellyfin-Nutzerkennung, aus dem Auth-Claim gelesen, nie vom Client vorgegeben.</summary>
    public required Guid UserId { get; set; }

    /// <summary>Vom Client vergebene Kennung (i.d.R. eine UUID), Teil des Primärschlüssels je Nutzer.</summary>
    public required string Id { get; set; }

    /// <summary>Anzeigename des Presets.</summary>
    public required string Name { get; set; }

    /// <summary>Der Schnappschuss (`{ knobs, rimColor }`) als kompaktes JSON, ungeprüft weitergereicht.</summary>
    public required string SnapshotJson { get; set; }

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
