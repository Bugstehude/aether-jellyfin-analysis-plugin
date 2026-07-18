#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
expected_file="$root/contracts/contract.sha256"

hash_stream() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | cut -d ' ' -f 1
  else
    shasum -a 256 | cut -d ' ' -f 1
  fi
}

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

actual="$({
  hash_file "$root/contracts/openapi/aether-analysis-v1.yaml"
  find "$root/contracts/schemas" -type f -name '*.json' -print \
    | LC_ALL=C sort \
    | while IFS= read -r file; do hash_file "$file"; done
} | hash_stream)"

if [[ "${1:-}" == "--check" ]]; then
  expected="$(tr -d '[:space:]' < "$expected_file")"
  if [[ "$actual" != "$expected" ]]; then
    echo "Contract hash is stale. Expected $expected, calculated $actual." >&2
    exit 1
  fi
  echo "$actual"
  exit 0
fi

echo "$actual"
