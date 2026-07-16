using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AetherAnalysis.Configuration;

/// <summary>Persistent plugin settings.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the maximum stored compressed bytes.</summary>
    public long MaxStoredBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Gets or sets retention in days; zero disables age-based expiry.</summary>
    public int RetentionDays { get; set; }

    /// <summary>Gets or sets the interval between maintenance runs.</summary>
    public int CleanupIntervalHours { get; set; } = 6;

    /// <summary>Gets or sets the maximum uncompressed upload size.</summary>
    public int MaxUploadBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Gets or sets the maximum item count in explicit batch operations.</summary>
    public int MaxBatchItems { get; set; } = 200;

    /// <summary>Gets or sets exact origins allowed to call the plugin from a browser.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Gets or sets non-administrator Jellyfin user ids allowed to upload.</summary>
    public string[] AllowedAnalyzerUserIds { get; set; } = [];
}
