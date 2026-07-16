using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Creates the initial plugin database before API use.</summary>
public sealed class AnalysisDatabaseInitializer(
    IDbContextFactory<AnalysisDbContext> contextFactory,
    ILogger<AnalysisDatabaseInitializer> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dataFolder = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("AETHER plugin data path is not initialized.");
        Directory.CreateDirectory(dataFolder);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("AETHER analysis database schema 1 is ready");
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
