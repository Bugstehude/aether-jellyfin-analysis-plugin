#!/usr/bin/env python3
"""Generate a deterministic CycloneDX SBOM from the production NuGet lockfile."""

from __future__ import annotations

import base64
import hashlib
import json
import re
import sys
from pathlib import Path
from urllib.parse import quote


ROOT = Path(__file__).resolve().parent.parent
LOCKFILE = ROOT / "src/Jellyfin.Plugin.AetherAnalysis/packages.lock.json"
BUILD_MANIFEST = ROOT / "build.yaml"
WORKERS = (
    ROOT / "worker/aether-analysis-worker.cjs",
    ROOT / "worker/aether-analysis-1.2-worker.cjs",
)
WORKER_MANIFEST = ROOT / "worker/analysis-1.2-worker-manifest.json"


def main() -> None:
    output = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "artifacts/package/aether-analysis.cdx.json"
    version_match = re.search(r'^version: "([0-9.]+)"$', BUILD_MANIFEST.read_text(), re.MULTILINE)
    if version_match is None:
        raise SystemExit("Unable to read plugin version from build.yaml")

    lock = json.loads(LOCKFILE.read_text())
    dependencies = lock["dependencies"]["net10.0"]
    components: list[dict[str, object]] = []
    for name, package in sorted(dependencies.items(), key=lambda item: item[0].lower()):
        resolved = package.get("resolved")
        content_hash = package.get("contentHash")
        if not isinstance(resolved, str) or not isinstance(content_hash, str):
            continue
        component: dict[str, object] = {
            "type": "library",
            "bom-ref": f"pkg:nuget/{quote(name)}@{quote(resolved)}",
            "name": name,
            "version": resolved,
            "purl": f"pkg:nuget/{quote(name)}@{quote(resolved)}",
            "scope": "required",
            "properties": [
                {"name": "aether:asset-boundary", "value": "host-provided; not embedded in plugin archive"}
            ],
        }
        try:
            component["hashes"] = [
                {"alg": "SHA-512", "content": base64.b64decode(content_hash).hex()}
            ]
        except (ValueError, TypeError):
            pass
        components.append(component)

    for worker in WORKERS:
        if not worker.is_file():
            raise SystemExit(f"Vendored worker is missing: {worker.relative_to(ROOT)}")
        worker_hash = hashlib.sha256(worker.read_bytes()).hexdigest()
        components.append(
            {
                "type": "file",
                "bom-ref": f"file:{worker.name}?sha256={worker_hash}",
                "name": worker.name,
                "hashes": [{"alg": "SHA-256", "content": worker_hash}],
                "scope": "required",
                "properties": [
                    {
                        "name": "aether:asset-boundary",
                        "value": "vendored executable worker; embedded in plugin archive",
                    }
                ],
            }
        )

    if not WORKER_MANIFEST.is_file():
        raise SystemExit(f"Worker pin manifest is missing: {WORKER_MANIFEST.relative_to(ROOT)}")
    try:
        pin_manifest = json.loads(WORKER_MANIFEST.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise SystemExit(f"Worker pin manifest is invalid JSON: {error}") from error
    pinned_worker_hash = pin_manifest.get("workerSha256")
    actual_worker_hash = hashlib.sha256(WORKERS[1].read_bytes()).hexdigest()
    if pin_manifest.get("workerPath") != WORKERS[1].name or pinned_worker_hash != actual_worker_hash:
        raise SystemExit("1.2 worker bundle does not match its runtime pin manifest")
    if pin_manifest.get("algorithm") != "aether-visual/1.2.0":
        raise SystemExit("1.2 worker pin manifest has an unexpected algorithm version")
    manifest_hash = hashlib.sha256(WORKER_MANIFEST.read_bytes()).hexdigest()
    components.append(
        {
            "type": "file",
            "bom-ref": f"file:{WORKER_MANIFEST.name}?sha256={manifest_hash}",
            "name": WORKER_MANIFEST.name,
            "hashes": [{"alg": "SHA-256", "content": manifest_hash}],
            "scope": "required",
            "properties": [
                {
                    "name": "aether:asset-boundary",
                    "value": "runtime source and dependency pins; embedded in plugin archive",
                }
            ],
        }
    )

    version = version_match.group(1)
    document = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "version": 1,
        "metadata": {
            "component": {
                "type": "application",
                "bom-ref": f"pkg:generic/aether-analysis@{version}",
                "name": "AETHER Analysis for Jellyfin",
                "version": version,
            },
            "properties": [
                {"name": "aether:target-jellyfin", "value": "12.0.0"},
                {
                    "name": "aether:archive-contents",
                    "value": "Jellyfin.Plugin.AetherAnalysis.dll,aether-analysis-worker.cjs,aether-analysis-1.2-worker.cjs,analysis-1.2-worker-manifest.json",
                },
            ],
        },
        "components": components,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n")


if __name__ == "__main__":
    main()
