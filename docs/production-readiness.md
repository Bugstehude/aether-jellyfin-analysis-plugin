# Production-readiness plan

Production-ready means installable, upgrade-safe, bounded, observable, recoverable and verified
against the exact supported Jellyfin build. Passing unit tests alone is insufficient.

## P0 release blockers

- [x] Native Jellyfin authentication and item visibility enforcement.
- [x] Bounded schema/cross-field validation and a request limit before controller processing.
- [x] Plugin-owned SQLite with a migration baseline that adopts 0.1 development databases.
- [x] Hard storage ceiling plus scheduled/manual/upload-time retention and LRU cleanup.
- [x] Persisted cleanup status and damped last-access writes.
- [x] Corrupt-record isolation and operational error telemetry.
- [x] Controller-level authorization, CORS, precondition and malformed-payload integration tests.
      *(v0.2.2.3: `AnalysisControllerTests` deckt Sichtbarkeit ohne Existenz-Leak,
      Upload-Recht vor Ausfuehrung, CORS-Preflight gegen fremde Herkunft, ungueltige
      Routen-Identitaet, ETag/If-None-Match je Detailstufe und den Fingerabdruck-Abgleich
      bei ersetzter Datei ab; `AnalysisDocumentValidatorTests` jede einzelne
      Vertragsgrenze eines Uploads.)*
- [x] Fresh-install and restart smoke test on Jellyfin 10.11.11 (ARM64 local and x64 CI).
- [ ] Upgrade and uninstall smoke test on the target Jellyfin 10.11.11 LXC.

### Bewusst gestrichen

- **`DefaultAuthorization`-Policy statt `[Authorize]`.** Sachlich richtig: der
  Handler von Jellyfin erzwingt zusätzlich deaktivierte Konten, Zugriffszeitpläne
  und „Fernzugriff für diesen Nutzer erlauben". Der Betreiber hat entschieden
  (2026-07-27), dass das hier keinen Nutzen hat: die Instanz läuft rein lokal,
  ohne Fernzugriff und ohne weitere Nutzer, gegen die eine solche Policy schützen
  würde. Der Wechsel trägt also kein Risiko ab, birgt aber eines — der Policy-Name
  wird serverseitig aufgelöst, und ein falscher ergibt zur Laufzeit 500er auf
  ALLEN Endpunkten. Wird der Server je von außen erreichbar oder bekommt weitere
  Nutzer, gehört die Entscheidung neu getroffen.

## P1 release engineering

- [x] Deterministic install archive with manifest, SHA-256 checksum and SBOM.
- [x] Dependency vulnerability gate.
- [ ] Automated dependency-license gate.
- [ ] Tagged release workflow; rollback and backup procedure is documented.
- [ ] Generated TypeScript client artifact and consumer contract-hash gate.
- [ ] Real-server test for multiple media sources, file replacement and access isolation.

## P2 device validation

- [ ] Quest 3S benchmark for `compact` and adaptive promotion to `balanced`.
- [ ] Long-video memory/latency test at maximum contract size.
- [ ] LXC backup/restore drill with several GiB of analysis data.

The repository's `docs/implementation-status.md` records shipped behavior. A release may be called
production-ready only when every P0 item passes and the supported-version matrix identifies the
exact verified artifact.
