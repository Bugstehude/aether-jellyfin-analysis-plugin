#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${CONFIGURATION:-Release}"
version="$(sed -n 's/^version: "\([^"]*\)"/\1/p' "$root/build.yaml")"
publish_dir="$root/artifacts/plugin"
package_dir="$root/artifacts/package"
staging_dir="$package_dir/staging"
archive="$package_dir/aether-analysis-${version}.zip"

if [[ -z "$version" ]]; then
  echo "Unable to read plugin version from build.yaml" >&2
  exit 1
fi
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Plugin version must contain four numeric components: $version" >&2
  exit 1
fi

dotnet publish "$root/src/Jellyfin.Plugin.AetherAnalysis/Jellyfin.Plugin.AetherAnalysis.csproj" \
  --configuration "$configuration" \
  --no-restore \
  --no-build \
  --output "$publish_dir"

worker_file="aether-analysis-worker.cjs"
if [[ ! -f "$publish_dir/$worker_file" ]]; then
  echo "Vendored worker bundle missing from publish output: $publish_dir/$worker_file" >&2
  echo "Rebuild it in the AETHER repo (pnpm --filter @aether/server-analysis-worker build:plugin) and run tools/vendor-worker.sh." >&2
  exit 1
fi

mkdir -p "$staging_dir" "$package_dir"
find "$staging_dir" -mindepth 1 -maxdepth 1 -delete
cp "$publish_dir/Jellyfin.Plugin.AetherAnalysis.dll" "$staging_dir/"
cp "$publish_dir/$worker_file" "$staging_dir/"
chmod 0644 "$staging_dir/Jellyfin.Plugin.AetherAnalysis.dll" "$staging_dir/$worker_file"
TZ=UTC touch -t 198001010000 "$staging_dir/Jellyfin.Plugin.AetherAnalysis.dll" "$staging_dir/$worker_file"
rm -f "$archive" "$archive.sha256"
(
  cd "$staging_dir"
  zip -X -q "$archive" Jellyfin.Plugin.AetherAnalysis.dll "$worker_file"
)

if command -v sha256sum >/dev/null 2>&1; then
  (cd "$package_dir" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256")
else
  (cd "$package_dir" && shasum -a 256 "$(basename "$archive")" > "$(basename "$archive").sha256")
fi

python3 "$root/tools/generate-sbom.py" "$package_dir/aether-analysis-${version}.cdx.json"
python3 "$root/tools/generate-repository-manifest.py" \
  "$archive" \
  "$package_dir/manifest.json" \
  --build "$root/build.yaml"

echo "$archive"
