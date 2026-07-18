#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:10.11.11@sha256:aefb67e6a7ff1debdd154a78a7bbb780fd0c873d8639210a7f6a2016ad2b35db}"
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
  echo "Expected the unauthenticated plugin endpoint to return 401, got $capabilities_status." >&2
  exit 1
fi

curl --connect-timeout 2 --max-time 5 --fail --silent \
  "http://127.0.0.1:${port}/Startup/User" > "$scratch/startup-user.json"
username="$(jq -r '.Name' "$scratch/startup-user.json")"
password="$(jq -r '.Password // ""' "$scratch/startup-user.json")"
curl --connect-timeout 2 --max-time 5 --fail --silent \
  --request POST \
  "http://127.0.0.1:${port}/Startup/Complete" >/dev/null
auth_identity='MediaBrowser Client="AETHER%20Smoke", DeviceId="aether-ci", Device="CI", Version="0.1.0"'
jq -n --arg username "$username" --arg password "$password" \
  '{Username: $username, Pw: $password}' > "$scratch/auth-request.json"
curl --connect-timeout 2 --max-time 5 --fail --silent \
  --request POST \
  --header "Authorization: $auth_identity" \
  --header 'Content-Type: application/json' \
  --data-binary "@$scratch/auth-request.json" \
  "http://127.0.0.1:${port}/Users/AuthenticateByName" > "$scratch/auth-response.json"
access_token="$(jq -r '.AccessToken' "$scratch/auth-response.json")"
if [[ -z "$access_token" || "$access_token" == "null" ]]; then
  echo "Jellyfin startup completed but did not issue a smoke-test token." >&2
  exit 1
fi
auth_header="$auth_identity, Token=$access_token"
curl --connect-timeout 2 --max-time 5 --fail --silent \
  --header "Authorization: $auth_header" \
  "http://127.0.0.1:${port}/AetherAnalysis/v1/capabilities" > "$scratch/capabilities.json"
jq -e '.apiVersion == "1.0" and (.supportedAnalysisSchemas | index(2)) != null' \
  "$scratch/capabilities.json" >/dev/null
curl --connect-timeout 2 --max-time 5 --fail --silent \
  --header "Authorization: $auth_header" \
  "http://127.0.0.1:${port}/AetherAnalysis/v1/status" > "$scratch/plugin-status.json"
jq -e '.service == "ready" and .databaseSchemaVersion == 2 and .recordCount == 0' \
  "$scratch/plugin-status.json" >/dev/null
logs="$(docker logs "$container" 2>&1)"
if grep -Eiq '(ERR.*AETHER|AETHER.*(exception|failed)|Jellyfin\.Plugin\.AetherAnalysis.*(exception|failed))' <<< "$logs"; then
  echo "$logs" >&2
  echo "Jellyfin reported an AETHER plugin error." >&2
  exit 1
fi

docker restart "$container" >/dev/null
for _ in $(seq 1 60); do
  restarted_status="$(curl --connect-timeout 2 --max-time 5 --silent \
    --header "Authorization: $auth_header" \
    --output /dev/null --write-out '%{http_code}' \
    "http://127.0.0.1:${port}/AetherAnalysis/v1/capabilities" || true)"
  [[ "$restarted_status" == "200" ]] && break
  sleep 2
done
if [[ "${restarted_status:-}" != "200" ]]; then
  docker logs "$container" >&2
  echo "Plugin endpoint did not recover after a Jellyfin restart." >&2
  exit 1
fi

curl --connect-timeout 2 --max-time 5 --fail --silent \
  --header "Authorization: $auth_header" \
  "http://127.0.0.1:${port}/AetherAnalysis/v1/status" \
  | jq -e '.service == "ready" and .databaseSchemaVersion == 2' >/dev/null

echo "Jellyfin 10.11.11 authenticated the final AETHER archive, initialized storage and survived restart."
