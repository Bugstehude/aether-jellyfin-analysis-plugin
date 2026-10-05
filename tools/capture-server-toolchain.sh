#!/usr/bin/env bash
# Records the FFmpeg/FFprobe a Jellyfin server actually hands to the analysis worker and,
# optionally, runs the Aether video regression with exactly these binaries.
#
# Run on the Jellyfin host (or inside its container), not on a development machine:
#   tools/capture-server-toolchain.sh OUTPUT_DIRECTORY [AETHER_CHECKOUT]
#
# Paths come from Jellyfin's encoding.xml (JELLYFIN_CONFIG_DIR, default /etc/jellyfin or
# /config/config) unless AETHER_FFMPEG/AETHER_FFPROBE are set explicitly. Nothing is
# installed, upgraded or written outside OUTPUT_DIRECTORY. No media file is opened.
set -euo pipefail

output="${1:?Usage: tools/capture-server-toolchain.sh OUTPUT_DIRECTORY [AETHER_CHECKOUT]}"
aether="${2:-}"
mkdir -p "$output"

config_value() {
  local file="$1" key="$2"
  sed -n "s:.*<$key>\(.*\)</$key>.*:\1:p" "$file" | head -n 1
}

encoding=""
for candidate in "${JELLYFIN_CONFIG_DIR:-}" /etc/jellyfin /config/config /config; do
  if [[ -n "$candidate" && -f "$candidate/encoding.xml" ]]; then
    encoding="$candidate/encoding.xml"
    break
  fi
done

ffmpeg="${AETHER_FFMPEG:-}"
if [[ -z "$ffmpeg" && -n "$encoding" ]]; then
  ffmpeg="$(config_value "$encoding" EncoderAppPathDisplay)"
  [[ -n "$ffmpeg" ]] || ffmpeg="$(config_value "$encoding" EncoderAppPath)"
fi
if [[ -z "$ffmpeg" ]]; then
  echo "FFmpeg path unknown: set AETHER_FFMPEG or JELLYFIN_CONFIG_DIR." >&2
  exit 1
fi
# Jellyfin resolves ffprobe beside the configured ffmpeg.
ffprobe="${AETHER_FFPROBE:-$(dirname "$ffmpeg")/ffprobe}"

describe() {
  local name="$1" path="$2"
  {
    echo "name=$name"
    echo "configured=$path"
    echo "resolved=$(readlink -f "$path" 2>/dev/null || echo "$path")"
    if command -v sha256sum >/dev/null 2>&1; then
      echo "sha256=$(sha256sum "$path" | cut -d' ' -f1)"
    else
      echo "sha256=$(shasum -a 256 "$path" | cut -d' ' -f1)"
    fi
    echo "--- $name -version"
    "$path" -version
  } >"$output/$name-version.txt" 2>&1
}

describe ffmpeg "$ffmpeg"
describe ffprobe "$ffprobe"
{
  echo "date=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host=$(uname -srm)"
  echo "encodingXml=${encoding:-not-found}"
  echo "ffmpeg=$(grep -m1 '^ffmpeg version' "$output/ffmpeg-version.txt" || echo unavailable)"
  echo "ffprobe=$(grep -m1 '^ffprobe version' "$output/ffprobe-version.txt" || echo unavailable)"
} | tee "$output/toolchain.txt"

if [[ -z "$aether" ]]; then
  echo "No Aether checkout given: video regression not run." | tee -a "$output/toolchain.txt"
  exit 0
fi

status=0
(
  cd "$aether"
  AETHER_FFMPEG="$ffmpeg" AETHER_FFPROBE="$ffprobe" pnpm exec vitest run \
    packages/server-analysis-worker/src/draft-video.test.ts \
    --reporter=default --reporter=json --outputFile="$output/video-regression.json"
) >"$output/video-regression.log" 2>&1 || status=$?
echo "videoRegressionExit=$status" | tee -a "$output/toolchain.txt"
# A skipped real-FFmpeg block is not a pass: report it explicitly.
if grep -q "skipped" "$output/video-regression.log"; then
  echo "videoRegressionNote=contains skipped tests, check video-regression.log" | tee -a "$output/toolchain.txt"
fi
exit "$status"
