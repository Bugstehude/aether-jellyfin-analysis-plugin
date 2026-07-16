# ADR 0001: Plugin repository owns the canonical contract

- Status: Accepted
- Date: 2026-07-16

## Context

Two AETHER application stacks need to exchange the same persistent video-analysis documents with
a Jellyfin plugin. Independent contract copies would drift and make Desktop/Quest interoperability
unreliable.

## Decision

This repository owns OpenAPI, JSON Schema, Golden Files, error codes, capabilities and version
history. Both AETHER stacks consume released artifacts from this repository and run the Golden
Files in CI. Neither stack may redefine the wire format.

The plugin is independent of either stack's current sidecar. Sidecar import is an external,
optional migration tool and not a runtime dependency.

## Consequences

- Contract changes require a changelog entry and compatibility classification here.
- A consumer can lag behind only when the advertised capabilities declare compatibility.
- Repository releases must provide immutable contract artifacts alongside the plugin package.
