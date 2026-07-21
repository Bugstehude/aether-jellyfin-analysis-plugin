#!/usr/bin/env bash
# Rebuilds the AETHER server-analysis worker bundle and vendors it into this repo.
#
# The plugin runs the SHARED perception-engine (option b) by shelling out to a
# single bundled JS file. That bundle is produced in the AETHER monorepo and
# checked in here at worker/aether-analysis-worker.cjs. Run this whenever the
# analysis algorithm changes, then bump the plugin version (and, if the algorithm
# itself changed, AetherAlgorithm.Version + capabilities + the client in lockstep).
#
#   AETHER_REPO=/path/to/AETHER_Codex_Starter_Kit tools/vendor-worker.sh
#   # or: tools/vendor-worker.sh /path/to/AETHER_Codex_Starter_Kit
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
aether_repo="${1:-${AETHER_REPO:-}}"

if [[ -z "$aether_repo" ]]; then
  echo "Usage: AETHER_REPO=/path/to/AETHER_Codex_Starter_Kit tools/vendor-worker.sh" >&2
  exit 1
fi
if [[ ! -d "$aether_repo/packages/server-analysis-worker" ]]; then
  echo "Not an AETHER repo (no packages/server-analysis-worker): $aether_repo" >&2
  exit 1
fi

echo "Building worker bundle in $aether_repo …"
( cd "$aether_repo" && pnpm --filter @aether/server-analysis-worker build:plugin )

src="$aether_repo/packages/server-analysis-worker/dist/aether-analysis-worker.cjs"
dst="$root/worker/aether-analysis-worker.cjs"
mkdir -p "$root/worker"
cp "$src" "$dst"
echo "Vendored $(wc -c < "$dst") bytes -> worker/aether-analysis-worker.cjs"
