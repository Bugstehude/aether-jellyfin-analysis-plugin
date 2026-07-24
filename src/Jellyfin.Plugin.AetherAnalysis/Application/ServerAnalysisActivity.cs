namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>
/// Thread-safe, in-memory view of what server-side analysis is doing right now. It is fed by the
/// <see cref="ServerAnalysisRunner"/> and surfaced by the admin <c>activity</c> endpoint, which the
/// plugin settings page polls to show live progress. Purely diagnostic: reset on restart, never
/// persisted, and not part of the versioned client contract.
/// </summary>
public sealed class ServerAnalysisActivity
{
    private const int RecentCapacity = 10;
    private readonly object _gate = new();
    private readonly LinkedList<ServerAnalysisRecentItem> _recent = new();

    private bool _running;
    private string? _source;
    private DateTimeOffset? _runStartedAt;
    private int _total;
    private int _checked;
    private int _analyzed;
    private int _stored;
    private int _skipped;
    private int _failed;
    private ServerAnalysisCurrentItem? _current;
    private ServerAnalysisLastRun? _lastRun;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

    /// <summary>Marks the start of a batch run (scheduled task or after-scan hook) and resets counters.</summary>
    public void BeginRun(string source, int total)
    {
        lock (_gate)
        {
            _running = true;
            _source = source;
            _runStartedAt = DateTimeOffset.UtcNow;
            _total = total;
            _checked = 0;
            _analyzed = 0;
            _stored = 0;
            _skipped = 0;
            _failed = 0;
            _current = null;
            Touch();
        }
    }

    /// <summary>Records the item currently being analyzed (also set for button-triggered runs).</summary>
    public void BeginItem(Guid itemId, string name)
    {
        lock (_gate)
        {
            _current = new ServerAnalysisCurrentItem(itemId, name, DateTimeOffset.UtcNow);
            Touch();
        }
    }

    /// <summary>Records the outcome of the current item: <c>stored</c>, <c>skipped</c> or <c>failed</c>.</summary>
    public void CompleteItem(string name, string outcome)
    {
        lock (_gate)
        {
            _checked++;
            _analyzed++;
            switch (outcome)
            {
                case "stored": _stored++; break;
                case "failed": _failed++; break;
                default: _skipped++; break;
            }

            _recent.AddFirst(new ServerAnalysisRecentItem(name, outcome, DateTimeOffset.UtcNow));
            while (_recent.Count > RecentCapacity)
            {
                _recent.RemoveLast();
            }

            _current = null;
            Touch();
        }
    }

    /// <summary>Clears the current item without recording an outcome (cancelled or errored before completion).</summary>
    public void AbandonItem()
    {
        lock (_gate)
        {
            _current = null;
            Touch();
        }
    }

    /// <summary>Counts an item that was already current (checked but not re-analyzed).</summary>
    public void MarkAlreadyCurrent()
    {
        lock (_gate)
        {
            _checked++;
            Touch();
        }
    }

    /// <summary>Marks the end of a batch run and captures its summary as the "last run".</summary>
    public void EndRun(bool cancelled)
    {
        lock (_gate)
        {
            if (_runStartedAt is { } startedAt)
            {
                _lastRun = new ServerAnalysisLastRun(
                    _source ?? "unknown",
                    DateTimeOffset.UtcNow,
                    cancelled,
                    _analyzed,
                    _stored,
                    _skipped,
                    _failed,
                    (DateTimeOffset.UtcNow - startedAt).TotalSeconds);
            }

            _running = false;
            _current = null;
            Touch();
        }
    }

    /// <summary>Takes an immutable snapshot for the status endpoint.</summary>
    public ServerAnalysisActivitySnapshot Snapshot()
    {
        lock (_gate)
        {
            var percent = _total > 0
                ? Math.Clamp(_checked * 100.0 / _total, 0, 100)
                : (_running ? 0 : 100);
            return new ServerAnalysisActivitySnapshot(
                _running,
                _source,
                _runStartedAt,
                _total,
                _checked,
                _analyzed,
                _stored,
                _skipped,
                _failed,
                percent,
                _current,
                _recent.ToArray(),
                _lastRun,
                _updatedAt);
        }
    }

    private void Touch() => _updatedAt = DateTimeOffset.UtcNow;
}

/// <summary>The item currently under analysis.</summary>
public sealed record ServerAnalysisCurrentItem(Guid ItemId, string Name, DateTimeOffset StartedAt);

/// <summary>A recently completed item and its outcome.</summary>
public sealed record ServerAnalysisRecentItem(string Name, string Outcome, DateTimeOffset At);

/// <summary>Summary of the most recently finished (or cancelled) batch run.</summary>
public sealed record ServerAnalysisLastRun(
    string Source,
    DateTimeOffset FinishedAt,
    bool Cancelled,
    int Analyzed,
    int Stored,
    int Skipped,
    int Failed,
    double ElapsedSeconds);

/// <summary>Immutable snapshot of server-side analysis activity.</summary>
public sealed record ServerAnalysisActivitySnapshot(
    bool Running,
    string? Source,
    DateTimeOffset? RunStartedAt,
    int TotalItems,
    int Checked,
    int Analyzed,
    int Stored,
    int Skipped,
    int Failed,
    double Percent,
    ServerAnalysisCurrentItem? Current,
    IReadOnlyList<ServerAnalysisRecentItem> Recent,
    ServerAnalysisLastRun? LastRun,
    DateTimeOffset UpdatedAt);
