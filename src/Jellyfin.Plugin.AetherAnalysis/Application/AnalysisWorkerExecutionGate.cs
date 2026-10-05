namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>Serializes production and experimental worker runs within the plugin process.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "This process-lifetime managed gate must remain releasable by workers still unwinding during host shutdown.")]
public sealed class AnalysisWorkerExecutionGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Acquires exclusive worker execution until the returned lease is disposed.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
