using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AetherAnalysis.Configuration;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>
/// Writes real Jellyfin chapter markers from an item's already-stored
/// <c>sceneCutProbability</c> analysis — no re-analysis, this only classifies data that
/// <see cref="ServerAnalysisRunner"/> already computed and persisted. Never touches an item that
/// already has chapters unless the admin explicitly opts in (the <c>overwriteExisting</c>
/// parameter on <see cref="GenerateAsync"/>) — some media ships with real, studio-authored
/// chapters that must not be silently clobbered by an auto-generated "Scene N" list.
/// </summary>
public sealed class ChapterGenerator(
    ILibraryManager libraryManager,
    IAnalysisRepository repository,
    IChapterManager chapterManager,
    ILogger<ChapterGenerator> logger) : IDisposable
{
    // Serializes runs against each other, same reasoning as DuplicateScanner in the sibling
    // duplicate-finder plugin: a second "Generate" click while one is already running would
    // otherwise redo the same work for nothing.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Cancels any in-flight run on Dispose (host/plugin shutdown), same pattern as
    // DuplicateScanner — GenerateAsync is started fire-and-forget from the controller.
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    /// <summary>Current/last run status, polled by the config page.</summary>
    public ChapterGenerationStatus Status { get; } = new();

    /// <summary>Enumerates the local video items eligible for chapter generation, per the configured analysis scope.</summary>
    public IReadOnlyList<BaseItem> SelectItems()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            IsVirtualItem = false
        };

        var libraryIds = ParseGuids(Configuration.AnalysisLibraryIds);
        if (libraryIds.Length > 0)
        {
            query.AncestorIds = libraryIds;
        }

        return libraryManager.GetItemList(query);
    }

    /// <summary>
    /// Runs over every eligible item in the configured scope, writing chapters for items that
    /// have stored analysis data. Runs to completion before returning — callers that want
    /// progress should poll <see cref="Status"/> from a separate request while this is in flight.
    /// </summary>
    public async Task GenerateAsync(bool overwriteExisting, CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        var runToken = linkedCancellation.Token;

        if (!await _gate.WaitAsync(0, runToken).ConfigureAwait(false))
        {
            // Already running — the caller's click is redundant, not an error.
            return;
        }

        try
        {
            var items = SelectItems();
            Status.Running = true;
            Status.Checked = 0;
            Status.Total = items.Count;
            Status.Created = 0;
            Status.SkippedHadChapters = 0;
            Status.SkippedNotAnalyzed = 0;
            Status.LastError = null;

            foreach (var item in items)
            {
                runToken.ThrowIfCancellationRequested();
                try
                {
                    await ProcessItemAsync(item, overwriteExisting, runToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "AETHER chapter generation failed for item {ItemId}", item.Id);
                }

                Status.Checked++;
            }

            Status.LastFinishedAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Status.LastError = exception.Message;
            logger.LogError(exception, "AETHER chapter generation run failed");
        }
        finally
        {
            Status.Running = false;
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose() (host/plugin shutdown) raced ahead of this background task's own
                // cleanup — nothing left to release into at that point.
            }
        }
    }

    private async Task ProcessItemAsync(BaseItem item, bool overwriteExisting, CancellationToken cancellationToken)
    {
        if (item is not Video video)
        {
            Status.SkippedNotAnalyzed++;
            return;
        }

        if (!overwriteExisting && chapterManager.GetChapters(item.Id).Count > 0)
        {
            Status.SkippedHadChapters++;
            return;
        }

        var frames = await LoadHardCutSourceFramesAsync(item, cancellationToken).ConfigureAwait(false);
        if (frames is null)
        {
            Status.SkippedNotAnalyzed++;
            return;
        }

        var cutTimestampsMs = SceneCutClassifier.FindHardCutTimestampsMs(frames);
        if (cutTimestampsMs.Count == 0)
        {
            // Analyzed, but nothing that qualifies as a hard cut — not an error, just nothing to write.
            return;
        }

        var chapters = cutTimestampsMs
            .Select((timestampMs, index) => new ChapterInfo
            {
                Name = $"Scene {index + 2}", // Scene 1 is implicitly "from the start" — Jellyfin needs no marker for it.
                StartPositionTicks = timestampMs * TimeSpan.TicksPerMillisecond
            })
            .Prepend(new ChapterInfo { Name = "Scene 1", StartPositionTicks = 0 })
            .ToList();

        chapterManager.SaveChapters(video, chapters);
        Status.Created++;
    }

    /// <summary>
    /// Reads the stored <c>sceneCutProbability</c> series for an item's first local source that
    /// has a current server analysis, or null if none of its local sources have been analyzed
    /// yet. Frames are returned ordered by timestamp, as <see cref="SceneCutClassifier"/> requires.
    /// </summary>
    private async Task<IReadOnlyList<(long TimestampMs, double SceneCutProbability)>?> LoadHardCutSourceFramesAsync(
        BaseItem item, CancellationToken cancellationToken)
    {
        foreach (var mediaSourceId in LocalSourceIds(item))
        {
            var key = new AnalysisKey(item.Id, mediaSourceId, AetherAlgorithm.Id, AetherAlgorithm.Version);
            var record = await repository.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                continue;
            }

            try
            {
                var master = CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes);
                return ExtractFrames(master);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                logger.LogWarning(
                    exception, "AETHER stored analysis is corrupt for item {ItemId}, skipping chapter generation", item.Id);
                return null;
            }
        }

        return null;
    }

    private static IReadOnlyList<(long TimestampMs, double SceneCutProbability)> ExtractFrames(ReadOnlySpan<byte> master)
    {
        using var document = JsonDocument.Parse(master.ToArray());
        if (!document.RootElement.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<(long TimestampMs, double SceneCutProbability)>(frames.GetArrayLength());
        foreach (var frame in frames.EnumerateArray())
        {
            if (frame.TryGetProperty("timestampMs", out var timestampMsElement)
                && frame.TryGetProperty("sceneCutProbability", out var probabilityElement)
                && timestampMsElement.TryGetInt64(out var timestampMs)
                && probabilityElement.TryGetDouble(out var probability))
            {
                result.Add((timestampMs, probability));
            }
        }

        result.Sort((a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
        return result;
    }

    private static IEnumerable<string> LocalSourceIds(BaseItem item) =>
        item.GetMediaSources(enablePathSubstitution: false)
            .Where(source => !source.IsRemote && !string.IsNullOrWhiteSpace(source.Path) && File.Exists(source.Path))
            .Select(source => source.Id);

    private static Guid[] ParseGuids(string[]? values) => (values ?? [])
        .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty)
        .ToArray();

    private static PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public void Dispose()
    {
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Status of an in-progress or last-completed chapter-generation run, polled by the config page.</summary>
public sealed class ChapterGenerationStatus
{
    /// <summary>Whether a run is currently in progress.</summary>
    public bool Running { get; set; }

    /// <summary>Items checked so far in the current/last run.</summary>
    public int Checked { get; set; }

    /// <summary>Total items to check in the current/last run.</summary>
    public int Total { get; set; }

    /// <summary>Items that received newly written chapters.</summary>
    public int Created { get; set; }

    /// <summary>Items skipped because they already had chapters and overwrite wasn't requested.</summary>
    public int SkippedHadChapters { get; set; }

    /// <summary>Items skipped because they have no stored analysis yet.</summary>
    public int SkippedNotAnalyzed { get; set; }

    /// <summary>When the last completed run finished, if any.</summary>
    public DateTimeOffset? LastFinishedAt { get; set; }

    /// <summary>Set if the last run failed with an unexpected error.</summary>
    public string? LastError { get; set; }
}
