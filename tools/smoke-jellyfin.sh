#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:10.11.11@sha256:aefb67e6a7ff1debdd154a78a7bbb780fd0c873d8639210a7f6a2016ad2b35db}"
archive="${1:-$(find "$root/artifacts/package" -maxdepth 1 -name 'aether-analysis-*.zip' -print -quit)}"
container="aether-jellyfin-smoke-${RANDOM}-${RANDOM}"
scratch="$(mktemp -d)"

cleanup() {
  status=$?
  trap - EXIT
  docker rm -f "$container" >/dev/null 2>&1 || true
  if [[ -d "$scratch" ]]; then
    docker run --rm --user 0:0 --entrypoint /bin/sh \
      --volume "$scratch:/scratch" "$image" -c 'rm -rf /scratch/*' >/dev/null 2>&1 || true
    rm -rf "$scratch" >/dev/null 2>&1 || true
  fi
  exit "$status"
}
trap cleanup EXIT

fail_with_logs() {
  docker logs "$container" >&2 || true
  echo "$1" >&2
  exit 1
}

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
ready=false
for _ in $(seq 1 60); do
  if curl --connect-timeout 2 --max-time 5 --fail --silent \
    "http://127.0.0.1:${port}/System/Info/Public" > "$scratch/system-info.json"; then
    ready=true
    break
  fi
  if ! docker inspect --format '{{.State.Running}}' "$container" | grep -q true; then
    fail_with_logs "Jellyfin stopped before its public system endpoint became ready."
  fi
  sleep 2
done
if [[ "$ready" != "true" ]]; then
  fail_with_logs "Jellyfin did not become ready within 120 seconds."
fi

if ! jq -e '(.Version // .version) == "10.11.11"' "$scratch/system-info.json" >/dev/null; then
  fail_with_logs "The smoke container is not running Jellyfin 10.11.11."
fi
capabilities_status="$(curl --connect-timeout 2 --max-time 5 --silent \
  --output /dev/null --write-out '%{http_code}' \
  "http://127.0.0.1:${port}/AetherAnalysis/v1/capabilities")"
if [[ "$capabilities_status" != "401" ]]; then
  fail_with_logs "Expected the unauthenticated plugin endpoint to return 401, got $capabilities_status."
fi

if ! curl --connect-timeout 2 --max-time 5 --fail --silent \
  "http://127.0.0.1:${port}/Startup/User" > "$scratch/startup-user.json"; then
  fail_with_logs "Jellyfin rejected the startup-user request."
fi
username="$(jq -r '.Name' "$scratch/startup-user.json")"
password="$(jq -r '.Password // ""' "$scratch/startup-user.json")"
if ! curl --connect-timeout 2 --max-time 5 --fail --silent \
  --request POST \
  "http://127.0.0.1:${port}/Startup/Complete" >/dev/null; then
  fail_with_logs "Jellyfin rejected startup completion."
fi
auth_identity='MediaBrowser Client="AETHER%20Smoke", DeviceId="aether-ci", Device="CI", Version="0.1.0"'
jq -n --arg username "$username" --arg password "$password" \
  '{Username: $username, Pw: $password}' > "$scratch/auth-request.json"
if ! curl --connect-timeout 2 --max-time 5 --fail --silent \
  --request POST \
  --header "Authorization: $auth_identity" \
  --header 'Content-Type: application/json' \
  --data-binary "@$scratch/auth-request.json" \
  "http://127.0.0.1:${port}/Users/AuthenticateByName" > "$scratch/auth-response.json"; then
  fail_with_logs "Jellyfin rejected smoke-test authentication."
fi
access_token="$(jq -r '.AccessToken' "$scratch/auth-response.json")"
if [[ -z "$access_token" || "$access_token" == "null" ]]; then
  echo "Jellyfin startup completed but did not issue a smoke-test token." >&2
  exit 1
fi
auth_header="$auth_identity, Token=$access_token"
if ! curl --connect-timeout 2 --max-time 5 --fail --silent \
  --header "Authorization: $auth_header" \
  "http://127.0.0.1:${port}/AetherAnalysis/v1/capabilities" > "$scratch/capabilities.json"; then
  fail_with_logs "The authenticated AETHER capabilities endpoint failed."
fi
if ! jq -e '.apiVersion == "1.0" and (.supportedAnalysisSchemas | index(2)) != null' \
  "$scratch/capabilities.json" >/dev/null; then
  fail_with_logs "The AETHER capabilities response did not match the client contract."
fi
if ! curl --connect-timeout 2 --max-time 5 --fail --silent \
  --header "Authorization: $auth_header" \
  "http://127.0.0.1:${port}/AetherAnalysis/v1/status" > "$scratch/plugin-status.json"; then
  fail_with_logs "The authenticated AETHER status endpoint failed."
fi
if ! jq -e '.service == "ready" and .databaseSchemaVersion == 2 and .recordCount == 0' \
  "$scratch/plugin-status.json" >/dev/null; then
  fail_with_logs "The initial AETHER storage status was not ready and empty."
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
    --header "Authorization: $auth_header" \
    --output /dev/null --write-out '%{http_code}' \
    "http://127.0.0.1:${port}/AetherAnalysis/v1/capabilities" || true)"
  [[ "$restarted_status" == "200" ]] && break
  sleep 2
done
if [[ "${restarted_status:-}" != "200" ]]; then
  fail_with_logs "Plugin endpoint did not recover after a Jellyfin restart."
fi

curl --connect-timeout 2 --max-time 5 --fail --silent \
  --header "Authorization: $auth_header" \
  "http://127.0.0.1:${port}/AetherAnalysis/v1/status" \
  | jq -e '.service == "ready" and .databaseSchemaVersion == 2' >/dev/null

echo "Jellyfin 10.11.11 authenticated the final AETHER archive, initialized storage and survived restart."
