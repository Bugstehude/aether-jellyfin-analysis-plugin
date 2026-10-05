using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

// This fixture discovers its newly created staging directory in the shared temp root.
// Keep it separate from runner tests that create directories with the same prefix.
[Collection("Draft composition staging")]
public sealed class DraftAnalysisCompositionTests
{
    private static readonly MediaFingerprint Media = new(
        Guid.Parse("61fd9006-c087-4f95-822d-cbf1997d5e1a"), "offline-composition-proof", "sha256:" + new string('a', 64), "strong", 2000);
    private const string ProducerRevision = "sha256:8ddc99b791acc18b70aba15237c909024a69364637f2b776fb9a48f96a758e3b";
    private static readonly DraftArtifactContext Context = new(Media.ItemId, Media.MediaSourceId, Media.Fingerprint, Media.DurationMs, 1, null, ProducerRevision);

    [Theory]
    [InlineData("audio-only")]
    [InlineData("video-only")]
    public void RealFullComponentsPassWithoutChangingRawUnknownFields(string mode)
    {
        var value = Component(mode);
        value["foreignEnvelope"] = new JsonObject { ["future"] = "retained" };
        value[mode == "audio-only" ? "audio" : "video"]!["foreignGroup"] = new JsonObject { ["future"] = 42 };
        var validated = DraftAnalysisArtifactValidator.Validate(Bytes(value), Context, mode);
        Assert.Equal("retained", validated.GetProperty("foreignEnvelope").GetProperty("future").GetString());
        Assert.Equal(42, validated.GetProperty(mode == "audio-only" ? "audio" : "video").GetProperty("foreignGroup").GetProperty("future").GetInt32());
    }

    [Theory]
    [InlineData("balanced")]
    [InlineData("compact")]
    public void ReducedInputsCannotBecomeFull(string detail)
    {
        var audio = Component("audio-only");
        audio["representation"]!["detail"] = detail;
        Assert.Throws<InvalidDataException>(() => DraftAnalysisArtifactValidator.Validate(Bytes(audio), Context, "audio-only"));
    }

    [Theory]
    [InlineData("old-component")]
    [InlineData("fingerprint")]
    [InlineData("duration")]
    [InlineData("method")]
    [InlineData("track")]
    [InlineData("invented-jellyfin-track")]
    [InlineData("peak-too-high")]
    [InlineData("peak-too-low")]
    [InlineData("coverage")]
    [InlineData("pcm-origin")]
    [InlineData("pcm-tolerance")]
    [InlineData("legacy-clock")]
    [InlineData("legacy-default-track")]
    [InlineData("duplicate-representation")]
    [InlineData("onset-strength")]
    [InlineData("onset-beyond-duration")]
    [InlineData("onset-unmeasured-point")]
    [InlineData("revision")]
    [InlineData("window-start")]
    [InlineData("valid-samples")]
    [InlineData("first-flux")]
    [InlineData("empty-available")]
    public void InvalidContextFailsClosed(string mutation)
    {
        var root = Component("audio-only");
        var analysis = root["audio"]!["audioAnalysis"]!;
        switch (mutation)
        {
            case "old-component": root["artifactVersion"] = 1; break;
            case "fingerprint": root["snapshot"]!["sourceFingerprint"] = "sha256:" + new string('b', 64); break;
            case "duration": root["durationMs"] = 1999; break;
            case "method": analysis["methods"]!["rms"] = "unknown"; break;
            case "track": analysis["referenceTrack"]!["ffmpegStreamIndex"] = 2; break;
            case "invented-jellyfin-track": analysis["referenceTrack"]!["jellyfinStreamIndex"] = 1; break;
            case "peak-too-high": analysis["normalization"]!["peakRmsLinear"] = 2; break;
            case "peak-too-low": analysis["normalization"]!["peakFluxLinear"] = 0; break;
            case "coverage": analysis["coverage"]![0]!["endMs"] = 1999; break;
            case "pcm-origin": analysis["sourcePcm"]!["firstPts"] = "1"; break;
            case "pcm-tolerance": analysis["sourcePcm"]!["quantizationToleranceSamples"] = 1; break;
            case "legacy-clock": root["audio"]!["legacyAudioAnalysis"]!["timeView"]!["sharedMediaGridProven"] = false; break;
            case "legacy-default-track": root["audio"]!["legacyAudioProvenance"]!["implicitDefaultStreamIndex"] = 2; break;
            case "duplicate-representation": root["audio"]!["denseAudioFrames"] = new JsonArray(); break;
            case "onset-strength": root["audio"]!["audioOnsets"] = Events(1.01, 1999, 1999, 2000); break;
            case "onset-beyond-duration": root["audio"]!["audioOnsets"] = Events(1, 1999, 1999, 2001); break;
            case "onset-unmeasured-point": root["audio"]!["audioOnsets"] = Events(1, 1999, 1999, 2000); break;
            case "revision": root["producerRevision"] = "sha256:" + new string('b', 64); break;
            case "window-start": MutateRecord(root, records => BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(4), -1022)); break;
            case "valid-samples": MutateRecord(root, records => BinaryPrimitives.WriteUInt16LittleEndian(records.AsSpan(8), 1024)); break;
            case "first-flux": MutateRecord(root, records => BinaryPrimitives.WriteDoubleLittleEndian(records.AsSpan(19), 0.000001)); break;
            case "empty-available":
                root["audio"]!["packedDenseAudioFrames"]!["dataBase64"] = "";
                root["audio"]!["packedDenseAudioFrames"]!["pointCount"] = 0;
                root["audio"]!["packedDenseAudioFrames"]!["checksum"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData([]));
                root["audio"]!["measuredFrameCount"] = 0;
                analysis["normalization"]!["peakRmsLinear"] = 0;
                analysis["normalization"]!["peakFluxLinear"] = 0;
                break;
        }

        Assert.Throws<InvalidDataException>(() => DraftAnalysisArtifactValidator.Validate(Bytes(root), Context, "audio-only"));
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejectedBeforeComposition()
    {
        var original = Encoding.UTF8.GetString(Bytes(Component("audio-only")));
        var duplicate = Encoding.UTF8.GetBytes("{\"artifactVersion\":1," + original[1..]);
        Assert.Throws<InvalidDataException>(() => DraftAnalysisArtifactValidator.Validate(duplicate, Context, "audio-only"));
    }

    [Fact]
    public void ActualVideoPtsCannotBeReplacedWithRequestedRaster()
    {
        var video = Component("video-only");
        Assert.Equal(520, video["video"]!["frames"]![1]!["timestampMs"]!.GetValue<int>());
        video["video"]!["frames"]![1]!["timestampMs"] = 500;
        Assert.Throws<InvalidDataException>(() => DraftAnalysisArtifactValidator.Validate(Bytes(video), Context, "video-only"));
    }

    [Theory]
    [InlineData("full", 100, 500)]
    [InlineData("balanced", 40, 500)]
    [InlineData("compact", 20, 1000)]
    public void CompositionAndIndependentAudioReductionMatchPythonWire(string detail, int count, int interval)
    {
        var audio = Component("audio-only");
        var video = Component("video-only");
        audio["audio"]!["foreignAudio"] = new JsonObject { ["value"] = 42 };
        audio["audio"]!["packedDenseAudioFrames"]!["foreignHeader"] = "retained";
        video["video"]!["foreignVideo"] = new JsonObject { ["value"] = 24 };
        video["video"]!["frames"]![0]!["foreignFrame"] = "opaque";
        var full = DraftAnalysisMasterBuilder.Build(Bytes(audio), Bytes(video), Context, Media, DateTimeOffset.UnixEpoch);
        using var fullDocument = JsonDocument.Parse(full);
        var original = fullDocument.RootElement;
        var result = DraftAnalysisMasterBuilder.Create(full, detail, 32 * 1024 * 1024);
        using var document = JsonDocument.Parse(result.Json);
        var root = document.RootElement;
        using var expectedDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "draft", "composition-wire-expected.json")));
        var expected = expectedDocument.RootElement.GetProperty(detail);
        var packed = root.GetProperty("packedDenseAudioFrames");
        Assert.Equal(count, packed.GetProperty("pointCount").GetInt32());
        Assert.Equal("retained", packed.GetProperty("foreignHeader").GetString());
        Assert.Equal(expected.GetProperty("checksum").GetString(), packed.GetProperty("checksum").GetString());
        Assert.Equal(expected.GetProperty("dataBase64").GetString(), packed.GetProperty("dataBase64").GetString());
        Assert.Equal(interval, result.IntervalMs);
        Assert.Equal(42, root.GetProperty("foreignAudio").GetProperty("value").GetInt32());
        Assert.Equal(24, root.GetProperty("foreignVideo").GetProperty("value").GetInt32());
        Assert.True(JsonElement.DeepEquals(original.GetProperty("audioAnalysis"), root.GetProperty("audioAnalysis")));
        Assert.True(JsonElement.DeepEquals(original.GetProperty("audioFrames"), root.GetProperty("audioFrames")));
        Assert.True(JsonElement.DeepEquals(original.GetProperty("legacyAudioAnalysis"), root.GetProperty("legacyAudioAnalysis")));
        Assert.True(JsonElement.DeepEquals(original.GetProperty("legacyAudioProvenance"), root.GetProperty("legacyAudioProvenance")));
        Assert.Equal(new long[] { 0, 500, 1000, 1500 }, root.GetProperty("audioFrames").EnumerateArray()
            .Select(frame => frame.GetProperty("timestampMs").GetInt64()).ToArray());
        Assert.True(JsonElement.DeepEquals(original.GetProperty("audioOnsets"), root.GetProperty("audioOnsets")));
        Assert.True(JsonElement.DeepEquals(original.GetProperty("cutAnalysis"), root.GetProperty("cutAnalysis")));
        Assert.True(JsonElement.DeepEquals(original.GetProperty("timebase"), root.GetProperty("timebase")));
        Assert.Equal(1520, root.GetProperty("timebase").GetProperty("video").GetProperty("sourceTimeline").GetProperty("lastTimestampMs").GetInt64());
        Assert.Equal(AnalysisRepresentationService.CreateEtag(result.Json), result.Etag);
        Assert.Equal(detail, root.GetProperty("representation").GetProperty("detail").GetString());
        Assert.DoesNotContain("\\u002B", Encoding.UTF8.GetString(result.Json), StringComparison.Ordinal);
        if (detail == "compact")
        {
            Assert.False(root.GetProperty("frames")[0].TryGetProperty("sourcePts", out _));
            Assert.Equal("opaque", root.GetProperty("opaqueSourceFrameExtensions")[0].GetProperty("properties").GetProperty("foreignFrame").GetString());
        }
        else
        {
            Assert.Equal("opaque", root.GetProperty("frames")[0].GetProperty("foreignFrame").GetString());
        }

        Assert.Equal(result.Json, DraftAnalysisMasterBuilder.Create(full, detail, 32 * 1024 * 1024).Json);
        var exportDirectory = Environment.GetEnvironmentVariable("AETHER_DRAFT_EXPORT_DIRECTORY");
        if (!string.IsNullOrEmpty(exportDirectory))
        {
            Directory.CreateDirectory(exportDirectory);
            File.WriteAllBytes(Path.Combine(exportDirectory, "composed-" + detail + ".json"), result.Json);
        }
    }

    [Fact]
    public void LegacyAudioAttachesOnlyAtExactMeasuredImageTimes()
    {
        var full = DraftAnalysisMasterBuilder.Build(Bytes(Component("audio-only")), Bytes(Component("video-only")), Context, Media, DateTimeOffset.UnixEpoch);
        using var document = JsonDocument.Parse(full);
        var frames = document.RootElement.GetProperty("frames");
        Assert.True(frames[0].TryGetProperty("audio", out _));
        Assert.False(frames[1].TryGetProperty("audio", out _)); // 520 ms is not legacy's 500 ms.
        Assert.True(frames[2].TryGetProperty("audio", out _));
        Assert.False(frames[3].TryGetProperty("audio", out _)); // 1520 ms is not 1500 ms.
        Assert.Equal(4, document.RootElement.GetProperty("audioFrames").GetArrayLength());
    }

    [Fact]
    public void MasterAndRepresentationBudgetsIncludeAllMetadata()
    {
        var audio = Bytes(Component("audio-only"));
        var video = Bytes(Component("video-only"));
        var small = Context with { MaximumDocumentBytes = Math.Max(audio.Length, video.Length) + 1 };
        Assert.Throws<InvalidDataException>(() => DraftAnalysisMasterBuilder.Build(audio, video, small, Media, DateTimeOffset.UnixEpoch));
        var full = DraftAnalysisMasterBuilder.Build(audio, video, Context, Media, DateTimeOffset.UnixEpoch);
        Assert.Throws<InvalidDataException>(() => DraftAnalysisMasterBuilder.Create(full, "balanced", 1000));
        var compact = DraftAnalysisMasterBuilder.Create(full, "compact", 32 * 1024 * 1024);
        Assert.Throws<InvalidDataException>(() => DraftAnalysisMasterBuilder.Create(compact.Json, "full", 32 * 1024 * 1024));
    }

    [Theory]
    [InlineData("success", DraftCompositionResult.Stored)]
    [InlineData("target-upload", DraftCompositionResult.AnalysisChanged)]
    [InlineData("source-upload", DraftCompositionResult.AnalysisChanged)]
    [InlineData("source-delete", DraftCompositionResult.AnalysisChanged)]
    [InlineData("fingerprint", DraftCompositionResult.MediaChanged)]
    [InlineData("duration", DraftCompositionResult.MediaChanged)]
    [InlineData("capacity", DraftCompositionResult.StorageLimitExceeded)]
    public async Task AtomicCompositionPreservesOldSourceAndCompetingWrites(string scenario, DraftCompositionResult expected)
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await using var session = await fixture.BeginAsync();
        Assert.Contains(fixture.SourceKey, fixture.Writes.ProtectedKeys);
        await StageBoth(session);
        Assert.Null(await fixture.Repository.GetAsync(fixture.TargetKey, default));
        AnalysisRecord? competing = null;
        using (await fixture.Writes.AcquireAsync(default))
        {
            if (scenario == "target-upload")
            {
                competing = StoredAnalysisTestData.Create(Media, DraftAnalysisMasterBuilder.AlgorithmVersion);
                competing.Etag = "\"competing\"";
                await fixture.Repository.UpsertAsync(competing, null, default);
            }
            else if (scenario == "source-upload")
            {
                competing = StoredAnalysisTestData.Create(Media, version: "1.1.0");
                competing.Etag = "\"changed-source\"";
                await fixture.Repository.UpsertAsync(competing, null, default);
            }
            else if (scenario == "source-delete")
            {
                await fixture.Repository.DeleteAsync(fixture.SourceKey, default);
            }
        }

        if (scenario == "fingerprint") fixture.CurrentMedia = Media with { Fingerprint = "sha256:" + new string('b', 64) };
        if (scenario == "duration") fixture.CurrentMedia = Media with { DurationMs = 2001 };
        Assert.Equal(expected, await session.CommitAsync(scenario == "capacity" ? 1 : 50 * 1024 * 1024, default));
        Assert.Empty(fixture.Writes.ProtectedKeys);
        var stored = await fixture.Repository.GetAsync(fixture.TargetKey, default);
        var old = await fixture.Repository.GetAsync(fixture.SourceKey, default);
        if (scenario == "success")
        {
            Assert.NotNull(stored);
            using var document = JsonDocument.Parse(CompressionCodec.Decompress(stored.CompressedDocument, stored.UncompressedBytes));
            Assert.Equal(100, document.RootElement.GetProperty("packedDenseAudioFrames").GetProperty("pointCount").GetInt32());
            Assert.Equal(4, document.RootElement.GetProperty("frames").GetArrayLength());
            Assert.Equal(fixture.Source.Etag, old!.Etag);
        }
        else if (scenario == "target-upload")
        {
            Assert.Equal(competing!.Etag, stored!.Etag);
            Assert.Equal(competing.CompressedDocument, stored.CompressedDocument);
        }
        else
        {
            Assert.Null(stored);
        }

        if (scenario != "source-delete") Assert.Equal(scenario == "source-upload" ? competing!.Etag : fixture.Source.Etag, old!.Etag);
        Assert.False(Directory.Exists(fixture.StagingDirectory));
    }

    [Theory]
    [InlineData("missing-component")]
    [InlineData("stale-snapshot")]
    [InlineData("cancel")]
    [InlineData("decode-failure")]
    public async Task AbortCannotPublishPartialOrReplaceExistingAnalysis(string scenario)
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await using var session = await fixture.BeginAsync();
        await Stage(session, "audio-only");
        if (scenario == "missing-component")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => session.CommitAsync(50 * 1024 * 1024, default));
        }
        else if (scenario == "stale-snapshot")
        {
            var value = Component("video-only");
            value["snapshot"] = JsonNode.Parse(session.Snapshot.GetRawText());
            value["snapshot"]!["sourceAnalysis"]!["etag"] = "\"invented\"";
            using var stream = new MemoryStream(Bytes(value));
            await Assert.ThrowsAsync<InvalidDataException>(() => session.StageAsync("video-only", stream, default));
        }
        else if (scenario == "cancel")
        {
            await Stage(session, "video-only");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CommitAsync(50 * 1024 * 1024, cancellation.Token));
        }
        else
        {
            var value = Component("video-only");
            value["snapshot"] = JsonNode.Parse(session.Snapshot.GetRawText());
            value["video"]!["cutAnalysis"]!["state"] = "decode-error";
            using var stream = new MemoryStream(Bytes(value));
            await Assert.ThrowsAsync<InvalidDataException>(() => session.StageAsync("video-only", stream, default));
        }

        Assert.Empty(fixture.Writes.ProtectedKeys);
        Assert.False(Directory.Exists(fixture.StagingDirectory));
        Assert.Null(await fixture.Repository.GetAsync(fixture.TargetKey, default));
        Assert.Equal(fixture.Source.Etag, (await fixture.Repository.GetAsync(fixture.SourceKey, default))!.Etag);
    }

    [Fact]
    public async Task CancellationAndRepositoryCapacityCannotEvictPinnedSource()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await using (var session = await fixture.BeginAsync())
        {
            var cleanup = await fixture.Repository.CleanupAsync(new AnalysisCleanupRequest(DateTimeOffset.UtcNow.AddDays(1), 0, null, "draft-test", DateTimeOffset.UtcNow), default);
            Assert.Equal(0, cleanup.RetentionDeletedRecords);
            Assert.Equal(0, cleanup.CapacityDeletedRecords);
            Assert.NotNull(await fixture.Repository.GetAsync(fixture.SourceKey, default));
        }

        Assert.Empty(fixture.Writes.ProtectedKeys);
        Assert.False(Directory.Exists(fixture.StagingDirectory));
    }

    [Fact]
    public async Task CompetingCompositionsUseCapturedTargetEtagAndReferenceCountedPins()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await using var first = await fixture.BeginAsync();
        var firstDirectory = fixture.StagingDirectory;
        await using var second = await fixture.BeginAsync();
        await StageBoth(first);
        await StageBoth(second);
        Assert.Equal(DraftCompositionResult.Stored, await first.CommitAsync(50 * 1024 * 1024, default));
        Assert.Contains(fixture.SourceKey, fixture.Writes.ProtectedKeys);
        var winner = (await fixture.Repository.GetAsync(fixture.TargetKey, default))!;
        Assert.Equal(DraftCompositionResult.AnalysisChanged, await second.CommitAsync(50 * 1024 * 1024, default));
        Assert.Equal(winner.Etag, (await fixture.Repository.GetAsync(fixture.TargetKey, default))!.Etag);
        Assert.Empty(fixture.Writes.ProtectedKeys);
        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(Directory.Exists(fixture.StagingDirectory));
    }

    [Fact]
    public async Task ExistingDraftTargetCanBeReplacedOnlyWithItsActualSnapshotEtag()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var prior = StoredAnalysisTestData.Create(Media, DraftAnalysisMasterBuilder.AlgorithmVersion);
        using (await fixture.Writes.AcquireAsync(default))
        {
            await fixture.Repository.UpsertAsync(prior, null, default);
        }

        await using var session = await fixture.BeginAsync();
        Assert.Equal(prior.Etag, session.Snapshot.GetProperty("targetAnalysis").GetProperty("etag").GetString());
        Assert.Contains(fixture.TargetKey, fixture.Writes.ProtectedKeys);
        await StageBoth(session);
        Assert.Equal(DraftCompositionResult.Stored, await session.CommitAsync(50 * 1024 * 1024, default));
        Assert.NotEqual(prior.Etag, (await fixture.Repository.GetAsync(fixture.TargetKey, default))!.Etag);
        Assert.Equal(fixture.Source.Etag, (await fixture.Repository.GetAsync(fixture.SourceKey, default))!.Etag);
    }

    [Fact]
    public async Task NoTrackComponentsUseTheSameAtomicHostSnapshotBoundary()
    {
        const string noTrackRevision = "sha256:c7ed93c561f2988a3496b93df6ab9f514707b23be8fdf6b59a5ab73e101e5308";
        await using var fixture = await RepositoryFixture.CreateAsync();
        await using var session = await fixture.BeginAsync(null, noTrackRevision);
        foreach (var mode in new[] { "audio-only", "video-only" })
        {
            var value = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "draft", "consumer-profiles", "missing-audio-track-" + mode + ".json")))!.AsObject();
            value["snapshot"] = JsonNode.Parse(session.Snapshot.GetRawText());
            foreach (var group in value["signalProvenance"]!.AsObject()) group.Value!["sourceFingerprint"] = Media.Fingerprint;
            if (value["video"] is JsonObject video)
            {
                video["mediaFingerprintAtStart"] = Media.Fingerprint;
                foreach (var group in video["signalProvenance"]!.AsObject()) group.Value!["sourceFingerprint"] = Media.Fingerprint;
            }

            using var stream = new MemoryStream(Bytes(value));
            await session.StageAsync(mode, stream, default);
        }

        Assert.Equal(DraftCompositionResult.Stored, await session.CommitAsync(50 * 1024 * 1024, default));
        var stored = (await fixture.Repository.GetAsync(fixture.TargetKey, default))!;
        using var document = JsonDocument.Parse(CompressionCodec.Decompress(stored.CompressedDocument, stored.UncompressedBytes));
        Assert.Equal("no-track", document.RootElement.GetProperty("audioAnalysis").GetProperty("state").GetString());
        Assert.False(document.RootElement.TryGetProperty("audioFrames", out _));
        Assert.Equal(fixture.Source.Etag, (await fixture.Repository.GetAsync(fixture.SourceKey, default))!.Etag);
        Assert.Empty(fixture.Writes.ProtectedKeys);
        Assert.False(Directory.Exists(fixture.StagingDirectory));
    }

    [Fact]
    public async Task OversizedStreamIsStoppedBeforeParsingAndAbortsStagedData()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await using var session = await DraftAnalysisCompositionSession.BeginAsync(fixture.Repository, fixture.Writes,
            _ => Task.FromResult<MediaFingerprint?>(fixture.CurrentMedia), Media.ItemId, Media.MediaSourceId, fixture.SourceKey,
            1, null, ProducerRevision, 1000, default);
        using var input = new MemoryStream(new byte[1001]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => session.StageAsync("audio-only", input, default));
        Assert.Equal("composition-component-budget", error.Message);
        Assert.Empty(fixture.Writes.ProtectedKeys);
        Assert.Null(await fixture.Repository.GetAsync(fixture.TargetKey, default));
    }

    [Fact]
    public void VideoCannotSmuggleUnvalidatedRecognizedAudioGroupIntoMaster()
    {
        var video = Component("video-only");
        video["video"]!["packedDenseAudioFrames"] = new JsonObject { ["checksum"] = "unvalidated" };
        Assert.Throws<InvalidDataException>(() => DraftAnalysisMasterBuilder.Build(Bytes(Component("audio-only")), Bytes(video), Context, Media, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void PendingEofBracketRemainsPartialAndCannotExtendCoverage()
    {
        var video = Component("video-only");
        var cuts = video["video"]!["cutAnalysis"]!;
        cuts["completeness"] = "partial";
        cuts["coverage"]![0]!["endMs"] = 1000;
        cuts["pendingAtEof"] = new JsonArray(new JsonObject
        {
            ["timestampMs"] = 1520,
            ["score"] = 0.3,
            ["uncertainty"] = new JsonObject { ["earliestMs"] = 1000, ["latestMs"] = 1520, ["basis"] = "sample-bracket" }
        });
        var full = DraftAnalysisMasterBuilder.Build(Bytes(Component("audio-only")), Bytes(video), Context, Media, DateTimeOffset.UnixEpoch);
        using var document = JsonDocument.Parse(DraftAnalysisMasterBuilder.Create(full, "compact", 32 * 1024 * 1024).Json);
        Assert.Equal("partial", document.RootElement.GetProperty("cutAnalysis").GetProperty("completeness").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("cutAnalysis").GetProperty("pendingAtEof").GetArrayLength());
        Assert.Empty(document.RootElement.GetProperty("cutEvents").EnumerateArray());
        cuts["coverage"]![0]!["endMs"] = 1520;
        Assert.Throws<InvalidDataException>(() => DraftAnalysisArtifactValidator.Validate(Bytes(video), Context, "video-only"));
    }

    [Fact]
    public void CutCoverageMustEndAtTheLastSelectedSourcePts()
    {
        // Agreed tail rule: a declared stream end is not an inspected image. Workers before
        // Aether 3ae4964 emitted this shape for sources with a video stream duration (MP4).
        var video = Component("video-only");
        _ = DraftAnalysisArtifactValidator.Validate(Bytes(video), Context, "video-only");
        var cuts = video["video"]!["cutAnalysis"]!;
        cuts["parameters"]!["coverageEndBasis"] = "video-stream-end";
        Assert.Throws<InvalidDataException>(() => DraftAnalysisArtifactValidator.Validate(Bytes(video), Context, "video-only"));
        cuts["coverage"]![0]!["endMs"] = 2000;
        Assert.Throws<InvalidDataException>(() => DraftAnalysisArtifactValidator.Validate(Bytes(video), Context, "video-only"));
    }

    [Fact]
    public void ConfirmedCutRequiresTwoActualFollowingSourceImages()
    {
        var video = Component("video-only");
        video["video"]!["cutEvents"] = new JsonArray(new JsonObject
        {
            ["id"] = "cut-520-0",
            ["timestampMs"] = 520,
            ["confirmedAtMs"] = 1520,
            ["score"] = 0.3,
            ["kind"] = "hard-cut",
            ["uncertainty"] = new JsonObject { ["earliestMs"] = 0, ["latestMs"] = 520, ["basis"] = "sample-bracket" }
        });
        _ = DraftAnalysisArtifactValidator.Validate(Bytes(video), Context, "video-only");
        video["video"]!["cutEvents"]![0]!["confirmedAtMs"] = 1000;
        Assert.Throws<InvalidDataException>(() => DraftAnalysisArtifactValidator.Validate(Bytes(video), Context, "video-only"));
    }

    [Fact]
    public void DeclaredSourceTimelineMustMatchTheFullInputImages()
    {
        var video = Component("video-only");
        video["video"]!["timebase"]!["video"]!["sourceTimeline"] = new JsonObject
        {
            ["frameCount"] = 4,
            ["firstTimestampMs"] = 0,
            ["firstSourcePts"] = "0",
            ["lastTimestampMs"] = 1500,
            ["lastSourcePts"] = "1500"
        };
        Assert.Throws<InvalidDataException>(() => DraftAnalysisArtifactValidator.Validate(Bytes(video), Context, "video-only"));
    }

    private static void MutateRecord(JsonObject root, Action<byte[]> mutation)
    {
        var packed = root["audio"]!["packedDenseAudioFrames"]!;
        var records = Convert.FromBase64String(packed["dataBase64"]!.GetValue<string>());
        mutation(records);
        packed["dataBase64"] = Convert.ToBase64String(records);
        packed["checksum"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(records));
    }

    private static JsonArray Events(double strength, long timestamp, long confirmed, long latest) => new(new JsonObject
    {
        ["id"] = "onset-test",
        ["timestampMs"] = timestamp,
        ["confirmedAtMs"] = confirmed,
        ["strength"] = strength,
        ["uncertainty"] = new JsonObject { ["earliestMs"] = timestamp - 50, ["latestMs"] = latest, ["basis"] = "fft-window-and-hop-v1" }
    });

    private static JsonObject Component(string mode) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
        "draft", mode == "audio-only" ? "audio-full-v2.json" : "video-full-v2.json")))!.AsObject();

    private static byte[] Bytes(JsonObject value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static async Task Stage(DraftAnalysisCompositionSession session, string mode)
    {
        var value = Component(mode);
        value["snapshot"] = JsonNode.Parse(session.Snapshot.GetRawText());
        using var stream = new MemoryStream(Bytes(value));
        await session.StageAsync(mode, stream, default);
    }

    private static async Task StageBoth(DraftAnalysisCompositionSession session)
    {
        await Stage(session, "audio-only");
        await Stage(session, "video-only");
    }

    private sealed class RepositoryFixture(string path, AnalysisWriteCoordinator writes, AnalysisRepository repository, AnalysisRecord source) : IAsyncDisposable
    {
        public AnalysisWriteCoordinator Writes { get; } = writes;
        public AnalysisRepository Repository { get; } = repository;
        public AnalysisRecord Source { get; } = source;
        public MediaFingerprint CurrentMedia { get; set; } = Media;
        public AnalysisKey SourceKey => new(Media.ItemId, Media.MediaSourceId, "aether-visual", "1.1.0");
        public AnalysisKey TargetKey => SourceKey with { AlgorithmVersion = DraftAnalysisMasterBuilder.AlgorithmVersion };
        public string StagingDirectory { get; private set; } = string.Empty;

        public async Task<DraftAnalysisCompositionSession> BeginAsync(int? ffmpegStreamIndex = 1, string expectedProducerRevision = ProducerRevision)
        {
            var before = Directory.GetDirectories(Path.GetTempPath(), "aether-draft-composition-*");
            var session = await DraftAnalysisCompositionSession.BeginAsync(Repository, Writes, _ => Task.FromResult<MediaFingerprint?>(CurrentMedia),
                Media.ItemId, Media.MediaSourceId, SourceKey, ffmpegStreamIndex, null, expectedProducerRevision, 50 * 1024 * 1024, default);
            StagingDirectory = Assert.Single(Directory.GetDirectories(Path.GetTempPath(), "aether-draft-composition-*").Except(before));
            return session;
        }

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "aether-draft-tests-" + Guid.NewGuid().ToString("N") + ".sqlite");
            var factory = new Factory(new DbContextOptionsBuilder<AnalysisDbContext>().UseSqlite("Data Source=" + path).Options);
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            var writes = new AnalysisWriteCoordinator();
            var repository = new AnalysisRepository(factory, writes);
            var source = StoredAnalysisTestData.Create(Media, version: "1.1.0");
            await repository.UpsertAsync(source, null, default);
            return new RepositoryFixture(path, writes, repository, source);
        }

        public ValueTask DisposeAsync()
        {
            Writes.Dispose();
            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Factory(DbContextOptions<AnalysisDbContext> options) : IDbContextFactory<AnalysisDbContext>
    {
        public AnalysisDbContext CreateDbContext() => new(options);
        public Task<AnalysisDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
