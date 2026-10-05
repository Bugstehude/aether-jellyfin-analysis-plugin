using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>
/// Summarizes the timing fields that the strict 1.2 profile checks. Only numbers and fixed
/// identifiers are emitted, never paths, titles or worker stderr.
/// </summary>
public static class DraftArtifactDiagnostics
{
    /// <summary><see cref="Exception.Data"/> key of the bounded timing summary.</summary>
    public const string DataKey = "aether.componentDiagnostics";

    /// <summary><see cref="Exception.Data"/> key of the rejected component mode.</summary>
    public const string ModeKey = "aether.componentMode";

    private const int MaximumLength = 900;
    private const int MaximumRanges = 4;

    /// <summary>Never throws. Unreadable input yields a marker instead of a summary.</summary>
    public static string Describe(ReadOnlySpan<byte> bytes, long hostDurationMs, string mode)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement;
            var text = new StringBuilder();
            Add(text, "hostDurationMs", hostDurationMs.ToString(CultureInfo.InvariantCulture));
            Add(text, "durationMs", Number(root, "durationMs"));
            Add(text, "durationSource", Identifier(root, "durationSource"));
            Add(text, "mediaOriginUs", Number(root, "mediaOriginUs"));
            if (mode == "audio-only" && root.TryGetProperty("audio", out var audio))
            {
                DescribeAudio(text, audio);
            }
            else if (mode == "video-only" && root.TryGetProperty("video", out var video))
            {
                DescribeVideo(text, video);
            }

            return text.Length > MaximumLength ? text.ToString(0, MaximumLength) + "…" : text.ToString();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or ArgumentException or FormatException or OverflowException)
        {
            return "unavailable (" + exception.GetType().Name + ")";
        }
    }

    private static void DescribeAudio(StringBuilder text, JsonElement audio)
    {
        Add(text, "measuredFrames", Number(audio, "measuredFrameCount"));
        if (audio.TryGetProperty("audioAnalysis", out var analysis))
        {
            Add(text, "state", Identifier(analysis, "state"));
            Add(text, "completeness", Identifier(analysis, "completeness"));
            Add(text, "errorCode", Identifier(analysis, "errorCode"));
            Add(text, "streamOffsetUs", Number(analysis, "streamOffsetUs"));
            Add(text, "coverage", Ranges(analysis));
            if (analysis.TryGetProperty("sourcePcm", out var pcm))
            {
                Add(text, "pcmFirstPts", Identifier(pcm, "firstPts"));
                Add(text, "pcmOriginUs", Number(pcm, "mediaOriginUs"));
                Add(text, "sourcePtsBase", Rational(pcm, "sourcePtsTimeBase"));
            }
        }

        if (audio.TryGetProperty("legacyAudioAnalysis", out var legacy))
        {
            Add(text, "legacyState", Identifier(legacy, "state"));
            Add(text, "legacyCompleteness", Identifier(legacy, "completeness"));
            Add(text, "legacyCoverage", Ranges(legacy));
            if (legacy.TryGetProperty("counters", out var counters))
            {
                var samples = Number(counters, "decodedSamples");
                Add(text, "decodedSamples", samples);
                if (long.TryParse(samples, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                {
                    // The strict profile expects coverage to end exactly here.
                    Add(text, "samplesEndMs", Math.Round(count * 1000d / 22050, MidpointRounding.AwayFromZero)
                        .ToString(CultureInfo.InvariantCulture));
                }

                Add(text, "measuredWindows", Number(counters, "measuredWindows"));
                Add(text, "zeroPaddedWindows", Number(counters, "zeroPaddedWindows"));
            }

            if (legacy.TryGetProperty("timeView", out var view))
            {
                Add(text, "legacySourceStartUs", Number(view, "sourceStartUs"));
                Add(text, "legacyContainerStartUs", Number(view, "containerStartUs"));
                Add(text, "legacyContiguous", Identifier(view, "contiguousOutputPts"));
                Add(text, "legacySharedGrid", Identifier(view, "sharedMediaGridProven"));
            }
        }

        if (audio.TryGetProperty("audioFrames", out var frames) && frames.ValueKind == JsonValueKind.Array)
        {
            Add(text, "legacyFrames", frames.GetArrayLength().ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void DescribeVideo(StringBuilder text, JsonElement video)
    {
        if (video.TryGetProperty("frames", out var frames) && frames.ValueKind == JsonValueKind.Array)
        {
            var count = frames.GetArrayLength();
            Add(text, "frames", count.ToString(CultureInfo.InvariantCulture));
            if (count > 0)
            {
                Add(text, "firstFrameMs", Number(frames[0], "timestampMs"));
                Add(text, "lastFrameMs", Number(frames[count - 1], "timestampMs"));
            }
        }

        if (video.TryGetProperty("cutAnalysis", out var cuts))
        {
            Add(text, "cutCompleteness", Identifier(cuts, "completeness"));
            Add(text, "cutCoverage", Ranges(cuts));
            if (cuts.TryGetProperty("parameters", out var parameters))
            {
                Add(text, "coverageEndBasis", Identifier(parameters, "coverageEndBasis"));
            }
        }
    }

    private static string Ranges(JsonElement owner)
    {
        if (!owner.TryGetProperty("coverage", out var coverage) || coverage.ValueKind != JsonValueKind.Array)
        {
            return "-";
        }

        var parts = coverage.EnumerateArray().Take(MaximumRanges)
            .Select(range => Number(range, "startMs") + "-" + Number(range, "endMs"));
        var count = coverage.GetArrayLength();
        return "[" + string.Join(",", parts) + (count > MaximumRanges ? ",…" : string.Empty) + "]#"
            + count.ToString(CultureInfo.InvariantCulture);
    }

    private static string Rational(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? Number(value, "num") + "/" + Number(value, "den")
            : "-";

    private static string Number(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : "-";

    /// <summary>Fixed identifiers only: anything that is not a short token is masked.</summary>
    private static string Identifier(JsonElement owner, string property)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(property, out var value))
        {
            return "-";
        }

        var raw = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
        return raw.Length is > 0 and <= 64 && raw.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')
            ? raw
            : "?";
    }

    private static void Add(StringBuilder text, string key, string value)
    {
        if (text.Length > 0)
        {
            text.Append(' ');
        }

        text.Append(key).Append('=').Append(value);
    }
}
