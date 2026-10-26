# Plugin-Gesamtreview: lokaler Wartungskandidat

Basis ist der frisch gelesene Remote-Main
`7b23e9ee4abe08bd6a8a160ef80a935ac51d8c75` auf
`review/gesamtfunktion-sicherheit-2026-10-26` im isolierten Worktree
`/tmp/aether-plugin-review-20261026`. Der ursprüngliche Checkout bleibt auf
`ca7cfa23130cba0868346297a0de00a4172cd836`. Keine fremden Änderungen wurden
übernommen oder überschrieben. Der Client und sein Contractpin bleiben unverändert.

Geprüft wurden insbesondere Authentifizierung/Itemzugriff, Uploadvalidierung,
Schreibgrenzen und Speicherung, Hintergrundjobs/Workerprozesse, Paket-/Runtimepins,
Vertragsidentität, Dashboard-Datenausgabe und dokumentierte Readiness-Grenzen.
Luna lieferte begrenzte Quellreviews für Presets, Geräteprofile, Prozessgrenzen und
Packaging; die Laufzeitbelege stammen vom Hauptworker. Das ist kein allgemeines
Produktions-, Hardware- oder Performancevotum.

## Korrigierte Befunde

| Befund | Vorheriger Beleg | Korrektur und positive Abgrenzung |
| --- | --- | --- |
| Null-Farben brechen den Analysevalidator ab | `palette: [null]` wirft bei Upload und StoredMaster-Prüfung jeweils `NullReferenceException`. | Null-Einträge werden regulär als ungültige Palette abgewiesen. Gültiger Upload und ein über `BuildMaster` gebauter Master bleiben akzeptiert. |
| Presetquote ist zwischen Prüfung und Schreiben umgehbar | Zwei vor ihren INSERTs angehaltene Anfragen lesen den Bestand 499; Originalcode speichert beide und endet bei 501. | Die bestehende gemeinsame Schreiblease umfasst Quote und Upsert sowie Delete. Bei überlappenden Anlagen wird eine gespeichert, eine mit dem schon vorhandenen `507` abgewiesen, Bestand 500. Updates bei voller Quote und Nutzertrennung bleiben erhalten. |
| Abbruch einer Reise-Spur ersetzt bereits alte Bytes | Abbruch am Ende des neuen Bodys: Aufruf wirft, aber die vorherigen 10 Byte sind schon durch 25 neue Byte ersetzt. | MIME wird zuerst in einer Nebendatei vorbereitet. Alle abbrechbaren Arbeiten und die letzte Cancellation-Prüfung liegen vor dem Austausch der bestehenden Dateien. Alte Bytes/MIME und das Aufräumen der Nebendateien sind geprüft. |
| Geräteprofilgröße wird erst nach Binding geprüft | Ein gültiger HTTP-Body mit mehr als 256 KiB führendem Leerraum wird mit `204` gespeichert. | Ein Resourcefilter prüft die vollständige Anfrage vor dem JSON-Binding. Content-Length und Chunked werden begrenzt; die bestehende `400`-Semantik bleibt. Gültige Anfragen mit genau 256 KiB bleiben akzeptiert. |
| Erst-Uploads desselben Geräteprofils kollidieren | Zwei tatsächlich überlappende INSERTs erzeugen mit Originalcontroller einen SQLite-UNIQUE-Fehler statt Last Write Wins. | Die bestehende Schreiblease serialisiert Upserts. Beide Anfragen ergeben `204`; ein vollständiges eingereichtes Dokument samt passender Zusammenfassung und genau eine Zeile bleiben gespeichert. |
| Worker im ZIP und in der SBOM können auseinanderlaufen | Ein veraltetes, neuer datiertes Publishasset bleibt durch `PreserveNewest` erhalten. Tatsächlicher ZIP-Workerhash unterscheidet sich von Quell- und SBOMhash. | Staging verwendet die kanonischen Vendordateien. `verify-package.py` prüft die genaue Archivdateiliste sowie alle drei Worker-/Manifesthashes gegen Quelle und SBOM. Die gleiche Stalecopy-Probe besteht, ein manipuliertes Archiv wird verworfen. |
| Nicht darstellbarer Prozesstimeout wird zu spät abgewiesen | Ein extrem großer Timeout wird vor Prozessstart nicht als ungültig erkannt. Der sichere Negativtest mit fehlendem Executable erhält `draft-process-start` statt `draft-process-invalid-request`. | Die `Task.Delay`-Obergrenze wird vor dem Start geprüft. Dieser Robustheitsfall ist kein belegter aktueller Produktaufruf: die bestehenden Job-/Probeaufrufer begrenzen bereits auf zwölf Stunden bzw. 30 Sekunden. |

Die Runtimepins, Dateiformate, Datenbankmodelle, Versionsmetadaten, Dependencies,
OpenAPI und JSONSchemas bleiben unverändert. Neue Ressourcen-/Berechtigungspolicy
wurde nicht eingeführt. Insbesondere bleibt der gewollte Geräteprofilzugriff für
alle authentifizierten Clients erhalten; es gibt keinen neuen Katalogdeckel oder
neuen `507`-Fall für Geräteprofile.

Die Versionsdokumentation wurde mit der aktuellen Implementierung abgeglichen:
Serverjobs produzieren 1.2; historische Planungs-/Gate-Notizen sind als Historie
gekennzeichnet. Bereinigung gelöschter Items ist implementiert, die separate
Bereinigung entfernter Media-Sources bei weiter existierendem Item bleibt offen.

## Prüfbelege

Baseline: Releasebuild ohne Warnungen/Fehler, **390 PASS / 2 optionale SKIP**.
Finaler Stand: **400 PASS / 2 optionale SKIP**, einschließlich positiver Upload-/
StoredMaster-Fälle, beider HTTP-Transportvarianten und echter überlappender
Datenbankoperationen. Die beiden Skips erfordern ausdrücklich ein bereitgestelltes
`AETHER_DRAFT_NATIVE_PROOF_MANIFEST`; das fehlt in diesem lokalen Standardlauf.

Fünf Originalcode-Gegenproben stellen jeweils eine Produktionsdatei aus der
festen Basis wieder her und setzen sie danach bytegenau zurück:

- Presets: die abschließende fachliche Probe zeigt zwei INSERTs und Bestand 501
  (`/tmp/aether-plugin-presets-original-verified.log`). Die frühere zusätzliche
  Schreiblease-Probe ist nicht der alleinige Quotenbeleg.
- Reise: alte 10 / tatsächlich neue 25 Byte trotz Abbruch.
- Timeout: Startversuch statt vorheriger stabiler InvalidRequest-Ablehnung.
- Palette: beide Prüfrichtungen werfen mit Originalcode.
- Geräteprofile: beide Größenproben speichern mit Originalcode; überlappende
  Erst-Uploads kollidieren am echten SQLite-Schlüssel.

Die Packaging-Negativprobe ist getrennt davon: Quell-/SBOMhash des Legacyworkers
`ba3dbd021c396ba88c70bc9bd445294ccfac7e839b8888210e43dcef2b1743e8`,
abweichender ZIP-Workerhash
`aea2460d644e2d418d31ab29eccfd4c517bbe8a73906029955dc6d5f26591848`.
Keine Vendordatei wurde hierfür verändert. Der wiederverwendete Publishcopy wurde
anschließend wiederhergestellt.

Weitere Gates:

- .NET SDK 10.0.400, `restore --locked-mode`, Releasebuild und Gesamtformat grün.
- NuGet-Audit beider Projekte einschließlich transitiver Abhängigkeiten meldet
  keine verwundbaren Pakete.
- EF meldet keine Modelländerung seit der letzten Migration.
- Release-Metadaten bleiben konsistent bei 0.4.3.0; keine neue Releaseversion vergeben.
- Vertragsidentität bleibt
  `bcbbb770ea9843f5348f66867c79c1ea86f84dba819d27f91b06591c2e215289`.
- Zwei lokale Paketbauten sind bytegleich; ZIP-SHA256
  `784d813d90b28645ad98d32a734acde24782b7581a6a809ca157575c221c7d12`.
- Alle Worker-/Manifesthashes im Archiv stimmen mit Quelle und SBOM überein;
  ein zusätzlich manipuliertes ZIP scheitert am neuen Prüfwerkzeug.
- Das finale ZIP bestand den vorhandenen isolierten Jellyfin-12.0-Docker-Smoke:
  authentifizierter Zugriff, Speicherinitialisierung und Neustart. Ausschließlich
  ein neues temporäres Testdatenverzeichnis und localhost wurden verwendet.

Logs/TRX: `/tmp/aether-plugin-baseline-{restore,build,tests}.log`,
`/tmp/aether-plugin-final-{build,tests,format-check,ef-model,smoke}.log`,
`/tmp/aether-plugin-final-results/final.trx`,
`/tmp/aether-plugin-mutation-{presets,journey,timeout,palette,profiles}.log`,
`/tmp/aether-plugin-profiles-original-verified.log`,
`/tmp/aether-plugin-package-stale-before.json` und die Paketlogs.
Archiv, SBOM, Manifest, Hashvergleich und manipulierte Negativdatei liegen unter
`/tmp/aether-plugin-final-artifacts`. Die Negativdatei ist kein installierbarer
Releasekandidat. Source und finale getestete DLL werden gegen das Smoke-Paket
zusätzlich abgeglichen.

## Verbleibende Grenzen und Folgeentscheidungen

- Zielsystem-Upgrade/Uninstall/Backup-Restore, reale Bibliotheken und Hardware,
  lange Videos unter Last sowie die zwei opt-in nativen Proof-Tests wurden hier
  nicht ersetzt. Die dokumentierten Readiness-Gates gelten weiter.
- Die Journey-Korrektur belegt Erhalt bei Cancellation vor dem Commit; es wird
  keine universelle Stromausfall-Atomizität zweier separater Dateiumbenennungen
  behauptet. Die serialisierten Schreibpfade folgen der vorhandenen globalen
  Schreiblease; deren Auswirkungen unter Last sind kein Performancevotum.
- Der Geräteprofilkatalog besitzt weiterhin keine gesonderte Gesamtquote oder
  Retention. Die bestehende 256-KiB-Anfragegrenze wurde repariert. Eine neue
  Katalogpolicy samt Clientfolgen erfordert eine getrennte Produktentscheidung;
  aus dem Quellbefund wird kein neuer Grenzwert erfunden.
- Ein gewöhnlicher Serverjob darf gültigen kompatiblen Legacybestand behalten.
  Der vorhandene Clientbutton heißt „Server“, mit Titel „Analyse auf dem Server
  berechnen lassen“, und sendet keinen erzwungenen Recalculate-Auftrag. Daraus
  wird keine eindeutige Zusage „vorhandene Analyse neu ersetzen“ abgeleitet und
  deshalb kein automatisches `recalculate=true` ergänzt. Eine explizite
  Neuberechnungsbedienung wäre gesondert zu beauftragen. Die aktuelle App nutzt
  bereits 1.2-Negotiation/SynchronizedClient; historische geschlossene Gates oder
  Legacy-DEFAULT 1.1 sind dafür kein Gegenbeleg.
- Der Client-Contractpin bleibt auf ca7cfa2. Dieser Plugin-Wartungskandidat ändert
  keine Wire-Verträge; er zieht den älteren Clientpin trotzdem nicht automatisch
  auf den semantisch neueren Plugin-Main nach.

Kein Push, CI-Dispatch, Release, Tag, Merge, Deployment oder produktiver
Jellyfinlauf wurde im Pluginauftrag durchgeführt. Der feste lokale Commit wird
mit diesen Belegen zur unabhängigen Prüfung übergeben.
