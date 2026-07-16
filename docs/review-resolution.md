# Peer-review resolution

This file records the decisions that turned the reviewed draft into the canonical v2 contract.
It prevents either AETHER stack from silently re-opening already settled interoperability issues.

## Resolved blockers

1. **Target ABI:** Jellyfin Server, Web and build are pinned to 10.11.11; the plugin targets
   `net9.0`, Jellyfin packages 10.11.11 and EF Core 9.0.11.
2. **Canonical ownership:** this repository, not either client or the transitional sidecar, owns
   OpenAPI, JSON Schema, Golden Files, capabilities, errors and semantic changelog.
3. **Media identity:** every key contains Jellyfin item ID, media-source ID, algorithm ID and
   algorithm version. Media fingerprints are checked independently from analysis ETags.
4. **Browser security:** positive origin allowlisting and complete preflight behavior are part of
   the contract. Authentication tokens are accepted only in Jellyfin-supported headers.

## Normative clarifications

- The wire document starts at `schemaVersion: 2`; an existing unrelated sidecar already uses 1.
- Default visual sampling is 4 frames/s at 480 px width and 270 px height for 16:9 content.
- `sceneCutProbability` is persisted; derived `isSceneCut` and interpreted scene state are not.
- Optional audio data is limited to raw `rms` and `flux` features.
- Analysis JSON is stored and served Brotli-compressed at level 5.
- Default retention is unlimited by age (`retentionDays: 0`); capacity pressure uses damped LRU.
- Uploads are limited to 50 MiB uncompressed and batch requests to 200 items.
- Ordinary readers receive 404 for missing, inaccessible or stale item analyses, preventing
  existence leaks. Administrative/query surfaces can report stale state when authorized.
- Server-side decoding, partial timeline reads and semantic interpretation are outside API v1.
- One accurate master can serve deterministic `compact`, `balanced` and `full` representations;
  representation reduction is a transport optimization, not semantic interpretation.

## Cross-stack conformance rule

Every consumer must validate the repository's valid Golden Files and reject its invalid Golden
Files in CI. Generated client code may be cached in consumers, but the generator input and release
version must point back to an immutable release of this repository.
