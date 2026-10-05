# Implementation status

This document distinguishes committed behavior from accepted design. It is updated with every
implementation milestone so consumers never infer features from the architecture document alone.

## Unreleased analysis version contract

- Explicit read compatibility between `aether-visual/1.0.0` and `1.1.0` in both directions.
- Opt-in metadata-only compatible batch selection, actual algorithm identity in available status,
  unchanged exact GET/HEAD/PUT/DELETE semantics and negotiated capability matrix.
- Routine upgrades of older analyses, with validated compatible reads during the upgrade.
- Source-scoped server jobs and explicit `recalculate=true`, protected upgrade sources and
  optimistic target checks against concurrent uploads.
- No change to schema v2 or current measurement version `1.1.0`.
- AetherTV reports local 1.0/1.1 negotiation and mock acceptance in both directions.
  Live Jellyfin/client acceptance and release pins remain open. Aether's production
  negotiation integration is still to be confirmed from
  [the handover](reviews/2026-10-03-analysis-version-client-handover.md).
- Production rollout of the shared-worker timing/event/audio improvements, planned measurement
  version `1.2.0`, and selective reuse/enrichment remain follow-up work.

## Offline 1.2 storage preparation, 2026-10-04

- [Consolidated decisions](reviews/2026-10-04-analysis-1.2-consolidated-decisions.md)
  select a 51-byte Packed-F64 draft for the next experiment. TV keeps its 32 MiB limit
  and Balanced default, while the plugin keeps its configured ceiling of at most 50 MiB.
- [The current field/wire contract](analysis-1.2-contract.md) is ready for isolated client
  adaptation. It does not enable production 1.2 or any new read-compatibility direction.
- Isolated F64 record codec, contextual v2 Full-component validation, master composition and
  independent audio selection. Private staging captures host identity and actual source/target
  ETags, pins old records, and atomically commits only complete validated compositions.
  Available audio requires complete contiguous zero-origin measurements and proven legacy
  track/time equivalence. Proven complete no-track is accepted without fabricated audio data.
  Unproven origins, audio offsets, PCM gaps and partial audio remain rejected. Group reuse is open.
- 306 passing plugin tests including three real offline shared-worker runs and 60 added
  process, mapping, resolver, pipeline and shared-gate checks. The previous 246-test composition
  checkpoint includes 53 context cases, 21 profiles and 12 wire cases.
  Releasebuild and format checks pass. No production route/job switch, advertisement or vendor update.
- [Legacy correction and profile results](reviews/2026-10-04-analysis-1.2-legacy-audio-fix-results.md)
  retain the complete independent legacy audio series in all three details. Direct Desktop and
  native Swift imports are diagnosis-free, including Compact cut coverage through the original
  source anchor. Measured silence, EOF bursts, counterphase, multichannel, second-default-track,
  VFR, video offset, PCM-tail and pending-cut profiles are checked.
  [Both client fixture follow-ups are accepted](reviews/2026-10-04-analysis-1.2-client-fixture-acceptance.md),
  with matching bytes and unchanged readers. No further fixture correction is required.
  [Host/worker integration](reviews/2026-10-04-analysis-1.2-host-worker-integration-results.md)
  is registered behind a disabled experimental gate with no API/routine caller. Fresh host
  metadata and bounded FFprobe verify global audio indices; a privately pinned standalone
  bundle produces both components. Existing records are protected before probing, concurrent
  uploads win, and host metadata is rechecked across all stages. Production and experiment share
  one worker execution lease. Legacy Jellyfin index is optional but checked when present.
  Native player track mapping, long-source plugin RSS, production integration and 1.2 read directions remain open.
- Aether reports 142 targeted tests for the final fixture correction and TV 618 Swift tests.
  These client suites were not rerun here. Earlier 1943 Aether tests, TV app builds and 18 parser
  tests remain historical results. Earlier long runs fit two-hour Balanced within 32 MiB,
  while Full exceeds it.
  Previous startup/RSS runs remain historical measurements of their pinned sources.
  Live and hardware gates stay open.
- [TV playback-session integration](../../aether-tv/docs/reviews/2026-10-04-tv-playback-signal-integration.md)
  additionally reports 627 Swift tests, 18 parser tests and both simulator builds. Media-time
  session epochs handle seek/pause/track/analysis changes and independent audio ticks. Jellyfin
  indices are retained, but native track proof and new DSP/effects remain unbound and gated off.
  The 23 source pins were checked here; those client suites were not rerun.
- [Both host-contract adapter returns are accepted](reviews/2026-10-04-analysis-1.2-contract-adaptation-acceptance.md).
  All nine real offline host documents match both client copies. Desktop reports 1979 passing
  tests and 15 skipped, with typecheck/build passing. TV's pinned log confirms 638 core tests,
  with no new app/parser/visual or long runs. Source and fixture pins match, including 22 plugin
  sources, ten Desktop sources and 27 TV sources. These suites were not rerun here.
  Desktop adds isolated legacy track/time/window guards and a local effect prototype.
  The original 2000 ms legacy-tail silence is measured, not a nominal padded slot.
  Field/wire adaptation is complete for these inputs. Historical-reader semantic coverage,
  native heard-track proof, production negotiation, effects and device/resource acceptance remain open.
- [Experimental routine migration](reviews/2026-10-05-analysis-1.2-routine-migration-results.md)
  adds direct stored-Full validation and a read-only migration planner. `AnalyzeIfNeededAsync`
  keeps a current exact target after fresh host/probe/profile validation and final ETag checks.
  Qualified EOF-pending cuts and proven no-track satisfy this experimental target. Old analyses
  or invalid targets require fresh audio/video components, with existing records protected.
  Explicit `AnalyzeAsync` still recalculates. No production API/scheduler caller or activation.
  368 Release tests pass with no skips, including three native offline jobs and their no-op checks.
  Format, build, contract hash and Diffcheck pass. Original wire/fixtures/bundle remain unchanged.
  Selective group reuse, persistent resume and general retry backoff remain open.
  The TV tail addendum's 184 pins and five-test log are accepted without rerunning its suite.
- [The rollout plan](reviews/2026-10-05-analysis-1.2-synchronized-rollout-plan.md) now follows
  Marc's simultaneous client update. New readers must handle 1.2 and actual legacy 1.0/1.1
  results. General old-reader acceptance of stored 1.2 is no longer required for this rollout.
  Active production version/matrix/gates remain unchanged until the joint release acceptance.
- [TV's synchronized rollout preparation is accepted](reviews/2026-10-05-analysis-1.2-tv-rollout-preparation-acceptance.md).
  Internal mock policy covers a new 1.2 reader selecting stored 1.2/1.1/1.0 with actual
  identity in query, GET/HEAD and raw cache. Public initialization still uses only 1.0/1.1.
  All source/input/archive pins and 77 historical files match. The pinned log confirms
  48 targeted tests, including 15 new cases, with zero failures. This is not a full-suite
  rerun or a live acceptance. Legacy contract examples and synthetic audio variants provide
  scoped fallback coverage. Aether's corresponding rollout return remains pending.
  No further TV negotiation/codec correction is requested. Native/live/device gates remain open.
- [Both synchronized reader preparations are accepted](reviews/2026-10-05-analysis-1.2-synchronized-reader-acceptance.md).
  Aether reports 2008 passing tests with 17 standard skips and an overlapping targeted run
  of 93 cases, including the externally configured OpenAPI checks. These suites were not
  rerun here. The new remote cache preserves actual identity and remains separate from the
  old local cache. Production gate is off and the main player still lacks the new replay wiring.
  The reported hash discrepancy compares the OpenAPI/schema bundle hash with the separate
  version-document file hash. Both are current and unchanged. Historical manifests are retained.
  The next isolated Aether task covers actual player integration, shared cancellation/deadline
  and aggregate remote cache bounds. Native/live/resource/device release gates remain open.
- [Aether's isolated main-player integration is accepted](reviews/2026-10-05-analysis-1.2-player-integration-acceptance.md).
  The real controller, Director and conductor planner are covered by six integration cases
  with synthetic transport/track evidence. The renderer hook is wired and built, not run.
  A shared 30-second remote deadline and cancellation cover all read/cache phases.
  Atomic LRU limits the separate remote cache to 128 MiB logical payload and 32 entries,
  including keys/ETags in the 32 MiB entry limit. These are not physical RAM/storage limits.
  All 19 current client sources and pinned raw evidence match. Vitest JSON confirms 2039
  repository passes with 17 skips and 124 overlapping focused passes with no skips/failures.
  Both client gates remain false. No suites were rerun here. The next separate Aether/TV
  tasks address actual selected source and heard-track evidence; native/live/resource/device
  acceptance remains required before release.
- [Source/session binding review](reviews/2026-10-05-analysis-1.2-source-track-binding-review.md)
  accepts the Desktop's local request context, explicit source choice and stale callback guards.
  Pinned Vitest JSON confirms 2071 repository passes with 17 skips and 164 overlapping focused
  passes with no failures. Current HTMLVideo/WKWebView main-player source/track proof is unknown,
  so new cuts and audio stay closed while baseline/legacy remain available. TV's native adapter
  adds source/session/attachment guards but lacks current fingerprint, delivered response-form
  and native-to-host crosswalk receipts. Its local scope is distinct from heard-track acceptance.
  A native Desktop player is not a schema requirement or an approved architectural change.
  The next investigation starts with evidence available in the existing browser path.
  Native/live/resource/device and final release gates remain open.
- Marc confirmed the supported playback scope is Direct Play with exactly one audio stream.
  [The current playback scope](analysis-1.2-playback-scope.md) replaces blanket native
  source/heard-track gating for that case. A matching current source/fingerprint,
  original Directplay timeline and unique source audio reference qualify the existing
  browser/native player route without a new Desktop player or multi-track crosswalk.
  Both clients still need the scoped adaptation behind closed gates. Source/session guards,
  valid methods/coverage and fallback for unsupported source/track/transcode cases remain.
  The intermediate Browser/native feasibility task is superseded. Live/resource/device
  and release acceptance remain separate.

## Compatibility update 0.3.0.0

- Exact Jellyfin 12.0.0, .NET 10 and EF Core 10.0.11 pins.
- Fresh-install, authentication, database initialization and restart smoke test on x64.
- Client API and stored analysis format unchanged; see [compatibility.md](compatibility.md).

## Foundation implemented through 0.2.4.0

- Server-owned SQLite runtime boundary plus patched, isolated native SQLite test runtime.
- Canonical OpenAPI, schema version 2 JSON Schemas and Golden Files.
- Native Jellyfin authentication with item visibility checks and non-leaking 404 responses.
- Administrator or explicit analyzer-user uploads; administrator-only deletion.
- Concrete media-source identity and server fingerprinting without exposing media paths.
- Plugin-owned EF Core SQLite database; no raw SQL and no Jellyfin database tables.
- Bounded schema/cross-field validation and 50 MiB default upload limit.
- Brotli level 5 storage and strong content ETags.
- Deterministic `compact`, `balanced` and `full` response representations.
- Batch status, explicit batch delete, storage-status endpoint and positive origin allowlisting.
- Jellyfin dashboard configuration for 10 GiB default capacity, retention and browser origins.
- Hard rejection before an upload would exceed configured capacity.
- Serialized capacity-check/commit section so concurrent uploads cannot bypass the hard ceiling.
- EF Core migration baseline with lossless adoption of 0.1 development databases.
- Scheduled, manual and upload-time retention/LRU cleanup with persisted status.
- Damped access-time updates to avoid a database write on every playback request.
- Metadata-only batch status plus metadata-only HEAD and conditional 304 responses.
- Absolute ASP.NET request-size limit and defensive null/identity/detail validation.
- Corrupt-record isolation plus non-sensitive process-local failure counters in admin status.
- Unit/Golden-File tests, NuGet vulnerability gate and public-repository CI.
- Deterministic DLL-and-worker archive and digest-pinned Jellyfin start/restart smoke harness.
- Reproducible Jellyfin catalog manifest tied to the versioned GitHub release archive.
- Optional serialized server analysis through scheduled, post-scan and authorized API triggers.
- Server-wide voice recordings and one range-enabled journey audio track with bounded storage.
- Server-wide measured device-profile catalog (`aether.device-quality-profile` v1) under
  `/device-profiles`, keyed by client/ladder/device class, EF Core-backed, last-write-wins.

## Accepted but not yet implemented

- Orphan cleanup for items or media sources removed from Jellyfin.
- External importer for the transitional AETHER sidecar.
- Generated TypeScript client package and automated consumer synchronization releases.
- Target-LXC install/upgrade/uninstall acceptance, backup test and Quest 3S benchmark.

Folder and multi-item selection remain AETHER client concerns. The optional server runner is
described by ADR 0005; with server analysis disabled, the plugin remains a storage-only service.

Version 0.3.0.0 remains a test release. Fresh installation, authenticated API access, storage
initialization and restart pass against Jellyfin 12.0.0 on local x64 Docker and x64 CI. It must
not be treated as production-ready until target-LXC upgrade and uninstall acceptance pass.
