# Compatibility matrix

| Component | Pinned version | Policy |
| --- | --- | --- |
| Jellyfin Server | 10.11.11 | Exact ABI target |
| Jellyfin Web | 10.11.11 | Test target for dashboard and CORS |
| Jellyfin build | 10.11.11 | Exact integration-test image |
| .NET target framework | net9.0 | Required by Jellyfin 10.11.x |
| .NET SDK | 9.0.304, roll-forward disabled | Reproducible local/CI build |
| EF Core SQLite Core | 9.0.11 | Matches Jellyfin 10.11.11; server owns native runtime |
| SQLite test bundle | SQLitePCLRaw 3.0.3 | Patched native runtime isolated to tests |
| HTTP API | `/AetherAnalysis/v1` | URL contract version |
| Analysis document | `schemaVersion: 2` | Canonical document schema |

Package references to `Jellyfin.Controller` and `Jellyfin.Model` must match the deployed server
exactly. A Jellyfin upgrade is not considered supported until the plugin builds and its API,
authorization, persistence and installation smoke tests pass against the target version.

The plugin uses a separate, plugin-owned SQLite database through EF Core. It never adds tables to
or executes raw SQL against Jellyfin's main database. The install archive intentionally contains
no native SQLite binary; see `docs/security.md` for the process ownership boundary.
