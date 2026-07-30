# ADR 0005: Server analysis is optional and shares the canonical storage path

- Status: accepted
- Date: 2026-07-30
- Supersedes: ADR 0004

## Context

ADR 0004 assigned all analysis work to AETHER clients. That boundary made the first storage
release small, but it also made every browser responsible for expensive media analysis and left
headsets dependent on analyses produced elsewhere.

The plugin now ships the AETHER perception engine as a vendored Node worker. Jellyfin provides
the media paths plus its own ffmpeg and ffprobe binaries; Node remains an explicit host
prerequisite.

## Decision

Server-side analysis is an optional plugin capability. A scheduled task, an asynchronous
post-scan trigger and an explicitly authorized API request feed one serialized runner. The runner
uses the same validation, media-fingerprint check, master-document construction and bounded
repository path as a client upload.

The client upload path remains supported. Folder selection and multi-item UI continue to belong
to AETHER clients; the plugin's scheduled and post-scan paths enumerate only the configured
Jellyfin libraries.

The worker is a release artifact. Its provenance, contract compatibility, vulnerability surface
and lifecycle are therefore part of the plugin's release and security boundary.

## Consequences

- Enabling server analysis creates deliberate background CPU and I/O load.
- A library scan does not wait for analysis; shutdown cancels the detached worker.
- Server and client results use the same algorithm identity and storage key.
- Releases must contain and inventory both the plugin DLL and the worker bundle.
- Disabling server analysis restores the original storage-only operating mode.
