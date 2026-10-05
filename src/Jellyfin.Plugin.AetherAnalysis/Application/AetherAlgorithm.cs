namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>
/// The single canonical analysis identity the server writes under. It MUST match
/// what <see cref="Api.AnalysisController.GetCapabilities"/> advertises and what the
/// AETHER browser client requests (see the app's plugin-analysis-client.ts:
/// <c>DEFAULT_ALGORITHM_ID</c>/<c>DEFAULT_ALGORITHM_VERSION</c>) — otherwise a
/// server-side analysis and a client cache lookup would use different storage keys
/// and never share. Bump <see cref="Version"/> in lockstep with the vendored
/// worker bundle, capabilities, and the client whenever the algorithm changes.
/// </summary>
public static class AetherAlgorithm
{
    /// <summary>The only algorithm id the plugin stores and advertises.</summary>
    public const string Id = "aether-visual";

    /// <summary>The algorithm version stored and advertised.</summary>
    public const string Version = "1.2.0";

    /// <summary>Older analyses readable by clients targeting the preferred version.</summary>
    public static IReadOnlyList<string> CompatibleReadVersions { get; } = Array.AsReadOnly(new[] { "1.1.0", "1.0.0" });

    /// <summary>
    /// Explicit data compatibility, ordered by preference for each reader. This does not
    /// assert identical measurements: 1.0.0 retains its original palette weighting.
    /// A future measurement revision must be reviewed before adding it to this matrix.
    /// </summary>
    public static IReadOnlyList<AnalysisReadCompatibility> ReadCompatibility { get; } = Array.AsReadOnly(new[]
    {
        new AnalysisReadCompatibility(Version, Array.AsReadOnly(new[] { Version, "1.1.0", "1.0.0" })),
        new AnalysisReadCompatibility("1.1.0", Array.AsReadOnly(new[] { "1.1.0", "1.0.0" })),
        new AnalysisReadCompatibility("1.0.0", Array.AsReadOnly(new[] { "1.0.0", "1.1.0" }))
    });
}

/// <summary>Versions whose known schema-v2 signals a reader can consume.</summary>
public sealed record AnalysisReadCompatibility(string ReaderVersion, IReadOnlyList<string> AnalysisVersions);
