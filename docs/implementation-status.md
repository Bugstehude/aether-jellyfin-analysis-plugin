# Implementation status

This document distinguishes committed behavior from accepted design. It is updated with every
implementation milestone so consumers never infer features from the architecture document alone.

## Implemented in 0.1 development baseline

- Exact Jellyfin 10.11.11, .NET 9 and EF Core 9.0.11 pins.
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
- Unit/Golden-File tests and private-repository CI.

## Accepted but not yet implemented

- LRU capacity cleanup and age-based retention task. The baseline enforces a hard capacity ceiling
  but does not yet evict older records automatically.
- EF Core migrations beyond initial schema creation.
- Library-scan invalidation hook and orphan cleanup.
- Per-folder/item analysis job management; the plugin does not decode media in v1.
- External importer for the transitional AETHER sidecar.
- Generated TypeScript client package and automated consumer synchronization releases.
- Real Jellyfin 10.11.11 installation smoke test, LXC backup test and Quest 3S benchmark.

Until the real-server smoke test passes, builds are development artifacts and must not be treated
as production-ready plugin releases.
