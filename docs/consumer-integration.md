# Consumer integration

The complete normative client behavior is defined in `docs/client-integration-contract.md`.

Both AETHER stacks integrate through the same repository abstraction and immutable contract
release from this repository.

The administrator must add each external AETHER web origin to the plugin allowlist and restart
Jellyfin after changing that list. Same-origin clients need no additional CORS entry.

## Required sequence

1. Call `GET /AetherAnalysis/v1/capabilities` with the active Jellyfin session.
2. Verify API v1, schema 2, the desired algorithm version and requested detail level.
3. Resolve the concrete Jellyfin item ID and media-source ID.
4. For reads, request `detail=compact`, `balanced` or `full` and revalidate the locally cached ETag.
5. Before analysis, fetch the server fingerprint; upload it as `mediaFingerprintAtStart`.
6. Treat 404 as inaccessible, missing or stale without trying to distinguish item existence.
7. Retry 503 only after the server's `Retry-After` delay.

Selection, decoding and analysis jobs always remain in the AETHER client. The plugin exposes no
server-side job API and never starts analysis for a library or folder.

## Device policy

- Quest defaults to `compact`; it may promote to `balanced` only after sustained frame-time headroom.
- Desktop defaults to `balanced`; authoring/diagnostics may request `full`.
- A client may select a lower detail at any time without regenerating the analysis.
- Playback must continue with safe live fallback when plugin data is missing or unavailable.

## Contract synchronization

Consumers pin a tagged release of this repository. CI validates all valid upload Golden Files,
rejects all invalid Golden Files and checks generated models against the release's OpenAPI/schema
hashes. Direct copies edited inside consumer repositories are forbidden.
