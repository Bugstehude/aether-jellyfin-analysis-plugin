using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class ServerAnalysisRunnerTests
{
    [Fact]
    public async Task ProductionWorkerRespectsTheSharedExperimentalExecutionGate()
    {
        var execution = new AnalysisWorkerExecutionGate();
        using var fixture = new RunnerFixture(workerExecutionGate: execution);
        using var cancellation = new CancellationTokenSource();
        using var lease = await execution.AcquireAsync(default);
        var pending = fixture.Runner.AnalyzeItemAsync(fixture.Item.Id, null, cancellation.Token, "source-1", recalculate: true);
        Assert.False(pending.IsCompleted);
        await fixture.Worker.DidNotReceiveWithAnyArgs().AnalyzeAsync(default!, 0, 0, 0, null, default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(fixture.Coordinator.ProtectedKeys);
    }

    [Theory]
    [InlineData("1.0.0", "browser")]
    [InlineData("1.0.0", "server")]
    [InlineData("1.1.0", "browser")]
    public async Task NormalRequestReusesCompatibleAnalysisWithoutOptionalAudio(string version, string platform)
    {
        using var fixture = new RunnerFixture();
        var record = fixture.Store(version, platform);
        var original = record.CompressedDocument.ToArray();

        var result = await fixture.Runner.AnalyzeItemAsync(fixture.Item.Id, null, CancellationToken.None, "source-1");

        Assert.False(result.AnyStored);
        Assert.Equal("already-current", Assert.Single(result.Sources).Detail);
        Assert.Equal(original, record.CompressedDocument);
        Assert.Equal(version, record.AlgorithmVersion);
        await fixture.Worker.DidNotReceiveWithAnyArgs().AnalyzeAsync(default!, 0, 0, 0, null, default);
        await fixture.Repository.DidNotReceiveWithAnyArgs().StoreBoundedAsync(default!, default);
    }

    [Fact]
    public async Task RoutineUpgradesOlderVersionThenSkipsItOnTheNextRun()
    {
        using var fixture = new RunnerFixture();
        var old = fixture.Store("1.0.0");

        await fixture.Runner.AnalyzePendingAsync(null, CancellationToken.None);
        await fixture.Runner.AnalyzePendingAsync(null, CancellationToken.None);

        await fixture.Worker.Received(1).AnalyzeAsync(fixture.Paths[0], Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
        Assert.Contains(fixture.Records.Values, value => value.AlgorithmVersion == AetherAlgorithm.Version);
        Assert.Same(old, fixture.Records[new AnalysisKey(fixture.Item.Id, "source-1", AetherAlgorithm.Id, "1.0.0")]);
        Assert.Empty(fixture.Coordinator.ProtectedKeys);
    }

    [Fact]
    public async Task RoutineDoesNotRecalculateCurrentBrowserAnalysisBecauseAudioIsMissing()
    {
        using var fixture = new RunnerFixture();
        fixture.Store(AetherAlgorithm.Version, "browser");

        await fixture.Runner.AnalyzePendingAsync(null, CancellationToken.None);

        await fixture.Worker.DidNotReceiveWithAnyArgs().AnalyzeAsync(default!, 0, 0, 0, null, default);
    }

    [Fact]
    public async Task ExplicitRecalculationTouchesOnlyTheSelectedSource()
    {
        using var fixture = new RunnerFixture(sourceCount: 2);
        fixture.Store(AetherAlgorithm.Version, sourceId: "source-1");
        var other = fixture.Store(AetherAlgorithm.Version, sourceId: "source-2");

        var result = await fixture.Runner.AnalyzeItemAsync(
            fixture.Item.Id, null, CancellationToken.None, "source-1", recalculate: true);

        Assert.True(result.AnyStored);
        Assert.Equal("source-1", Assert.Single(result.Sources).MediaSourceId);
        await fixture.Worker.Received(1).AnalyzeAsync(fixture.Paths[0], Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
        await fixture.Worker.DidNotReceive().AnalyzeAsync(fixture.Paths[1], Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
        Assert.Same(other, fixture.Records[new AnalysisKey(fixture.Item.Id, "source-2", AetherAlgorithm.Id, AetherAlgorithm.Version)]);
    }

    [Fact]
    public async Task FileChangePreventsReuseOfOlderAnalysis()
    {
        using var fixture = new RunnerFixture();
        fixture.Store("1.0.0");
        File.AppendAllText(fixture.Paths[0], "changed");

        var result = await fixture.Runner.AnalyzeItemAsync(fixture.Item.Id, null, CancellationToken.None, "source-1");

        Assert.True(result.AnyStored);
        await fixture.Worker.ReceivedWithAnyArgs(1).AnalyzeAsync(default!, 0, 0, 0, null, default);
    }

    [Fact]
    public async Task FailedUpgradeKeepsOlderAnalysisReadableAndReleasesItsProtection()
    {
        using var fixture = new RunnerFixture();
        var old = fixture.Store("1.0.0");
        fixture.Worker.AnalyzeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.Equal("1.0.0", Assert.Single(fixture.Coordinator.ProtectedKeys).AlgorithmVersion);
                return Task.FromException<string>(new ServerAnalysisWorkerException("fixture decoder failure"));
            });

        await fixture.Runner.AnalyzePendingAsync(null, CancellationToken.None);

        Assert.Single(fixture.Records);
        Assert.Same(old, fixture.Records.Values.Single());
        Assert.Empty(fixture.Coordinator.ProtectedKeys);
        await fixture.Repository.DidNotReceiveWithAnyArgs().StoreBoundedAsync(default!, default);
        var result = await fixture.Runner.AnalyzeItemAsync(fixture.Item.Id, null, CancellationToken.None, "source-1");
        Assert.Equal("already-current", Assert.Single(result.Sources).Detail);
    }

    [Fact]
    public async Task CorruptCurrentRecordDoesNotHideValidOlderAnalysis()
    {
        using var fixture = new RunnerFixture();
        var current = fixture.Store(AetherAlgorithm.Version);
        current.CompressedDocument = [1, 2, 3];
        var old = fixture.Store("1.0.0");
        var media = new MediaFingerprintService().Create(fixture.Item, "source-1")!;

        var readable = await AnalysisVersionPolicy.ReadAsync(
            fixture.Repository, media, new AnalysisDocumentValidator(), allowCompatible: true, CancellationToken.None);

        Assert.NotNull(readable);
        Assert.Same(old, readable.Record);
    }

    [Fact]
    public async Task CancelledUpgradeKeepsSourceAndReleasesProtection()
    {
        using var fixture = new RunnerFixture();
        var old = fixture.Store("1.0.0");
        using var cancellation = new CancellationTokenSource();
        fixture.Worker.AnalyzeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<string>(call.Arg<CancellationToken>());
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Runner.AnalyzePendingAsync(null, cancellation.Token));

        Assert.Same(old, fixture.Records.Values.Single());
        Assert.Empty(fixture.Coordinator.ProtectedKeys);
    }

    [Fact]
    public async Task UploadDuringUpgradeWinsOverTheWorkerResult()
    {
        using var fixture = new RunnerFixture();
        fixture.Store("1.0.0");
        AnalysisRecord? uploaded = null;
        fixture.Worker.AnalyzeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                uploaded = fixture.Store(AetherAlgorithm.Version, "browser");
                return StoredAnalysisTestData.Upload().ToJsonString();
            });

        var result = await fixture.Runner.AnalyzeItemAsync(
            fixture.Item.Id, null, CancellationToken.None, "source-1", upgradeCompatible: true);

        Assert.Equal("analysis-changed", Assert.Single(result.Sources).Detail);
        Assert.Same(uploaded, fixture.Records[new AnalysisKey(fixture.Item.Id, "source-1", AetherAlgorithm.Id, AetherAlgorithm.Version)]);
        await fixture.Repository.DidNotReceiveWithAnyArgs().StoreBoundedAsync(default!, default);
        Assert.Empty(fixture.Coordinator.ProtectedKeys);
    }

    [Fact]
    public async Task DisposalCancelsNewWorkWithoutTouchingDisposedGates()
    {
        var runner = new ServerAnalysisRunner(
            Substitute.For<ILibraryManager>(),
            Substitute.For<IAnalysisRepository>(),
            new AnalysisDocumentValidator(),
            new MediaFingerprintService(),
            new AnalysisRepresentationService(),
            new AnalysisWriteCoordinator(),
            worker: null!,
            new ServerAnalysisActivity(),
            NullLogger<ServerAnalysisRunner>.Instance);

        runner.Dispose();
        runner.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.AnalyzePendingAsync(null, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.AnalyzeItemAsync(Guid.NewGuid(), null, CancellationToken.None));
    }

    [Fact]
    public void RoutineVisitsNewestAdditionsFirst()
    {
        using var fixture = new RunnerFixture();

        Assert.Single(fixture.Runner.SelectItems());

        fixture.Library.Received(1).GetItemList(Arg.Is<InternalItemsQuery>(query =>
            query.OrderBy.Count == 2
            && query.OrderBy[0].Item1 == ItemSortBy.DateCreated
            && query.OrderBy[0].Item2 == SortOrder.Descending
            && query.OrderBy[1].Item1 == ItemSortBy.SortName
            && query.OrderBy[1].Item2 == SortOrder.Ascending));
    }

    private sealed class RunnerFixture : IDisposable
    {
        public string[] Paths { get; }
        public BaseItem Item { get; } = Substitute.For<BaseItem>();
        public IAnalysisRepository Repository { get; } = Substitute.For<IAnalysisRepository>();
        public IServerAnalysisWorkerRunner Worker { get; } = Substitute.For<IServerAnalysisWorkerRunner>();
        public AnalysisWriteCoordinator Coordinator { get; } = new();
        public Dictionary<AnalysisKey, AnalysisRecord> Records { get; } = new();
        public ServerAnalysisRunner Runner { get; }
        public ILibraryManager Library { get; }

        public RunnerFixture(int sourceCount = 1, AnalysisWorkerExecutionGate? workerExecutionGate = null)
        {
            Paths = Enumerable.Range(0, sourceCount)
                .Select(_ => Path.Combine(Path.GetTempPath(), $"aether-runner-{Guid.NewGuid():N}.mkv")).ToArray();
            foreach (var path in Paths)
            {
                File.WriteAllBytes(path, [0]);
            }

            Item.Id = Guid.NewGuid();
            Item.Name = "Fixture";
            Item.GetMediaSources(false).Returns(Paths.Select((path, index) => new MediaSourceInfo
            {
                Id = $"source-{index + 1}",
                Path = path,
                RunTimeTicks = TimeSpan.FromMinutes(1).Ticks
            }).ToList());
            var library = Substitute.For<ILibraryManager>();
            Library = library;
            library.GetItemById<BaseItem>(Item.Id).Returns(Item);
            library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([Item]);
            Repository.GetAsync(Arg.Any<AnalysisKey>(), Arg.Any<CancellationToken>())
                .Returns(call => Records.GetValueOrDefault(call.Arg<AnalysisKey>()));
            Repository.GetMetadataAsync(Arg.Any<IReadOnlyCollection<AnalysisKey>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<IReadOnlyCollection<AnalysisKey>>()
                    .Where(Records.ContainsKey).ToDictionary(key => key, key =>
                    {
                        var record = Records[key];
                        return new AnalysisRecordMetadata(key, record.MediaFingerprint, record.CreatedAt,
                            record.LastAccessedAt, record.FrameCount, record.CompressedDocument.Length, record.Etag);
                    }));
            Repository.StoreBoundedAsync(Arg.Any<AnalysisStoreRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var record = call.Arg<AnalysisStoreRequest>().Record;
                    var key = new AnalysisKey(record.ItemId, record.MediaSourceId, record.AlgorithmId, record.AlgorithmVersion);
                    var replaced = Records.ContainsKey(key);
                    Records[key] = record;
                    return replaced ? AnalysisStoreResult.Replaced : AnalysisStoreResult.Created;
                });
            Worker.AnalyzeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
                .Returns(StoredAnalysisTestData.Upload().ToJsonString());
            Runner = new ServerAnalysisRunner(library, Repository, new AnalysisDocumentValidator(),
                new MediaFingerprintService(), new AnalysisRepresentationService(), Coordinator, Worker,
                new ServerAnalysisActivity(), NullLogger<ServerAnalysisRunner>.Instance, workerExecutionGate);
        }

        public AnalysisRecord Store(string version, string platform = "server", string sourceId = "source-1")
        {
            var media = new MediaFingerprintService().Create(Item, sourceId)!;
            var record = StoredAnalysisTestData.Create(media, version, platform);
            Records[new AnalysisKey(Item.Id, sourceId, AetherAlgorithm.Id, version)] = record;
            return record;
        }

        public void Dispose()
        {
            Runner.Dispose();
            Coordinator.Dispose();
            foreach (var path in Paths)
            {
                File.Delete(path);
            }
        }
    }
}
