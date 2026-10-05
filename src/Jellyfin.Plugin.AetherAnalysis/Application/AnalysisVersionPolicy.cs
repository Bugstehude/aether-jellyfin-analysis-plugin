using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;

namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>Chooses explicitly compatible analyses without relabeling their algorithm identity.</summary>
public static class AnalysisVersionPolicy
{
    /// <summary>Gets exact or explicitly compatible keys in reader preference order.</summary>
    public static IReadOnlyList<AnalysisKey> GetReadKeys(AnalysisKey requested, bool allowCompatible)
    {
        if (allowCompatible && string.Equals(requested.AlgorithmId, AetherAlgorithm.Id, StringComparison.Ordinal))
        {
            var compatibility = AetherAlgorithm.ReadCompatibility.FirstOrDefault(value =>
                string.Equals(value.ReaderVersion, requested.AlgorithmVersion, StringComparison.Ordinal));
            if (compatibility is not null)
            {
                return compatibility.AnalysisVersions.Select(version => requested with { AlgorithmVersion = version }).ToArray();
            }
        }

        return [requested];
    }

    /// <summary>Chooses metadata for the current media, skipping stale preferred versions.</summary>
    public static AnalysisRecordMetadata? SelectMetadata(
        IReadOnlyDictionary<AnalysisKey, AnalysisRecordMetadata> metadata,
        AnalysisKey requested,
        string fingerprint,
        bool allowCompatible) => GetReadKeys(requested, allowCompatible)
        .Select(key => metadata.GetValueOrDefault(key))
        .FirstOrDefault(value => value is not null
            && string.Equals(value.MediaFingerprint, fingerprint, StringComparison.Ordinal));

    /// <summary>Reads a validated master. Missing optional signals do not make it unusable.</summary>
    public static async Task<StoredAnalysis?> ReadAsync(
        IAnalysisRepository repository,
        MediaFingerprint media,
        AnalysisDocumentValidator validator,
        bool allowCompatible,
        CancellationToken cancellationToken)
    {
        var requested = new AnalysisKey(media.ItemId, media.MediaSourceId, AetherAlgorithm.Id, AetherAlgorithm.Version);
        foreach (var key in GetReadKeys(requested, allowCompatible))
        {
            var record = await repository.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (record is null || !string.Equals(record.MediaFingerprint, media.Fingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var master = CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes);
                using var document = JsonDocument.Parse(master);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object
                    || !Matches(item, "id", media.ItemId.ToString())
                    || !Matches(item, "mediaSourceId", media.MediaSourceId)
                    || !Matches(item, "fingerprint", media.Fingerprint)
                    || !root.TryGetProperty("algorithm", out var algorithm) || algorithm.ValueKind != JsonValueKind.Object
                    || !Matches(algorithm, "id", key.AlgorithmId)
                    || !Matches(algorithm, "version", key.AlgorithmVersion))
                {
                    continue;
                }

                if (validator.IsValidStoredMaster(root, media.Fingerprint))
                {
                    return new StoredAnalysis(record, master);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                // A damaged preferred record must not hide a usable compatible one.
            }
        }

        return null;
    }

    private static bool Matches(JsonElement element, string property, string expected) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.Ordinal);
}

/// <summary>A stored record and its validated, unmodified canonical master.</summary>
public sealed record StoredAnalysis(AnalysisRecord Record, byte[] Master);
