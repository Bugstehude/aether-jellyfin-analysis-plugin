# Analysis version and read compatibility contract

This document is normative together with the OpenAPI contract. It applies to Aether Web/Desktop
and AetherTV. It separates API version, document schema and measurement algorithm.

| Identity | Current value | Purpose |
| --- | --- | --- |
| HTTP API | `/AetherAnalysis/v1`, API `1.0` | Routes and request/response behavior |
| Document schema | `2` | JSON structure and field types |
| Preferred measurement algorithm | `aether-visual/1.2.0` | Measurements produced by new server analyses |
| Compatible existing algorithm | `aether-visual/1.1.0`, `1.0.0` | Existing measurements, retaining their original meaning |

Plugin 0.4.0.0 produces fresh server compositions under `1.2.0`. It retains the original
component algorithm and measured-group provenance from its pinned worker. Existing drafts and
legacy records are never relabeled. Marc authorized first use with Aether Desktop, with device
RAM, latency and TV acceptance following in live operation. These checks are not claimed complete.

The [1.2 field/wire contract](analysis-1.2-contract.md) keeps independently measured legacy audio
and dense audio separate. Method/codec names are unchanged. Only server composition jobs may
create stable 1.2 records. Unverified legacy-style HTTP uploads to 1.2 are rejected with 422.

## Additive signals and their limits

Compatible schema-v2 documents keep required known signals readable. Readers ignore unknown
optional fields and fall back when optional fields are absent. They use the signals they support
without requiring a stored copy projected to their older measurement version. `compact`,
`balanced` and `full` control temporal detail, not feature compatibility.

Read compatibility means known data can be consumed. It does not mean identical measurement
semantics. Version `1.1.0` changed palette weighting relative to `1.0.0`. The actual algorithm
identity remains visible and is never relabeled. A client requiring the exact old measurement
semantics must use exact selection rather than compatible selection.

A format-breaking change needs a new document schema. Changed normalization, timestamp meaning
or measurement semantics needs a new algorithm version and an explicitly reviewed compatibility
entry. Missing dense audio, spectra or regional image information cannot be recovered by
relabeling old results. Optional new signal fields must be implemented throughout the schemas,
validator, master builder, reducers and consuming clients before they are advertised.

## Capabilities negotiation

An algorithm entry now includes the following additive properties:

```json
{
  "id": "aether-visual",
  "versions": ["1.2.0"],
  "preferredVersion": "1.2.0",
  "compatibleReadVersions": ["1.1.0", "1.0.0"],
  "readCompatibility": [
    { "readerVersion": "1.2.0", "analysisVersions": ["1.2.0", "1.1.0", "1.0.0"] },
    { "readerVersion": "1.1.0", "analysisVersions": ["1.1.0", "1.0.0"] },
    { "readerVersion": "1.0.0", "analysisVersions": ["1.0.0", "1.1.0"] }
  ]
}
```

`versions` continues to advertise the current production version. `preferredVersion` identifies
the version used for new server analyses. `compatibleReadVersions` lists older results usable by
the preferred reader. `readCompatibility` is the complete explicit matrix. Each list orders
stored versions by preference, with the requested version first. A matching fingerprint is
required. Unknown versions are not accepted merely because they have a lower or higher SemVer.

These properties are optional in OpenAPI so clients can still connect to previous plugin
releases. If the matrix is absent, use the previous exact-version flow. A previously installed
client that only requests a fixed exact version is not retroactively upgraded by the plugin.
The 1.2 reader direction is explicit. Older reader rows do not authorize reading stored 1.2.

## Selecting and reading an existing result

`POST /analyses/query` accepts optional top-level `allowCompatible`, default `false`:

```json
{
  "algorithm": { "id": "aether-visual", "version": "1.0.0" },
  "allowCompatible": true,
  "items": [
    { "itemId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "mediaSourceId": "source-1" }
  ]
}
```

With only a matching `1.1.0` analysis present, the item is `available` and contains
`algorithm: { "id": "aether-visual", "version": "1.1.0" }`. The selected record's creation
time, frame count, stored bytes and master ETag accompany that identity. Inaccessible items
remain `missing`. A matching compatible result is preferred over a stale exact result. If all
available candidates have a different fingerprint the result is `stale`. If none exist it is
`missing`. Status lookup remains metadata-only. A corrupt master may still fail at GET, in which
case playback falls back safely rather than repeatedly triggering analysis.

The client then calls the existing exact GET or HEAD route using the returned algorithm version.
For the example it reads `/analyses/aether-visual/1.1.0`, even though its reader requested
`1.0.0`. It verifies that the returned document identity agrees with this selected version and
caches by the actual algorithm identity, server, user, item, media source and detail level.
GET/HEAD representation ETags stay separate from the master ETag reported by the batch query.

Exact GET, HEAD, PUT and DELETE never perform fallback. Without `allowCompatible`, the batch query
also remains exact. `allowCompatible: true` is rejected with 400 by batch deletion. A compatible
read does not authorize rewriting an old document under another version. Client-produced uploads
must use the version of the algorithm actually executed, not merely the plugin's preferred version.

## Routine upgrades and explicit recalculation

Scheduled and post-scan runs use the preferred server measurement version. They serially analyze
configured-library media without a valid result under that version, including media with only an
older compatible result. A valid current result is skipped even if optional audio is absent or it
was produced by a browser. A later run skips a successfully upgraded item. Disabling server-side
analysis still disables these server jobs.

Normal `POST .../analyze` requests skip a valid compatible result. The optional query parameter
`recalculate=true` requests a full new analysis of the concrete media source in the route. It does
not affect other media sources of the item. Status and queue deduplication are scoped to that
same item/source. An already queued or running job retains its original options when requested
again. To change its options, wait for completion and submit a new request.

During a replacement, the old readable source is protected from retention and capacity cleanup.
It is not rewritten while the worker runs. The target is validated and atomically stored only if
the media fingerprint and the target ETag have not changed. A concurrent upload wins over an
older worker result. Cancellation, invalid output and decoder failure leave the source intact.
If source and target cannot coexist within the capacity limit, storage fails without evicting
the protected source. After the job finishes, ordinary retention/LRU rules apply again. Protection
is process-local and is not a permanent archival guarantee.

The current worker still performs a complete analysis when upgrading. Audio-only enrichment and
video-only enrichment are follow-up work in the shared worker. This contract creates the read and
version foundation, not those new measurement algorithms.
