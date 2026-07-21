# Changelog

All notable changes to implementation and canonical contracts are recorded here.

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
