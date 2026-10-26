#!/usr/bin/env python3
"""Verify that the install archive contains the workers described by source and SBOM."""
from __future__ import annotations

import hashlib
import json
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = (
    "aether-analysis-worker.cjs",
    "aether-analysis-1.2-worker.cjs",
    "analysis-1.2-worker-manifest.json",
)


def main() -> None:
    archive, sbom = map(Path, sys.argv[1:])
    components = json.loads(sbom.read_text(encoding="utf-8"))["components"]
    with zipfile.ZipFile(archive) as package:
        expected_names = sorted(("Jellyfin.Plugin.AetherAnalysis.dll", *ASSETS))
        if sorted(package.namelist()) != expected_names:
            raise SystemExit("Install archive has missing, duplicate or unexpected files")
        for name in ASSETS:
            actual = hashlib.sha256(package.read(name)).hexdigest()
            source = hashlib.sha256((ROOT / "worker" / name).read_bytes()).hexdigest()
            matches = [value for value in components if value["name"] == name]
            if len(matches) != 1:
                raise SystemExit(f"SBOM must identify exactly one {name}")
            hashes = {value["alg"]: value["content"] for value in matches[0].get("hashes", [])}
            if actual != source or hashes.get("SHA-256") != actual:
                raise SystemExit(f"Archive, source and SBOM disagree for {name}")
    print("Install archive matches canonical worker assets and SBOM")


if __name__ == "__main__":
    main()
