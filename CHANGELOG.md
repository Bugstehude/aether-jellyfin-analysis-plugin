# Changelog

All notable changes to implementation and canonical contracts are recorded here.

## [0.2.2.4] — Drei implementierte Endpunkte standen nicht im Vertrag

### Fixed

- Die OpenAPI kannte sieben Pfade, der Controller implementiert zehn. `POST …/analyze`
  und `GET …/analyze/status` **werden vom AETHER-Client aktiv genutzt**, `GET /activity`
  von der Einstellungsseite des Plugins. Der Vertrags-Hash war grün, weil die drei
  fehlten — nicht weil sie stimmten. Ein Vertrag, der schweigt, prüft nichts. Alle drei
  sind jetzt beschrieben, samt der 403/429-Antworten, die sie tatsächlich geben; der
  Hash ist neu gesetzt.
- `GET /activity` ist dabei ausdrücklich als **nicht** Teil des versionierten
  Client-Vertrags gekennzeichnet: rein diagnostisch, nur für Administratoren, im
  Speicher, beim Neustart weg. Kein Client darf sich auf seine Form verlassen.

## [0.2.2.3] — Die Begrenzung hat nicht begrenzt, sondern verschluckt

### Fixed

- Die in 0.2.2.2 eingeführte Warteschlangen-Begrenzung nutzte
  `BoundedChannelFullMode.DropWrite`. Das verwirft den Auftrag **still und meldet
  trotzdem Erfolg** — der Ablehnungspfad lief also nie an: Statt der gewollten
  429-Antwort bekam der Aufrufer „queued", das Item blieb dauerhaft in diesem
  Zustand stehen, und ein erneuter Versuch kam wegen der Dedupe-Regel nie mehr
  durch. Jetzt `Wait`, wo `TryWrite` bei vollem Kanal sofort fehlschlägt.
  **Gefunden vom allerersten Test, der je für diese Klasse geschrieben wurde.**

### Added

- **48 neue Tests** (20 → 68). Damit ist der P0-Blocker „Controller-level authorization, CORS, precondition and
  malformed-payload integration tests“ aus `docs/production-readiness.md`
  geschlossen. Der Schwerpunkt liegt dort, wo fremde Daten den
  Server betreten und bisher nichts geprüft wurde:
  - `AnalysisDocumentValidator`: jede Vertragsgrenze einzeln — Schema-Version,
    Dauer, Zeitstempel aus der Zukunft, Abtastgrenzen, Produzent, Fingerabdruck-
    Format, aufsteigende Zeitstempel, Messwerte außerhalb 0..1, Größenlimit und
    kaputtes JSON. Inklusive der Gegenprobe, dass ein vertragskonformer Upload
    auch durchkommt — ein Validator, der alles ablehnt, wäre ebenso kaputt wie
    einer, der alles durchlässt, nur unauffälliger.
  - Master-Dokument: unbekannte Felder werden verworfen, alle Vertragsfelder
    bleiben erhalten, und die Identität (Item, Fingerabdruck, Algorithmus) kommt
    vom SERVER — ein Upload kann sich nicht einem fremden Medium unterschieben.
  - Warteschlange: Annahme, Dedupe, Ablehnung bei vollem Kanal, und dass ein
    abgelehntes Item nicht als „queued" hängen bleibt.
  - **`AnalysisController`** (13 Endpunkte, vorher komplett ungetestet): beide
    Sicherheitszusagen des README stehen jetzt unter Test. „404 ohne
    Existenz-Leak" — ein Item, das der Nutzer nicht sehen darf, ergibt exakt
    dieselbe Antwort wie ein Item, das es nicht gibt; ein abweichender
    Statuscode verriete bereits dessen Existenz. „Schreibzugriffe nur für
    Berechtigte" — die Server-Analyse, der teuerste Endpunkt überhaupt, weist
    einen Nutzer ohne Upload-Recht ab, bevor irgendetwas geprüft oder gestartet
    wird. Dazu: ungültige Routen-Identität wird vor jedem Datenzugriff
    abgelehnt, und der einzige unauthentifizierte Pfad (CORS-Preflight) erteilt
    keiner fremden Herkunft eine Freigabe. Dazu die Preconditions: ein ETag
    gilt nur fuer die Detailstufe, fuer die es ausgestellt wurde, und eine
    gespeicherte Analyse wird nicht mehr ausgeliefert, wenn die Datei
    dahinter ersetzt wurde.

## [0.2.2.2] — Warteschlange begrenzt, ungeprüfte Felder nicht mehr gespeichert

### Fixed

- Die Analyse-Warteschlange war **unbegrenzt** (`Channel.CreateUnbounded`), und die
  Statustabelle wurde nie geleert. Wer Upload-Recht auf dem Server hat, konnte damit
  beliebig viele jeweils minuten- bis stundenlange ffmpeg-Läufe aufstauen und den Server
  über Stunden auslasten. Die Warteschlange fasst jetzt 64 Einträge; darüber hinaus
  antwortet `POST …/analyze` mit **429** und `Retry-After`, statt still anzunehmen und nie
  zu liefern. Abgeschlossene Statuseinträge werden nach einer Stunde verworfen.
- Unbekannte Top-Level-Felder eines Uploads wurden **ungeprüft** ins gespeicherte Dokument
  übernommen und später an jeden Leser des Items ausgeliefert — der Validator sieht sie
  nicht. Übernommen wird jetzt nur noch, was der Vertrag kennt. Abschnitt 11.3 des
  Konzepts deckt das ausdrücklich: Autoren dürfen sich nicht darauf verlassen, dass
  unbekannte Felder ein erneutes Speichern überleben. Der AETHER-Client sendet keine
  solchen Felder, für ihn ändert sich nichts.

### Offen (bewusst nicht geändert)

- Der Controller nutzt `[Authorize]` statt Jellyfins `DefaultAuthorization`-Policy. Deren
  Handler erzwingt zusätzlich die Nutzer-Policy (deaktiviertes Konto, Zugriffszeitplan,
  „Fernzugriff erlauben"). Der Policy-Name wird serverseitig aufgelöst und konnte hier
  nicht gegen 10.11.11 verifiziert werden — ein falscher Name ergibt zur Laufzeit 500er.
  Gehört gegen die laufende Instanz geprüft und dann gesetzt.

## [0.2.2.1] — Sichtbarer Fortschritt statt scheinbarem Stillstand

### Fixed

- Der Fortschritt zählte nur ABGESCHLOSSENE Items. Ein langes erstes Video ließ den
  Balken deshalb minutenlang auf 0 % stehen und der Lauf sah aus, als hinge er. Der
  Batch-Pfad reicht jetzt den Fortschritt INNERHALB des Items durch (er wurde bisher
  nur für den manuellen Knopf erfasst), und das laufende Item zählt anteilig in den
  Gesamtwert.
- Die Anzeige nannte gar keine Prozentzahl, nur „3/547" und einen Balken. Jetzt steht
  der Prozentwert ausgeschrieben, dazu der Fortschritt des laufenden Videos.

## [0.2.2.0] — Centre-weighted palette (algorithm `aether-visual/1.1.0`)

### Changed

- The vendored analysis worker now extracts dominant colours with the perception engine's
  `centreBias` (0.75), matching what the AETHER app already does. On real footage ~25% of a frame is
  near-black and that black sits at the **border** (37.7% of border pixels vs 1.0% of centre
  pixels), so the palette described the surroundings rather than the subject: the dominant colour
  measured as `rgb(7, 3, 6)` (luma 0.018) and now measures `rgb(74, 38, 38)` (luma 0.191). Server
  analyses stayed on the old, duller palette and disagreed with locally analysed ones; they now
  agree again.
- The analysis algorithm version is bumped to **`1.1.0`** (`AetherAlgorithm.Version`, the
  `capabilities` endpoint and the documented canonical key). The document schema is unchanged —
  only the stored values differ — but the version is part of the storage key, so old analyses are
  never served under the new key.

### Operational consequence — a full re-analysis

- **Every stored analysis is invalidated.** Nothing is served from the cache under
  `aether-visual/1.1.0` until an item has been re-analyzed, and the scheduled task / after-scan
  hook will walk the entire library again. On a weak server this is **hours** of background work
  and elevated CPU/IO for the whole run. The old `1.0.0` records are not deleted by this upgrade;
  they age out via the normal retention/storage bound, so peak storage is temporarily higher.
- Clients must request `aether-visual@1.1.0`; a client still pinned to `1.0.0` will get cache
  misses because `capabilities` no longer advertises `1.0.0`.

## [0.2.1.5] — Stop double-analyzing every item

### Fixed

- The scheduled task and the after-scan hook both call `AnalyzePendingAsync`, but only the
  per-source worker was serialized — not whole runs. When two runs overlapped (e.g. a library scan
  finishing during the scheduled task), each iterated the full library and analyzed **and stored
  every item twice**, doubling an already multi-hour run on a weak server. Whole runs are now
  serialized: one runs while at most one follow-up waits, and any further concurrent trigger is
  skipped (the waiting run re-scans everything, so nothing is missed). The waiting run re-selects
  items after the active one finishes, so already-current items cost only a metadata lookup.

## [0.2.1.4] — Pick libraries from a list

### Changed

- The "Libraries to analyze" setting is now a checkbox list of the server's actual libraries
  (loaded via `getVirtualFolders`) instead of a free-text field expecting collection-folder GUIDs.
  Previously a mistyped or name-based entry failed `Guid.TryParse`, was silently discarded, and the
  run fell back to **all** libraries — so a scope you thought you set was ignored. The stored value
  is unchanged (an array of collection-folder ids); stored ids with no matching library are shown as
  checked "(removed library …)" rows so a lookup blip never drops a selection.
- The background-analysis start log now names the libraries in scope
  (`… (libraries: Movies, Shows)` or `libraries: all`).

## [0.2.1.3] — Settings page actually runs its script

### Fixed

- The configuration page never showed saved values and could not persist changes, because its
  `<script>` was placed **after** the `.pluginConfigurationPage` element instead of inside it.
  Jellyfin's view loader injects only that element's content and executes the scripts within it,
  so the page's script was silently dropped — neither the load-on-show nor the save handler ever
  ran (which is why the two prior camelCase/`pageshow` fixes had no effect: they changed a script
  that never executed). The script now lives inside the page element, and the element declares
  `data-require` for its emby inputs like the official plugin template. This also lets the live
  activity panel added in 0.2.1.2 run.

## [0.2.1.2] — Visible background analysis progress

### Added

- A **live activity panel** on the plugin settings page. It polls a new administrator-only
  `GET /AetherAnalysis/v1/activity` endpoint (not part of the versioned client contract, like the
  existing `analyze` endpoints) and shows what server-side analysis is doing right now: current
  run source and progress bar, the item being analyzed, a running stored/skipped/failed tally, the
  last (or cancelled) run summary, and the ten most recently analyzed items. Backed by a new
  in-memory `ServerAnalysisActivity` singleton fed by the runner; purely diagnostic, reset on
  restart, never persisted.

### Changed

- The background analysis path (`AETHER: Analyze library` scheduled task and the after-scan hook)
  now logs its progress. Previously only the manual "Server-Analyse" button path logged a result,
  so a multi-hour background run was almost silent in the server log. It now logs a start line with
  the item count, one `AETHER [i/N] analyzing <name>` line per item that is actually analyzed plus
  its per-item outcome (stored / skipped / failed), and a final summary — also emitted when the run
  is cancelled — with elapsed time and stored/skipped/failed counts. No contract change.

## [0.2.1.1] — Settings page shows saved values

### Fixed

- The configuration page left all fields empty even though values were saved. Jellyfin's
  `GET /Plugins/{id}/Configuration` returns the config with camelCase keys while the page read
  PascalCase; it now reads case-insensitively and writes a single-cased payload on save.

## [0.2.1.0] — Upgrade browser analyses to server

### Changed

- The scheduled task and the after-scan hook now also **replace** stored analyses that were
  **not produced by the server** (a browser precompute is visual-only) with the richer server
  analysis (visual + audio). Staleness therefore triggers on the media fingerprint **or** a
  non-server producer. Once every stored analysis is server-produced this is a no-op. The manual
  "Server-Analyse" button already always replaced.

## [0.2.0.0] — In-plugin server-side analysis

### Added

- In-plugin server-side analysis: the plugin runs the shared AETHER perception-engine (visual +
  audio) via a bundled Node worker (`aether-analysis-worker.cjs`) using Jellyfin's own ffmpeg, and
  stores results directly through the repository (no HTTP/auth round-trip), under the canonical
  `aether-visual`/`1.0.0` key. Requires Node (18+) on the Jellyfin server.
- Three triggers over one serial runner: an `AETHER: Analyze library` scheduled task (daily default
  trigger, runnable from the dashboard), an after-scan hook for new/changed items, and
  `POST …/analyze` + `GET …/analyze/status` endpoints for the AETHER "Server-Analyse" button.
- Staleness replacement keyed on the algorithm version and the media fingerprint.
- Settings: enable server-side analysis, auto-analyze after scan, Node path, sampling fps and frame
  width, per-item worker timeout, and an optional library allow-list.
- Vendored worker bundle shipped in the install archive next to the DLL; `tools/vendor-worker.sh`
  refreshes it from the AETHER monorepo.

### Fixed

- Settings page now loads the current saved values on open (carried from 0.1.0.1).

## [Unreleased]

### Added

- Canonical schema version 2 analysis contract.
- Jellyfin 10.11.11 and EF Core 9.0.11 compatibility pin.
- Initial plugin, storage and API skeleton.
- Multi-resolution `compact`, `balanced` and `full` analysis delivery with representation ETags.
- Upgrade-safe EF migration baseline with development-database adoption.
- Scheduled, upload-time and administrator-triggered retention/LRU cleanup.
- Persisted maintenance status and damped last-access writes.
- Hard request-size and defensive malformed-payload validation.
- Deterministic install archive, checksum and Jellyfin 10.11.11 container load gate.
- Normative client workflow for checkbox selection and client-owned analysis jobs.
- Atomic bounded upload transactions and batched retention/LRU cleanup.
- Metadata-only batch status, HEAD and conditional 304 paths.
- Process-local corruption/touch-failure telemetry in administrator status.
- Patched native SQLite test runtime with an explicit Jellyfin-owned production runtime boundary.
- Contract synchronization hash, CycloneDX SBOM and backup/rollback operations guide.
- Authenticated, digest-pinned Jellyfin start/restart smoke harness.
- Reproducible Jellyfin repository manifest with ABI, release URL and package checksum validation.
- Jellyfin-safe deferred plugin data/configuration access during service registration.
