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
        serviceCollection.AddCors(options => options.AddPolicy(AetherCorsPolicy.Name, policy =>
        {
            policy.SetIsOriginAllowed(IsAllowedOrigin)
                .WithMethods("GET", "HEAD", "PUT", "DELETE", "POST", "OPTIONS")
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

    private static bool IsAllowedOrigin(string origin)
    {
        var normalized = NormalizeOrigin(origin);
        return normalized is not null
            && (Plugin.Instance?.Configuration.AllowedOrigins ?? [])
                .Select(NormalizeOrigin)
                .OfType<string>()
                .Contains(normalized, StringComparer.OrdinalIgnoreCase);
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
