# Implementation status

This document distinguishes committed behavior from accepted design. It is updated with every
implementation milestone so consumers never infer features from the architecture document alone.

## Implemented through 0.2.4.0

- Exact Jellyfin 10.11.11, .NET 9 and EF Core 9.0.11 pins.
- Server-owned SQLite runtime boundary plus patched, isolated native SQLite test runtime.
- Canonical OpenAPI, schema version 2 JSON Schemas and Golden Files.
- Native Jellyfin authentication with item visibility checks and non-leaking 404 responses.
- Administrator or explicit analyzer-user uploads; administrator-only deletion.
- Concrete media-source identity and server fingerprinting without exposing media paths.
- Plugin-owned EF Core SQLite database; no raw SQL and no Jellyfin database tables.
- Bounded schema/cross-field validation and 50 MiB default upload limit.
- Brotli level 5 storage and strong content ETags.
- Deterministic `compact`, `balanced` and `full` response representations.
- Batch status, explicit batch delete, storage-status endpoint and positive origin allowlisting.
- Jellyfin dashboard configuration for 10 GiB default capacity, retention and browser origins.
- Hard rejection before an upload would exceed configured capacity.
- Serialized capacity-check/commit section so concurrent uploads cannot bypass the hard ceiling.
- EF Core migration baseline with lossless adoption of 0.1 development databases.
- Scheduled, manual and upload-time retention/LRU cleanup with persisted status.
- Damped access-time updates to avoid a database write on every playback request.
- Metadata-only batch status plus metadata-only HEAD and conditional 304 responses.
- Absolute ASP.NET request-size limit and defensive null/identity/detail validation.
- Corrupt-record isolation plus non-sensitive process-local failure counters in admin status.
- Unit/Golden-File tests, NuGet vulnerability gate and public-repository CI.
- Deterministic DLL-and-worker archive and digest-pinned Jellyfin start/restart smoke harness.
- Reproducible Jellyfin catalog manifest tied to the versioned GitHub release archive.
- Optional serialized server analysis through scheduled, post-scan and authorized API triggers.
- Server-wide voice recordings and one range-enabled journey audio track with bounded storage.

## Accepted but not yet implemented

- Orphan cleanup for items or media sources removed from Jellyfin.
- External importer for the transitional AETHER sidecar.
- Generated TypeScript client package and automated consumer synchronization releases.
- Target-LXC install/upgrade/uninstall acceptance, backup test and Quest 3S benchmark.

Folder and multi-item selection remain AETHER client concerns. The optional server runner is
described by ADR 0005; with server analysis disabled, the plugin remains a storage-only service.

Version 0.2.4.0 remains a test release. Fresh installation, authenticated API access, storage
initialization and restart pass against Jellyfin 10.11.11 on local ARM64 Docker and x64 CI. It must
not be treated as production-ready until target-LXC upgrade and uninstall acceptance pass.
