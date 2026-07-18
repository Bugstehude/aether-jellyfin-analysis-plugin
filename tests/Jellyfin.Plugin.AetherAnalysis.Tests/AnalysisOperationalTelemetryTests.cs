using Jellyfin.Plugin.AetherAnalysis.Application;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class AnalysisOperationalTelemetryTests
{
    [Fact]
    public void SnapshotContainsOnlyRecordedProcessLocalFailures()
    {
        var telemetry = new AnalysisOperationalTelemetry();

        telemetry.RecordCorruptRead();
        telemetry.RecordTouchFailure();
        telemetry.RecordTouchFailure();
        var snapshot = telemetry.Snapshot();

        Assert.Equal(1, snapshot.CorruptReadCount);
        Assert.NotNull(snapshot.LastCorruptReadAt);
        Assert.Equal(2, snapshot.TouchFailureCount);
        Assert.NotNull(snapshot.LastTouchFailureAt);
    }
}
