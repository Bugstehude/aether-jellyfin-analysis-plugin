using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>Builds trusted master documents and deterministic client-specific representations.</summary>
public sealed class AnalysisRepresentationService
{
    private const string ReductionVersion = "aether-reduction-1";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>Builds the canonical server-owned master document.</summary>
    public byte[] BuildMaster(
        JsonObject upload,
        MediaFingerprint media,
        string algorithmId,
        string algorithmVersion,
        DateTimeOffset storedAt)
    {
        var sampling = upload["sampling"]!.DeepClone();
        var sourceInterval = sampling["intervalMs"]!.GetValue<int>();
        var root = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["item"] = new JsonObject
            {
                ["id"] = media.ItemId.ToString(),
                ["mediaSourceId"] = media.MediaSourceId,
                ["fingerprint"] = media.Fingerprint,
                ["fingerprintQuality"] = media.FingerprintQuality
            },
            ["algorithm"] = new JsonObject
            {
                ["id"] = algorithmId,
                ["version"] = algorithmVersion
            },
            ["createdAt"] = upload["createdAt"]!.DeepClone(),
            ["storedAt"] = storedAt.ToString("O", CultureInfo.InvariantCulture),
            ["durationMs"] = upload["durationMs"]!.DeepClone(),
            ["sampling"] = sampling,
            ["producer"] = upload["producer"]!.DeepClone(),
            ["representation"] = Representation("full", sourceInterval, derived: false),
            ["frames"] = upload["frames"]!.DeepClone()
        };

        CopyOptional(upload, root, "clientContentFingerprint");
        CopyOptional(upload, root, "audioFrames");
        foreach (var property in upload)
        {
            if (!ReservedUploadProperties.Contains(property.Key))
            {
                root[property.Key] = property.Value?.DeepClone();
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(root, SerializerOptions);
    }

    /// <summary>Produces a deterministic compact, balanced or full representation.</summary>
    public AnalysisRepresentation Create(
        ReadOnlySpan<byte> masterJson,
        string requestedDetail,
        string? masterEtag = null)
    {
        var detail = requestedDetail switch
        {
            "compact" => "compact",
            "full" => "full",
            _ => "balanced"
        };
        var root = JsonNode.Parse(masterJson)!.AsObject();
        var sampling = root["sampling"]!.AsObject();
        var sourceInterval = sampling["intervalMs"]!.GetValue<int>();
        var targetInterval = detail switch
        {
            "compact" => Math.Max(sourceInterval, 1000),
            "balanced" => Math.Max(sourceInterval, 250),
            _ => sourceInterval
        };

        if (targetInterval > sourceInterval)
        {
            root["frames"] = ReduceVisualFrames(root["frames"]!.AsArray(), targetInterval);
            if (root["audioFrames"] is JsonArray audioFrames)
            {
                root["audioFrames"] = ReduceAudioFrames(audioFrames, targetInterval);
            }

            sampling["intervalMs"] = targetInterval;
        }

        root["representation"] = Representation(detail, sourceInterval, targetInterval > sourceInterval);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, SerializerOptions);
        return new AnalysisRepresentation(
            detail,
            bytes,
            CreateRepresentationEtag(masterEtag ?? CreateEtag(masterJson), detail),
            targetInterval);
    }

    /// <summary>Creates a strong content ETag.</summary>
    public static string CreateEtag(ReadOnlySpan<byte> bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return $"\"sha256-{hash}\"";
    }

    /// <summary>Creates a strong validator for one deterministic detail representation.</summary>
    public static string CreateRepresentationEtag(string masterEtag, string detail)
    {
        var identity = string.Concat(ReductionVersion, "\n", detail, "\n", masterEtag);
        return CreateEtag(System.Text.Encoding.UTF8.GetBytes(identity));
    }

    private static JsonArray ReduceVisualFrames(JsonArray source, int intervalMs)
    {
        var result = new JsonArray();
        foreach (var bucket in source
                     .Select(node => node!.AsObject())
                     .GroupBy(frame => frame["timestampMs"]!.GetValue<long>() / intervalMs))
        {
            var frames = bucket.ToArray();
            var output = new JsonObject
            {
                ["timestampMs"] = frames[0]["timestampMs"]!.GetValue<long>(),
                ["luminance"] = Average(frames, "luminance"),
                ["contrast"] = Average(frames, "contrast"),
                ["saturation"] = Average(frames, "saturation"),
                ["motionEnergy"] = Average(frames, "motionEnergy"),
                ["sceneCutProbability"] = frames.Max(frame => frame["sceneCutProbability"]!.GetValue<double>()),
                ["palette"] = ReducePalette(frames)
            };
            var audio = frames.Where(frame => frame["audio"] is JsonObject).Select(frame => frame["audio"]!.AsObject()).ToArray();
            if (audio.Length > 0)
            {
                output["audio"] = new JsonObject
                {
                    ["rms"] = Average(audio, "rms"),
                    ["flux"] = Average(audio, "flux")
                };
            }

            result.Add(output);
        }

        return result;
    }

    private static JsonArray ReduceAudioFrames(JsonArray source, int intervalMs)
    {
        var result = new JsonArray();
        foreach (var bucket in source
                     .Select(node => node!.AsObject())
                     .GroupBy(frame => frame["timestampMs"]!.GetValue<long>() / intervalMs))
        {
            var frames = bucket.ToArray();
            result.Add(new JsonObject
            {
                ["timestampMs"] = frames[0]["timestampMs"]!.GetValue<long>(),
                ["rms"] = Average(frames, "rms"),
                ["flux"] = Average(frames, "flux")
            });
        }

        return result;
    }

    private static JsonArray ReducePalette(IEnumerable<JsonObject> frames)
    {
        var colors = frames
            .SelectMany(frame => frame["palette"]!.AsArray())
            .Select(node => node!.AsObject())
            .GroupBy(color => (
                Red: color["red"]!.GetValue<int>() / 32,
                Green: color["green"]!.GetValue<int>() / 32,
                Blue: color["blue"]!.GetValue<int>() / 32))
            .Select(group => new
            {
                Red = (int)Math.Round(group.Average(color => color["red"]!.GetValue<int>())),
                Green = (int)Math.Round(group.Average(color => color["green"]!.GetValue<int>())),
                Blue = (int)Math.Round(group.Average(color => color["blue"]!.GetValue<int>())),
                Coverage = group.Sum(color => color["coverage"]!.GetValue<double>())
            })
            .OrderByDescending(color => color.Coverage)
            .Take(5)
            .ToArray();
        var total = colors.Sum(color => color.Coverage);
        var result = new JsonArray();
        foreach (var color in colors)
        {
            result.Add(new JsonObject
            {
                ["red"] = color.Red,
                ["green"] = color.Green,
                ["blue"] = color.Blue,
                ["coverage"] = total > 0 ? color.Coverage / total : 0
            });
        }

        return result;
    }

    private static double Average(IEnumerable<JsonObject> values, string name) =>
        values.Average(value => value[name]!.GetValue<double>());

    private static JsonObject Representation(string detail, int sourceIntervalMs, bool derived) => new()
    {
        ["detail"] = detail,
        ["sourceIntervalMs"] = sourceIntervalMs,
        ["derived"] = derived,
        ["reductionVersion"] = ReductionVersion
    };

    private static void CopyOptional(JsonObject source, JsonObject destination, string property)
    {
        if (source[property] is JsonNode value)
        {
            destination[property] = value.DeepClone();
        }
    }

    private static readonly HashSet<string> ReservedUploadProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "createdAt",
        "durationMs",
        "sampling",
        "producer",
        "mediaFingerprintAtStart",
        "clientContentFingerprint",
        "frames",
        "audioFrames",
        "item",
        "algorithm",
        "storedAt",
        "representation"
    };
}

/// <summary>Serialized representation and its cache identity.</summary>
public sealed record AnalysisRepresentation(string Detail, byte[] Json, string Etag, int IntervalMs);
