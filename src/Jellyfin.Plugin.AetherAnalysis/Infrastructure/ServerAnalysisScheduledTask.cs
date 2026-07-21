using Jellyfin.Plugin.AetherAnalysis.Application;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>
/// Dashboard-visible task that analyzes every video item missing a current AETHER
/// analysis. Users can run it on demand ("Run" button) or schedule it; a daily
/// default trigger keeps the library current even without a library scan.
/// </summary>
public sealed class ServerAnalysisScheduledTask(ServerAnalysisRunner runner) : IScheduledTask
{
    /// <inheritdoc />
    public string Name => "AETHER: Analyze library";

    /// <inheritdoc />
    public string Key => "AetherAnalysisLibrary";

    /// <inheritdoc />
    public string Description =>
        "Runs AETHER server-side visual + audio analysis for all video items that lack a current analysis.";

    /// <inheritdoc />
    public string Category => "AETHER";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.ServerAnalysisEnabled)
        {
            progress.Report(100);
            return;
        }

        await runner.AnalyzePendingAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        }
    ];
}
