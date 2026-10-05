using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;
using Jellyfin.Plugin.AetherAnalysis.Configuration;

namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>Settings for the released, pinned fresh-component worker beside the plugin DLL.</summary>
public static class Analysis12WorkerBundle
{
    /// <summary>Loads package-owned pins. Every job copies and verifies the actual bundle before execution.</summary>
    public static DraftWorkerSettings CreateSettings(PluginConfiguration configuration, string ffmpegPath, string ffprobePath)
    {
        var directory = Path.GetDirectoryName(typeof(Analysis12WorkerBundle).Assembly.Location)!;
        var manifestPath = Path.Combine(directory, "analysis-1.2-worker-manifest.json");
        if (new FileInfo(manifestPath).Length > 64 * 1024)
        {
            throw new InvalidDataException("release-worker-manifest-budget");
        }

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = manifest.RootElement;
        if (root.GetProperty("algorithm").GetString() != "aether-visual/1.2.0"
            || root.GetProperty("componentAlgorithm").GetString() != "aether-visual/1.2.0-draft"
            || root.GetProperty("workerPath").GetString() != "aether-analysis-1.2-worker.cjs")
        {
            throw new InvalidDataException("release-worker-manifest-identity");
        }

        var settings = new DraftWorkerSettings(configuration.NodePath,
            Path.Combine(directory, "aether-analysis-1.2-worker.cjs"),
            root.GetProperty("workerSha256").GetString()!, root.GetProperty("producerRevision").GetString()!,
            ffmpegPath, ffprobePath, Enabled: configuration.ServerAnalysisEnabled,
            MaximumDocumentBytes: Math.Clamp(configuration.MaxUploadBytes, 1, 50 * 1024 * 1024),
            MaximumWorkerRssBytes: configuration.ExperimentalDraftMaximumWorkerRssBytes,
            Fps: Math.Clamp(configuration.AnalysisFps, 1, 4), Width: Math.Clamp(configuration.AnalysisMaxWidth, 16, 1920),
            Timeout: TimeSpan.FromMinutes(Math.Clamp(configuration.AnalysisTimeoutMinutes, 1, 720)),
            MaximumStoredBytes: Math.Clamp(configuration.MaxStoredBytes, 1024 * 1024, 1024L * 1024 * 1024 * 1024),
            TargetVersion: AetherAlgorithm.Version);
        DraftWorkerProcessRunner.ValidateSettings(settings);
        return settings;
    }
}
