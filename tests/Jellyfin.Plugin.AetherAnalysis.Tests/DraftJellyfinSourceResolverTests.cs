using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using NSubstitute;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class DraftJellyfinSourceResolverTests
{
    [Fact]
    public async Task ResolvesCanonicalSourceIdWithStrongFingerprintAndFullHostPath()
    {
        using var fixture = new ResolverFixture();
        var result = await fixture.Resolver.ResolveAsync(fixture.Item.Id, "SOURCE-1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("source-1", result.Media.MediaSourceId);
        Assert.Equal("strong", result.Media.FingerprintQuality);
        Assert.True(result.Media.DurationMs > 0);
        Assert.Equal(System.IO.Path.GetFullPath(fixture.Path), result.InputPath);
        Assert.Equal(new MediaFingerprintService().Create(fixture.Item, "source-1"), result.Media);
        Assert.Equal(1, result.Streams.Single(stream => stream.Type == "audio").Index);
    }

    [Fact]
    public async Task AcceptsExplicitEmptyHostStreamListForLaterNoTrackProof()
    {
        using var fixture = new ResolverFixture();
        fixture.SetStreams([]);
        fixture.Source.DefaultAudioStreamIndex = null;

        var result = await fixture.Resolver.ResolveAsync(fixture.Item.Id, "source-1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Streams);
        Assert.Null(result.DefaultAudioStreamIndex);
    }

    [Fact]
    public async Task RejectsSourceWithoutRuntimeDuration()
    {
        using var fixture = new ResolverFixture();
        fixture.Source.RunTimeTicks = null;
        fixture.Item.RunTimeTicks = null;

        var result = await fixture.Resolver.ResolveAsync(fixture.Item.Id, "source-1", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task UsesItemRuntimeWhenSourceRuntimeIsMissing()
    {
        using var fixture = new ResolverFixture();
        fixture.Source.RunTimeTicks = null;

        var result = await fixture.ResolveAsync();

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromMinutes(1).Ticks / TimeSpan.TicksPerMillisecond, result.Media.DurationMs);
    }

    [Fact]
    public async Task ReturnsNullForMissingItemOrSource()
    {
        using var fixture = new ResolverFixture();
        fixture.Library.GetItemById<BaseItem>(fixture.Item.Id).Returns((BaseItem?)null);
        Assert.Null(await fixture.Resolver.ResolveAsync(fixture.Item.Id, "source-1", CancellationToken.None));

        fixture.Library.GetItemById<BaseItem>(fixture.Item.Id).Returns(fixture.Item);
        fixture.SetSources([]);
        Assert.Null(await fixture.Resolver.ResolveAsync(fixture.Item.Id, "source-1", CancellationToken.None));
    }

    [Fact]
    public async Task ReturnsNullForRemoteNonlocalAndMissingHostStreamSources()
    {
        using var fixture = new ResolverFixture();
        fixture.Source.IsRemote = true;
        Assert.Null(await fixture.ResolveAsync());

        fixture.Source.IsRemote = false;
        fixture.Source.Path = "relative/media.mkv";
        Assert.Null(await fixture.ResolveAsync());

        fixture.Source.Path = fixture.Path;
        fixture.Source.MediaStreams = null!;
        Assert.Null(await fixture.ResolveAsync());
    }

    [Theory]
    [InlineData("default")]
    [InlineData("codec")]
    [InlineData("index")]
    public async Task StreamIdentityChangesWhenHostAudioSelectionOrDescriptorChanges(string mutation)
    {
        using var fixture = new ResolverFixture();
        var before = await fixture.ResolveAsync();
        Assert.NotNull(before);
        var fingerprintBefore = before.Media.Fingerprint;

        switch (mutation)
        {
            case "default":
                fixture.Source.DefaultAudioStreamIndex = 2;
                break;
            case "codec":
                fixture.Source.MediaStreams![1].Codec = "opus";
                break;
            case "index":
                fixture.Source.MediaStreams![1].Index = 7;
                break;
        }

        var after = await fixture.ResolveAsync();

        Assert.NotNull(after);
        Assert.NotEqual(before.StreamIdentity, after.StreamIdentity);
        Assert.Equal(fingerprintBefore, after.Media.Fingerprint);
    }

    [Fact]
    public async Task StreamIdentityIgnoresHostListOrdering()
    {
        using var fixture = new ResolverFixture();
        var before = await fixture.ResolveAsync();
        Assert.NotNull(before);

        fixture.Source.MediaStreams = fixture.Source.MediaStreams!.Reverse().ToList();
        var after = await fixture.ResolveAsync();

        Assert.NotNull(after);
        Assert.Equal(before.StreamIdentity, after.StreamIdentity);
    }

    [Fact]
    public async Task RereadsLibraryItemForEveryResolution()
    {
        using var fixture = new ResolverFixture();
        var secondPath = fixture.CreateAnotherPath();
        var secondItem = Substitute.For<BaseItem>();
        secondItem.Id = fixture.Item.Id;
        secondItem.RunTimeTicks = fixture.Item.RunTimeTicks;
        var secondSource = fixture.CreateSource(secondPath);
        secondItem.GetMediaSources(false).Returns([secondSource]);

        var itemCalls = 0;
        fixture.Library.GetItemById<BaseItem>(fixture.Item.Id).Returns(_ =>
            itemCalls++ == 0 ? fixture.Item : secondItem);

        var first = await fixture.Resolver.ResolveAsync(fixture.Item.Id, "source-1", CancellationToken.None);
        var second = await fixture.Resolver.ResolveAsync(fixture.Item.Id, "source-1", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(Path.GetFullPath(fixture.Path), first.InputPath);
        Assert.Equal(Path.GetFullPath(secondPath), second.InputPath);
        Assert.Equal(2, itemCalls);
    }

    private sealed class ResolverFixture : IDisposable
    {
        private readonly List<string> _paths = [];

        public ILibraryManager Library { get; } = Substitute.For<ILibraryManager>();
        public BaseItem Item { get; } = Substitute.For<BaseItem>();
        public string Path { get; }
        public MediaSourceInfo Source { get; }
        public DraftJellyfinSourceResolver Resolver { get; }

        public ResolverFixture()
        {
            Path = CreateAnotherPath();
            Item.Id = Guid.NewGuid();
            Item.RunTimeTicks = TimeSpan.FromMinutes(1).Ticks;
            Source = CreateSource(Path);
            Item.GetMediaSources(false).Returns(_ => [Source]);
            Library.GetItemById<BaseItem>(Item.Id).Returns(Item);
            Resolver = new DraftJellyfinSourceResolver(Library, new MediaFingerprintService());
        }

        public Task<DraftHostSource?> ResolveAsync() =>
            Resolver.ResolveAsync(Item.Id, "source-1", CancellationToken.None);

        public string CreateAnotherPath()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aether-resolver-{Guid.NewGuid():N}.mkv");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            _paths.Add(path);
            return path;
        }

        public MediaSourceInfo CreateSource(string path) => new()
        {
            Id = "source-1",
            Path = path,
            RunTimeTicks = TimeSpan.FromMinutes(1).Ticks,
            DefaultAudioStreamIndex = 1,
            MediaStreams = [
                new MediaStream { Index = 0, Type = MediaStreamType.Video },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, SampleRate = 48000, IsDefault = true },
                new MediaStream { Index = 3, Type = MediaStreamType.Subtitle, Codec = "subrip", IsExternal = true }
            ]
        };

        public void SetStreams(IReadOnlyList<MediaStream> streams) => Source.MediaStreams = streams.ToList();

        public void SetSources(IReadOnlyList<MediaSourceInfo> sources) =>
            Item.GetMediaSources(false).Returns(_ => sources.ToList());

        public void Dispose()
        {
            foreach (var path in _paths)
            {
                File.Delete(path);
            }
        }
    }
}
