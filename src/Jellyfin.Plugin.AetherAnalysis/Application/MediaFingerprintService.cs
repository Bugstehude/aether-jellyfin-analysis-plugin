using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>Builds a path-safe fingerprint for one Jellyfin media source.</summary>
public sealed class MediaFingerprintService
{
    /// <summary>Finds a media source and computes its current fingerprint.</summary>
    public MediaFingerprint? Create(BaseItem item, string mediaSourceId)
    {
        var source = item.GetMediaSources(enablePathSubstitution: false)
            .FirstOrDefault(value => string.Equals(value.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return null;
        }

        var fileInfo = TryGetFileInfo(source);
        var size = fileInfo?.Length ?? source.Size;
        var modifiedTicks = fileInfo?.LastWriteTimeUtc.Ticks;
        var quality = size.HasValue && modifiedTicks.HasValue ? "strong" : "weak";
        var material = string.Join(
            '|',
            item.Id.ToString("N", CultureInfo.InvariantCulture),
            source.Id,
            source.Path ?? string.Empty,
            size?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            modifiedTicks?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            source.RunTimeTicks?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            source.ETag ?? string.Empty);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        var durationMs = Math.Max(1, (source.RunTimeTicks ?? item.RunTimeTicks ?? 10_000) / 10_000);
        return new MediaFingerprint(item.Id, source.Id, $"sha256:{hash}", quality, durationMs);
    }

    private static FileInfo? TryGetFileInfo(MediaSourceInfo source)
    {
        if (string.IsNullOrWhiteSpace(source.Path) || source.IsRemote || !File.Exists(source.Path))
        {
            return null;
        }

        return new FileInfo(source.Path);
    }
}

/// <summary>Server-derived media identity exposed without a filesystem path.</summary>
public sealed record MediaFingerprint(
    Guid ItemId,
    string MediaSourceId,
    string Fingerprint,
    string FingerprintQuality,
    long DurationMs);
