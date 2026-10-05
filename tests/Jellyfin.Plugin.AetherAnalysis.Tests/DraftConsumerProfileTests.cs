using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class DraftConsumerProfileTests
{
    private const string AudioRevision = "sha256:c7ed93c561f2988a3496b93df6ab9f514707b23be8fdf6b59a5ab73e101e5308";
    private const string TimeRevision = "sha256:a694d1a030ab89a26fa7509b12e505cbb82205ae65c3eac455d7588449866b12";
    private static readonly Guid ItemId = Guid.Parse("61fd9006-c087-4f95-822d-cbf1997d5e1a");
    private const string MediaSourceId = "synthetic-host-profile-test";
    private static readonly string Fingerprint = "sha256:" + new string('a', 64);

    [Theory]
    [InlineData("available-silence", 1, 0)]
    [InlineData("impulse-at-eof", 1, 0)]
    [InlineData("impulse-confirmable-before-eof", 1, 1)]
    [InlineData("multichannel-3.0", 1, 0)]
    [InlineData("stereo-counterphase", 1, 0)]
    [InlineData("two-tracks-default-second", 2, 0)]
    public void RealMeasuredAudioProfilesComposeAndKeepLegacyInEveryDetail(string profile, int track, int expectedOnsets)
    {
        var audio = Load(profile + "-audio-only.json");
        var video = Load(profile + "-video-only.json");
        RebindSyntheticHost(audio);
        RebindSyntheticHost(video);
        var context = Context(2000, track, AudioRevision);
        var full = DraftAnalysisMasterBuilder.Build(Bytes(audio), Bytes(video), context, Media(context), DateTimeOffset.UnixEpoch);
        using var fullDocument = JsonDocument.Parse(full);
        var root = fullDocument.RootElement;
        Assert.Equal(100, root.GetProperty("packedDenseAudioFrames").GetProperty("pointCount").GetInt32());
        Assert.Equal(expectedOnsets, root.GetProperty("audioOnsets").GetArrayLength());
        if (profile == "available-silence" || profile == "stereo-counterphase")
        {
            Assert.Equal(0, root.GetProperty("audioAnalysis").GetProperty("normalization").GetProperty("peakRmsLinear").GetDouble());
        }

        foreach (var detail in new[] { "full", "balanced", "compact" })
        {
            using var representation = JsonDocument.Parse(DraftAnalysisMasterBuilder.Create(full, detail, 32 * 1024 * 1024).Json);
            var result = representation.RootElement;
            Assert.True(JsonElement.DeepEquals(root.GetProperty("audioFrames"), result.GetProperty("audioFrames")));
            Assert.True(JsonElement.DeepEquals(root.GetProperty("legacyAudioAnalysis"), result.GetProperty("legacyAudioAnalysis")));
            Assert.True(JsonElement.DeepEquals(root.GetProperty("audioOnsets"), result.GetProperty("audioOnsets")));
            Assert.True(JsonElement.DeepEquals(root.GetProperty("audioAnalysis"), result.GetProperty("audioAnalysis")));
            Assert.Equal(4, result.GetProperty("audioFrames").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("full")]
    [InlineData("balanced")]
    [InlineData("compact")]
    public void ProvenNoTrackProducesNoFabricatedAudio(string detail)
    {
        var audio = Load("missing-audio-track-audio-only.json");
        var video = Load("missing-audio-track-video-only.json");
        RebindSyntheticHost(audio);
        RebindSyntheticHost(video);
        var context = Context(2000, null, AudioRevision);
        var full = DraftAnalysisMasterBuilder.Build(Bytes(audio), Bytes(video), context, Media(context), DateTimeOffset.UnixEpoch);
        using var document = JsonDocument.Parse(DraftAnalysisMasterBuilder.Create(full, detail, 32 * 1024 * 1024).Json);
        var root = document.RootElement;
        Assert.Equal("no-track", root.GetProperty("audioAnalysis").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("audioAnalysis").GetProperty("referenceTrack").ValueKind);
        Assert.Equal("complete", root.GetProperty("audioAnalysis").GetProperty("completeness").GetString());
        foreach (var property in new[] { "audioFrames", "audioOnsets", "packedDenseAudioFrames", "denseAudioFrames", "legacyAudioAnalysis", "legacyAudioProvenance" })
        {
            Assert.False(root.TryGetProperty(property, out _));
        }

        Assert.False(root.GetProperty("representation").TryGetProperty("denseAudioSelection", out _));
        Assert.All(root.GetProperty("frames").EnumerateArray(), frame => Assert.False(frame.TryGetProperty("audio", out _)));
        Assert.Empty(root.GetProperty("cutEvents").EnumerateArray());
    }

    [Theory]
    [InlineData("claimed-track")]
    [InlineData("partial")]
    [InlineData("count")]
    [InlineData("packed")]
    [InlineData("normalization")]
    [InlineData("legacy")]
    [InlineData("method")]
    public void NoTrackRequiresHostAbsenceAndOnlyTheVerifiedProbeProfile(string mutation)
    {
        var audio = Load("missing-audio-track-audio-only.json");
        RebindSyntheticHost(audio);
        var context = Context(2000, null, AudioRevision);
        switch (mutation)
        {
            case "claimed-track": context = context with { FfmpegStreamIndex = 1 }; break;
            case "partial": audio["audio"]!["audioAnalysis"]!["completeness"] = "partial"; break;
            case "count": audio["audio"]!["measuredFrameCount"] = 1; break;
            case "packed": audio["audio"]!["packedDenseAudioFrames"] = new JsonObject(); break;
            case "normalization": audio["audio"]!["audioAnalysis"]!["normalization"] = new JsonObject { ["peakRmsLinear"] = 0 }; break;
            case "legacy": audio["audio"]!["audioFrames"] = new JsonArray(); break;
            case "method": audio["signalProvenance"]!["denseAudio"]!["methodId"] = "hann-linear-audio-v1-draft"; break;
        }

        Assert.Throws<InvalidDataException>(() => DraftAnalysisArtifactValidator.Validate(Bytes(audio), context, "audio-only"));
    }

    [Theory]
    [InlineData("video-start-offset", 2300, 5, 0)]
    [InlineData("vfr-source-times", 2125, 4, 2)]
    [InlineData("nominal-duration-real-end", 2250, 4, 4)]
    [InlineData("eof-cut-pending", 2000, 4, 4)]
    public void RealTimeMeasurementsRetainActualPtsTailAndPendingSemantics(string profile, long duration, int imageCount, int attachedCount)
    {
        // These original files are combined Full artifacts. This test projects their measured
        // groups into the existing separate-component API, without inventing measurements.
        // The worker's already-attached legacy values are discarded and independently reattached.
        var original = Load(profile + "-full.json");
        var audio = ProjectFull(original, "audio-only");
        var video = ProjectFull(original, "video-only");
        RebindSyntheticHost(audio);
        RebindSyntheticHost(video);
        var context = Context(duration, 1, TimeRevision);
        var full = DraftAnalysisMasterBuilder.Build(Bytes(audio), Bytes(video), context, Media(context), DateTimeOffset.UnixEpoch);
        using var document = JsonDocument.Parse(full);
        var root = document.RootElement;
        Assert.Equal(imageCount, root.GetProperty("frames").GetArrayLength());
        Assert.Equal(attachedCount, root.GetProperty("frames").EnumerateArray().Count(frame => frame.TryGetProperty("audio", out _)));
        using var originalVideo = JsonDocument.Parse(Bytes(original["video"]!.AsObject()));
        Assert.True(JsonElement.DeepEquals(originalVideo.RootElement.GetProperty("frames"), root.GetProperty("frames")));
        var compact = DraftAnalysisMasterBuilder.Create(full, "compact", 32 * 1024 * 1024);
        using var reduced = JsonDocument.Parse(compact.Json);
        Assert.True(JsonElement.DeepEquals(root.GetProperty("audioFrames"), reduced.RootElement.GetProperty("audioFrames")));
        Assert.True(JsonElement.DeepEquals(root.GetProperty("cutAnalysis"), reduced.RootElement.GetProperty("cutAnalysis")));
        Assert.Equal(duration, root.GetProperty("durationMs").GetInt64());
        if (profile == "eof-cut-pending")
        {
            Assert.Equal("partial", root.GetProperty("cutAnalysis").GetProperty("completeness").GetString());
            Assert.Equal(1, root.GetProperty("cutAnalysis").GetProperty("pendingAtEof").GetArrayLength());
            Assert.Empty(root.GetProperty("cutEvents").EnumerateArray());
        }
    }

    [Fact]
    public void OriginalOfflineIdentityIsNotAcceptedAsHostProof()
    {
        var audio = Load("available-silence-audio-only.json");
        Assert.Throws<InvalidDataException>(() => DraftAnalysisArtifactValidator.Validate(Bytes(audio), Context(2000, 1, AudioRevision), "audio-only"));
    }

    private static DraftArtifactContext Context(long duration, int? track, string revision) =>
        new(ItemId, MediaSourceId, Fingerprint, duration, track, null, revision);

    private static MediaFingerprint Media(DraftArtifactContext context) =>
        new(context.ItemId, context.MediaSourceId, context.SourceFingerprint, "strong", context.DurationMs);

    private static JsonObject Load(string name) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
        "draft", "consumer-profiles", name)))!.AsObject();

    private static byte[] Bytes(JsonObject value) => JsonSerializer.SerializeToUtf8Bytes(value);

    // All original checked-in JSON remains byte-identical to the worker reports. Only the
    // in-memory test copy receives a synthetic host context, not a real Jellyfin identity.
    private static void RebindSyntheticHost(JsonObject component)
    {
        component["snapshot"]!["itemId"] = ItemId.ToString();
        component["snapshot"]!["mediaSourceId"] = MediaSourceId;
        component["snapshot"]!["sourceFingerprint"] = Fingerprint;
        foreach (var group in component["signalProvenance"]!.AsObject())
        {
            group.Value!["sourceFingerprint"] = Fingerprint;
        }

        if (component["video"] is JsonObject video)
        {
            video["mediaFingerprintAtStart"] = Fingerprint;
            foreach (var group in video["signalProvenance"]!.AsObject())
            {
                group.Value!["sourceFingerprint"] = Fingerprint;
            }
        }
    }

    private static JsonObject ProjectFull(JsonObject full, string mode)
    {
        var component = full.DeepClone().AsObject();
        component["mode"] = mode;
        component.Remove(mode == "audio-only" ? "video" : "audio");
        var provenance = component["signalProvenance"]!.AsObject();
        foreach (var name in provenance.Select(property => property.Key).ToArray())
        {
            if (mode == "audio-only" ? name is "visual" or "cuts" : name is "denseAudio" or "legacyAudio") provenance.Remove(name);
        }

        if (mode == "video-only")
        {
            foreach (var property in new[] { "denseAudioTargetIntervalMs", "denseAudioSelection", "denseAudioMaxGapMs" })
            {
                component["representation"]!.AsObject().Remove(property);
            }

            foreach (var frame in component["video"]!["frames"]!.AsArray()) frame!.AsObject().Remove("audio");
        }

        return component;
    }
}
