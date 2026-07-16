# ADR 0003: Multi-resolution analysis delivery

- Status: Accepted
- Date: 2026-07-16

## Context

A detailed master analysis improves scene-cut localization, motion curves and color sampling, but
mobile XR clients should not pay its full transfer, parsing, memory and lookup cost. Persisting
decoded frames is explicitly out of scope.

## Decision

The plugin stores one validated master analysis and exposes deterministic representations through
the `detail` query parameter:

| Detail | Maximum cadence | Intended client |
| --- | --- | --- |
| `compact` | 1 sample/s | Quest and constrained clients |
| `balanced` | 4 samples/s | Default Desktop/Web |
| `full` | Uploaded master cadence, at most contract limits | Authoring and diagnostics |

The server never invents temporal detail. If the master is sparser than a requested level, it
returns the master cadence and reports it in `sampling.intervalMs`.

Downsampling is deterministic per output bucket:

- arithmetic mean for luminance, contrast, saturation, motion energy and audio raw features;
- maximum for `sceneCutProbability`, preserving short cuts;
- palette entries are merged by quantized RGB cell, summed by coverage and the five highest
  normalized coverages are returned;
- output `timestampMs` is the first source timestamp in the bucket.

Each representation has a distinct strong ETag derived from master content hash, detail level and
representation algorithm version. `If-None-Match` is evaluated against that representation.

## Consequences

- One accurate analysis serves clients with different budgets.
- Compact delivery improves network, parse, memory and lookup cost, while avoiding live decoding
  remains the dominant XR performance benefit.
- Representations may be generated lazily and cached; they are evicted before the master.
- Changing the deterministic reduction algorithm changes its version and therefore every derived
  ETag, without changing upload schema 2.
