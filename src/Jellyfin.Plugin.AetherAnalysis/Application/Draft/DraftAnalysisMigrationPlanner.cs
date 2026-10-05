using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>Experimental migration action. Neither action activates production 1.2.</summary>
public enum DraftMigrationAction
{
    /// <summary>Both Full components must be measured afresh, preserving existing records.</summary>
    AnalyzeFull,
    /// <summary>The exact stored Full target already satisfies the current experimental profile.</summary>
    KeepCurrent
}

/// <summary>A read-only decision with a non-sensitive reason and explicit EOF coverage limitation.</summary>
public sealed record DraftMigrationDecision(DraftMigrationAction Action, string Reason, bool PendingCutAtEof = false);

/// <summary>
/// Qualifies an existing exact draft target against freshly verified host/probe context.
/// Old measurements and reduced representations never supply invented PTS or audio features.
/// No source decoding, writes, matrix changes or historical document relabeling occur here.
/// </summary>
public static class DraftAnalysisMigrationPlanner
{
    /// <summary>
    /// Validates bounded stored bytes, metadata and current profile. Unknown or damaged targets
    /// require fresh components. A completed EOF-pending cut profile is retained without retry.
    /// </summary>
    public static DraftMigrationDecision Evaluate(AnalysisRecord? target, DraftArtifactContext context, int fps, int width)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fps, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 1920);
        ArgumentOutOfRangeException.ThrowIfLessThan(context.MaximumDocumentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(context.MaximumDocumentBytes, 50 * 1024 * 1024);
        if (target is null) return Fresh("target-missing");
        if (target.AlgorithmId != "aether-visual" || target.AlgorithmVersion != context.TargetVersion)
            return Fresh("new-measurements-required");
        if (target.ItemId != context.ItemId || target.MediaSourceId != context.MediaSourceId
            || target.MediaFingerprint != context.SourceFingerprint || target.FingerprintQuality != "strong")
            return Fresh("target-source-mismatch");
        if (target.UncompressedBytes <= 0 || target.UncompressedBytes > context.MaximumDocumentBytes)
            return Fresh("target-budget");

        try
        {
            var bytes = CompressionCodec.Decompress(target.CompressedDocument, target.UncompressedBytes);
            if (bytes.Length != target.UncompressedBytes || AnalysisRepresentationService.CreateEtag(bytes) != target.Etag)
                return Fresh("target-content-mismatch");
            var root = DraftAnalysisArtifactValidator.ValidateStoredMaster(bytes, context);
            var sampling = root.GetProperty("sampling");
            var rate = root.GetProperty("timebase").GetProperty("video").GetProperty("requestedRate");
            if (sampling.GetProperty("frameWidth").GetInt32() != width
                || rate.GetProperty("num").GetInt64() != checked(fps * rate.GetProperty("den").GetInt64()))
                return Fresh("target-sampling-profile");
            if (target.FrameCount != root.GetProperty("frames").GetArrayLength()
                || target.SourceIntervalMs != sampling.GetProperty("intervalMs").GetInt32()
                || target.CreatedAtUnixTimeMilliseconds != root.GetProperty("createdAt").GetDateTimeOffset().ToUnixTimeMilliseconds()
                || target.StoredAtUnixTimeMilliseconds != root.GetProperty("storedAt").GetDateTimeOffset().ToUnixTimeMilliseconds())
                return Fresh("target-metadata-mismatch");
            var pending = root.GetProperty("cutAnalysis").GetProperty("pendingAtEof").GetArrayLength() != 0;
            return new DraftMigrationDecision(DraftMigrationAction.KeepCurrent, "current-full-profile", pending);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            return Fresh("target-invalid-profile");
        }
    }

    private static DraftMigrationDecision Fresh(string reason) => new(DraftMigrationAction.AnalyzeFull, reason);
}
