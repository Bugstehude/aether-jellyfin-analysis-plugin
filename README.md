# AETHER Analysis for Jellyfin

Canonical source repository for the AETHER analysis plugin targeting **Jellyfin 10.11.11**.
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

Version 0.1 implements the storage-plugin foundation:

- authenticated capabilities and item-scoped analysis endpoints;
- Jellyfin item-access checks that return 404 without leaking inaccessible item existence;
- administrator-only writes for the initial implementation;
- EF Core backed plugin-owned SQLite storage;
- Brotli-compressed, bounded JSON documents with ETags;
- a minimal configuration page and storage defaults;
- contract and unit tests.

The plugin stores no video frames, thumbnails, source media, Jellyfin tokens or user passwords.
It uses Jellyfin's process-owned SQLite runtime in production; the patched native SQLite bundle in
the test project is isolated from the install archive to avoid native-library conflicts.
Actual video analysis permanently remains an AETHER-client responsibility. Folder and multi-item
checkbox selection are implemented by the client; the plugin never decodes media or starts jobs.

See `docs/implementation-status.md` before deployment. The 0.1 test release passes fresh-install
and restart smoke tests against Jellyfin 10.11.11 on ARM64 locally and x64 in CI. Target-LXC
installation, upgrade, uninstall and backup/restore acceptance remain required before the release
may be called production-ready.

## Build

Requirements: .NET SDK 9 and network access to NuGet.

```bash
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-restore --no-build
tools/package-plugin.sh
```

The installable artifact is `artifacts/package/aether-analysis-0.1.0.0.zip`. Its SHA-256 checksum
and CycloneDX SBOM are generated beside it. The archive contains only
`Jellyfin.Plugin.AetherAnalysis.dll`; do not copy
test-native libraries or host framework assemblies into Jellyfin's plugin directory. Jellyfin
10.11.11 supplies the exactly pinned runtime dependencies.

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

The repository is public for the 0.1 test release so Jellyfin can fetch its repository manifest
and install archive without GitHub credentials. The plugin links against Jellyfin's GPL-licensed
assemblies and is therefore licensed under GPL-3.0-or-later. Public distribution must include
corresponding source code and satisfy the license obligations. Making this repository private
again leaves an installed copy intact but disables fresh installations and catalog updates unless
the manifest, archive and corresponding source are mirrored on an accessible endpoint.
