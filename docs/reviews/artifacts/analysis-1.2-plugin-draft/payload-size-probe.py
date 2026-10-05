"""Illustrative dense-array byte estimate, not a real worker/RSS measurement."""

import json
from pathlib import Path

example = json.loads(Path(__file__).with_name("available.json").read_text())
for minutes in (90, 120):
    sample_count = minutes * 60 * 22050
    total_bytes = 2  # Array brackets. Document, video, legacy audio, events excluded.
    for index in range(minutes * 60 * 50):
        point = dict(example["denseAudioFrames"][index % 5])
        start = -1023 + index * 441
        point.update(
            timestampMs=index * 20,
            windowStartSample=start,
            validSamples=min(start + 2048, sample_count) - max(start, 0),
        )
        total_bytes += len(json.dumps(point, separators=(",", ":")).encode())
        total_bytes += int(index > 0)
    print(f"{minutes} min dense array: {total_bytes} bytes ({total_bytes / 1024**2:.2f} MiB)")
