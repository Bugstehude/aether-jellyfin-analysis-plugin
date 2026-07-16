# ADR 0004: Analysis jobs are owned by AETHER clients

- Status: accepted
- Date: 2026-07-16

## Decision

The Jellyfin plugin never decodes or analyzes media and exposes no server-side analysis-job API.
An AETHER client explicitly starts every analysis. Folder selection and multi-item checkbox
selection are client UI concerns. The client resolves the selected Jellyfin items and media
sources, queries their current plugin status in bounded batches, analyzes the required items and
uploads each completed schema-2 document through the canonical `PUT` endpoint.

The plugin is responsible only for capability negotiation, Jellyfin authorization, media-source
identity, validation, persistence, deterministic detail reduction, cleanup and distribution.

## Consequences

- Jellyfin never receives an unexpected FFmpeg analysis workload.
- There is deliberately no “analyze entire library” endpoint or implicit library-wide job.
- A desktop client may process a checked selection with bounded concurrency and resumable local
  progress. The Quest normally consumes results and need not analyze the same video again.
- Missing or evicted data is represented as `missing`; the client may offer explicit reanalysis.
- Any future server worker would be a separate AETHER client using the same public API and would
  not expand the plugin's responsibilities.
