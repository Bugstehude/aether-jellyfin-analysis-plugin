"""Offline F64 record probe and size arithmetic from Aether measurement artifacts."""

import argparse
import json
import math
import struct
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--artifacts", type=Path, required=True)
args = parser.parse_args()
artifact = json.loads((args.artifacts / "measured-2s-audio-only.json").read_text())
rows = artifact["audio"]["denseAudioFrames"]
layout = struct.Struct("<IiHB5d")
assert layout.size == 51
for point in rows:
    bands = point.get("bands")
    values = [
        point["rmsLinear"],
        point["spectralFluxLinear"],
        *(bands[name] if bands else 0.0 for name in ("bassRms", "midRms", "trebleRms")),
    ]
    assert all(math.isfinite(v) and v >= 0 for v in values)
    encoded = layout.pack(
        point["timestampMs"], point["windowStartSample"], point["validSamples"],
        int(bands is not None), *values,
    )
    decoded = layout.unpack(encoded)
    assert decoded[:4] == (
        point["timestampMs"], point["windowStartSample"], point["validSamples"],
        int(bands is not None),
    )
    assert all(struct.pack("<d", a) == struct.pack("<d", b) for a, b in zip(values, decoded[4:]))

metrics = json.loads((args.artifacts / "all-metrics.json").read_text())["results"]
estimates = []
for record in metrics:
    count = record["counts"]["denseAudioFrames"]
    packed = record["sizes"]["packedProposal"]
    assert packed["pointCount"] == count and packed["binaryBytes"] == 31 * count
    # Same header string lengths, same row count, checksum length and unchanged other JSON.
    estimate = packed["rawBytes"] + 4 * (math.ceil(51 * count / 3) - math.ceil(31 * count / 3))
    estimates.append({
        "durationMs": record["fixture"]["durationMs"],
        "repeat": record["repeat"],
        "pointCount": count,
        "f64DocumentBytesEstimate": estimate,
        "belowCurrent50MiBCeiling": estimate <= record["pluginCeilingBytes"],
    })
print(json.dumps({
    "kind": "offline-f64-record-probe-not-production",
    "strideBytes": layout.size,
    "verifiedRealFixtureRows": len(rows),
    "f64RoundTripBitExact": True,
    "estimates": estimates,
    "limitations": [
        "No full F64 worker run or RSS benchmark",
        "Size arithmetic assumes unchanged JSON header lengths and other fields",
        "No legacy audio included in measured source documents",
        "No plugin, TypeScript or Swift decoder integration tested",
    ],
}, indent=2))
