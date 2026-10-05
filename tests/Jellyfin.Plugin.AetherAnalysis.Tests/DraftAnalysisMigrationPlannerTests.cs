using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class DraftAnalysisMigrationPlannerTests
{
    [Theory]
    [InlineData("available", 1)]
    [InlineData("no-track", null)]
    [InlineData("second-default", 2)]
    public void RealHostFullDocumentsAreKeptWithoutRewritingTheirBytes(string profile, int? track)
    {
        var (record, context) = Host(profile, track);
        var original = record.CompressedDocument.ToArray();
        var etag = record.Etag;
        var decision = DraftAnalysisMigrationPlanner.Evaluate(record, context, 2, 480);
        Assert.Equal(DraftMigrationAction.KeepCurrent, decision.Action);
        Assert.False(decision.PendingCutAtEof);
        Assert.Equal(original, record.CompressedDocument);
        Assert.Equal(etag, record.Etag);
    }

    [Theory]
    [InlineData("vfr-source-times", 2125)]
    [InlineData("video-start-offset", 2300)]
    [InlineData("nominal-duration-real-end", 2250)]
    [InlineData("eof-cut-pending", 2000)]
    public void CompletedMeasuredProfilesIncludingEOFPendingDoNotRequireAnotherFullRun(string profile, long duration)
    {
        var (record, context) = Consumer(profile, duration);
        var decision = DraftAnalysisMigrationPlanner.Evaluate(record, context, 2, 64);
        Assert.Equal(DraftMigrationAction.KeepCurrent, decision.Action);
        Assert.Equal(profile == "eof-cut-pending", decision.PendingCutAtEof);
    }

    [Fact]
    public void MissingTargetRequiresBothFreshComponents()
    {
        var (_, context) = Host("available", 1);
        Assert.Equal(DraftMigrationAction.AnalyzeFull, DraftAnalysisMigrationPlanner.Evaluate(null, context, 2, 480).Action);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    public void OldAnalysesNeverBecomeNewMeasurementsThroughRelabeling(string version)
    {
        var (_, context) = Host("available", 1);
        var media = new MediaFingerprint(context.ItemId, context.MediaSourceId, context.SourceFingerprint, "strong", context.DurationMs);
        var legacy = StoredAnalysisTestData.Create(media, version);
        var original = legacy.CompressedDocument.ToArray();
        Assert.Equal(DraftMigrationAction.AnalyzeFull, DraftAnalysisMigrationPlanner.Evaluate(legacy, context, 2, 480).Action);
        Assert.Equal(version, legacy.AlgorithmVersion);
        Assert.Equal(original, legacy.CompressedDocument);
    }

    [Theory]
    [InlineData("balanced")]
    [InlineData("compact")]
    public void ReducedRepresentationsDoNotQualifyAsFullMasters(string detail)
    {
        var (record, context) = Host("available", 1);
        var full = CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes);
        var reduced = DraftAnalysisMasterBuilder.Create(full, detail, context.MaximumDocumentBytes);
        var result = Record(reduced.Json);
        Assert.Equal(DraftMigrationAction.AnalyzeFull, DraftAnalysisMigrationPlanner.Evaluate(result, context, 2, 480).Action);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("algorithm")]
    [InlineData("item")]
    [InlineData("fingerprint")]
    [InlineData("quality")]
    [InlineData("duration")]
    [InlineData("audio-partial")]
    [InlineData("audio-decode-error")]
    [InlineData("pcm-origin")]
    [InlineData("legacy-clock")]
    [InlineData("legacy-track")]
    [InlineData("full-peak")]
    [InlineData("dense-checksum")]
    [InlineData("missing-pts")]
    [InlineData("source-anchor")]
    [InlineData("frame-audio-value")]
    [InlineData("frame-audio-missing")]
    [InlineData("frame-audio-unmeasured")]
    [InlineData("created-at")]
    [InlineData("metadata-snapshot")]
    [InlineData("metadata-revision")]
    [InlineData("provenance")]
    [InlineData("partial-without-pending")]
    public void InvalidMeasurementOrCompositionProofCannotSuppressReanalysis(string mutation)
    {
        var (record, context) = Host("available", 1);
        var root = JsonNode.Parse(CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes))!.AsObject();
        switch (mutation)
        {
            case "schema": root["schemaVersion"] = 3; break;
            case "algorithm": root["algorithm"]!["version"] = "1.1.0"; break;
            case "item": root["item"]!["id"] = Guid.NewGuid().ToString(); break;
            case "fingerprint": root["item"]!["fingerprint"] = "sha256:" + new string('b', 64); break;
            case "quality": root["item"]!["fingerprintQuality"] = "weak"; break;
            case "duration": root["durationMs"] = 2001; break;
            case "audio-partial": root["audioAnalysis"]!["completeness"] = "partial"; break;
            case "audio-decode-error": root["audioAnalysis"]!["state"] = "decode-error"; break;
            case "pcm-origin": root["audioAnalysis"]!["sourcePcm"]!["firstPts"] = "1"; break;
            case "legacy-clock": root["legacyAudioAnalysis"]!["timeView"]!["sharedMediaGridProven"] = false; break;
            case "legacy-track": root["legacyAudioAnalysis"]!["referenceTrack"]!["ffmpegStreamIndex"] = 2; break;
            case "full-peak": root["audioAnalysis"]!["normalization"]!["peakRmsLinear"] = 0; break;
            case "dense-checksum": root["packedDenseAudioFrames"]!["checksum"] = "sha256:" + new string('0', 64); break;
            case "missing-pts": root["frames"]![1]!.AsObject().Remove("sourcePts"); break;
            case "source-anchor": root["timebase"]!["video"]!["sourceTimeline"]!["lastTimestampMs"] = 1500; break;
            case "frame-audio-value": root["frames"]![0]!["audio"]!["rms"] = 0.4; break;
            case "frame-audio-missing": root["frames"]![0]!.AsObject().Remove("audio"); break;
            case "frame-audio-unmeasured": root["frames"]![3]!["audio"] = new JsonObject { ["rms"] = 1, ["flux"] = 0 }; break;
            case "created-at": root["createdAt"] = DateTimeOffset.UnixEpoch.ToString("O"); break;
            case "metadata-snapshot": root["draftComposition"]!["audioArtifact"]!["snapshot"]!["targetAnalysis"] = new JsonObject(); break;
            case "metadata-revision": root["draftComposition"]!["videoArtifact"]!["producerRevision"] = "sha256:" + new string('b', 64); break;
            case "provenance": root["signalProvenance"]!["denseAudio"]!["operation"] = "reused"; break;
            case "partial-without-pending": root["cutAnalysis"]!["completeness"] = "partial"; break;
        }

        SetBytes(record, JsonSerializer.SerializeToUtf8Bytes(root));
        Assert.Equal(DraftMigrationAction.AnalyzeFull, DraftAnalysisMigrationPlanner.Evaluate(record, context, 2, 480).Action);
    }

    [Theory]
    [InlineData("etag")]
    [InlineData("compressed")]
    [InlineData("bytes")]
    [InlineData("count")]
    [InlineData("interval")]
    [InlineData("created-at")]
    [InlineData("stored-at")]
    [InlineData("budget")]
    [InlineData("fingerprint")]
    [InlineData("quality")]
    public void CorruptOrMismatchedRepositoryMetadataIsNotTrusted(string mutation)
    {
        var (record, context) = Host("available", 1);
        switch (mutation)
        {
            case "etag": record.Etag = "\"different\""; break;
            case "compressed": record.CompressedDocument = [0, 1, 2]; break;
            case "bytes": record.UncompressedBytes++; break;
            case "count": record.FrameCount++; break;
            case "interval": record.SourceIntervalMs++; break;
            case "created-at": record.CreatedAtUnixTimeMilliseconds++; break;
            case "stored-at": record.StoredAtUnixTimeMilliseconds++; break;
            case "budget": context = context with { MaximumDocumentBytes = record.UncompressedBytes - 1 }; break;
            case "fingerprint": record.MediaFingerprint = "sha256:" + new string('b', 64); break;
            case "quality": record.FingerprintQuality = "weak"; break;
        }

        Assert.Equal(DraftMigrationAction.AnalyzeFull, DraftAnalysisMigrationPlanner.Evaluate(record, context, 2, 480).Action);
    }

    [Theory]
    [InlineData("producer")]
    [InlineData("ffmpeg-track")]
    [InlineData("jellyfin-track")]
    [InlineData("fps")]
    [InlineData("width")]
    public void CurrentHostAndConfiguredGoalMustStillMatch(string change)
    {
        var (record, context) = Host("available", 1);
        if (change == "producer") context = context with { ExpectedProducerRevision = "sha256:" + new string('b', 64) };
        if (change == "ffmpeg-track") context = context with { FfmpegStreamIndex = 2 };
        if (change == "jellyfin-track") context = context with { JellyfinStreamIndex = 37 };
        Assert.Equal(DraftMigrationAction.AnalyzeFull, DraftAnalysisMigrationPlanner.Evaluate(record, context,
            change == "fps" ? 1 : 2, change == "width" ? 640 : 480).Action);
    }

    [Theory]
    [InlineData("claimed-track")]
    [InlineData("frame-audio")]
    [InlineData("dense-data")]
    public void NoTrackRequiresActualHostAbsenceAndContainsNoAudioMeasurement(string mutation)
    {
        var (record, context) = Host("no-track", null);
        if (mutation == "claimed-track") context = context with { FfmpegStreamIndex = 1 };
        else
        {
            var root = JsonNode.Parse(CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes))!.AsObject();
            if (mutation == "frame-audio") root["frames"]![0]!["audio"] = new JsonObject { ["rms"] = 0, ["flux"] = 0 };
            else root["packedDenseAudioFrames"] = new JsonObject();
            SetBytes(record, JsonSerializer.SerializeToUtf8Bytes(root));
        }

        Assert.Equal(DraftMigrationAction.AnalyzeFull, DraftAnalysisMigrationPlanner.Evaluate(record, context, 2, 480).Action);
    }

    [Fact]
    public void DuplicateJsonPropertiesCannotHideInvalidStoredValues()
    {
        var (record, context) = Host("available", 1);
        var text = Encoding.UTF8.GetString(CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes));
        SetBytes(record, Encoding.UTF8.GetBytes(text[..^1] + ",\"schemaVersion\":2}"));
        Assert.Equal(DraftMigrationAction.AnalyzeFull, DraftAnalysisMigrationPlanner.Evaluate(record, context, 2, 480).Action);
    }

    private static (AnalysisRecord Record, DraftArtifactContext Context) Host(string profile, int? track)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "draft", "host-worker-integration", profile + "-full.json"));
        var record = Record(bytes);
        using var document = JsonDocument.Parse(bytes);
        return (record, new DraftArtifactContext(record.ItemId, record.MediaSourceId, record.MediaFingerprint,
            document.RootElement.GetProperty("durationMs").GetInt64(), track, track,
            "sha256:c32d0318eb9be1c4190eede941396ce93917c756d984fd3c4bdb20ad88a8843d"));
    }

    private static (AnalysisRecord Record, DraftArtifactContext Context) Consumer(string profile, long duration)
    {
        var original = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "draft", "consumer-profiles", profile + "-full.json")))!.AsObject();
        var item = Guid.Parse("61fd9006-c087-4f95-822d-cbf1997d5e1a");
        var fingerprint = "sha256:" + new string('a', 64);
        var revision = "sha256:a694d1a030ab89a26fa7509b12e505cbb82205ae65c3eac455d7588449866b12";
        var context = new DraftArtifactContext(item, "synthetic-migration-profile", fingerprint, duration, 1, null, revision);
        original["snapshot"]!["itemId"] = item.ToString();
        original["snapshot"]!["mediaSourceId"] = context.MediaSourceId;
        original["snapshot"]!["sourceFingerprint"] = fingerprint;
        original["video"]!["mediaFingerprintAtStart"] = fingerprint;
        foreach (var group in original["signalProvenance"]!.AsObject()) group.Value!["sourceFingerprint"] = fingerprint;
        original["video"]!["signalProvenance"] = original["signalProvenance"]!.DeepClone();
        var audio = original.DeepClone().AsObject();
        var video = original.DeepClone().AsObject();
        audio["mode"] = "audio-only";
        video["mode"] = "video-only";
        audio.Remove("video");
        video.Remove("audio");
        foreach (var name in new[] { "visual", "cuts" }) audio["signalProvenance"]!.AsObject().Remove(name);
        foreach (var name in new[] { "denseAudio", "legacyAudio" }) video["signalProvenance"]!.AsObject().Remove(name);
        video["video"]!["signalProvenance"] = video["signalProvenance"]!.DeepClone();
        foreach (var name in new[] { "denseAudioTargetIntervalMs", "denseAudioSelection", "denseAudioMaxGapMs" }) video["representation"]!.AsObject().Remove(name);
        foreach (var frame in video["video"]!["frames"]!.AsArray()) frame!.AsObject().Remove("audio");
        var media = new MediaFingerprint(item, context.MediaSourceId, fingerprint, "strong", duration);
        return (Record(DraftAnalysisMasterBuilder.Build(JsonSerializer.SerializeToUtf8Bytes(audio), JsonSerializer.SerializeToUtf8Bytes(video), context, media, DateTimeOffset.UnixEpoch)), context);
    }

    private static AnalysisRecord Record(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        return new AnalysisRecord
        {
            ItemId = root.GetProperty("item").GetProperty("id").GetGuid(),
            MediaSourceId = root.GetProperty("item").GetProperty("mediaSourceId").GetString()!,
            MediaFingerprint = root.GetProperty("item").GetProperty("fingerprint").GetString()!,
            FingerprintQuality = "strong",
            AlgorithmId = "aether-visual",
            AlgorithmVersion = DraftAnalysisMasterBuilder.AlgorithmVersion,
            CompressedDocument = CompressionCodec.Compress(bytes),
            Etag = AnalysisRepresentationService.CreateEtag(bytes),
            UncompressedBytes = bytes.Length,
            FrameCount = root.GetProperty("frames").GetArrayLength(),
            SourceIntervalMs = root.GetProperty("sampling").GetProperty("intervalMs").GetInt32(),
            CreatedAt = root.GetProperty("createdAt").GetDateTimeOffset(),
            StoredAt = root.GetProperty("storedAt").GetDateTimeOffset(),
            LastAccessedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static void SetBytes(AnalysisRecord record, byte[] bytes)
    {
        record.CompressedDocument = CompressionCodec.Compress(bytes);
        record.UncompressedBytes = bytes.Length;
        record.Etag = AnalysisRepresentationService.CreateEtag(bytes);
    }
}
