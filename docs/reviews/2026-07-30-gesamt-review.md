# Gesamt-Review des AETHER-Jellyfin-Plugins

## Gegenstand und Urteil

Geprüft wurde `main` auf Basis von `a12f0d3a6b5e2cd6022faa3bbd87797df37652cf`
(`v0.2.4.0`). `main` blieb unverändert; alle Korrekturen liegen einzeln auf
`agent/gesamt-review`.

Die Grundarchitektur ist für die Größe des Plugins angemessen: ein klar versionierter Vertrag,
eine plugin-eigene Datenbank, ein begrenzter Speicherpfad und ein gemeinsam genutzter
Analysealgorithmus. Der Stand war jedoch noch nicht releasefest. Besonders die kurz zuvor
ergänzten Audio- und Server-Worker-Pfade umgingen an mehreren Stellen bereits vorhandene
Sicherheits- oder Serialisierungsregeln. Die direkt behebbaren Fälle sind im Review-Branch
korrigiert.

Eine Übernahme sollte commitweise erfolgen. Vor einer produktiven Freigabe bleiben der
Langfilm-Speicherbedarf des Workers und der reale LXC-Upgrade-/Uninstall-Test offen.

## Behobene Befunde

### Hohe Priorität

1. **Paket und Assembly hatten verschiedene Versionen.** `build.yaml` und Tag nannten
   `0.2.4.0`, die gebaute Assembly weiterhin `0.2.3.0`. Die Version ist korrigiert; CI und
   Packaging brechen bei künftiger Abweichung ab.
2. **Gespeicherter aktiver Inhalt war möglich.** Ein Analyzer durfte etwa `text/html` als
   Sprach- oder Reiseaufnahme speichern; der GET-Endpunkt lieferte diesen Typ unter der
   Jellyfin-Herkunft wieder aus. PUT akzeptiert nun nur Audio-Medientypen, GET setzt
   `X-Content-Type-Options: nosniff`, und OpenAPI sowie Tests bilden die 415-Antwort ab.
3. **Parallele Audio-Schreibvorgänge waren nicht atomar.** Die Kapazitätsrechnung des
   Sprachpakets bestand aus getrennten Lese- und Schreiboperationen; Reise-Uploads teilten sich
   dieselbe `.part`-Datei. PUT und DELETE laufen nun über die vorhandene gemeinsame
   Schreibsperre.
4. **Der Server-Worker umging die gemeinsame Kapazitätssperre.** HTTP-Upload und Bereinigung
   waren serialisiert, `ServerAnalysisRunner.StoreBoundedAsync` nicht. Der kurze Store-Abschnitt
   des Workers nutzt jetzt denselben Coordinator.
5. **Shutdown konnte aktive Analysen nicht zuverlässig beenden.** Der Runner entsorgte seine
   SemaphoreSlim-Objekte, während aktive `finally`-Blöcke sie noch freigaben, und der losgelöste
   Post-Scan-Lauf erhielt kein Host-Lebenszeitsignal. Eine eigene Lebenszeit-Cancellation beendet
   Workerprozesse; die Sperren bleiben bis zum natürlichen Ende der aktiven Tasks nutzbar.
6. **Korrupte Datenbankmetadaten ermöglichten eine ungebundene Allokation.**
   `UncompressedBytes` wurde vor jeder Integritätsprüfung direkt als Arraygröße verwendet. Die
   Dekomprimierung lehnt negative Werte und Dokumente über 64 MiB vor der Allokation ab.
7. **Zwei Auslöser konnten denselben Film nacheinander vollständig analysieren.** Die
   Aktualitätsprüfung lag vor der Worker-Sperre. Der Bedarf wird nun nach Eintritt in die Sperre
   erneut geprüft.

### Mittlere Priorität

8. **Fehlerhafte Batch-Elemente konnten 500er auslösen.** `null`-Elemente und ungültige,
   überlange Identitäten wurden nicht geprüft; gleichzeitig meldete ein fehlender Body
   irreführend 413. Ungültige Form ergibt jetzt 400, nur eine echte Überschreitung 413.
9. **Der SBOM verschwieg den ausführbaren Node-Worker.** Der Worker ist nun als erforderliche
   Datei mit SHA-256 enthalten; die Archivbeschreibung nennt beide Artefakte.
10. **Der Smoke-Test installierte nicht das geprüfte Archiv.** Er extrahierte ausschließlich die
    DLL und ließ den Worker weg. Er erlaubt jetzt exakt die zwei erwarteten Dateien und entpackt
    das vollständige ZIP.
11. **Release-Metadaten und reproduzierbares ZIP waren nicht belastbar.** Der Repository-
    Zeitstempel war auf den 18. Juli fest verdrahtet. Er liegt jetzt versioniert in `build.yaml`.
    Außerdem erzeugte `zip` je nach lokaler Zeitzone andere DOS-Zeitstempel; Packaging läuft nun
    mit UTC. Gegenprobe Honolulu/Tokio: identische SHA-256-Prüfsumme.
12. **Normative Dokumente beschrieben noch die 0.1-Architektur.** ADR 0004 behauptete weiterhin,
    das Plugin starte nie Analysen; Betriebs- und Sicherheitsdokumente verschwiegen Audio- und
    Workerdateien. ADR 0005 und die betroffenen Dokumente bilden den gelieferten Stand ab.

## Offen und nicht direkt im Plugin zu lösen

### P1 — vor produktiver Freigabe

- **Worker-Speicher wächst bei Langfilmen linear.** Der vendorte Worker sammelt den vollständig
  dekodierten Mono-Audiostream zunächst mit `Buffer.concat` im Speicher. Bei 22.050 Hz Float32
  sind das rund 317 MiB pro Stunde Audio, bevor weitere Analyseobjekte hinzukommen. Das sollte im
  AETHER-Quellrepository auf eine streamingfähige Berechnung umgestellt, dort getestet und
  anschließend neu vendort werden.
- **Worker-Provenienz ist nicht reproduzierbar belegt.** Der Dateihash ist jetzt inventarisiert,
  aber das Plugin notiert weder AETHER-Commit noch Buildwerkzeug-Version. Beim AETHER-Review ist
  ein reproduzierbarer Build mit Quell-Commit und Gegenprüfung gegen den vendorten Hash
  einzuführen.
- **Zielsystem-Abnahme fehlt.** Upgrade, Uninstall und Backup/Restore auf dem tatsächlichen
  Jellyfin-10.11.11-LXC bleiben der letzte P0-Betriebsgate des Repositories.

### P2 — bewusst nicht als Sofortkorrektur

- Ungültige gespeicherte Bibliotheks-IDs werden verworfen; bleibt keine gültige ID, bedeutet das
  „alle Bibliotheken“. Für einen Mehrbibliotheksserver sollte ein ungültiger expliziter Scope
  sichtbar warnen statt still zu erweitern.
- Sprachpaket und Reiseaufnahme sind serverweit für jeden authentifizierten Jellyfin-Nutzer
  lesbar. Das passt zur derzeit dokumentierten Ein-Nutzer-/LAN-Annahme, braucht bei mehreren
  Nutzern aber eine bewusste Datenschutzentscheidung.
- Reise-Audio und sein Content-Type liegen in zwei Dateien. Ein Absturz genau zwischen beiden
  Umbenennungen kann neue Audiodaten mit dem alten, weiterhin sicheren Audio-Typ ausliefern.
- Lizenz-Gate, generierter TypeScript-Client und reale Mehrquellen-/Dateiersetzungs-
  Integrationstests bleiben wie im Production-Readiness-Plan offen.

## Verifikation

- Build: erfolgreich, Warnungen als Fehler.
- Formatprüfung: erfolgreich.
- Tests: **90/90 erfolgreich** nach der letzten Codeänderung.
- Vertrags-Hash: `3992073cd9f431db0acdbc7bf589536c9d730a0c705b1c6002898f91536aa3fd`.
- Release-Metadatenprüfung: `0.2.4.0`, Paket und Assembly konsistent.
- Packaging: erfolgreich; ZIP enthält ausschließlich DLL und Worker.
- Reproduzierbarkeit: identisches ZIP unter `Pacific/Honolulu` und `Asia/Tokyo`.
- Manifest und CycloneDX-SBOM: gültiges JSON; Worker-Hash vorhanden.
- Secret-Suche: keine Zugangsdaten im verwalteten Bestand gefunden.

Nicht lokal wiederholt werden konnten der Docker-Smoke-Test (kein laufender Docker-Daemon) und
die aktuelle NuGet-Onlinedatenbank-Abfrage (Netz-/Ausführungsgrenze der Review-Umgebung). Restore
mit Lockfile war erfolgreich; der Draft-PR muss deshalb die vorhandenen CI-Gates einschließlich
NuGet-Audit und Jellyfin-Smoke-Test vollständig grün zeigen.

## Übernahmereihenfolge

Die Commits sind absichtlich klein und thematisch getrennt. Empfohlen ist die Reihenfolge des
Branches. Besonders zusammengehören:

1. Version + Metadaten + Packaging + vollständiger Smoke-Test,
2. Audio-MIME-Härtung + gemeinsame Schreibsperre,
3. Dekompressionsgrenze,
4. Worker-Shutdown + Kapazitätssperre + Deduplizierung,
5. SBOM und Dokumentationskorrektur.

