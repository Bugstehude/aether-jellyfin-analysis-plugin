using Jellyfin.Plugin.AetherAnalysis.Application;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class SceneCutClassifierTests
{
    [Fact]
    public void NoFrameAboveThresholdProducesNoCuts()
    {
        var frames = new (long, double)[]
        {
            (0, 0.1), (500, 0.3), (1000, 0.5), (1500, 0.2),
        };

        var cuts = SceneCutClassifier.FindHardCutTimestampsMs(frames);

        Assert.Empty(cuts);
    }

    [Fact]
    public void SingleClearSpikeAboveThresholdIsReported()
    {
        var frames = new (long, double)[]
        {
            (0, 0.1), (2000, 0.1), (4000, 0.9), (6000, 0.1),
        };

        var cuts = SceneCutClassifier.FindHardCutTimestampsMs(frames);

        var cut = Assert.Single(cuts);
        Assert.Equal(4000, cut);
    }

    [Fact]
    public void ValueExactlyAtThresholdCounts()
    {
        var frames = new (long, double)[] { (5000, 0.63) };

        var cuts = SceneCutClassifier.FindHardCutTimestampsMs(frames);

        Assert.Equal([5000L], cuts);
    }

    [Fact]
    public void ValueJustBelowThresholdDoesNotCount()
    {
        var frames = new (long, double)[] { (5000, 0.6299999) };

        var cuts = SceneCutClassifier.FindHardCutTimestampsMs(frames);

        Assert.Empty(cuts);
    }

    [Fact]
    public void MultipleWellSeparatedSpikesAreAllReported()
    {
        var frames = new (long, double)[]
        {
            (0, 0.1), (10_000, 0.95), (20_000, 0.1), (30_000, 0.8), (40_000, 0.1),
        };

        var cuts = SceneCutClassifier.FindHardCutTimestampsMs(frames);

        Assert.Equal([10_000L, 30_000L], cuts);
    }

    [Fact]
    public void TwoSpikesWithinTheMinimumGapAreCollapsedToOne()
    {
        // A real cut occasionally spikes across two adjacent sampled frames
        // (compression/re-encode artifacts) — must not produce two markers a
        // fraction of a second apart.
        var frames = new (long, double)[]
        {
            (10_000, 0.9), (10_400, 0.85),
        };

        var cuts = SceneCutClassifier.FindHardCutTimestampsMs(frames);

        var cut = Assert.Single(cuts);
        Assert.Equal(10_000, cut);
    }

    [Fact]
    public void ASpikeInTheFirstSecondIsSuppressed()
    {
        // Jellyfin already starts playback at 0 — a "Scene" marker a few hundred
        // milliseconds in adds nothing.
        var frames = new (long, double)[] { (400, 0.99) };

        var cuts = SceneCutClassifier.FindHardCutTimestampsMs(frames);

        Assert.Empty(cuts);
    }

    [Fact]
    public void EmptyInputProducesNoCuts()
    {
        var cuts = SceneCutClassifier.FindHardCutTimestampsMs([]);

        Assert.Empty(cuts);
    }
}
