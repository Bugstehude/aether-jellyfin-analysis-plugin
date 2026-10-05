using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;
using Jellyfin.Plugin.AetherAnalysis.Api;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
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
        serviceCollection.AddSingleton<VoiceRecordingRepository>();
        serviceCollection.AddSingleton<ManualPresetRepository>();
        serviceCollection.AddSingleton<DeviceQualityProfileRepository>();
        serviceCollection.AddSingleton<DeviceQualityProfileValidator>();
        serviceCollection.AddSingleton(_ => new JourneyTrackStore(
            Plugin.Instance?.DataFolderPath
                ?? throw new InvalidOperationException("AETHER plugin data path is not initialized.")));
        serviceCollection.AddSingleton<AnalysisDocumentValidator>();
        serviceCollection.AddSingleton<MediaFingerprintService>();
        serviceCollection.AddSingleton<AnalysisRepresentationService>();
        serviceCollection.AddSingleton<AnalysisOperationalTelemetry>();
        serviceCollection.AddSingleton<AnalysisWriteCoordinator>();
        serviceCollection.AddSingleton<AnalysisWorkerExecutionGate>();
        serviceCollection.AddSingleton<AnalysisUploadResourceFilter>();

        // In-plugin server-side analysis (option b): the plugin runs the shared
        // perception-engine worker and stores results directly, no HTTP/auth hop.
        serviceCollection.AddSingleton<ServerAnalysisWorkerRunner>();
        serviceCollection.AddSingleton<IServerAnalysisWorkerRunner>(provider => provider.GetRequiredService<ServerAnalysisWorkerRunner>());
        serviceCollection.AddSingleton<ServerAnalysisActivity>();
        serviceCollection.AddSingleton<ServerAnalysisRunner>(provider => new ServerAnalysisRunner(
            provider.GetRequiredService<ILibraryManager>(), provider.GetRequiredService<IAnalysisRepository>(),
            provider.GetRequiredService<AnalysisDocumentValidator>(), provider.GetRequiredService<MediaFingerprintService>(),
            provider.GetRequiredService<AnalysisRepresentationService>(), provider.GetRequiredService<AnalysisWriteCoordinator>(),
            provider.GetRequiredService<IServerAnalysisWorkerRunner>(), provider.GetRequiredService<ServerAnalysisActivity>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ServerAnalysisRunner>>(),
            provider.GetRequiredService<AnalysisWorkerExecutionGate>(),
            provider.GetRequiredService<DraftServerAnalysisRunner>()));
        serviceCollection.AddSingleton<AnalysisJobDispatcher>();
        // Fresh-component engine. Its default test/offline settings remain draft-only.
        serviceCollection.AddSingleton<IDraftSourceResolver, DraftJellyfinSourceResolver>();
        serviceCollection.AddSingleton<IDraftWorkerProcessRunner, DraftWorkerProcessRunner>();
        serviceCollection.AddSingleton<Func<DraftWorkerSettings>>(provider => () =>
        {
            var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
            var encoder = provider.GetRequiredService<IMediaEncoder>();
            return Analysis12WorkerBundle.CreateSettings(config, encoder.EncoderPath ?? string.Empty,
                encoder.ProbePath ?? string.Empty);
        });
        serviceCollection.AddSingleton<DraftServerAnalysisRunner>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<AnalysisJobDispatcher>());
        serviceCollection.AddSingleton<IScheduledTask, ServerAnalysisScheduledTask>();
        serviceCollection.AddSingleton<ILibraryPostScanTask, ServerAnalysisPostScanTask>();

        // Chapter-marker generation from already-stored scene-cut analysis (no re-analysis).
        serviceCollection.AddSingleton<ChapterGenerator>();

        serviceCollection.AddHostedService<AnalysisDatabaseInitializer>();
        serviceCollection.AddHostedService<AnalysisCleanupWorker>();
    }

}
