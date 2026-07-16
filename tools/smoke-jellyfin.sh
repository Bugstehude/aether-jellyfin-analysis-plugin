#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:10.11.11}"
archive="${1:-$(find "$root/artifacts/package" -maxdepth 1 -name 'aether-analysis-*.zip' -print -quit)}"
container="aether-jellyfin-smoke-${RANDOM}-${RANDOM}"
scratch="$(mktemp -d)"

cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  rm -rf "$scratch"
}
trap cleanup EXIT

if [[ -z "$archive" || ! -f "$archive" ]]; then
  echo "Final plugin archive is missing. Run tools/package-plugin.sh first." >&2
  exit 1
fi

mkdir -p "$scratch/config/plugins/AETHER Analysis"
unzip -p "$archive" Jellyfin.Plugin.AetherAnalysis.dll > \
  "$scratch/config/plugins/AETHER Analysis/Jellyfin.Plugin.AetherAnalysis.dll"
docker run --detach --name "$container" \
  --publish 127.0.0.1::8096 \
  --volume "$scratch/config:/config" \
  "$image" >/dev/null

port="$(docker port "$container" 8096/tcp | sed -n 's/.*://p')"
for _ in $(seq 1 60); do
  if curl --connect-timeout 2 --max-time 5 --fail --silent \
    "http://127.0.0.1:${port}/System/Info/Public" > "$scratch/system-info.json"; then
    break
  fi
  if ! docker inspect --format '{{.State.Running}}' "$container" | grep -q true; then
    docker logs "$container" >&2
    exit 1
  fi
  sleep 2
done

jq -e '.Version == "10.11.11"' "$scratch/system-info.json" >/dev/null
capabilities_status="$(curl --connect-timeout 2 --max-time 5 --silent \
  --output /dev/null --write-out '%{http_code}' \
  "http://127.0.0.1:${port}/AetherAnalysis/v1/capabilities")"
if [[ "$capabilities_status" != "401" ]]; then
  docker logs "$container" >&2
  echo "Expected the authenticated plugin endpoint to return 401, got $capabilities_status." >&2
  exit 1
fi
logs="$(docker logs "$container" 2>&1)"
if grep -Eiq '(ERR.*AETHER|AETHER.*(exception|failed)|Jellyfin\.Plugin\.AetherAnalysis.*(exception|failed))' <<< "$logs"; then
  echo "$logs" >&2
  echo "Jellyfin reported an AETHER plugin error." >&2
  exit 1
fi

docker restart "$container" >/dev/null
for _ in $(seq 1 60); do
  restarted_status="$(curl --connect-timeout 2 --max-time 5 --silent \
    --output /dev/null --write-out '%{http_code}' \
    "http://127.0.0.1:${port}/AetherAnalysis/v1/capabilities" || true)"
  [[ "$restarted_status" == "401" ]] && break
  sleep 2
done
if [[ "${restarted_status:-}" != "401" ]]; then
  docker logs "$container" >&2
  echo "Plugin endpoint did not recover after a Jellyfin restart." >&2
  exit 1
fi

echo "Jellyfin 10.11.11 loaded the final AETHER archive and survived restart."
