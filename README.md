# AETHER Analysis for Jellyfin

Private development repository for the AETHER analysis plugin targeting **Jellyfin 10.11.11**.
The repository is the canonical source of truth for the plugin and for every AETHER client that
reads or writes persistent video analyses.

## Source-of-truth rule

The following artifacts are normative and are versioned together here:

- `contracts/openapi/aether-analysis-v1.yaml` — HTTP API under `/AetherAnalysis/v1`
- `contracts/schemas/analysis-upload-v2.schema.json` — upload document schema
- `contracts/examples/` — valid and invalid Golden Files
- `docs/architecture/plugin-concept.md` — architecture, security and lifecycle decisions
- `docs/compatibility.md` — exact Jellyfin/.NET/EF Core compatibility matrix
- `docs/implementation-status.md` — implemented behavior versus accepted follow-up work
- `docs/client-integration-contract.md` — normative client workflow and retry/device policy
- `docs/production-readiness.md` — release blockers and verification gates
- `CHANGELOG.md` — semantic contract and implementation changes

Consumers must import or generate from these files. They must not maintain divergent copies.
The URL version (`v1`), document schema (`2`) and analysis algorithm version are deliberately
independent.

## Current scope

Version 0.1 implements the storage-plugin foundation:

- authenticated capabilities and item-scoped analysis endpoints;
- Jellyfin item-access checks that return 404 without leaking inaccessible item existence;
- administrator-only writes for the initial implementation;
- EF Core backed plugin-owned SQLite storage;
- Brotli-compressed, bounded JSON documents with ETags;
- a minimal configuration page and storage defaults;
- contract and unit tests.

The plugin stores no video frames, thumbnails, source media, Jellyfin tokens or user passwords.
Actual video analysis permanently remains an AETHER-client responsibility. Folder and multi-item
checkbox selection are implemented by the client; the plugin never decodes media or starts jobs.

See `docs/implementation-status.md` before deployment. The first baseline builds and tests locally,
but is not production-ready until it passes the real Jellyfin 10.11.11/LXC smoke test.

## Build

Requirements: .NET SDK 9 and network access to NuGet.

```bash
dotnet restore --locked-mode
dotnet test --configuration Release -p:AetherIncludeRuntimeDependencies=true
dotnet publish src/Jellyfin.Plugin.AetherAnalysis/Jellyfin.Plugin.AetherAnalysis.csproj \
  --configuration Release --output artifacts/plugin
```

The installable artifact is `artifacts/plugin/Jellyfin.Plugin.AetherAnalysis.dll`. Do not copy the
generated `runtimes/` directory or host framework assemblies into Jellyfin's plugin directory;
Jellyfin 10.11.11 already supplies the exactly pinned runtime dependencies.

The package references are pinned to Jellyfin 10.11.11. Do not upgrade them independently of
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

The repository remains private during early development. The plugin links against Jellyfin's
GPL-licensed assemblies and is therefore licensed under GPL-3.0-or-later. Public distribution
must include corresponding source code and license obligations.
