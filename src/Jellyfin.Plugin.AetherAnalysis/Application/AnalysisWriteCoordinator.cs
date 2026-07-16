namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>Serializes the short capacity-check and commit section across concurrent uploads.</summary>
public sealed class AnalysisWriteCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Acquires the write commit lease.</summary>
    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_gate);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
