using Jellyfin.Plugin.AetherAnalysis.Infrastructure;

namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>Serializes the short capacity-check and commit section across concurrent uploads.</summary>
public sealed class AnalysisWriteCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<AnalysisKey, int> _protectedKeys = new();

    /// <summary>Snapshots analyses that must remain readable during a running replacement.</summary>
    public IReadOnlyCollection<AnalysisKey> ProtectedKeys
    {
        get
        {
            lock (_protectedKeys)
            {
                return _protectedKeys.Keys.ToArray();
            }
        }
    }

    /// <summary>Pins a source until its replacement has finished or failed.</summary>
    public IDisposable Protect(AnalysisKey key)
    {
        lock (_protectedKeys)
        {
            _protectedKeys[key] = _protectedKeys.GetValueOrDefault(key) + 1;
        }

        return new Protection(this, key);
    }

    /// <summary>Acquires the write commit lease.</summary>
    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_gate);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private void ReleaseProtection(AnalysisKey key)
    {
        lock (_protectedKeys)
        {
            var count = _protectedKeys[key];
            if (count == 1)
            {
                _protectedKeys.Remove(key);
            }
            else
            {
                _protectedKeys[key] = count - 1;
            }
        }
    }

    private sealed class Protection(AnalysisWriteCoordinator owner, AnalysisKey key) : IDisposable
    {
        private AnalysisWriteCoordinator? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseProtection(key);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
