using Jellyfin.Plugin.AetherAnalysis.Application;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>
/// Runs after every library scan to analyze newly added or changed video items,
/// so a freshly imported title is analyzed without waiting for the daily task.
/// Already-current items cost only a metadata lookup.
/// </summary>
public sealed class ServerAnalysisPostScanTask(ServerAnalysisRunner runner) : ILibraryPostScanTask
{
    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.ServerAnalysisEnabled || !configuration.AutoAnalyzeOnScan)
        {
            progress.Report(100);
            return;
        }

        await runner.AnalyzePendingAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
