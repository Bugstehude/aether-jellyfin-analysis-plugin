using System.Text;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>
/// Covers the one safety-relevant behavior of <see cref="ChapterGenerator"/>: it must never
/// silently replace chapters an item already has unless the admin explicitly opted in, and it
/// must correctly tell "not yet analyzed" apart from "analyzed but nothing to write".
/// </summary>
public sealed class ChapterGeneratorTests : IDisposable
{
    private static readonly Guid ItemId = Guid.NewGuid();
    private const string MediaSourceId = "quelle-1";

    // A real file: ChapterGenerator's LocalSourceIds filters sources via File.Exists, same as
    // the sibling ServerAnalysisRunner — a fake path would silently look like "no local source".
    private readonly string _mediaPath = Path.Combine(Path.GetTempPath(), "aether-chapter-tests-" + Guid.NewGuid() + ".mkv");

    public ChapterGeneratorTests()
    {
        File.WriteAllBytes(_mediaPath, [0]);
    }

    public void Dispose()
    {
        if (File.Exists(_mediaPath))
        {
            File.Delete(_mediaPath);
        }
    }

    private Video MakeVideo(Guid id)
    {
        var item = Substitute.For<Video>();
        item.Id = id;
        item.Name = "Film";
        item.GetMediaSources(enablePathSubstitution: false).Returns(new List<MediaSourceInfo>
        {
            new() { Id = MediaSourceId, Path = _mediaPath, IsRemote = false },
        });
        return item;
    }

    private static AnalysisRecord MakeRecord(IEnumerable<(long TimestampMs, double SceneCutProbability)> frames)
    {
        var framesJson = string.Join(
            ",",
            frames.Select(f => $"{{\"timestampMs\":{f.TimestampMs},\"sceneCutProbability\":{f.SceneCutProbability.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"));
        var json = $"{{\"frames\":[{framesJson}]}}";
        var bytes = Encoding.UTF8.GetBytes(json);
        var compressed = CompressionCodec.Compress(bytes);
        return new AnalysisRecord
        {
            ItemId = ItemId,
            MediaSourceId = MediaSourceId,
            AlgorithmId = AetherAlgorithm.Id,
            AlgorithmVersion = AetherAlgorithm.Version,
            MediaFingerprint = "sha256:whatever",
            FingerprintQuality = "high",
            Etag = "\"abc\"",
            CompressedDocument = compressed,
            UncompressedBytes = bytes.Length
        };
    }

    private static (ChapterGenerator Generator, ILibraryManager LibraryManager, IAnalysisRepository Repository, IChapterManager ChapterManager) Create()
    {
        var libraryManager = Substitute.For<ILibraryManager>();
        var repository = Substitute.For<IAnalysisRepository>();
        var chapterManager = Substitute.For<IChapterManager>();
        var generator = new ChapterGenerator(libraryManager, repository, chapterManager, NullLogger<ChapterGenerator>.Instance);
        return (generator, libraryManager, repository, chapterManager);
    }

    [Fact]
    public async Task ItemWithoutStoredAnalysisIsSkippedAsNotAnalyzed()
    {
        var (generator, libraryManager, repository, chapterManager) = Create();
        var video = MakeVideo(ItemId);
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([video]);
        repository.GetAsync(Arg.Any<AnalysisKey>(), Arg.Any<CancellationToken>()).Returns((AnalysisRecord?)null);

        await generator.GenerateAsync(overwriteExisting: false, CancellationToken.None);

        Assert.Equal(0, generator.Status.Created);
        Assert.Equal(1, generator.Status.SkippedNotAnalyzed);
        chapterManager.DidNotReceive().SaveChapters(Arg.Any<Video>(), Arg.Any<IReadOnlyList<ChapterInfo>>());
    }

    [Fact]
    public async Task AnalyzedItemWithoutExistingChaptersGetsChaptersWritten()
    {
        var (generator, libraryManager, repository, chapterManager) = Create();
        var video = MakeVideo(ItemId);
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([video]);
        repository
            .GetAsync(new AnalysisKey(ItemId, MediaSourceId, AetherAlgorithm.Id, AetherAlgorithm.Version), Arg.Any<CancellationToken>())
            .Returns(MakeRecord([(0, 0.1), (10_000, 0.9), (20_000, 0.1)]));
        chapterManager.GetChapters(ItemId).Returns(new List<ChapterInfo>());

        await generator.GenerateAsync(overwriteExisting: false, CancellationToken.None);

        Assert.Equal(1, generator.Status.Created);
        Assert.Equal(0, generator.Status.SkippedHadChapters);
        chapterManager.Received(1).SaveChapters(
            video,
            Arg.Is<IReadOnlyList<ChapterInfo>>(chapters => chapters.Count == 2
                && chapters[0].StartPositionTicks == 0
                && chapters[1].StartPositionTicks == 10_000 * TimeSpan.TicksPerMillisecond));
    }

    [Fact]
    public async Task ItemWithExistingChaptersIsSkippedWithoutOverwriteFlag()
    {
        var (generator, libraryManager, repository, chapterManager) = Create();
        var video = MakeVideo(ItemId);
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([video]);
        repository
            .GetAsync(Arg.Any<AnalysisKey>(), Arg.Any<CancellationToken>())
            .Returns(MakeRecord([(10_000, 0.9)]));
        chapterManager.GetChapters(ItemId).Returns(new List<ChapterInfo> { new() { Name = "Studio Chapter 1" } });

        await generator.GenerateAsync(overwriteExisting: false, CancellationToken.None);

        Assert.Equal(0, generator.Status.Created);
        Assert.Equal(1, generator.Status.SkippedHadChapters);
        chapterManager.DidNotReceive().SaveChapters(Arg.Any<Video>(), Arg.Any<IReadOnlyList<ChapterInfo>>());
    }

    [Fact]
    public async Task ItemWithExistingChaptersIsOverwrittenWhenFlagIsSet()
    {
        var (generator, libraryManager, repository, chapterManager) = Create();
        var video = MakeVideo(ItemId);
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([video]);
        repository
            .GetAsync(Arg.Any<AnalysisKey>(), Arg.Any<CancellationToken>())
            .Returns(MakeRecord([(10_000, 0.9)]));
        chapterManager.GetChapters(ItemId).Returns(new List<ChapterInfo> { new() { Name = "Studio Chapter 1" } });

        await generator.GenerateAsync(overwriteExisting: true, CancellationToken.None);

        Assert.Equal(1, generator.Status.Created);
        Assert.Equal(0, generator.Status.SkippedHadChapters);
        chapterManager.Received(1).SaveChapters(video, Arg.Any<IReadOnlyList<ChapterInfo>>());
    }

    [Fact]
    public async Task AnalyzedItemWithNoHardCutsWritesNothingAndIsNotCountedAsSkipped()
    {
        var (generator, libraryManager, repository, chapterManager) = Create();
        var video = MakeVideo(ItemId);
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([video]);
        repository
            .GetAsync(Arg.Any<AnalysisKey>(), Arg.Any<CancellationToken>())
            .Returns(MakeRecord([(0, 0.1), (5000, 0.2), (10_000, 0.1)]));

        await generator.GenerateAsync(overwriteExisting: false, CancellationToken.None);

        Assert.Equal(0, generator.Status.Created);
        Assert.Equal(0, generator.Status.SkippedHadChapters);
        Assert.Equal(0, generator.Status.SkippedNotAnalyzed);
        chapterManager.DidNotReceive().SaveChapters(Arg.Any<Video>(), Arg.Any<IReadOnlyList<ChapterInfo>>());
    }
}
