using Jellyfin.Plugin.AetherAnalysis.Application;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>
/// Runs after every library scan to analyze newly added or changed video items,
/// so a freshly imported title is analyzed without waiting for the daily task.
///
/// ## Warum das hier NICHT auf den Lauf wartet
/// Ein Post-Scan-Task hält den Bibliotheks-Scan auf, bis er fertig ist. Genau
/// das ist am 26.07.2026 passiert: die Analyse lief über eine Stunde, Jellyfin
/// hat den Scan schließlich abgebrochen ("Timed out waiting for task to stop —
/// Aborted after 67 minutes"), den Host abgeräumt und unsere Schleife lief in
/// bereits entsorgte Dienste weiter. Für den Nutzer sah das aus, als hätte das
/// Plugin den ganzen Server heruntergezogen — und ein Neustart half nicht, weil
/// beim Hochfahren derselbe Scan wieder ansetzte.
///
/// Ein Scan darf nicht Stunden dauern, nur weil wir daran hängen. Die Arbeit
/// wird deshalb angestoßen und der Scan sofort freigegeben; der Lauf selbst
/// läuft im Hintergrund weiter und ist gegen den geplanten Lauf ohnehin
/// serialisiert (siehe <see cref="ServerAnalysisRunner.AnalyzePendingAsync"/>).
/// Das Abbruchsignal des Scans wird bewusst NICHT durchgereicht: es feuert,
/// sobald der Scan endet, und würde den gerade angestoßenen Lauf sofort wieder
/// abwürgen.
/// </summary>
public sealed class ServerAnalysisPostScanTask(
    ServerAnalysisRunner runner,
    ILogger<ServerAnalysisPostScanTask> logger) : ILibraryPostScanTask
{
    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.ServerAnalysisEnabled || !configuration.AutoAnalyzeOnScan)
        {
            progress.Report(100);
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "AETHER: library scan finished — starting background analysis. The scan is NOT waiting for it.");
        _ = Task.Run(
            async () =>
            {
                try
                {
                    // Ohne Fortschritts-Empfänger: die Anzeige des Scans ist zu
                    // diesem Zeitpunkt bereits abgeschlossen. Der Fortschritt
                    // steht weiterhin im Live-Panel der Einstellungsseite.
                    await runner.AnalyzePendingAsync(null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Der Host fährt herunter — kein Fehler, nur ein Ende.
                }
                catch (OperationCanceledException)
                {
                    // Ebenso.
                }
                catch (Exception exception)
                {
                    // Eine unbeobachtete Ausnahme in einem losgelösten Task
                    // beendet im schlimmsten Fall den ganzen Prozess. Hier
                    // endet sie.
                    logger.LogError(exception, "AETHER background analysis after the scan failed.");
                }
            },
            CancellationToken.None);

        progress.Report(100);
        return Task.CompletedTask;
    }
}
