using System.Text.Json.Nodes;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

internal static class StoredAnalysisTestData
{
    public static JsonObject Upload() => JsonNode.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "contracts", "examples", "valid", "minimal-upload-v2.json")))!.AsObject();

    public static AnalysisRecord Create(
        MediaFingerprint media,
        string version = AetherAlgorithm.Version,
        string platform = "server",
        IEnumerable<(long TimestampMs, double SceneCutProbability)>? frames = null)
    {
        var upload = Upload();
        upload["producer"]!["platform"] = platform;
        upload["durationMs"] = media.DurationMs;
        if (frames is not null)
        {
            var template = upload["frames"]![0]!.AsObject();
            var source = new JsonArray();
            foreach (var (timestamp, probability) in frames)
            {
                var frame = template.DeepClone().AsObject();
                frame["timestampMs"] = timestamp;
                frame["sceneCutProbability"] = probability;
                source.Add(frame);
            }

            upload["frames"] = source;
        }

        var now = DateTimeOffset.UtcNow;
        var master = new AnalysisRepresentationService().BuildMaster(upload, media, AetherAlgorithm.Id, version, now);
        return new AnalysisRecord
        {
            ItemId = media.ItemId,
            MediaSourceId = media.MediaSourceId,
            AlgorithmId = AetherAlgorithm.Id,
            AlgorithmVersion = version,
            MediaFingerprint = media.Fingerprint,
            FingerprintQuality = media.FingerprintQuality,
            Etag = AnalysisRepresentationService.CreateEtag(master),
            CompressedDocument = CompressionCodec.Compress(master),
            UncompressedBytes = master.Length,
            FrameCount = upload["frames"]!.AsArray().Count,
            SourceIntervalMs = upload["sampling"]!["intervalMs"]!.GetValue<int>(),
            CreatedAt = upload["createdAt"]!.GetValue<DateTimeOffset>(),
            StoredAt = now,
            LastAccessedAt = now
        };
    }
}
