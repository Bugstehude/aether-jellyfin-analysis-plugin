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
- [ ] Controller-level authorization, CORS, precondition and malformed-payload integration tests.
- [x] Fresh-install and restart smoke test on Jellyfin 10.11.11 (ARM64 local and x64 CI).
- [ ] Upgrade and uninstall smoke test on the target Jellyfin 10.11.11 LXC.

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
