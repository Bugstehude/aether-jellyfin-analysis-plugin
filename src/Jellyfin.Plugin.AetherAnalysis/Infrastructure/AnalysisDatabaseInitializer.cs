using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Applies plugin-owned database migrations before API use.</summary>
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
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        var migrations = await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "AETHER analysis database schema 2 is ready at migration {Migration}",
            migrations.LastOrDefault() ?? "none");
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
