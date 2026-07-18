# Security and dependency boundary

## Reporting

Do not open a public issue for a suspected vulnerability. Report it privately through the GitHub
repository's security-advisory interface. Do not include Jellyfin access tokens, media paths,
database files or analysis documents containing private library metadata.

## SQLite ownership

The production plugin targets Jellyfin 10.11.11 and deliberately references
`Microsoft.EntityFrameworkCore.Sqlite.Core` 9.0.11. Jellyfin owns the process-wide managed and
native SQLite runtime. The plugin install archive must not contain a second `e_sqlite3` binary,
because loading competing native providers into one server process is unsafe and platform
dependent.

The test project runs outside Jellyfin. It therefore pins `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3,
which resolves to the patched SourceGear native SQLite package. The CI vulnerability gate audits
both the production and test dependency graphs and fails on every NuGet advisory severity.

The deterministic install archive is verified to contain only
`Jellyfin.Plugin.AetherAnalysis.dll`. The Jellyfin smoke test loads that exact archive into the
digest-pinned official Jellyfin 10.11.11 image and repeats the endpoint check after restart.

## Stored data

The plugin stores compressed analysis features and media fingerprints, never frames, thumbnails,
source media, Jellyfin tokens or user passwords. API errors and status responses must not expose
filesystem paths, SQL text, stack traces or credentials.
