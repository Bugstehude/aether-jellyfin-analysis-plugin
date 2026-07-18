# Changelog

All notable changes to implementation and canonical contracts are recorded here.

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
