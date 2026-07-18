# Implementation status

This document distinguishes committed behavior from accepted design. It is updated with every
implementation milestone so consumers never infer features from the architecture document alone.

## Implemented in 0.1 development baseline

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
- Unit/Golden-File tests, NuGet vulnerability gate and private-repository CI.
- Deterministic single-DLL archive and digest-pinned Jellyfin start/restart smoke harness.

## Accepted but not yet implemented

- Library-scan invalidation hook and orphan cleanup.
- External importer for the transitional AETHER sidecar.
- Generated TypeScript client package and automated consumer synchronization releases.
- Successful CI execution of the Jellyfin 10.11.11 smoke harness, LXC backup test and Quest 3S benchmark.

Folder and multi-item analysis management is intentionally implemented by AETHER clients and is
not an open plugin task. The plugin never decodes media or starts analysis jobs; see ADR 0004.

Until the real-server smoke test passes, builds are development artifacts and must not be treated
as production-ready plugin releases.
