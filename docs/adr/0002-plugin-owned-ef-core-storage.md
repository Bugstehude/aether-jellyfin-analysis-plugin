# ADR 0002: Plugin-owned EF Core SQLite storage

- Status: Accepted
- Date: 2026-07-16

## Context

Jellyfin 10.11 moved database access to EF Core and explicitly disallows plugin raw-SQL access to
the server database. Analysis timelines are read and replaced as whole documents.

## Decision

The plugin stores indexed metadata plus Brotli-compressed JSON blobs in a separate SQLite database
inside its Jellyfin plugin data directory. All access uses EF Core 9.0.11. The plugin never alters
Jellyfin's main database and never writes beside media files.

## Consequences

- Plugin data can be backed up and removed independently.
- Atomic replacement and LRU cleanup are straightforward.
- Database schema changes require plugin-owned migrations and downgrade-safe behavior.
