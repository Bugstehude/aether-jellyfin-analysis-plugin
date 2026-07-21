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
    public const string Version = "1.0.0";
}
