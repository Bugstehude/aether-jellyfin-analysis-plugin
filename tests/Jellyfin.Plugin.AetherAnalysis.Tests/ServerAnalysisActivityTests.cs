using Jellyfin.Plugin.AetherAnalysis.Application;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class ServerAnalysisActivityTests
{
    [Fact]
    public void SnapshotIsIdleBeforeAnyRun()
    {
        var snapshot = new ServerAnalysisActivity().Snapshot();

        Assert.False(snapshot.Running);
        Assert.Null(snapshot.Current);
        Assert.Null(snapshot.LastRun);
        Assert.Empty(snapshot.Recent);
        Assert.Equal(100, snapshot.Percent);
    }

    [Fact]
    public void RunTracksCurrentItemCountsAndPercent()
    {
        var activity = new ServerAnalysisActivity();
        var itemId = Guid.NewGuid();

        activity.BeginRun("scheduled", total: 4);
        activity.BeginItem(itemId, "Movie A");

        var running = activity.Snapshot();
        Assert.True(running.Running);
        Assert.Equal("scheduled", running.Source);
        Assert.NotNull(running.Current);
        Assert.Equal("Movie A", running.Current!.Name);
        Assert.Equal(itemId, running.Current.ItemId);
        Assert.Equal(0, running.Percent);

        activity.CompleteItem("Movie A", "stored");
        activity.MarkAlreadyCurrent();
        activity.BeginItem(Guid.NewGuid(), "Movie B");
        activity.CompleteItem("Movie B", "failed");

        var mid = activity.Snapshot();
        Assert.Null(mid.Current);
        Assert.Equal(3, mid.Checked);
        Assert.Equal(2, mid.Analyzed);
        Assert.Equal(1, mid.Stored);
        Assert.Equal(1, mid.Failed);
        Assert.Equal(75, mid.Percent);
        Assert.Collection(
            mid.Recent,
            first => Assert.Equal("Movie B", first.Name),
            second => Assert.Equal("Movie A", second.Name));
    }

    [Fact]
    public void EndRunCapturesLastRunSummaryAndClearsRunning()
    {
        var activity = new ServerAnalysisActivity();
        activity.BeginRun("scheduled", total: 2);
        activity.BeginItem(Guid.NewGuid(), "Movie A");
        activity.CompleteItem("Movie A", "stored");
        activity.EndRun(cancelled: true);

        var snapshot = activity.Snapshot();
        Assert.False(snapshot.Running);
        Assert.NotNull(snapshot.LastRun);
        Assert.True(snapshot.LastRun!.Cancelled);
        Assert.Equal("scheduled", snapshot.LastRun.Source);
        Assert.Equal(1, snapshot.LastRun.Stored);
        Assert.Equal(1, snapshot.LastRun.Analyzed);
    }

    [Fact]
    public void AbandonItemClearsCurrentWithoutRecording()
    {
        var activity = new ServerAnalysisActivity();
        activity.BeginItem(Guid.NewGuid(), "Movie A");
        activity.AbandonItem();

        var snapshot = activity.Snapshot();
        Assert.Null(snapshot.Current);
        Assert.Empty(snapshot.Recent);
        Assert.Equal(0, snapshot.Checked);
    }

    [Fact]
    public void RecentIsCappedAtTenNewestFirst()
    {
        var activity = new ServerAnalysisActivity();
        for (var i = 0; i < 13; i++)
        {
            activity.CompleteItem("Movie " + i, "stored");
        }

        var recent = activity.Snapshot().Recent;
        Assert.Equal(10, recent.Count);
        Assert.Equal("Movie 12", recent[0].Name);
        Assert.Equal("Movie 3", recent[9].Name);
    }
}
