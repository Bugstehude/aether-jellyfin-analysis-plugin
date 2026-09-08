# Compatibility matrix

| Component | Pinned version | Policy |
| --- | --- | --- |
| Jellyfin Server | 12.0.0 | Exact ABI target |
| Jellyfin Web | 12.0.0 | Test target for dashboard and CORS |
| Jellyfin build | 12.0.0 | Exact integration-test image |
| .NET target framework | net10.0 | Required by Jellyfin 12.0 |
| .NET SDK | 10.0.400, roll-forward disabled | Reproducible local/CI build |
| EF Core SQLite Core | 10.0.11 | Matches Jellyfin 12.0.0; server owns native runtime |
| SQLite test bundle | SQLitePCLRaw 3.0.3 | Patched native runtime isolated to tests |
| HTTP API | `/AetherAnalysis/v1` | URL contract version |
| Analysis document | `schemaVersion: 2` | Canonical document schema |

Package references to `Jellyfin.Controller` and `Jellyfin.Model` must match the deployed server
exactly. A Jellyfin upgrade is not considered supported until the plugin builds and its API,
authorization, persistence and installation smoke tests pass against the target version.

The plugin uses a separate, plugin-owned SQLite database through EF Core. It never adds tables to
or executes raw SQL against Jellyfin's main database. The install archive intentionally contains
no native SQLite binary; see `docs/security.md` for the process ownership boundary.

Plugin **0.3.0.0 requires Jellyfin 12.0.0**; it cannot load on Jellyfin 10.11.11.
Plugin 0.2.9.0 remains the version for Jellyfin 10.11.11. The HTTP API and stored
analysis format are unchanged. Fresh-install/authentication/database initialization
and restart are covered by the Docker smoke test on x64; target-system upgrade,
backup/restore and dashboard acceptance remain deployment checks.
