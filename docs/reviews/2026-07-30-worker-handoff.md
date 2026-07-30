# Übergabe an den Worker

## Branch

- Repository: `Bugstehude/aether-jellyfin-analysis-plugin`
- Basis: `a12f0d3a6b5e2cd6022faa3bbd87797df37652cf`
- Review-Branch: `agent/gesamt-review`
- Keine Merge- oder Deployment-Aktion ist Teil des Reviews.

## So bitte prüfen

1. Zuerst `docs/reviews/2026-07-30-gesamt-review.md` lesen.
2. Die Commits ab `origin/main` einzeln prüfen; sie sind als selektiv übernehmbare Einheiten
   geschnitten.
3. Besonders die Semantik der drei Änderungen bestätigen:
   - Audio-Endpunkte akzeptieren fehlenden Content-Type weiterhin als Legacy-`audio/mpeg`, lehnen
     aber explizite Nicht-Audio-Typen mit 415 ab.
   - Ein bereits aktuelles serverproduziertes Ergebnis wird nach Eintritt in die Worker-Sperre
     übersprungen; clientproduzierte Ergebnisse bleiben gemäß Upgrade-Regel ersetzbar.
   - Shutdown storniert aktive Worker, entsorgt die beiden Runner-Semaphoren aber nicht während
     ihrer möglichen Nutzung.
4. Vollständige CI ausführen: Restore im Lock-Modus, Build, Format, Vertrags-Hash, 90 Tests,
   EF-Snapshot, NuGet-Audit, Packaging und Jellyfin-Smoke-Test.
5. Im erzeugten ZIP exakt DLL und `aether-analysis-worker.cjs` erwarten; im SBOM den SHA-256-
   Eintrag des Workers und im Manifest den versionierten Zeitstempel prüfen.
6. Nicht ungeprüft als „produktionsreif“ markieren. Langfilm-Speicherbedarf und
   Ziel-LXC-Abnahme sind offen.

## Anschluss an den AETHER-Review

Im AETHER-Repository sind zwei konkrete Aufgaben zu verfolgen:

- Audioanalyse streamingfähig machen, damit nicht der vollständige dekodierte Audiostream im
  Arbeitsspeicher gesammelt wird.
- Worker-Build reproduzierbar mit Quell-Commit/Toolversion auszeichnen und den erzeugten Hash
  automatisiert gegen die vendorte Plugin-Datei prüfen.

Erst nach diesen Änderungen den Worker neu vendorisieren; dabei Algorithmusversion nur erhöhen,
wenn sich die erzeugten Analysewerte und nicht lediglich Speicher- oder Buildverhalten ändern.
