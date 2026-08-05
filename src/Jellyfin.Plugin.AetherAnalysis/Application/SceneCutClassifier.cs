namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>
/// Turns an already-stored <c>sceneCutProbability</c> time series into discrete hard-cut
/// timestamps, for use as Jellyfin chapter markers. Ported from the hard-cut branch of the
/// desktop client's <c>classifyCutKind</c> (apps/desktop-web/src/analysis/director-controller.ts,
/// ADR-024, calibrated 2026-08-04: threshold 0.63, measured against ~35 real transitions).
///
/// Only the HARD-cut rule is ported — a single frame's probability at/above the threshold. The
/// client's sliding window is only needed to additionally classify FADES (a streak of frames held
/// in a middle band); this feature deliberately only marks hard cuts (see plan: fades are noisier
/// and would clutter a chapter list), so no window state is needed here at all.
/// </summary>
public static class SceneCutClassifier
{
    /// <summary>Matches the client's calibrated hard-cut threshold exactly — see class docs.</summary>
    private const double HardCutThreshold = 0.63;

    /// <summary>
    /// Minimum spacing enforced between emitted chapter marks (and between the file's start and
    /// the first one). Not part of the client's per-frame classifier: a real cut occasionally
    /// spikes across two adjacent sampled frames (compression artifacts, re-encoding), which would
    /// otherwise emit two chapters a fraction of a second apart. A mark within the first second is
    /// also suppressed — Jellyfin already starts playback at 0, so a "Scene 1" a few hundred
    /// milliseconds in adds nothing.
    /// </summary>
    private const long MinGapMs = 1000;

    /// <summary>
    /// Finds hard-cut timestamps in <paramref name="frames"/>. Frames must be ordered ascending by
    /// timestamp — the caller (reading from a stored analysis document) is responsible for that.
    /// </summary>
    public static IReadOnlyList<long> FindHardCutTimestampsMs(
        IReadOnlyList<(long TimestampMs, double SceneCutProbability)> frames)
    {
        var result = new List<long>();
        var lastEmittedMs = 0L;
        foreach (var frame in frames)
        {
            if (frame.SceneCutProbability < HardCutThreshold)
            {
                continue;
            }

            if (frame.TimestampMs - lastEmittedMs < MinGapMs)
            {
                continue;
            }

            result.Add(frame.TimestampMs);
            lastEmittedMs = frame.TimestampMs;
        }

        return result;
    }
}
