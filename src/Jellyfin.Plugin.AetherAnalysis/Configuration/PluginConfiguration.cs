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

    /// <summary>Gets or sets whether the plugin runs its own server-side analysis worker.</summary>
    public bool ServerAnalysisEnabled { get; set; } = true;

    /// <summary>Gets or sets whether new/changed items are analyzed automatically after each library scan.</summary>
    public bool AutoAnalyzeOnScan { get; set; } = true;

    /// <summary>Gets or sets the libraries to analyze (collection-folder ids). Empty analyzes all libraries.</summary>
    public string[] AnalysisLibraryIds { get; set; } = [];

    /// <summary>Gets or sets the command used to launch Node for the bundled worker (PATH name or absolute path).</summary>
    public string NodePath { get; set; } = "node";

    /// <summary>Gets or sets the visual sampling rate (frames per second) for server-side analysis.</summary>
    public int AnalysisFps { get; set; } = 2;

    /// <summary>Gets or sets the analysis-frame width cap; the browser client uses 480.</summary>
    public int AnalysisMaxWidth { get; set; } = 480;

    /// <summary>Gets or sets the per-item worker timeout in minutes.</summary>
    public int AnalysisTimeoutMinutes { get; set; } = 60;
}
