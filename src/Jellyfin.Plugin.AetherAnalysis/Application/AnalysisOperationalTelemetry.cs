namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>Tracks process-local operational failures without retaining sensitive request data.</summary>
public sealed class AnalysisOperationalTelemetry
{
    private long _corruptReadCount;
    private long _lastCorruptReadUnixTimeMilliseconds;
    private long _touchFailureCount;
    private long _lastTouchFailureUnixTimeMilliseconds;

    /// <summary>Records a corrupt stored representation read.</summary>
    public void RecordCorruptRead()
    {
        Interlocked.Increment(ref _corruptReadCount);
        Interlocked.Exchange(
            ref _lastCorruptReadUnixTimeMilliseconds,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Records a non-fatal access-time write failure.</summary>
    public void RecordTouchFailure()
    {
        Interlocked.Increment(ref _touchFailureCount);
        Interlocked.Exchange(
            ref _lastTouchFailureUnixTimeMilliseconds,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Gets an immutable current-process snapshot.</summary>
    public AnalysisOperationalSnapshot Snapshot()
    {
        var corruptReadCount = Interlocked.Read(ref _corruptReadCount);
        var touchFailureCount = Interlocked.Read(ref _touchFailureCount);
        return new AnalysisOperationalSnapshot(
            corruptReadCount,
            ToTimestamp(Interlocked.Read(ref _lastCorruptReadUnixTimeMilliseconds)),
            touchFailureCount,
            ToTimestamp(Interlocked.Read(ref _lastTouchFailureUnixTimeMilliseconds)));
    }

    private static DateTimeOffset? ToTimestamp(long value) => value > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(value)
        : null;
}

/// <summary>Non-sensitive operational counters since the current Jellyfin process started.</summary>
public sealed record AnalysisOperationalSnapshot(
    long CorruptReadCount,
    DateTimeOffset? LastCorruptReadAt,
    long TouchFailureCount,
    DateTimeOffset? LastTouchFailureAt);
