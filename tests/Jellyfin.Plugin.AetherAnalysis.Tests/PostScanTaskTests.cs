using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>
/// Der Post-Scan-Hook darf den Bibliotheks-Scan nicht aufhalten.
///
/// Am 26.07.2026 tat er genau das: die Analyse lief über eine Stunde, Jellyfin
/// brach den Scan ab ("Timed out waiting for task to stop — Aborted after 67
/// minutes"), räumte den Host ab, und unsere Schleife lief in entsorgte Dienste
/// weiter. Für den Nutzer sah das aus, als hätte das Plugin den Server
/// heruntergezogen — und ein Neustart half nicht, weil beim Hochfahren derselbe
/// Scan wieder ansetzte.
/// </summary>
public sealed class PostScanTaskTests
{
    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = [];

        public void Report(double value) => Reports.Add(value);
    }

    [Fact]
    public async Task ReturnsImmediatelyInsteadOfWaitingForTheAnalysis()
    {
        // Ohne Plugin-Instanz ist die Konfiguration null — der Hook meldet dann
        // sofort fertig. Das ist genau der Pfad, der hier zählt: er darf unter
        // KEINEN Umständen blockieren.
        var task = new ServerAnalysisPostScanTask(
            runner: null!,
            NullLogger<ServerAnalysisPostScanTask>.Instance);
        var progress = new RecordingProgress();

        var run = task.Run(progress, CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(run, finished);
        Assert.Contains(100, progress.Reports);
    }

    [Fact]
    public void ReportsCompletionSynchronously()
    {
        // Der Rückgabewert muss bereits abgeschlossen sein: alles andere lässt
        // den Scan warten, und sei es kurz.
        var task = new ServerAnalysisPostScanTask(
            runner: null!,
            NullLogger<ServerAnalysisPostScanTask>.Instance);

        var run = task.Run(new RecordingProgress(), CancellationToken.None);

        Assert.True(run.IsCompleted);
    }
}
