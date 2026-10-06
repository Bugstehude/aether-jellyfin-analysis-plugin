using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>Isolated Full composition and independent visual/audio reduction. Not a production service.</summary>
public static class DraftAnalysisMasterBuilder
{
    /// <summary>Draft identity deliberately excluded from the active compatibility matrix.</summary>
    public const string AlgorithmVersion = "1.2.0-draft";

    // application/json output, never embedded HTML. Keep Base64 '+' literal, as in the
    // worker's JSON output, so escaping does not artificially expand the payload budget.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly HashSet<string> ProtectedProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "item", "algorithm", "createdAt", "storedAt", "durationMs", "sampling",
        "producer", "representation", "frames", "timebase", "cutAnalysis", "cutEvents", "signalProvenance",
        "draftComposition", "mediaFingerprintAtStart"
    };

    /// <summary>Builds a Full document only from two contextually validated Full components.</summary>
    public static byte[] Build(
        ReadOnlyMemory<byte> audioJson,
        ReadOnlyMemory<byte> videoJson,
        DraftArtifactContext context,
        MediaFingerprint media,
        DateTimeOffset storedAt)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.ItemId != context.ItemId || media.MediaSourceId != context.MediaSourceId
            || media.Fingerprint != context.SourceFingerprint || media.DurationMs != context.DurationMs)
        {
            throw new InvalidDataException("composition-host-context");
        }

        var audioArtifact = DraftAnalysisArtifactValidator.Validate(audioJson, context, "audio-only");
        var videoArtifact = DraftAnalysisArtifactValidator.Validate(videoJson, context, "video-only");
        if (!JsonElement.DeepEquals(audioArtifact.GetProperty("snapshot"), videoArtifact.GetProperty("snapshot"))
            || !JsonElement.DeepEquals(audioArtifact.GetProperty("localInputCheck"), videoArtifact.GetProperty("localInputCheck"))
            || audioArtifact.GetProperty("durationSource").GetString() != videoArtifact.GetProperty("durationSource").GetString()
            || audioArtifact.GetProperty("producerRevision").GetString() != videoArtifact.GetProperty("producerRevision").GetString()
            || audioArtifact.GetProperty("representation").GetProperty("sourceIntervalMs").GetInt32()
                != videoArtifact.GetProperty("representation").GetProperty("sourceIntervalMs").GetInt32())
        {
            throw new InvalidDataException("composition-components-disagree");
        }

        var root = JsonNode.Parse(videoArtifact.GetProperty("video").GetRawText())!.AsObject();
        var audio = JsonNode.Parse(audioArtifact.GetProperty("audio").GetRawText())!.AsObject();
        var collisions = new JsonObject();
        foreach (var property in audio)
        {
            if (ProtectedProperties.Contains(property.Key) || root.ContainsKey(property.Key))
            {
                collisions[property.Key] = property.Value?.DeepClone();
            }
            else
            {
                root[property.Key] = property.Value?.DeepClone();
            }
        }

        var provenance = root["signalProvenance"]!.AsObject();
        foreach (var group in audioArtifact.GetProperty("signalProvenance").EnumerateObject())
        {
            if (provenance.ContainsKey(group.Name))
            {
                throw new InvalidDataException("composition-provenance-collision");
            }

            provenance[group.Name] = JsonNode.Parse(group.Value.GetRawText());
        }

        root.Remove("mediaFingerprintAtStart");
        root["item"] = new JsonObject
        {
            ["id"] = media.ItemId.ToString(),
            ["mediaSourceId"] = media.MediaSourceId,
            ["fingerprint"] = media.Fingerprint,
            ["fingerprintQuality"] = media.FingerprintQuality
        };
        if (context.TargetVersion is not (AlgorithmVersion or "1.2.0"))
        {
            throw new InvalidDataException("composition-target-version");
        }

        // A stable master is created only by composing fresh, validated components.
        // Component identities and measured-group provenance remain byte-faithful.
        root["algorithm"] = new JsonObject { ["id"] = "aether-visual", ["version"] = context.TargetVersion };
        root["storedAt"] = storedAt.ToString("O", CultureInfo.InvariantCulture);
        root["createdAt"] = new[] { audioArtifact.GetProperty("createdAt").GetDateTimeOffset(), videoArtifact.GetProperty("createdAt").GetDateTimeOffset() }
            .Max().ToString("O", CultureInfo.InvariantCulture);
        root["draftComposition"] = new JsonObject
        {
            ["audioArtifact"] = EnvelopeMetadata(audioArtifact),
            ["videoArtifact"] = EnvelopeMetadata(videoArtifact),
            ["opaqueAudioCollisions"] = collisions
        };
        var interval = root["sampling"]!["intervalMs"]!.GetValue<int>();
        var sourceFrames = root["frames"]!.AsArray();
        var sourceTimeline = root["timebase"]!["video"]!["sourceTimeline"] as JsonObject ?? new JsonObject();
        sourceTimeline["frameCount"] = sourceFrames.Count;
        sourceTimeline["firstTimestampMs"] = sourceFrames[0]!["timestampMs"]!.DeepClone();
        sourceTimeline["lastTimestampMs"] = sourceFrames[^1]!["timestampMs"]!.DeepClone();
        sourceTimeline["firstSourcePts"] = sourceFrames[0]!["sourcePts"]!.DeepClone();
        sourceTimeline["lastSourcePts"] = sourceFrames[^1]!["sourcePts"]!.DeepClone();
        if (sourceTimeline.Parent is null)
        {
            root["timebase"]!["video"]!["sourceTimeline"] = sourceTimeline;
        }

        // Attach only independently measured legacy values with exact media-time equality.
        // A padded nominal tail row is not a measurement and must not be attached to an image.
        // Legacy values without a shared media-grid proof keep their own grid and stay unattached.
        if (root["audioFrames"] is JsonArray rows
            && root["legacyAudioAnalysis"]!["timeView"]!["sharedMediaGridProven"]!.GetValue<bool>())
        {
            if (audio["legacyAudioAnalysis"]!["timeView"]!["intervalMs"]!.GetValue<int>() != interval)
            {
                throw new InvalidDataException("composition-legacy-interval");
            }

            var measuredWindows = root["legacyAudioAnalysis"]!["counters"]!["measuredWindows"]!.GetValue<int>();
            var end = root["legacyAudioAnalysis"]!["coverage"]![0]!["endMs"]!.GetValue<long>();
            foreach (var frame in root["frames"]!.AsArray())
            {
                var time = frame!["timestampMs"]!.GetValue<long>();
                var index = time / interval;
                if (time % interval == 0 && time < end && index < measuredWindows && index < rows.Count)
                {
                    var row = rows[(int)index]!;
                    frame["audio"] = new JsonObject { ["rms"] = row["rms"]!.DeepClone(), ["flux"] = row["flux"]!.DeepClone() };
                }
            }
        }

        root["representation"] = JsonNode.Parse(audioArtifact.GetProperty("representation").GetRawText());
        return SerializeBounded(root, context.MaximumDocumentBytes);
    }

    /// <summary>
    /// Derives Balanced/Compact from an admitted Full master. Dense audio is reduced even when
    /// the visual interval already meets the requested profile. Full reference peaks stay intact.
    /// </summary>
    public static AnalysisRepresentation Create(ReadOnlyMemory<byte> fullJson, string detail, int maximumDocumentBytes)
    {
        if (detail is not ("full" or "balanced" or "compact"))
        {
            throw new ArgumentException("Unknown draft detail.", nameof(detail));
        }

        if (maximumDocumentBytes is <= 0 or > 50 * 1024 * 1024 || fullJson.Length > 50 * 1024 * 1024)
        {
            throw new InvalidDataException("representation-budget");
        }

        using var document = JsonDocument.Parse(fullJson);
        var full = document.RootElement;
        var hasMeasuredAudio = full.GetProperty("audioAnalysis").GetProperty("state").GetString() == "available";
        if (full.GetProperty("algorithm").GetProperty("version").GetString() is not (AlgorithmVersion or "1.2.0")
            || full.GetProperty("representation").GetProperty("detail").GetString() != "full"
            || (hasMeasuredAudio && full.GetProperty("representation").GetProperty("denseAudioSelection").GetString() != "all-measurements-v1")
            || (!hasMeasuredAudio && full.GetProperty("audioAnalysis").GetProperty("state").GetString() is not ("no-track" or "decode-error" or "unsupported")))
        {
            throw new InvalidDataException("representation-requires-full-master");
        }

        if (detail == "full")
        {
            if (fullJson.Length > maximumDocumentBytes)
            {
                throw new InvalidDataException("representation-budget");
            }

            return new AnalysisRepresentation(detail, fullJson.ToArray(), AnalysisRepresentationService.CreateEtag(fullJson.Span),
                full.GetProperty("sampling").GetProperty("intervalMs").GetInt32());
        }

        var visual = new AnalysisRepresentationService().Create(fullJson.Span, detail);
        var root = JsonNode.Parse(visual.Json)!.AsObject();
        // The production reducer couples legacy audio to visual buckets. In this draft
        // the independently measured legacy timeline keeps its original grid and values.
        // Leave the active 1.0/1.1 reducer unchanged and restore the complete Full series.
        if (full.TryGetProperty("audioFrames", out var legacyAudio))
        {
            root["audioFrames"] = JsonNode.Parse(legacyAudio.GetRawText());
        }

        var sourceInterval = full.GetProperty("sampling").GetProperty("intervalMs").GetInt32();
        var aggregated = visual.IntervalMs > sourceInterval;
        var representation = JsonNode.Parse(full.GetProperty("representation").GetRawText())!.AsObject();
        representation["detail"] = detail;
        representation["derived"] = true;
        representation["visualAggregation"] = aggregated ? "bucket-mean-v1" : "none";
        if (hasMeasuredAudio)
        {
            var source = DraftAnalysisArtifactValidator.DecodeAudio(full, full.GetProperty("durationMs").GetInt64(), 50 * 1024 * 1024);
            var targetInterval = detail == "balanced" ? 50 : 100;
            var selected = source.Reduce(targetInterval);
            var packed = root["packedDenseAudioFrames"]!.AsObject();
            foreach (var property in JsonSerializer.SerializeToNode(selected.ToEnvelope(), Options)!.AsObject())
            {
                packed[property.Key] = property.Value?.DeepClone();
            }

            representation["denseAudioTargetIntervalMs"] = targetInterval;
            representation["denseAudioSelection"] = "bucket-max-rms-select-v1";
            representation["denseAudioMaxGapMs"] = DraftAnalysisArtifactValidator.MaximumGap(selected);
        }
        root["representation"] = representation;
        if (aggregated)
        {
            // Keep foreign frame metadata opaque and timed, without attributing a source PTS
            // to an average of multiple images. Known new events and group metadata stay raw.
            var opaque = new JsonArray();
            var known = new HashSet<string>(StringComparer.Ordinal)
            {
                "timestampMs", "sourcePts", "requestedTimestampMs", "luminance", "contrast", "saturation",
                "motionEnergy", "sceneCutProbability", "palette", "audio"
            };
            foreach (var frame in full.GetProperty("frames").EnumerateArray())
            {
                var extras = new JsonObject();
                foreach (var property in frame.EnumerateObject().Where(property => !known.Contains(property.Name)))
                {
                    extras[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }

                if (extras.Count > 0)
                {
                    opaque.Add(new JsonObject { ["timestampMs"] = frame.GetProperty("timestampMs").GetInt64(), ["properties"] = extras });
                }
            }

            if (opaque.Count > 0)
            {
                root["opaqueSourceFrameExtensions"] = opaque;
            }
        }

        var bytes = SerializeBounded(root, maximumDocumentBytes);
        return new AnalysisRepresentation(detail, bytes, AnalysisRepresentationService.CreateEtag(bytes), visual.IntervalMs);
    }

    private static JsonObject EnvelopeMetadata(JsonElement artifact)
    {
        var metadata = new JsonObject();
        foreach (var property in artifact.EnumerateObject().Where(property => property.Name is not ("audio" or "video")))
        {
            metadata[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        return metadata;
    }

    private static byte[] SerializeBounded(JsonObject root, int maximumDocumentBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, Options);
        if (bytes.Length > maximumDocumentBytes)
        {
            throw new InvalidDataException("composition-document-budget");
        }

        return bytes;
    }
}
