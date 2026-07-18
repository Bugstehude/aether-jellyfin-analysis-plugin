using Jellyfin.Plugin.AetherAnalysis.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Runs bounded retention and over-capacity cleanup without analyzing media.</summary>
public sealed class AnalysisCleanupWorker(
    IAnalysisRepository repository,
    AnalysisWriteCoordinator writeCoordinator,
    ILogger<AnalysisCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var configuration = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
                var now = DateTimeOffset.UtcNow;
                var retentionDays = Math.Clamp(configuration.RetentionDays, 0, 36500);
                var maxStoredBytes = Math.Clamp(
                    configuration.MaxStoredBytes,
                    1024 * 1024,
                    1024L * 1024 * 1024 * 1024);
                var cutoff = retentionDays > 0
                    ? now.AddDays(-retentionDays)
                    : (DateTimeOffset?)null;
                using var writeLease = await writeCoordinator.AcquireAsync(stoppingToken).ConfigureAwait(false);
                var result = await repository.CleanupAsync(
                    new AnalysisCleanupRequest(cutoff, maxStoredBytes, null, "scheduled", now),
                    stoppingToken).ConfigureAwait(false);
                logger.LogInformation(
                    "AETHER cleanup completed: {RetentionDeleted} retention, {CapacityDeleted} LRU, {DeletedBytes} bytes deleted",
                    result.RetentionDeletedRecords,
                    result.CapacityDeletedRecords,
                    result.DeletedBytes);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AETHER analysis cleanup failed; stored analyses remain available");
            }

            var hours = Math.Clamp(
                Plugin.Instance?.Configuration.CleanupIntervalHours ?? 6,
                1,
                168);
            await Task.Delay(TimeSpan.FromHours(hours), stoppingToken).ConfigureAwait(false);
        }
    }
}
