# Übergabe des Versionsvertrags an Aether und AetherTV

Stand: 3. Oktober 2026. Die Pluginseite ist im lokalen Arbeitsstand umgesetzt. Ausgangscommit ist
`ca7cfa23130cba0868346297a0de00a4172cd836`. Die Änderungen sind noch nicht veröffentlicht. Dieser
Ausgangscommit enthält den neuen Vertrag noch nicht und darf nicht als dessen Zielstand gepinnt
werden. Nach Übernahme ist der tatsächliche Änderungscommit in den Clients zu pinnen.

**Analysen aus `1.0.0` und `1.1.0` sind ausdrücklich in beiden Leserichtungen kompatibel. Die
tatsächliche gespeicherte Version bleibt sichtbar. Routineläufe aktualisieren ältere
Messversionen, während Clients die Altanalyse weiter verwenden können. Schema v2 bleibt
bestehen. Die neue Messrevision `1.2.0` ist geplant, aber noch nicht implementiert oder
freigegeben.**

## Verbindliche Dateien

- [Versionsvertrag](../analysis-version-contract.md)
- [Clientworkflow](../client-integration-contract.md)
- [OpenAPI](../../contracts/openapi/aether-analysis-v1.yaml)
- [Vertragshash](../../contracts/contract.sha256), aktuell
  `d07b7140ec4985a88f286d44986d914175bff89c2bd36bdc899e0664faf184af`
- [Algorithmusmatrix als Fixture](../../contracts/examples/valid/algorithm-read-compatibility-v1.json)
- [Kompatible Abfrage eines älteren Lesers](../../contracts/examples/valid/compatible-reader-query-v1.json)
- [Antwort mit tatsächlicher neuerer Messversion](../../contracts/examples/valid/compatible-reader-status-v1.json)

Die Übergabe ändert keine Dateien in den Clientrepositories und ersetzt nicht deren Integration.
Cross-Repo-Abstimmung und Rollout laufen gemäß dem Aether-Arbeitsvertrag über den Orchestrator.
Es wurde keine externe Nachricht gesendet und kein Push, Merge oder Deploy ausgelöst.

## Gemeinsamer Clientauftrag

1. Bei `/capabilities` die optionalen Felder `preferredVersion`, `compatibleReadVersions` und
   `readCompatibility` innerhalb des passenden `supportedAlgorithms`-Eintrags dekodieren.
   Matrixeinträge enthalten `readerVersion` und geordnete `analysisVersions`. Fehlen die neuen
   Felder bei einem älteren Plugin, beim bisherigen exakten Ablauf bleiben.
2. Der Client führt weiterhin seine eigene bekannte Leser-/Produzentenidentität. Zum Lesen
   `POST /analyses/query` mit `algorithm: { id, version: readerVersion }` und
   `allowCompatible: true` verwenden, wenn die Matrix diesen Leser unterstützt. Grenzen für
   Stapelgröße beachten. Normale Wiedergabe braucht nur eine Item-/Source-Auswahl.
3. Bei `available` die additive `algorithm`-Identität der Antwort übernehmen. GET und HEAD über
   genau diese Versionsroute ausführen. Ein `1.0.0`-Leser kann also `/.../1.1.0` lesen. Das
   Dokument muss zur ausgewählten Identität passen, nicht zwangsläufig zur angefragten
   Leserversion. Server-, Nutzer-, Item-, Source- und Detailprüfungen bleiben erhalten.
4. Cache und ETags nach der tatsächlichen Messversion führen. Den Master-ETag aus der Abfrage
   nicht als GET-/HEAD-Repräsentations-ETag verwenden. Nach einer Routineaktualisierung erneut
   auswählen, damit eine neue kompatible Messversion entdeckt werden kann. Ein Leser, der
   ausdrücklich `1.0.0` bevorzugt und diese noch findet, darf gemäß Matrix bei ihr bleiben.
5. Optionale neue Felder ignorieren, solange der Client sie nicht verarbeitet. Fehlende
   optionale Signale erlauben einen konservativen Rückfall. `detail=compact` ist eine
   zeitliche Reduktion und keine automatische Projektion auf einen alten Algorithmus.
6. Lesekompatibilität nicht als Schreibkompatibilität verwenden. Lokal erzeugte Analysen unter
   der tatsächlich ausgeführten Algorithmusversion hochladen. Altanalysen nicht unter einer
   neuen Version speichern, nur um einen Treffer im Cache zu erzwingen.
7. „Neu berechnen“ an `POST .../analyze?recalculate=true` anbinden. Das betrifft nur die
   konkrete Media-Source im Pfad. Ohne diesen Parameter überspringt der manuelle Job eine
   brauchbare kompatible Analyse. Status ist ebenfalls pro Item und Media-Source. Ein
   laufender Auftrag wird bei erneutem Aufruf dedupliziert und behält seine ursprünglichen
   Optionen. Die Routineaktualisierung läuft unabhängig davon im Plugin.

`available`, `missing` und `stale` bleiben die vorhandenen Statuswerte. Es gibt keinen neuen
Status wie `available-compatible`. `allowCompatible` ist standardmäßig `false` und wird beim
Stapel-Löschen mit 400 abgelehnt. Exakte GET-, HEAD-, PUT- und DELETE-Routen ändern ihre
Bedeutung nicht. Wiedergabe muss auch bei einem fehlenden oder defekten Master weiterlaufen.

## Aether Web/Desktop

Die gelesenen Integrationsstellen sind
`apps/desktop-web/src/jellyfin/plugin-analysis-client.ts` und der Vertragstest
`apps/desktop-web/src/jellyfin/plugin-contract.test.ts`. Der Client verwendet bisher eine feste
Versionsroute und vergleicht `data.algorithm.version` mit dieser Anfrage. Auswahl und Abruf
des tatsächlich gelieferten Algorithmus müssen daher gemeinsam angepasst werden. Die
Identitätsprüfung soll bestehen bleiben und auf die ausgehandelte Auswahl zeigen.

Die nachgelagerten Guards, Codecs und Replay-/Planungszugriffe in `@aether/analysis-cache` sind
gegen die unveränderten schema-v2-Signale beider Versionen zu prüfen. UI und lokale Analyse
dürfen die kompatible Leseversion nicht mit der Produktionsversion verwechseln. Die neue
OpenAPI sowie die Fixtures und den Vertragshash aus diesem Repository synchronisieren.

## AetherTV

Die gelesenen Integrationsstellen sind
`Sources/AetherJellyfin/JellyfinSession.swift`,
`Sources/AetherJellyfin/JellyfinRequests.swift`,
`Sources/AetherSupply/AnalysisSupply.swift` und
`Sources/AetherAnalysis/AnalysisDocumentDecoder.swift`.

`PluginAlgorithm.current` bleibt zunächst `1.1.0`. Die neue Fähigkeits- und Statusauswahl
liefert daneben eine tatsächliche gespeicherte Algorithmusidentität. Der Cachekey in
`AnalysisSupply` enthält bereits Algorithmus und Version. Er muss künftig die ausgewählte
Identität erhalten. Der Decoder bleibt bei Schema v2 und ignoriert zusätzliche optionale
Felder. Bei einem älteren Plugin bleibt die exakte Route als Rückfall bestehen.

## Gemeinsame Abnahme für beide Clients

| Bestand | Leser | Erwartung mit kompatibler Auswahl |
| --- | --- | --- |
| Nur `1.0.0`, gültiger Fingerprint | `1.1.0` | Voranalyse aus `1.0.0`, GET und Cache unter `1.0.0` |
| Nur `1.1.0`, gültiger Fingerprint | `1.0.0` | Bekannte Signale aus `1.1.0`, GET und Cache unter `1.1.0` |
| Beide gültig | Jeweilige Leserversion | Eigene Version bevorzugt, wie in der Matrix geordnet |
| Angefragte Version stale, andere kompatible Version gültig | Beide | Gültige andere Version verwenden |
| Beide stale oder nicht vorhanden | Beide | Sicherer Wiedergabefallback, kein Endlosretry |
| Unbekannte Messversion | Beide | Keine Kompatibilität aus SemVer erraten |
| Alte Pluginversion ohne neue Felder | Beide | Bisherige exakte Auswahl weiter verwendbar |
| Neue optionale Signale fehlen | Beide | Bisherige Signale verwenden, keine erzwungene Neuanalyse |
| Routineupdate während Wiedergabe | Beide | Altanalyse bleibt bis zum erfolgreichen Speichern nutzbar |
| Gezielte Neuberechnung | Beide | Nur die angefragte Media-Source und deren Status verfolgen |

Zusätzlich die Antwortidentity, Stapellimits, Zugangskontrollen, GET-ETags und Cachetrennung
zwischen Servern und Nutzern prüfen. Eine Schema-v2-Datei mit unbekanntem Zusatzfeld und eine
ohne optionale Audiofelder gehören in die Clientfixtures.

## Folgeauftrag für den geteilten Worker, geplante Revision 1.2.0

Fortschreibung vom 04.10.: Nach beiden Rückgaben die
[aktuellen Entscheidungen](2026-10-04-analysis-1.2-consolidated-decisions.md) und die neuen
[Aether-Speicheraufträge](2026-10-04-analysis-1.2-aether-storage-worker-prompt.md) beziehungsweise
[TV-Packed-Aufträge](2026-10-04-analysis-1.2-tv-packed-worker-prompt.md) verwenden.
Die unten verlinkten Prompts vom 03.10. beschreiben den vorherigen Abstimmungsschritt.

Die Desktop- und TV-Rückmeldungen sind im [konsolidierten Pluginentwurf](2026-10-03-analysis-1.2-contract-draft.md)
zusammengeführt. Für den nächsten Schritt die getrennten Prompts für
[Aether](2026-10-03-analysis-1.2-aether-worker-prompt.md) und
[AetherTV](2026-10-03-analysis-1.2-tv-worker-prompt.md) verwenden. Der Entwurf aktiviert keine neue Version.

Zeitbasis, Kandidatentimestamps und Ereigniserhalt korrigieren. Audio unabhängig vom
Bildraster messen und tatsächlich in Replay und Planung nutzen. Bänder, Spuridentität und
Audiozustände ausdrücklich definieren. Das ist ein eigener fachlicher Ausbau gemäß
[Migrationsreview](2026-10-03-plugin-analysis-migration-review.md).

Vor Freigabe von `1.2.0` die ergänzten Signale, Normalisierung und Zeitsemantik mit beiden
Clients abstimmen. Nur belegte Leserichtungen in die Matrix aufnehmen. Einen Leser mit
weniger Funktionen dürfen neue optionale Felder nicht vom bestehenden Signalumfang
ausschließen. Das gilt nicht automatisch für inkompatible Änderungen bekannter Signale.
Audio- und Videoteilläufe zur Wiederverwendung vorhandener Daten sind bisher nicht vorhanden.

## Prüfung der Pluginseite

Der lokale Release-Build ist erfolgreich. Alle **160 Tests** bestehen, einschließlich der
Vertragsfixtures, bidirektionalen Versionsauswahl, Routineaktualisierung, Dateiauswahl,
Abbruch-/Fehlerfälle, Quellschutz bei knapper Kapazität und paralleler Uploads. Formatprüfung,
Vertragshash, Release-Metadaten und EF-Modellprüfung bestehen. NuGet meldet für beide Projekte
keine bekannten anfälligen Pakete.

Das lokale Testpaket wurde gebaut. Es enthält ausschließlich Plugin-DLL und Workerbundle.
SHA-256, Manifest und SBOM wurden geprüft. Die Paketversion bleibt `0.3.1.0`, da kein Release
erstellt wurde. Dieses lokale Paket ist nicht der veröffentlichte Release dieser Versionsnummer.

Ein Docker-/Jellyfin-Smoke-Test wurde hier nicht ausgeführt, weil Docker nicht installiert ist.
Reale Clientabnahme und Jellyfin-Smoke bleiben vor einer Freigabe erforderlich. Die Clients
wurden für diese Übergabe gelesen, aber nicht geändert oder getestet.
