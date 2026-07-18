using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Api;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
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
        var dataFolder = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("AETHER plugin data path is not initialized.");
        var databasePath = Path.Combine(dataFolder, "aether-analysis.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        serviceCollection.AddPooledDbContextFactory<AnalysisDbContext>(options =>
            options.UseSqlite(connectionString));
        var origins = (Plugin.Instance.Configuration.AllowedOrigins ?? [])
            .Select(NormalizeOrigin)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        serviceCollection.AddCors(options => options.AddPolicy(AetherCorsPolicy.Name, policy =>
        {
            if (origins.Length > 0)
            {
                policy.WithOrigins(origins);
            }
            else
            {
                policy.SetIsOriginAllowed(_ => false);
            }

            policy.WithMethods("GET", "HEAD", "PUT", "DELETE", "POST", "OPTIONS")
                .WithHeaders("Authorization", "Content-Type", "If-Match", "If-None-Match")
                .WithExposedHeaders("ETag", "X-Aether-Analysis-Created-At", "Retry-After")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
        }));
        serviceCollection.AddSingleton<IAnalysisRepository, AnalysisRepository>();
        serviceCollection.AddSingleton<AnalysisDocumentValidator>();
        serviceCollection.AddSingleton<MediaFingerprintService>();
        serviceCollection.AddSingleton<AnalysisRepresentationService>();
        serviceCollection.AddSingleton<AnalysisOperationalTelemetry>();
        serviceCollection.AddSingleton<AnalysisWriteCoordinator>();
        serviceCollection.AddSingleton<AnalysisUploadResourceFilter>();
        serviceCollection.AddHostedService<AnalysisDatabaseInitializer>();
        serviceCollection.AddHostedService<AnalysisCleanupWorker>();
    }

    private static string? NormalizeOrigin(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
