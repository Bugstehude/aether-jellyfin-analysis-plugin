using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Application;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>A Jellyfin-declared stream in one immutable source snapshot.</summary>
public sealed record DraftHostStreamDescriptor(
    int Index,
    string Type,
    string? Codec,
    int? Channels,
    int? SampleRate,
    bool IsDefault,
    bool IsExternal);

/// <summary>The host identity and stream declarations captured for one media source.</summary>
public sealed record DraftHostSource(
    MediaFingerprint Media,
    string InputPath,
    string StreamIdentity,
    IReadOnlyList<DraftHostStreamDescriptor> Streams,
    int? DefaultAudioStreamIndex);

/// <summary>A stream selection proven by matching host and ffprobe global indices and metadata.</summary>
public sealed record DraftTrackSelection(int? FfmpegStreamIndex, int? JellyfinStreamIndex);

/// <summary>Conservatively maps Jellyfin stream declarations to a complete ffprobe listing.</summary>
public static class DraftSourceTrackMapper
{
    /// <summary>Maximum accepted ffprobe response size.</summary>
    public const int MaxProbeBytes = 1024 * 1024;

    /// <summary>Maximum accepted number of ffprobe streams.</summary>
    public const int MaxStreams = 4096;

    /// <summary>Matches one host source snapshot to a complete, bounded ffprobe response.</summary>
    /// <exception cref="InvalidDataException">The source declarations are incomplete, conflicting, or ambiguous.</exception>
    public static DraftTrackSelection Select(
        DraftHostSource host,
        ReadOnlyMemory<byte> ffprobeJson,
        int? requestedFfmpegStreamIndex = null)
    {
        try
        {
            return SelectCore(host, ffprobeJson, requestedFfmpegStreamIndex);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Invalid("probe-json");
        }
        catch (ArgumentException)
        {
            throw Invalid("source-metadata");
        }
    }

    private static DraftTrackSelection SelectCore(
        DraftHostSource host,
        ReadOnlyMemory<byte> ffprobeJson,
        int? requestedFfmpegStreamIndex)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.Media is null || string.IsNullOrWhiteSpace(host.InputPath)
            || string.IsNullOrWhiteSpace(host.StreamIdentity) || host.Streams is null)
        {
            throw Invalid("source-metadata");
        }

        if (ffprobeJson.Length is 0 or > MaxProbeBytes)
        {
            throw Invalid("probe-size");
        }

        var hostStreams = ValidateHost(host);
        using var document = JsonDocument.Parse(ffprobeJson, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicateKeys(document.RootElement);
        var probeStreams = ReadProbeStreams(document.RootElement);

        if (!hostStreams.Any(stream => IsVideo(stream.Type))
            || !probeStreams.Any(stream => IsVideo(stream.Type)))
        {
            throw Invalid("video-required");
        }

        var hostAudio = hostStreams.Where(stream => IsAudio(stream.Type)).ToDictionary(stream => stream.Index);
        var probeAudio = probeStreams.Where(stream => IsAudio(stream.Type)).ToDictionary(stream => stream.Index);

        if (hostAudio.Count == 0 || probeAudio.Count == 0)
        {
            if (hostAudio.Count != 0 || probeAudio.Count != 0 || requestedFfmpegStreamIndex.HasValue
                || host.DefaultAudioStreamIndex.HasValue)
            {
                throw Invalid("audio-list-mismatch");
            }

            return new DraftTrackSelection(null, null);
        }

        if (!hostAudio.Keys.ToHashSet().SetEquals(probeAudio.Keys))
        {
            throw Invalid("audio-list-mismatch");
        }

        foreach (var (index, hostStream) in hostAudio)
        {
            var probeStream = probeAudio[index];
            if (!MetadataMatches(hostStream, probeStream))
            {
                throw Invalid("audio-metadata-mismatch");
            }
        }

        int selectedIndex;
        if (requestedFfmpegStreamIndex.HasValue)
        {
            selectedIndex = requestedFfmpegStreamIndex.Value;
            if (!hostAudio.ContainsKey(selectedIndex))
            {
                throw Invalid("requested-audio-unknown");
            }
        }
        else if (host.DefaultAudioStreamIndex.HasValue)
        {
            selectedIndex = host.DefaultAudioStreamIndex.Value;
            if (!hostAudio.ContainsKey(selectedIndex))
            {
                throw Invalid("default-audio-unknown");
            }
        }
        else if (hostAudio.Count == 1)
        {
            selectedIndex = hostAudio.Keys.Single();
        }
        else
        {
            throw Invalid("audio-selection-ambiguous");
        }

        return new DraftTrackSelection(selectedIndex, selectedIndex);
    }

    private static List<DraftHostStreamDescriptor> ValidateHost(DraftHostSource host)
    {
        if (host.Streams.Count > MaxStreams)
        {
            throw Invalid("host-stream-limit");
        }

        var indexes = new HashSet<int>();
        foreach (var stream in host.Streams)
        {
            if (stream is null || stream.Index < 0 || !indexes.Add(stream.Index)
                || !IsKnownType(stream.Type))
            {
                throw Invalid("host-stream-invalid");
            }

            if (IsAudio(stream.Type))
            {
                if (stream.IsExternal)
                {
                    throw Invalid("external-audio");
                }

                if (!ValidAudioMetadata(stream.Codec, stream.Channels, stream.SampleRate))
                {
                    throw Invalid("host-audio-metadata");
                }
            }
        }

        if (host.DefaultAudioStreamIndex is int defaultIndex
            && (!indexes.Contains(defaultIndex)
                || !host.Streams.Any(stream => stream.Index == defaultIndex && IsAudio(stream.Type))))
        {
            throw Invalid("default-audio-unknown");
        }

        return host.Streams.ToList();
    }

    private static List<ProbeStream> ReadProbeStreams(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array
            || streams.GetArrayLength() is 0 or > MaxStreams)
        {
            throw Invalid("probe-stream-list");
        }

        var result = new List<ProbeStream>(streams.GetArrayLength());
        var indexes = new HashSet<int>();
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object
                || !stream.TryGetProperty("index", out var indexElement)
                || indexElement.ValueKind != JsonValueKind.Number
                || !indexElement.TryGetInt32(out var index) || index < 0 || !indexes.Add(index)
                || !stream.TryGetProperty("codec_type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                throw Invalid("probe-stream-invalid");
            }

            var type = typeElement.GetString() ?? throw Invalid("probe-stream-invalid");
            if (!IsKnownType(type))
            {
                throw Invalid("probe-stream-invalid");
            }

            if (IsAudio(type))
            {
                var codec = ReadRequiredString(stream, "codec_name");
                var channels = ReadRequiredPositiveInt(stream, "channels");
                var sampleRate = ReadRequiredSampleRate(stream);
                result.Add(new ProbeStream(index, type, codec, channels, sampleRate));
            }
            else
            {
                result.Add(new ProbeStream(index, type, null, null, null));
            }
        }

        return result;
    }

    private static string ReadRequiredString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw Invalid("probe-audio-metadata");
        }

        return element.GetString()!;
    }

    private static int ReadRequiredPositiveInt(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var number) || number <= 0)
        {
            throw Invalid("probe-audio-metadata");
        }

        return number;
    }

    private static int ReadRequiredSampleRate(JsonElement value)
    {
        if (!value.TryGetProperty("sample_rate", out var element))
        {
            throw Invalid("probe-audio-metadata");
        }

        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
        if (text is null || !IsCanonicalPositiveDecimal(text)
            || !int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var sampleRate)
            || sampleRate <= 0)
        {
            throw Invalid("probe-audio-metadata");
        }

        return sampleRate;
    }

    private static bool IsCanonicalPositiveDecimal(string text) =>
        text.Length > 0 && text[0] is >= '1' and <= '9'
        && text.All(character => character is >= '0' and <= '9');

    private static bool MetadataMatches(DraftHostStreamDescriptor host, ProbeStream probe) =>
        ValidAudioMetadata(host.Codec, host.Channels, host.SampleRate)
        && string.Equals(host.Codec, probe.Codec, StringComparison.OrdinalIgnoreCase)
        && host.Channels == probe.Channels
        && host.SampleRate == probe.SampleRate;

    private static bool ValidAudioMetadata(string? codec, int? channels, int? sampleRate) =>
        !string.IsNullOrWhiteSpace(codec) && channels is > 0 && sampleRate is > 0;

    private static bool IsKnownType(string? type) =>
        IsAudio(type) || IsVideo(type) || IsSubtitle(type) || IsData(type) || IsAttachment(type);

    private static bool IsAudio(string? type) =>
        string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideo(string? type) =>
        string.Equals(type, "video", StringComparison.OrdinalIgnoreCase);

    private static bool IsSubtitle(string? type) =>
        string.Equals(type, "subtitle", StringComparison.OrdinalIgnoreCase);

    private static bool IsData(string? type) =>
        string.Equals(type, "data", StringComparison.OrdinalIgnoreCase);

    private static bool IsAttachment(string? type) =>
        string.Equals(type, "attachment", StringComparison.OrdinalIgnoreCase);

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Invalid("probe-duplicate-key");
                }

                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                RejectDuplicateKeys(value);
            }
        }
    }

    private static InvalidDataException Invalid(string code) => new($"source-track-{code}");

    private sealed record ProbeStream(int Index, string Type, string? Codec, int? Channels, int? SampleRate);
}
