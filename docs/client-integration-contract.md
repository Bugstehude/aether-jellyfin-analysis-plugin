# Normative AETHER client integration

This document is the behavioral source of truth for every AETHER client. The OpenAPI file and JSON
Schemas remain normative for wire shapes. If prose and a machine-readable artifact disagree, the
machine-readable artifact wins and the discrepancy must be fixed in this repository.

## Responsibility boundary

The client owns selection, analysis scheduling, video decoding, progress, pause/resume and retry.
The plugin never analyzes a video. It stores one high-quality master analysis and derives the
requested temporal detail at read time.

Client selection may contain one or more checked folders and individually checked items. The
client expands folders through the normal Jellyfin library API, deduplicates by
`(itemId, mediaSourceId)`, shows the resulting count and requires an explicit start action. It must
not silently select the entire library.

## Required startup negotiation

1. Build the plugin base URL from the already configured Jellyfin server URL plus
   `/AetherAnalysis/v1`. Do not configure a second origin or token for the plugin.
2. Send the same native Jellyfin `Authorization` header used by the client for Jellyfin APIs.
3. Call `GET /capabilities` once per authenticated server session.
4. Require API `1.0`, schema `2`, algorithm `aether-visual@1.0.0` and the desired detail level.
5. Honor the returned upload and batch limits. A client must not hard-code a larger value.

Tokens must never be placed in URLs, analysis documents, telemetry or persistent analysis caches.

## Analyze checked selection

For a checked selection, the desktop client performs this state machine for each concrete media
source:

```text
selected -> status query -> available: done
                         -> missing/stale: fingerprint -> local analysis -> fingerprint check/upload
                         -> inaccessible: hidden as missing
                         -> transient failure: bounded retry or paused
```

The exact flow is:

1. Split the deduplicated selection into chunks no larger than `limits.maxBatchItems` and call
   `POST /analyses/query`.
2. Skip `available` items unless the user explicitly selected “recalculate”.
3. For a required item, call the fingerprint endpoint immediately before decoding and retain the
   returned `fingerprint`.
4. Analyze locally. The canonical default is one sample every 250 ms at 480 px width. Producers
   may use another cadence within the schema limits, but must report the actual values.
5. Put the retained fingerprint into `mediaFingerprintAtStart`, validate against
   `analysis-upload-v2.schema.json`, and upload to the canonical item/media-source/algorithm URL.
6. Treat `201` or `204` as success and retain the response ETag. A `409` means that the media
   changed during work; discard the result and require a new analysis. A `412` means another
   writer won; re-query before deciding whether work is still necessary.
7. Keep per-item progress locally so a stopped selection can resume by querying server status
   again. Never persist the Jellyfin token inside the progress record.

Recommended analysis concurrency starts at one video per client. A client may make status and
fingerprint reads concurrently, but must keep video decoding concurrency explicitly bounded and
user-configurable.

## Read and playback policy

- Quest requests `detail=compact` first. It may promote to `balanced` only after measured frame
  time and memory headroom remain healthy. It should not request `full` during normal playback.
- Desktop playback requests `balanced`; diagnostics and authoring may request `full`.
- Cache each representation by server identity, user identity, item ID, media-source ID,
  algorithm, version and detail. Store its ETag and revalidate with `If-None-Match`.
- Never reuse an analysis between different Jellyfin servers merely because item IDs match.
- A `404` means missing, stale or inaccessible. The playback UI must not try to distinguish those
  cases and must continue using the safe live-analysis fallback.
- A failed plugin request must not block video playback.

## Retry and error policy

| Result | Required client behavior |
| --- | --- |
| `400`, `422` | Programming/contract error; do not retry unchanged payload. |
| `401` | Refresh or re-establish the Jellyfin session once. |
| `403` | Disable the unavailable write/admin action for this user. |
| `404` | Treat as missing for readers. |
| `409` | Media changed; discard local result and restart only on explicit/user-approved retry. |
| `412` | Re-query current server state; never overwrite blindly. |
| `413` | Regenerate a smaller valid analysis; do not resend unchanged. |
| `429`, `503` | Honor `Retry-After`; otherwise exponential backoff with jitter. |
| `507` | Inform the user that the result cannot fit after server cleanup; do not loop. |

Retries of reads are safe. A repeated `PUT` without `If-Match` intentionally replaces the current
analysis; clients that coordinate multiple writers should send the master ETag returned by PUT or
`POST /analyses/query` in `If-Match`. GET/HEAD ETags identify one `detail` representation and are
used only with `If-None-Match`.

## Contract synchronization gate

Consumers pin a tagged release or immutable commit of this repository. Their CI must:

- compare `contracts/contract.sha256` before generated clients are built;
- validate upload fixtures against the pinned schema and the plugin Golden Files;
- compile generated models from `contracts/openapi/aether-analysis-v1.yaml`;
- verify that API v1, schema 2 and `aether-visual@1.0.0` are supported;
- run read, upload, stale-fingerprint and adaptive-detail contract tests;
- fail when the pinned contract hash changes without regenerating client artifacts.

No integration behavior may exist only in a chat, an unversioned sidecar document or one consumer
repository.
