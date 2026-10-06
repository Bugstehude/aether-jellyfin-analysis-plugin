# AETHER Analysis for Jellyfin

Canonical source repository for the AETHER analysis plugin targeting **Jellyfin 12.0.0**.
The repository is the canonical source of truth for the plugin and for every AETHER client that
reads or writes persistent video analyses.

## Source-of-truth rule

The following artifacts are normative and are versioned together here:

- `contracts/openapi/aether-analysis-v1.yaml` — HTTP API under `/AetherAnalysis/v1`
- `contracts/schemas/analysis-upload-v2.schema.json` — upload document schema
- `contracts/examples/` — valid and invalid Golden Files
- `contracts/contract.sha256` — synchronization identity over OpenAPI and JSON Schemas
- `docs/architecture/plugin-concept.md` — architecture, security and lifecycle decisions
- `docs/compatibility.md` — exact Jellyfin/.NET/EF Core compatibility matrix
- `docs/implementation-status.md` — implemented behavior versus accepted follow-up work
- `docs/client-integration-contract.md` — normative client workflow and retry/device policy
- `docs/analysis-version-contract.md` — algorithm versions, compatible reads and routine upgrades
- `docs/production-readiness.md` — release blockers and verification gates
- `docs/security.md` — reporting process and the process-owned SQLite dependency boundary
- `docs/operations.md` — capacity, backup, restore, rollback and uninstall procedures
- `CHANGELOG.md` — semantic contract and implementation changes

Consumers must import or generate from these files. They must not maintain divergent copies.
The URL version (`v1`), document schema (`2`) and analysis algorithm version are deliberately
independent.

After an intentional OpenAPI or schema change, run `tools/contract-hash.sh` and update
`contracts/contract.sha256` in the same commit. CI rejects stale contract identities.

## Current scope

Version **0.3.0.0 requires Jellyfin 12.0.0**. For Jellyfin 10.11.11, use plugin
0.2.9.0. The compatibility update preserves the client API and stored analysis format.

Version 0.2.4.0 includes the storage-plugin foundation:

- authenticated capabilities and item-scoped analysis endpoints;
- Jellyfin item-access checks that return 404 without leaking inaccessible item existence;
- administrator or explicitly configured analyzer-user writes;
- EF Core backed plugin-owned SQLite storage;
- Brotli-compressed, bounded JSON documents with ETags;
- a minimal configuration page and storage defaults;
- contract and unit tests.

The plugin stores no decoded video frames, thumbnails, source videos, Jellyfin tokens or user
passwords. Optional user-supplied voice and journey audio is kept inside the plugin data folder.
The plugin uses Jellyfin's process-owned SQLite runtime in production; the patched native SQLite
bundle in the test project is isolated from the install archive to avoid native-library conflicts.

Version **0.2** adds optional **in-plugin server-side analysis** (see below): the server can now
compute analyses itself in addition to accepting client uploads. The client upload path is
unchanged and remains fully supported; server-side analysis is enabled by default but can be
switched off in the settings.

## Server-side analysis (0.2)

The plugin can run the **shared AETHER perception-engine** (the exact same visual + audio algorithm
the browser client runs) directly on the server and store the result through its own repository —
no HTTP upload and no auth round-trip. This means a title is analyzed once, on the always-on
server, and every client instantly gets the cached, consistent result.

How it works:

- The analysis algorithm is bundled from the AETHER monorepo into a single
  `aether-analysis-1.2-worker.cjs` with a checked SHA-256 and producer revision
  (`worker/analysis-1.2-worker-manifest.json`, shipped next to the DLL). The original 1.1 worker
  is retained in the package. Jellyfin supplies its own `ffmpeg`/`ffprobe`
  (`IMediaEncoder`), so no separate ffmpeg install is needed. **Node (22+) must be installed on the
  Jellyfin server** (`apt install nodejs` or NodeSource); set its path in the settings if it is not
  on the service `PATH`.
- Three triggers feed one serial runner: a **scheduled task** (`AETHER: Analyze library`, runnable
  from Dashboard → Scheduled Tasks, daily default trigger), an **after-scan hook** that analyzes new
  or changed items, and an **analyze endpoint** the AETHER "Server-Analyse" button calls.
- New server analyses use two fresh, validated Full components and atomically compose a master
  under `aether-visual`/`1.2.0`. The current source, track, producer revision and source/target ETags
  are checked before storage. Routine runs upgrade older versions while keeping previous records
  protected and readable. A matching full target is revalidated without rerunning its components.
  HTTP uploads remain available for legacy versions. Stable 1.2 requires the server job endpoint.

Clients can opt into compatible selection with `allowCompatible: true` on `POST /analyses/query`
after negotiating `supportedAlgorithms[].readCompatibility` from capabilities. The response
identifies the actual algorithm for subsequent exact GET/HEAD and caching. Reader `1.2.0` can use
stored `1.2.0`, `1.1.0` and `1.0.0`. The existing `1.0.0` and `1.1.0` directions remain unchanged.
A normal server-analysis request
skips compatible existing results. `POST .../analyze?recalculate=true` explicitly recalculates only
the media source in that route. See [the version contract](docs/analysis-version-contract.md).

Endpoints (upload permission required, i.e. administrator or an allowed analyzer user id):

```text
POST /AetherAnalysis/v1/items/{itemId}/media-sources/{mediaSourceId}/analyze         -> 202 (queued)
GET  /AetherAnalysis/v1/items/{itemId}/media-sources/{mediaSourceId}/analyze/status  -> {state, progress}
```

Authenticate with the Jellyfin 12 header `Authorization: MediaBrowser Token="<token>"`; the legacy
`X-Emby-Token` header can be disabled server-side (a Jellyfin 12.1 server answered `401`). A Jellyfin API key (Dashboard → API keys) acts as
administrator and can address any item. A user access token works for administrators and for user
ids listed in the analyzer allow-list. To analyse one item immediately (it runs after the current
routine item, one analysis at a time):

```sh
SERVER=https://jellyfin.example
TOKEN=<api-key-or-user-token>
ID=<item-id>                      # from the item URL: …/details?id=<item-id>
SOURCE=$ID                        # single-source items: media source id equals the item id
curl -s -X POST -H "Authorization: MediaBrowser Token=\"$TOKEN\"" \
  "$SERVER/AetherAnalysis/v1/items/$ID/media-sources/$SOURCE/analyze?recalculate=true"
curl -s -H "Authorization: MediaBrowser Token=\"$TOKEN\"" \
  "$SERVER/AetherAnalysis/v1/items/$ID/media-sources/$SOURCE/analyze/status"
```

Folder and multi-item checkbox selection remain client features. When server-side analysis is
disabled, the plugin never decodes media or starts jobs and behaves exactly like 0.1.

See `docs/implementation-status.md` before deployment. The current test release passes fresh-install
and restart smoke tests against Jellyfin 12.0.0 on x64 locally and in CI. Target-LXC
installation, upgrade, uninstall and backup/restore acceptance remain required before the release
may be called production-ready.

## Build

Requirements: .NET SDK 10.0.400 and network access to NuGet.

```bash
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-restore --no-build
tools/package-plugin.sh
```

The installable artifact is `artifacts/package/aether-analysis-<build.yaml version>.zip`. Its SHA-256 checksum
and CycloneDX SBOM are generated beside it. The archive contains
`Jellyfin.Plugin.AetherAnalysis.dll` and the vendored `aether-analysis-worker.cjs`; do not copy
test-native libraries or host framework assemblies into Jellyfin's plugin directory. Jellyfin
12.0.0 supplies the exactly pinned runtime dependencies.

To refresh the worker bundle after an analysis-algorithm change, rebuild it in the AETHER monorepo
and vendor it before packaging:

```bash
AETHER_REPO=/path/to/AETHER_Codex_Starter_Kit tools/vendor-worker.sh
```

## Install through Jellyfin

Once this repository and its release assets are public, add this URL in
**Dashboard > Plugins > Repositories**:

```text
https://github.com/Bugstehude/aether-jellyfin-analysis-plugin/releases/latest/download/manifest.json
```

After saving, open the Jellyfin plugin catalog, select **AETHER Analysis**, install it and restart
Jellyfin. The manifest and ZIP are generated together for each release, so its Jellyfin ABI,
download URL and MD5 checksum always describe that exact release artifact. A private GitHub
repository cannot serve this unauthenticated URL; for private long-term operation, mirror
`manifest.json` and the referenced release ZIP on a public HTTPS endpoint and update `sourceUrl`
accordingly.

The package references are pinned to Jellyfin 12.0.0. Do not upgrade them independently of
the target-server compatibility matrix and an integration test against that exact server build.

## Database migrations

The plugin applies checked-in EF Core migrations to its own SQLite database at startup. To create
the next migration after changing `AnalysisDbContext` or an entity:

```bash
dotnet restore --locked-mode
dotnet tool restore
dotnet tool run dotnet-ef -- migrations add <MigrationName> \
  --project src/Jellyfin.Plugin.AetherAnalysis/Jellyfin.Plugin.AetherAnalysis.csproj \
  --startup-project tests/Jellyfin.Plugin.AetherAnalysis.Tests/Jellyfin.Plugin.AetherAnalysis.Tests.csproj \
  --output-dir Infrastructure/Migrations
```

Commit the migration, designer and updated model snapshot together. CI rejects a model change
without its matching migration. The production-baseline migration alone uses idempotent DDL so it
can adopt databases created by the private 0.1 development build without deleting analyses.

## License and repository visibility

The repository is public so Jellyfin can fetch its repository manifest
and install archive without GitHub credentials. The plugin links against Jellyfin's GPL-licensed
assemblies and is therefore licensed under GPL-3.0-or-later. Public distribution must include
corresponding source code and satisfy the license obligations. Making this repository private
again leaves an installed copy intact but disables fresh installations and catalog updates unless
the manifest, archive and corresponding source are mirrored on an accessible endpoint.
