using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Api;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AetherAnalysis;

/// <summary>Registers plugin services in Jellyfin's container.</summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddPooledDbContextFactory<AnalysisDbContext>((_, options) =>
        {
            var dataFolder = Plugin.Instance?.DataFolderPath
                ?? throw new InvalidOperationException("AETHER plugin data path is not initialized.");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataFolder, "aether-analysis.sqlite"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
            }.ToString();

            options.UseSqlite(connectionString);
        });
        serviceCollection.AddSingleton<IAnalysisRepository, AnalysisRepository>();
        serviceCollection.AddSingleton<AnalysisDocumentValidator>();
        serviceCollection.AddSingleton<MediaFingerprintService>();
        serviceCollection.AddSingleton<AnalysisRepresentationService>();
        serviceCollection.AddSingleton<AnalysisOperationalTelemetry>();
        serviceCollection.AddSingleton<AnalysisWriteCoordinator>();
        serviceCollection.AddSingleton<AnalysisUploadResourceFilter>();

        // In-plugin server-side analysis (option b): the plugin runs the shared
        // perception-engine worker and stores results directly, no HTTP/auth hop.
        serviceCollection.AddSingleton<ServerAnalysisWorkerRunner>();
        serviceCollection.AddSingleton<ServerAnalysisRunner>();
        serviceCollection.AddSingleton<AnalysisJobDispatcher>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<AnalysisJobDispatcher>());
        serviceCollection.AddSingleton<IScheduledTask, ServerAnalysisScheduledTask>();
        serviceCollection.AddSingleton<ILibraryPostScanTask, ServerAnalysisPostScanTask>();

        serviceCollection.AddHostedService<AnalysisDatabaseInitializer>();
        serviceCollection.AddHostedService<AnalysisCleanupWorker>();
    }

}
