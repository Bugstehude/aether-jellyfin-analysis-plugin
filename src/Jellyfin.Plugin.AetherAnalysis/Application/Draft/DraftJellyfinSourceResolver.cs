using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>Fresh host resolution for experimental jobs, never derived from worker JSON.</summary>
public interface IDraftSourceResolver
{
    /// <summary>Resolves the current local source and immutable stream metadata.</summary>
    Task<DraftHostSource?> ResolveAsync(Guid itemId, string mediaSourceId, CancellationToken cancellationToken);
}

/// <summary>Requeries Jellyfin for each stage and binds stream declarations separately from the media fingerprint.</summary>
public sealed class DraftJellyfinSourceResolver(
    ILibraryManager libraryManager,
    MediaFingerprintService fingerprints) : IDraftSourceResolver
{
    /// <inheritdoc />
    public Task<DraftHostSource?> ResolveAsync(Guid itemId, string mediaSourceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = libraryManager.GetItemById<BaseItem>(itemId);
        if (item is null)
        {
            return Task.FromResult<DraftHostSource?>(null);
        }

        var source = Find(item, mediaSourceId);
        if (source is null || source.IsRemote || string.IsNullOrWhiteSpace(source.Path)
            || !Path.IsPathFullyQualified(source.Path) || !File.Exists(source.Path) || source.MediaStreams is null
            || (source.RunTimeTicks ?? item.RunTimeTicks) is null or <= 0)
        {
            return Task.FromResult<DraftHostSource?>(null);
        }

        var streams = source.MediaStreams.Select(stream => new DraftHostStreamDescriptor(
            stream.Index, stream.Type.ToString().ToLowerInvariant(), stream.Codec,
            stream.Channels, stream.SampleRate, stream.IsDefault, stream.IsExternal)).OrderBy(stream => stream.Index).ToArray();
        var identity = Identity(source, streams);
        var media = fingerprints.Create(item, source.Id);
        var current = Find(item, source.Id);
        if (media is null || media.FingerprintQuality != "strong" || media.DurationMs <= 0
            || current?.MediaStreams is null || Identity(current, current.MediaStreams.Select(stream => new DraftHostStreamDescriptor(
                stream.Index, stream.Type.ToString().ToLowerInvariant(), stream.Codec,
                stream.Channels, stream.SampleRate, stream.IsDefault, stream.IsExternal)).OrderBy(stream => stream.Index).ToArray()) != identity)
        {
            return Task.FromResult<DraftHostSource?>(null);
        }

        return Task.FromResult<DraftHostSource?>(new DraftHostSource(media, Path.GetFullPath(source.Path), identity,
            Array.AsReadOnly(streams), source.DefaultAudioStreamIndex));
    }

    private static MediaSourceInfo? Find(BaseItem item, string mediaSourceId)
    {
        var sources = item.GetMediaSources(enablePathSubstitution: false)
            .Where(source => string.Equals(source.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return sources.Length == 1 ? sources[0] : null;
    }

    private static string Identity(MediaSourceInfo source, IReadOnlyList<DraftHostStreamDescriptor> streams)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            source.Id,
            source.Path,
            source.IsRemote,
            source.RunTimeTicks,
            source.ETag,
            source.DefaultAudioStreamIndex,
            Streams = streams
        });
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
