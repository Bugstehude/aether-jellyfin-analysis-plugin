using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>Host-verified identity and track selection for an isolated composition.</summary>
public sealed record DraftArtifactContext(
    Guid ItemId,
    string MediaSourceId,
    string SourceFingerprint,
    long DurationMs,
    int? FfmpegStreamIndex,
    int? JellyfinStreamIndex,
    string ExpectedProducerRevision,
    int MaximumDocumentBytes = 50 * 1024 * 1024,
    string TargetVersion = DraftAnalysisMasterBuilder.AlgorithmVersion);

/// <summary>
/// Experimental Full component and composed-master validator. The admitted audio profile has a proven
/// zero-origin contiguous legacy clock. Other profiles fail closed until separately verified.
/// This class is not registered in production.
/// </summary>
public static class DraftAnalysisArtifactValidator
{
    /// <summary>Validates a bounded v2 component, preserving unknown raw properties.</summary>
    public static JsonElement Validate(ReadOnlyMemory<byte> json, DraftArtifactContext context, string expectedMode)
    {
        ArgumentNullException.ThrowIfNull(context);
        Check(context.MaximumDocumentBytes is > 0 and <= 50 * 1024 * 1024
            && json.Length <= context.MaximumDocumentBytes, "artifact-budget");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RejectDuplicates(root);
            var revision = ValidateEnvelope(root, context, expectedMode);
            if (expectedMode == "audio-only")
            {
                Audio(root.GetProperty("audio"), root.GetProperty("representation"), root.GetProperty("signalProvenance"), context, revision);
            }
            else
            {
                Video(root.GetProperty("video"), root.GetProperty("representation"), root.GetProperty("signalProvenance"), context, revision);
            }

            return root.Clone();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("artifact-invalid-shape", exception);
        }
    }

    /// <summary>
    /// Revalidates a stored Full composition against fresh host/track context without rewriting
    /// its document or reconstructing historical component payloads. EOF-pending cuts retain
    /// their measured partial coverage and are a valid completed result of this profile.
    /// </summary>
    public static JsonElement ValidateStoredMaster(ReadOnlyMemory<byte> json, DraftArtifactContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Check(context.MaximumDocumentBytes is > 0 and <= 50 * 1024 * 1024
            && json.Length <= context.MaximumDocumentBytes, "master-budget");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RejectDuplicates(root);
            Check(root.GetProperty("schemaVersion").GetInt32() == 2, "master-schema");
            Check(context.TargetVersion is "1.2.0-draft" or "1.2.0", "master-target-version");
            Equal(root.GetProperty("algorithm"), "id", "aether-visual");
            Equal(root.GetProperty("algorithm"), "version", context.TargetVersion);
            var item = root.GetProperty("item");
            Check(Guid.Parse(item.GetProperty("id").GetString()!) == context.ItemId, "master-item");
            Equal(item, "mediaSourceId", context.MediaSourceId);
            Equal(item, "fingerprint", context.SourceFingerprint);
            Equal(item, "fingerprintQuality", "strong");
            _ = root.GetProperty("storedAt").GetDateTimeOffset();

            var composition = root.GetProperty("draftComposition");
            var audio = composition.GetProperty("audioArtifact");
            var video = composition.GetProperty("videoArtifact");
            var revision = ValidateEnvelope(audio, context, "audio-only");
            _ = ValidateEnvelope(video, context, "video-only");
            foreach (var property in new[] { "snapshot", "localInputCheck", "durationSource" })
            {
                Check(JsonElement.DeepEquals(audio.GetProperty(property), video.GetProperty(property)), "master-components-disagree");
            }

            Check(JsonElement.DeepEquals(root.GetProperty("representation"), audio.GetProperty("representation"))
                && audio.GetProperty("representation").GetProperty("sourceIntervalMs").GetInt32()
                    == video.GetProperty("representation").GetProperty("sourceIntervalMs").GetInt32(), "master-representation");
            var createdAt = new[] { audio.GetProperty("createdAt").GetDateTimeOffset(), video.GetProperty("createdAt").GetDateTimeOffset() }.Max();
            Check(root.GetProperty("createdAt").GetDateTimeOffset() == createdAt, "master-created-at");
            Check(composition.GetProperty("opaqueAudioCollisions").ValueKind == JsonValueKind.Object, "master-collision-metadata");

            var provenance = root.GetProperty("signalProvenance");
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var metadata in new[] { audio, video })
            {
                foreach (var group in metadata.GetProperty("signalProvenance").EnumerateObject())
                {
                    Check(groups.Add(group.Name) && provenance.TryGetProperty(group.Name, out var value)
                        && JsonElement.DeepEquals(value, group.Value), "master-provenance");
                }
            }

            Check(provenance.EnumerateObject().Count() == groups.Count, "master-provenance");
            Audio(root, root.GetProperty("representation"), provenance, context, revision);
            Video(root, video.GetProperty("representation"), provenance, context, revision, storedMaster: true);
            _ = root.GetProperty("timebase").GetProperty("video").GetProperty("sourceTimeline");
            ValidateStoredFrameAudio(root);
            return root.Clone();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("master-invalid-shape", exception);
        }
    }

    private static void ValidateStoredFrameAudio(JsonElement root)
    {
        var available = root.GetProperty("audioAnalysis").GetProperty("state").GetString() == "available"
            && root.GetProperty("legacyAudioAnalysis").GetProperty("timeView").GetProperty("sharedMediaGridProven").GetBoolean();
        var interval = root.GetProperty("sampling").GetProperty("intervalMs").GetInt32();
        var rows = available ? root.GetProperty("audioFrames") : default;
        var measured = available ? root.GetProperty("legacyAudioAnalysis").GetProperty("counters").GetProperty("measuredWindows").GetInt32() : 0;
        var end = available ? root.GetProperty("legacyAudioAnalysis").GetProperty("coverage")[0].GetProperty("endMs").GetInt64() : 0;
        if (available)
        {
            Check(root.GetProperty("legacyAudioAnalysis").GetProperty("timeView").GetProperty("intervalMs").GetInt32() == interval,
                "master-legacy-interval");
        }

        foreach (var frame in root.GetProperty("frames").EnumerateArray())
        {
            var time = frame.GetProperty("timestampMs").GetInt64();
            var index = time / interval;
            var expected = available && time % interval == 0 && time < end && index < measured && index < rows.GetArrayLength();
            var attached = frame.TryGetProperty("audio", out var audio);
            Check(attached == expected, "master-frame-audio-attachment");
            if (attached)
            {
                var row = rows[(int)index];
                Check(audio.GetProperty("rms").GetDouble() == row.GetProperty("rms").GetDouble()
                    && audio.GetProperty("flux").GetDouble() == row.GetProperty("flux").GetDouble(), "master-frame-audio-values");
            }
        }
    }

    /// <summary>Decodes the sole packed representation with bounded record memory.</summary>
    public static PackedDenseAudioDraftCodec DecodeAudio(JsonElement audio, long durationMs, int maximumBytes)
    {
        Check(!audio.TryGetProperty("denseAudioFrames", out _), "artifact-double-audio-representation");
        var value = audio.GetProperty("packedDenseAudioFrames");
        return PackedDenseAudioDraftCodec.Decode(new PackedDenseAudioDraftEnvelope(
            value.GetProperty("encoding").GetString()!, value.GetProperty("pointCount").GetInt32(),
            value.GetProperty("strideBytes").GetInt32(), value.GetProperty("checksum").GetString()!,
            value.GetProperty("dataBase64").GetString()!), durationMs, maximumBytes);
    }

    private static string ValidateEnvelope(JsonElement root, DraftArtifactContext context, string expectedMode)
    {
        Check(root.GetProperty("artifactVersion").GetInt32() == 2, "artifact-version");
        Equal(root, "kind", "aether-analysis-job-artifact");
        Equal(root, "mode", expectedMode);
        Check(expectedMode is "audio-only" or "video-only", "artifact-mode");
        Algorithm(root.GetProperty("algorithm"));
        var snapshot = root.GetProperty("snapshot");
        Check(Guid.Parse(snapshot.GetProperty("itemId").GetString()!) == context.ItemId, "artifact-item");
        Equal(snapshot, "mediaSourceId", context.MediaSourceId);
        Equal(snapshot, "sourceFingerprint", context.SourceFingerprint);
        Check(root.GetProperty("durationMs").GetInt64() == context.DurationMs, "artifact-duration");
        Check(root.GetProperty("mediaOriginUs").GetInt64() == 0, "artifact-unverified-origin-profile");
        Equal(root.GetProperty("representation"), "detail", "full");
        Check(!root.GetProperty("representation").GetProperty("derived").GetBoolean(), "artifact-reduced-input");
        Equal(root.GetProperty("representation"), "visualAggregation", "none");
        Equal(root.GetProperty("representation"), "reductionVersion", "aether-reduction-2-draft");
        var revision = root.GetProperty("producerRevision").GetString()!;
        Check(IsHash(revision) && revision == context.ExpectedProducerRevision && IsHash(context.SourceFingerprint)
            && context.SourceFingerprint != "sha256:" + new string('0', 64), "artifact-unverified-identity");
        Check(root.GetProperty("localInputCheck").GetProperty("stableAtEnd").GetBoolean(), "artifact-input-changed");
        _ = root.GetProperty("createdAt").GetDateTimeOffset();
        Check(!root.TryGetProperty(expectedMode == "audio-only" ? "video" : "audio", out _), "artifact-extra-component");
        return revision;
    }

    private static void Audio(JsonElement audio, JsonElement representation, JsonElement provenance, DraftArtifactContext context, string revision)
    {
        var analysis = audio.GetProperty("audioAnalysis");
        if (analysis.GetProperty("state").GetString() == "no-track")
        {
            NoTrack(audio, representation, provenance, context, revision);
            return;
        }

        if (analysis.GetProperty("state").GetString() is "decode-error" or "unsupported")
        {
            NotQualified(audio, representation, provenance, context, revision);
            return;
        }

        Equal(analysis, "state", "available");
        Equal(analysis, "completeness", "complete");
        Track(analysis.GetProperty("referenceTrack"), context);
        Check(analysis.GetProperty("sampleRateHz").GetInt32() == 22050
            && analysis.GetProperty("fftSize").GetInt32() == 2048
            && analysis.GetProperty("hopSamples").GetInt32() == 441
            && analysis.GetProperty("streamOffsetUs").GetInt64() >= 0
            && analysis.GetProperty("streamOffsetUs").GetInt64() < context.DurationMs * 1000, "audio-dsp-parameters");
        // A decoded start after media zero (e.g. a dropped AAC priming frame) shifts every dense
        // timestamp by this offset. It is derived exactly from the first decoded PCM PTS below.
        var offsetUs = analysis.GetProperty("streamOffsetUs").GetInt64();
        Equal(analysis, "window", "hann-symmetric-v1");
        Equal(analysis, "downmix", "channel-arithmetic-mean-v1");
        Equal(analysis, "timestampReference", "window-center-v1");
        Equal(analysis, "producerVersion", revision);
        var methods = analysis.GetProperty("methods");
        Equal(methods, "methodId", "hann-linear-audio-v1-draft");
        Equal(methods, "rms", "hann-squared-valid-weight-v1");
        Equal(methods, "bands", "one-sided-parseval-valid-weight-v1");
        Equal(methods, "flux", "positive-mean-one-sided-amplitude-v1");
        Equal(methods, "fluxBins", "20-through-8000hz-half-open-v1");
        Equal(methods, "edges", "outer-zero-pad-valid-weight-v1");
        Equal(methods, "gaps", "discard-crossing-windows-reset-flux-v1");
        Equal(methods, "timestamp", "decoded-pcm-pts-v1");
        Equal(methods, "onsetStrength", "file-peak-flux-v1");
        Equal(methods, "onsetStationarityGate", "preceding-window-rms-rise-v1-draft");
        Equal(methods, "onsetEof", "unconfirmed-candidates-discarded-v1-draft");
        var onset = analysis.GetProperty("onsetMethod");
        Equal(onset, "methodId", "spectral-flux-mad-v1-draft");
        Equal(onset, "boundaryPolicy", "full-valid-windows-only-v1-draft");
        Check(onset.GetProperty("baselineWindowMs").GetInt32() == 1000
            && onset.GetProperty("madMultiplier").GetDouble() == 3
            && onset.GetProperty("minFlux").GetDouble() == 1e-6
            && onset.GetProperty("refractoryMs").GetInt32() == 60
            && onset.GetProperty("confirmationHops").GetInt32() == 1
            && onset.GetProperty("minRmsRiseFraction").GetDouble() == 0.01, "audio-onset-parameters");
        var pcm = analysis.GetProperty("sourcePcm");
        var pcmBase = pcm.GetProperty("ptsTimeBase");
        Check(pcmBase.GetProperty("num").GetInt32() == 1
            && pcmBase.GetProperty("den").GetInt32() == 22050, "audio-pcm-timebase");
        var firstPts = Pts(pcm.GetProperty("firstPts").GetString()!);
        Check(pcm.GetProperty("mediaOriginUs").GetInt64() == 0 && firstPts >= 0
            && offsetUs == Round(firstPts * 1_000_000, 22050), "audio-unverified-pcm-origin");
        var sourceBase = pcm.GetProperty("sourcePtsTimeBase");
        var tolerance = (int)BigInteger.DivRem(
            2 * (BigInteger)Positive(sourceBase, "num") * 22050 + Positive(sourceBase, "den") - 1,
            Positive(sourceBase, "den"), out _);
        Check(tolerance == pcm.GetProperty("quantizationToleranceSamples").GetInt32(), "audio-pcm-tolerance");
        Equal(pcm, "reconciliation", "within-two-source-ticks-contiguous-pcm-v1-draft");
        var features = analysis.GetProperty("features").EnumerateArray().Select(value => value.GetString()).ToArray();
        Check(features.Length == 4 && features.Distinct(StringComparer.Ordinal).Count() == 4
            && features.All(value => value is "rms-linear" or "flux-linear" or "bands" or "onsets"), "audio-features");
        var legacy = Legacy(audio, context);
        var samples = legacy.GetProperty("counters").GetProperty("decodedSamples").GetInt64();
        Check(samples > 0 && samples <= int.MaxValue, "audio-sample-count");
        // Both groups describe the same decoded PCM, each with its documented integer rule:
        // dense audio rounds sample time (JavaScript Math.round), legacy floors conservatively.
        // Neither may claim media time beyond the host duration, so both are capped there.
        var coverage = Coverage(analysis.GetProperty("coverage"), context.DurationMs);
        Check(coverage.Length == 1 && coverage[0].Start == Round(offsetUs, 1000)
            && coverage[0].End == Math.Min(context.DurationMs, Round(offsetUs * (BigInteger)22050 + samples * (BigInteger)1_000_000, 22_050_000)),
            "audio-unverified-coverage-profile");
        var legacyCoverage = Coverage(legacy.GetProperty("coverage"), context.DurationMs);
        var legacyEnd = Math.Min(context.DurationMs, (long)(samples * (BigInteger)1000 / 22050));
        Check(legacyEnd > 0 && legacyCoverage.Length == 1 && legacyCoverage[0].Start == 0
            && legacyCoverage[0].End == legacyEnd, "audio-legacy-coverage");
        var series = DecodeAudio(audio, context.DurationMs, context.MaximumDocumentBytes);
        Check(series.Count > 0 && series.Count == ExpectedDenseCount(samples, context.DurationMs, offsetUs)
            && audio.GetProperty("measuredFrameCount").GetInt32() == series.Count, "audio-full-count");
        var normalization = analysis.GetProperty("normalization");
        Equal(normalization, "raw", "digital-full-scale-v1");
        Equal(normalization, "driveProjection", "file-peak-v1");
        double maxRms = 0, maxFlux = 0;
        var timestamps = new long[series.Count];
        for (var index = 0; index < series.Count; index++)
        {
            var point = series.GetPoint(index);
            var start = checked(index * 441 - 1023);
            var valid = Math.Min(samples, start + 2048L) - Math.Max(0, start);
            Check(point.WindowStartSample == start && point.ValidSamples == valid && point.HasBands
                && point.TimestampMs == DenseTimestamp(start, offsetUs)
                && point.TimestampMs < coverage[0].End, "audio-sample-time-or-window");
            Check(index != 0 || point.SpectralFluxLinear == 0, "audio-first-flux");
            timestamps[index] = point.TimestampMs;
            maxRms = Math.Max(maxRms, point.RmsLinear);
            maxFlux = Math.Max(maxFlux, point.SpectralFluxLinear);
        }

        Check(normalization.GetProperty("peakRmsLinear").GetDouble() == maxRms
            && normalization.GetProperty("peakFluxLinear").GetDouble() == maxFlux, "audio-full-peaks");
        Equal(representation, "denseAudioSelection", "all-measurements-v1");
        Check(representation.GetProperty("denseAudioTargetIntervalMs").GetInt32() == 20
            && representation.GetProperty("denseAudioMaxGapMs").GetInt64() == MaximumGap(series), "audio-full-representation");
        Events(audio.GetProperty("audioOnsets"), "strength", context.DurationMs, coverage);
        long previousOnset = -60;
        foreach (var item in audio.GetProperty("audioOnsets").EnumerateArray())
        {
            var timestamp = item.GetProperty("timestampMs").GetInt64();
            var index = Array.BinarySearch(timestamps, timestamp);
            Check(index > 0 && index + 1 < series.Count, "onset-unmeasured-point");
            var point = series.GetPoint(index);
            var confirmed = series.GetPoint(index + 1);
            Equal(item, "id", "onset-" + point.WindowStartSample.ToString(CultureInfo.InvariantCulture));
            Equal(item.GetProperty("uncertainty"), "basis", "fft-window-and-hop-v1");
            var radius = (1023.5 + 441.0 / 2) * 1000 / 22050;
            Check(point.TimestampMs == timestamp && point.ValidSamples == 2048 && confirmed.ValidSamples == 2048
                && item.GetProperty("confirmedAtMs").GetInt64() == confirmed.TimestampMs
                && maxFlux > 0 && item.GetProperty("strength").GetDouble() == point.SpectralFluxLinear / maxFlux
                && timestamp - previousOnset >= 60
                && item.GetProperty("uncertainty").GetProperty("earliestMs").GetInt64() == Math.Max(0, (long)Math.Floor(timestamp - radius))
                && item.GetProperty("uncertainty").GetProperty("latestMs").GetInt64() == Math.Min(context.DurationMs, (long)Math.Ceiling(timestamp + radius)),
                "onset-measurement-reference");
            previousOnset = timestamp;
        }

        Provenance(provenance, "denseAudio", "hann-linear-audio-v1-draft", "1.2.0-draft", context, revision);
        Provenance(provenance, "legacyAudio", "legacy-rms-flux-audio-1.1", "1.1.0", context, revision);
    }

    private static void NoTrack(JsonElement audio, JsonElement representation, JsonElement provenance, DraftArtifactContext context, string revision)
    {
        var analysis = audio.GetProperty("audioAnalysis");
        Equal(analysis, "completeness", "complete");
        Check(context.FfmpegStreamIndex is null && context.JellyfinStreamIndex is null
            && analysis.GetProperty("referenceTrack").ValueKind == JsonValueKind.Null
            && audio.GetProperty("measuredFrameCount").GetInt32() == 0, "audio-no-track-host-selection");
        foreach (var property in new[] { "packedDenseAudioFrames", "denseAudioFrames", "audioFrames", "audioOnsets", "legacyAudioAnalysis", "legacyAudioProvenance" })
        {
            Check(!audio.TryGetProperty(property, out _), "audio-no-track-fabricated-group");
        }

        foreach (var property in new[] { "sampleRateHz", "streamOffsetUs", "downmix", "fftSize", "window", "hopSamples", "timestampReference", "coverage", "features", "normalization", "onsetMethod", "methods", "sourcePcm" })
        {
            Check(!analysis.TryGetProperty(property, out _), "audio-no-track-fabricated-measurement");
        }

        foreach (var property in new[] { "denseAudioTargetIntervalMs", "denseAudioSelection", "denseAudioMaxGapMs" })
        {
            Check(!representation.TryGetProperty(property, out _), "audio-no-track-fabricated-representation");
        }

        Check(!provenance.TryGetProperty("legacyAudio", out _), "audio-no-track-legacy-provenance");
        Provenance(provenance, "denseAudio", "audio-stream-probe-v1-draft", "1.2.0-draft", context, revision);
    }

    /// <summary>
    /// Audio of the selected track that is not a complete contiguous measurement. Image and cuts
    /// are still published; audio carries only its state, error code and actual reference track.
    /// </summary>
    private static void NotQualified(JsonElement audio, JsonElement representation, JsonElement provenance, DraftArtifactContext context, string revision)
    {
        var analysis = audio.GetProperty("audioAnalysis");
        Equal(analysis, "completeness", "absent");
        Check(context.FfmpegStreamIndex is not null && audio.GetProperty("measuredFrameCount").GetInt32() == 0, "audio-unqualified-host-selection");
        Track(analysis.GetProperty("referenceTrack"), context);
        var code = analysis.GetProperty("errorCode").GetString() ?? string.Empty;
        Check(code.Length is > 0 and <= 64 && code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'),
            "audio-unqualified-error-code");
        if (analysis.TryGetProperty("producerVersion", out _))
        {
            Equal(analysis, "producerVersion", revision);
        }

        foreach (var property in analysis.EnumerateObject())
        {
            Check(property.Name is "state" or "completeness" or "referenceTrack" or "errorCode" or "producerVersion" or "sourcePcm",
                "audio-unqualified-fabricated-measurement");
        }

        foreach (var property in new[] { "packedDenseAudioFrames", "denseAudioFrames", "audioFrames", "audioOnsets", "legacyAudioAnalysis", "legacyAudioProvenance" })
        {
            Check(!audio.TryGetProperty(property, out _), "audio-unqualified-fabricated-group");
        }

        foreach (var property in new[] { "denseAudioTargetIntervalMs", "denseAudioSelection", "denseAudioMaxGapMs" })
        {
            Check(!representation.TryGetProperty(property, out _), "audio-unqualified-fabricated-representation");
        }

        Check(!provenance.TryGetProperty("legacyAudio", out _), "audio-unqualified-legacy-provenance");
        Provenance(provenance, "denseAudio", "audio-stream-probe-v1-draft", "1.2.0-draft", context, revision);
    }

    private static JsonElement Legacy(JsonElement audio, DraftArtifactContext context)
    {
        var legacy = audio.GetProperty("legacyAudioAnalysis");
        Equal(legacy, "state", "available");
        Equal(legacy, "completeness", "complete");
        // Legacy declares its original FFmpeg namespace. A Jellyfin index is optional
        // here, but when present it must match the separately verified host mapping.
        Track(legacy.GetProperty("referenceTrack"), context, requireJellyfinIndex: false);
        var view = legacy.GetProperty("timeView");
        Equal(view, "methodId", "legacy-concatenated-pcm-window-start-v1");
        Equal(view, "nominalGrid", "timestampMs=index*intervalMs");
        Equal(view, "decodedWindowGrid", "startSample=index*round(intervalMs/1000*22050)");
        var interval = view.GetProperty("intervalMs").GetInt32();
        Check(interval is >= 250 and <= 10000 && interval * 22050L % 1000 == 0
            && view.GetProperty("samplesPerWindow").GetInt32() == interval * 22050 / 1000
            && view.GetProperty("sampleRateHz").GetInt32() == 22050
            && view.GetProperty("mediaOriginUs").GetInt64() == 0, "legacy-unverified-clock");
        if (view.GetProperty("sharedMediaGridProven").GetBoolean())
        {
            Equal(view, "firstOutputPts", "0");
            Check(view.GetProperty("contiguousOutputPts").GetBoolean()
                && view.GetProperty("sourceStartUs").GetInt64() == 0
                && view.GetProperty("containerStartUs").GetInt64() == 0, "legacy-unverified-clock");
        }
        else
        {
            // Without a shared media-grid proof the series keeps its own grid and actual first
            // output PTS. It is never attached to image frames (see ValidateStoredFrameAudio).
            Check(Pts(view.GetProperty("firstOutputPts").GetString()!) >= 0
                && view.GetProperty("contiguousOutputPts").ValueKind is JsonValueKind.True or JsonValueKind.False,
                "legacy-unverified-clock");
        }
        var provenance = audio.GetProperty("legacyAudioProvenance");
        Equal(provenance, "methodId", "legacy-rms-flux-audio-1.1");
        Equal(provenance, "rms", "unweighted-whole-decoded-window-v1");
        Equal(provenance, "flux", "positive-magnitude-sum-first-window-zero-v1");
        Equal(provenance, "fft", "symmetric-hann-first-2048-zero-pad-1024-bins-exclude-nyquist-v1");
        Equal(provenance, "downmix", "ffmpeg-implicit-ac1-ar22050-f32le-v1");
        Equal(provenance, "normalization", "file-peak-floor-1e-9-v1");
        Equal(provenance, "defaultTrackProof", "actual-implicit-ffmpeg-output-stream-mapping-t0-v1");
        Equal(provenance, "sourceReuse", "fresh-decode-no-legacy-reuse");
        Check(provenance.GetProperty("sampleRateHz").GetInt32() == 22050
            && provenance.GetProperty("fftSize").GetInt32() == 2048
            && provenance.GetProperty("selectedStreamIndex").GetInt32() == context.FfmpegStreamIndex
            && provenance.GetProperty("implicitDefaultStreamIndex").GetInt32() == context.FfmpegStreamIndex
            && provenance.GetProperty("productionDefaultTrackEquivalent").GetBoolean(), "legacy-unverified-track");
        var counters = legacy.GetProperty("counters");
        var count = audio.GetProperty("audioFrames").GetArrayLength();
        var samples = counters.GetProperty("decodedSamples").GetInt64();
        var measured = counters.GetProperty("measuredWindows").GetInt32();
        var normalization = legacy.GetProperty("normalization");
        Check(count is > 0 and <= 864000
            // The legacy grid has one window per started interval of host duration. Audio
            // decoded past that grid is not measured, so the window count is capped there.
            && samples > 0 && measured == Math.Min(count,
                (samples + view.GetProperty("samplesPerWindow").GetInt32() - 1) / view.GetProperty("samplesPerWindow").GetInt32())
            && measured + counters.GetProperty("zeroPaddedWindows").GetInt32() == count
            && counters.GetProperty("zeroPaddedWindows").GetInt32() >= 0
            && counters.GetProperty("pcmBytes").GetInt64() == samples * 4
            && double.IsFinite(normalization.GetProperty("peakRms").GetDouble()) && normalization.GetProperty("peakRms").GetDouble() >= 1e-9
            && double.IsFinite(normalization.GetProperty("peakFlux").GetDouble()) && normalization.GetProperty("peakFlux").GetDouble() >= 1e-9,
            "legacy-counters");
        Check(legacy.GetProperty("normalization").GetProperty("floor").GetDouble() == 1e-9, "legacy-normalization");
        var index = 0;
        foreach (var row in audio.GetProperty("audioFrames").EnumerateArray())
        {
            Check(row.GetProperty("timestampMs").GetInt64() == index * (long)interval, "legacy-raster");
            Unit(row, "rms");
            Unit(row, "flux");
            Check(index != 0 || row.GetProperty("flux").GetDouble() == 0, "legacy-first-flux");
            index++;
        }

        return legacy;
    }

    private static void Video(JsonElement video, JsonElement representation, JsonElement provenance, DraftArtifactContext context, string revision, bool storedMaster = false)
    {
        foreach (var property in new[] { "audioFrames", "audioAnalysis", "audioOnsets", "denseAudioFrames", "packedDenseAudioFrames", "legacyAudioAnalysis", "legacyAudioProvenance", "measuredFrameCount" })
        {
            Check(storedMaster || !video.TryGetProperty(property, out _), "video-unvalidated-audio-group");
        }
        if (storedMaster)
        {
            Equal(video.GetProperty("algorithm"), "id", "aether-visual");
            Equal(video.GetProperty("algorithm"), "version", context.TargetVersion);
        }
        else
        {
            Algorithm(video.GetProperty("algorithm"));
        }
        Check(video.GetProperty("durationMs").GetInt64() == context.DurationMs, "video-duration");
        if (!storedMaster) Equal(video, "mediaFingerprintAtStart", context.SourceFingerprint);
        Equal(video.GetProperty("producer"), "sourceRevision", revision);
        Check(new AnalysisDocumentValidator().IsValidStoredMaster(video, context.SourceFingerprint), "video-legacy-base");
        var timebase = video.GetProperty("timebase");
        Check(timebase.GetProperty("mediaOriginUs").GetInt64() == 0, "video-origin");
        var videoTime = timebase.GetProperty("video");
        Equal(videoTime, "timestampBasis", "source-pts");
        var ptsBase = videoTime.GetProperty("ptsTimeBase");
        var num = Positive(ptsBase, "num");
        var den = Positive(ptsBase, "den");
        var rate = videoTime.GetProperty("requestedRate");
        var rateNum = Positive(rate, "num");
        var rateDen = Positive(rate, "den");
        var interval = video.GetProperty("sampling").GetProperty("intervalMs").GetInt32();
        Check(interval == Round(1000 * (BigInteger)rateDen, rateNum)
            && representation.GetProperty("sourceIntervalMs").GetInt32() == interval, "video-requested-rate");
        long previous = -1;
        var index = 0;
        foreach (var frame in video.GetProperty("frames").EnumerateArray())
        {
            var timestamp = frame.GetProperty("timestampMs").GetInt64();
            Check(timestamp > previous && timestamp < context.DurationMs
                && timestamp == Round(Pts(frame.GetProperty("sourcePts").GetString()!) * num * 1000, den)
                && frame.GetProperty("requestedTimestampMs").GetInt64() == Round(index * (BigInteger)1000 * rateDen, rateNum)
                && (storedMaster || !frame.TryGetProperty("audio", out _)), "video-source-pts");
            previous = timestamp;
            index++;
        }

        var cuts = video.GetProperty("cutAnalysis");
        Equal(cuts, "state", "measured");
        Check(cuts.GetProperty("completeness").GetString() is "complete" or "partial", "cuts-completeness");
        Equal(cuts, "methodId", "sampled-spatial-cut-v1-draft");
        Check(cuts.GetProperty("confirmationFrames").GetInt32() == 2, "cuts-confirmation");
        var parameters = cuts.GetProperty("parameters");
        Equal(parameters, "descriptor", "16x9-rgb-tile-means");
        Equal(parameters, "eofCoverageRule", "exclude-pending-bracket-and-tail");
        Equal(parameters, "coverageEndBasis", "last-selected-source-pts");
        Check(parameters.GetProperty("jumpThreshold").GetDouble() == 0.22
            && parameters.GetProperty("stabilityThreshold").GetDouble() == 0.1
            && parameters.GetProperty("panSearchTiles").GetProperty("x").GetInt32() == 2
            && parameters.GetProperty("panSearchTiles").GetProperty("y").GetInt32() == 1, "cuts-method-parameters");
        var coverage = Coverage(cuts.GetProperty("coverage"), context.DurationMs);
        Check(coverage.All(value => value.End <= previous), "cuts-unmeasured-tail");
        Events(video.GetProperty("cutEvents"), "score", context.DurationMs, coverage);
        var frames = video.GetProperty("frames").EnumerateArray().ToArray();
        if (videoTime.TryGetProperty("sourceTimeline", out var sourceTimeline))
        {
            Check(sourceTimeline.GetProperty("frameCount").GetInt32() == frames.Length
                && sourceTimeline.GetProperty("firstTimestampMs").GetInt64() == frames[0].GetProperty("timestampMs").GetInt64()
                && sourceTimeline.GetProperty("lastTimestampMs").GetInt64() == frames[^1].GetProperty("timestampMs").GetInt64()
                && sourceTimeline.GetProperty("firstSourcePts").GetString() == frames[0].GetProperty("sourcePts").GetString()
                && sourceTimeline.GetProperty("lastSourcePts").GetString() == frames[^1].GetProperty("sourcePts").GetString(), "video-source-timeline");
        }

        foreach (var item in video.GetProperty("cutEvents").EnumerateArray())
        {
            Equal(item, "kind", "hard-cut");
            CheckCutBracket(item, frames, true);
        }

        Check(cuts.GetProperty("pendingAtEof").GetArrayLength() <= 1, "cuts-multiple-eof-candidates");
        foreach (var pending in cuts.GetProperty("pendingAtEof").EnumerateArray())
        {
            Unit(pending, "score");
            CheckCutBracket(pending, frames, false);
            var timestamp = pending.GetProperty("timestampMs").GetInt64();
            var earliest = pending.GetProperty("uncertainty").GetProperty("earliestMs").GetInt64();
            var latest = pending.GetProperty("uncertainty").GetProperty("latestMs").GetInt64();
            Check(earliest >= 0 && earliest <= timestamp && timestamp <= latest && latest <= previous
                && coverage.All(value => value.End <= earliest), "cuts-pending-coverage");
        }

        Check((cuts.GetProperty("pendingAtEof").GetArrayLength() == 0)
            == (cuts.GetProperty("completeness").GetString() == "complete"), "cuts-eof-state");
        var startTime = frames[0].GetProperty("timestampMs").GetInt64();
        var endTime = cuts.GetProperty("pendingAtEof").GetArrayLength() == 0 ? previous
            : cuts.GetProperty("pendingAtEof")[0].GetProperty("uncertainty").GetProperty("earliestMs").GetInt64();
        Check(endTime <= startTime ? coverage.Length == 0
            : coverage.Length == 1 && coverage[0] == (startTime, endTime), "cuts-declared-coverage");
        Provenance(provenance, "visual", "visual-globals-1.1", "1.1.0", context, revision);
        Provenance(provenance, "cuts", "sampled-spatial-cut-v1-draft", "1.2.0-draft", context, revision);
        foreach (var group in new[] { "visual", "cuts" })
        {
            var outer = provenance.GetProperty(group);
            Check(JsonElement.DeepEquals(outer, video.GetProperty("signalProvenance").GetProperty(group)), "video-provenance-mismatch");
            Equal(outer, "timeTransformation", "decoder-source-pts-minus-shared-media-origin-v1-draft");
        }
    }

    private static void CheckCutBracket(JsonElement item, JsonElement[] frames, bool confirmed)
    {
        var timestamp = item.GetProperty("timestampMs").GetInt64();
        var index = Array.FindIndex(frames, frame => frame.GetProperty("timestampMs").GetInt64() == timestamp);
        var uncertainty = item.GetProperty("uncertainty");
        Equal(uncertainty, "basis", "sample-bracket");
        Check(index > 0 && item.GetProperty("score").GetDouble() >= 0.22
            && uncertainty.GetProperty("latestMs").GetInt64() == timestamp
            && uncertainty.GetProperty("earliestMs").GetInt64() == frames[index - 1].GetProperty("timestampMs").GetInt64(), "cuts-sample-bracket");
        if (confirmed)
        {
            Check(index + 2 < frames.Length
                && item.GetProperty("confirmedAtMs").GetInt64() == frames[index + 2].GetProperty("timestampMs").GetInt64(), "cuts-source-confirmation");
        }
        else
        {
            Check(index + 2 >= frames.Length && !item.TryGetProperty("confirmedAtMs", out _), "cuts-eof-candidate");
        }
    }

    private static void Provenance(JsonElement provenance, string group, string method, string version, DraftArtifactContext context, string revision)
    {
        var value = provenance.GetProperty(group);
        Equal(value, "methodId", method);
        Equal(value, "algorithmVersion", version);
        Equal(value, "operation", "measured");
        Equal(value, "sourceFingerprint", context.SourceFingerprint);
        Equal(value, "producerVersion", revision);
    }

    private static void Track(JsonElement track, DraftArtifactContext context, bool requireJellyfinIndex = true)
    {
        Check(track.GetProperty("ffmpegStreamIndex").GetInt32() == context.FfmpegStreamIndex, "audio-track");
        var hasIndex = track.TryGetProperty("jellyfinStreamIndex", out var index);
        Check(context.JellyfinStreamIndex.HasValue
                ? (hasIndex ? index.GetInt32() == context.JellyfinStreamIndex : !requireJellyfinIndex)
                : !hasIndex,
            "audio-unverified-jellyfin-track");
    }

    private static (long Start, long End)[] Coverage(JsonElement value, long duration)
    {
        var result = new List<(long Start, long End)>();
        long previous = -1;
        foreach (var interval in value.EnumerateArray())
        {
            var start = interval.GetProperty("startMs").GetInt64();
            var end = interval.GetProperty("endMs").GetInt64();
            Check(start >= 0 && start >= previous && end > start && end <= duration, "coverage-invalid");
            result.Add((start, end));
            previous = end;
        }

        return result.ToArray();
    }

    private static void Events(JsonElement events, string score, long duration, (long Start, long End)[] coverage)
    {
        long previous = -1;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in events.EnumerateArray())
        {
            var timestamp = item.GetProperty("timestampMs").GetInt64();
            var confirmed = item.GetProperty("confirmedAtMs").GetInt64();
            var uncertainty = item.GetProperty("uncertainty");
            var earliest = uncertainty.GetProperty("earliestMs").GetInt64();
            var latest = uncertainty.GetProperty("latestMs").GetInt64();
            var id = item.GetProperty("id").GetString();
            Check(!string.IsNullOrEmpty(id) && ids.Add(id) && timestamp >= 0 && timestamp > previous
                && timestamp < duration && confirmed >= timestamp && confirmed < duration
                && earliest >= 0 && earliest <= timestamp && latest >= timestamp && latest <= duration
                && coverage.Any(value => timestamp >= value.Start && timestamp < value.End), "event-time-or-identity");
            Unit(item, score);
            previous = timestamp;
        }
    }

    /// <summary>Actual adjacent time gap, including irregular whole-record bucket selection.</summary>
    public static long MaximumGap(PackedDenseAudioDraftCodec series)
    {
        long gap = 0;
        for (var index = 1; index < series.Count; index++)
        {
            gap = Math.Max(gap, series.GetPoint(index).TimestampMs - series.GetPoint(index - 1).TimestampMs);
        }

        return gap;
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Check(names.Add(property.Name), "artifact-duplicate-property");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                RejectDuplicates(value);
            }
        }
    }

    private static BigInteger Pts(string value)
    {
        Check(value.Length is > 0 and <= 32 && value != "-"
            && value.Skip(value[0] == '-' ? 1 : 0).All(character => character is >= '0' and <= '9'), "pts-invalid");
        return BigInteger.Parse(value, CultureInfo.InvariantCulture);
    }

    private static long Positive(JsonElement value, string property)
    {
        var result = value.GetProperty(property).GetInt64();
        Check(result is > 0 and <= int.MaxValue, "timebase-invalid");
        return result;
    }

    /// <summary>
    /// Windows start every 441 samples at -1023. The worker keeps a window whose centre lies
    /// before the decoded end and whose rounded centre time lies before the host duration.
    /// </summary>
    private static long ExpectedDenseCount(long samples, long durationMs, long offsetUs)
    {
        var count = (samples + 440) / 441;
        while (count > 0 && DenseTimestamp((count - 1) * 441 - 1023, offsetUs) >= durationMs)
        {
            count--;
        }

        return count;
    }

    /// <summary>Window centre time: offset + (start + 1023.5) / 22050 s, rounded like JavaScript.</summary>
    private static long DenseTimestamp(long windowStart, long offsetUs) =>
        Round(offsetUs * (BigInteger)44100 + (2 * (BigInteger)windowStart + 2047) * 1_000_000, 44_100_000);

    // JavaScript Math.round ties toward positive infinity, including negative PTS.
    private static long Round(BigInteger numerator, BigInteger denominator)
    {
        var doubled = 2 * numerator + denominator;
        var divisor = 2 * denominator;
        var quotient = BigInteger.DivRem(doubled, divisor, out var remainder);
        return checked((long)(remainder.Sign < 0 ? quotient - 1 : quotient));
    }

    private static bool IsHash(string value) => value.Length == 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).ToArray().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Algorithm(JsonElement value)
    {
        Equal(value, "id", "aether-visual");
        Equal(value, "version", "1.2.0-draft");
    }

    private static void Unit(JsonElement value, string property)
    {
        var number = value.GetProperty(property).GetDouble();
        Check(double.IsFinite(number) && number is >= 0 and <= 1, "event-or-legacy-unit-range");
    }

    private static void Equal(JsonElement value, string property, string expected) =>
        Check(value.GetProperty(property).GetString() == expected, "artifact-" + property);

    private static void Check(bool condition, string error)
    {
        if (!condition)
        {
            throw new InvalidDataException(error);
        }
    }
}
