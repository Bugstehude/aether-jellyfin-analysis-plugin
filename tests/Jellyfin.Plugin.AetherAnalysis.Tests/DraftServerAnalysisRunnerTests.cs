using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

[Collection("Draft composition staging")]
public sealed class DraftServerAnalysisRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExperimentalRoutineKeepsValidatedFullTargetWithoutAnotherComponentWorker(bool noTrack)
    {
        await using var fixture = await Fixture.CreateAsync(noTrack);
        if (noTrack) fixture.Settings = fixture.Settings with { Width = 64 }; // Original no-track measurement profile.
        Assert.Equal(DraftCompositionResult.Stored, await fixture.RunAsync());
        var target = (await fixture.Repository.GetAsync(fixture.TargetKey, default))!;
        var etag = target.Etag;
        var original = target.CompressedDocument.ToArray();
        fixture.Modes.Clear();
        fixture.Worker.ClearReceivedCalls();
        Assert.Equal(DraftCompositionResult.AlreadyCurrent, await fixture.RunIfNeededAsync());
        Assert.Empty(fixture.Modes);
        await fixture.Worker.DidNotReceiveWithAnyArgs().ProduceAsync(default!, default!, default, default!, default!, default!, default);
        await fixture.Worker.Received(1).ProbeAsync(Arg.Any<DraftHostSource>(), Arg.Any<DraftWorkerSettings>(), Arg.Any<CancellationToken>());
        var kept = (await fixture.Repository.GetAsync(fixture.TargetKey, default))!;
        Assert.Equal(etag, kept.Etag);
        Assert.Equal(original, kept.CompressedDocument);
        Assert.NotNull(await fixture.Repository.GetAsync(fixture.SourceKey, default));
        Assert.Empty(fixture.Writes.ProtectedKeys);
    }

    [Fact]
    public async Task ExperimentalRoutineMigratesAnOldAnalysisThenSkipsItsValidTarget()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.Source.CompressedDocument.ToArray();
        Assert.Equal(DraftCompositionResult.Stored, await fixture.RunIfNeededAsync());
        Assert.Equal(new[] { "audio-only", "video-only" }, fixture.Modes);
        Assert.Equal(original, (await fixture.Repository.GetAsync(fixture.SourceKey, default))!.CompressedDocument);
        fixture.Modes.Clear();
        Assert.Equal(DraftCompositionResult.AlreadyCurrent, await fixture.RunIfNeededAsync());
        Assert.Empty(fixture.Modes);
    }

    [Fact]
    public async Task ExplicitRecalculationStillRunsBothFreshComponentsOnCurrentTarget()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(DraftCompositionResult.Stored, await fixture.RunAsync());
        var previous = (await fixture.Repository.GetAsync(fixture.TargetKey, default))!.Etag;
        fixture.Modes.Clear();
        fixture.AfterStage = _ => { };
        Assert.Equal(DraftCompositionResult.Stored, await fixture.RunAsync());
        Assert.Equal(new[] { "audio-only", "video-only" }, fixture.Modes);
        Assert.NotEqual(previous, (await fixture.Repository.GetAsync(fixture.TargetKey, default))!.Etag);
    }

    [Fact]
    public async Task CorruptTargetIsRebuiltWhileItsOlderSourceStaysReadable()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(DraftCompositionResult.Stored, await fixture.RunAsync());
        var target = (await fixture.Repository.GetAsync(fixture.TargetKey, default))!;
        target.CompressedDocument = [0, 1, 2];
        await fixture.Repository.UpsertAsync(target, null, default);
        fixture.Modes.Clear();
        fixture.AfterStage = _ => { };
        Assert.Equal(DraftCompositionResult.Stored, await fixture.RunIfNeededAsync());
        Assert.Equal(new[] { "audio-only", "video-only" }, fixture.Modes);
        Assert.NotNull(await fixture.Repository.GetAsync(fixture.SourceKey, default));
        Assert.Empty(fixture.Writes.ProtectedKeys);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("target")]
    [InlineData("source")]
    public async Task FinalNoOpRecheckDoesNotAcceptChangedHostOrRepositoryState(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(DraftCompositionResult.Stored, await fixture.RunAsync());
        var original = (await fixture.Repository.GetAsync(fixture.TargetKey, default))!.CompressedDocument.ToArray();
        fixture.Modes.Clear();
        var calls = 0;
        fixture.Resolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++calls == 4)
            {
                if (mutation == "host") fixture.Current = fixture.Current with { StreamIdentity = "changed-during-no-op" };
                else
                {
                    var key = mutation == "target" ? fixture.TargetKey : fixture.SourceKey;
                    var record = fixture.Repository.GetAsync(key, default).GetAwaiter().GetResult()!;
                    record.Etag = "\"newer-upload-during-no-op\"";
                    fixture.Repository.UpsertAsync(record, null, default).GetAwaiter().GetResult();
                }
            }

            return fixture.Current;
        });
        Assert.Equal(mutation == "host" ? DraftCompositionResult.MediaChanged : DraftCompositionResult.AnalysisChanged,
            await fixture.RunIfNeededAsync());
        Assert.Empty(fixture.Modes);
        Assert.Equal(original, (await fixture.Repository.GetAsync(fixture.TargetKey, default))!.CompressedDocument);
        if (mutation == "target") Assert.Equal("\"newer-upload-during-no-op\"", (await fixture.Repository.GetAsync(fixture.TargetKey, default))!.Etag);
        Assert.Empty(fixture.Writes.ProtectedKeys);
    }

    [Fact]
    public async Task DisabledGateDoesNotResolveMediaOrExecuteProcesses()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings = fixture.Settings with { Enabled = false };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RunAsync());
        Assert.Equal("draft-execution-disabled", error.Message);
        await fixture.Resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default, default!, default);
        Assert.Null(fixture.WorkDirectory);
        Assert.Equal("1.2.0", AetherAlgorithm.Version);
        Assert.False(new Jellyfin.Plugin.AetherAnalysis.Configuration.PluginConfiguration().ExperimentalDraftAnalysisEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshComponentsAreStoredTogetherAndPreserveTheOlderSource(bool noTrack)
    {
        await using var fixture = await Fixture.CreateAsync(noTrack);
        var original = fixture.Source.CompressedDocument.ToArray();
        Assert.Equal(DraftCompositionResult.Stored, await fixture.RunAsync());
        var record = await fixture.Repository.GetAsync(fixture.TargetKey, default);
        Assert.NotNull(record);
        using var document = JsonDocument.Parse(CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes));
        Assert.Equal(noTrack ? "no-track" : "available", document.RootElement.GetProperty("audioAnalysis").GetProperty("state").GetString());
        Assert.Equal(original, (await fixture.Repository.GetAsync(fixture.SourceKey, default))!.CompressedDocument);
        Assert.Empty(fixture.Writes.ProtectedKeys);
        Assert.False(Directory.Exists(fixture.WorkDirectory));
        Assert.Equal(new[] { "audio-only", "video-only" }, fixture.Modes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StableTargetUsesFreshComponentsAndKeepsDraftAndLegacyRecordsUnchanged(bool noTrack)
    {
        await using var fixture = await Fixture.CreateAsync(noTrack);
        if (noTrack) fixture.Settings = fixture.Settings with { Width = 64 };
        var legacyBytes = fixture.Source.CompressedDocument.ToArray();
        var draftKey = fixture.TargetKey;

        Assert.Equal(DraftCompositionResult.Stored, await fixture.RunAsync());
        var draft = (await fixture.Repository.GetAsync(draftKey, default))!;
        var draftBytes = draft.CompressedDocument.ToArray();
        var draftEtag = draft.Etag;

        fixture.Settings = fixture.Settings with { TargetVersion = "1.2.0" };
        fixture.Modes.Clear();
        Assert.Equal(DraftCompositionResult.Stored, await fixture.RunIfNeededAsync());
        Assert.Equal(new[] { "audio-only", "video-only" }, fixture.Modes);

        var stableKey = fixture.TargetKey;
        Assert.Equal("1.2.0", stableKey.AlgorithmVersion);
        var stable = (await fixture.Repository.GetAsync(stableKey, default))!;
        var stableBytes = CompressionCodec.Decompress(stable.CompressedDocument, stable.UncompressedBytes);
        var context = new DraftArtifactContext(
            fixture.Current.Media.ItemId,
            fixture.Current.Media.MediaSourceId,
            fixture.Current.Media.Fingerprint,
            fixture.Current.Media.DurationMs,
            noTrack ? null : 1,
            noTrack ? null : 1,
            fixture.Settings.ProducerRevision,
            fixture.Settings.MaximumDocumentBytes,
            fixture.Settings.TargetVersion);
        var validated = DraftAnalysisArtifactValidator.ValidateStoredMaster(stableBytes, context);
        Assert.Equal("1.2.0", validated.GetProperty("algorithm").GetProperty("version").GetString());

        var decision = DraftAnalysisMigrationPlanner.Evaluate(stable, context, fixture.Settings.Fps, fixture.Settings.Width);
        Assert.True(decision.Action == DraftMigrationAction.KeepCurrent,
            $"Expected the stable master to validate as current, got {decision.Action}: {decision.Reason}");
        Assert.Equal("current-full-profile", decision.Reason);
        using (var document = JsonDocument.Parse(stableBytes))
        {
            var root = document.RootElement;
            Assert.Equal("1.2.0", root.GetProperty("algorithm").GetProperty("version").GetString());
            var composition = root.GetProperty("draftComposition");
            Assert.Equal("1.2.0-draft", composition.GetProperty("audioArtifact").GetProperty("algorithm").GetProperty("version").GetString());
            Assert.Equal("1.2.0-draft", composition.GetProperty("videoArtifact").GetProperty("algorithm").GetProperty("version").GetString());
            Assert.Equal("1.2.0-draft", root.GetProperty("signalProvenance").GetProperty("denseAudio").GetProperty("algorithmVersion").GetString());
            Assert.Equal("1.2.0-draft", root.GetProperty("signalProvenance").GetProperty("cuts").GetProperty("algorithmVersion").GetString());
            Assert.Equal(noTrack ? "no-track" : "available",
                root.GetProperty("audioAnalysis").GetProperty("state").GetString());
        }

        fixture.Modes.Clear();
        fixture.Worker.ClearReceivedCalls();
        Assert.Equal(DraftCompositionResult.AlreadyCurrent, await fixture.RunIfNeededAsync());
        Assert.Empty(fixture.Modes);
        await fixture.Worker.DidNotReceiveWithAnyArgs().ProduceAsync(default!, default!, default, default!, default!, default!, default);

        var keptStable = (await fixture.Repository.GetAsync(stableKey, default))!;
        var keptDraft = (await fixture.Repository.GetAsync(draftKey, default))!;
        var keptLegacy = (await fixture.Repository.GetAsync(fixture.SourceKey, default))!;
        Assert.Equal(stable.Etag, keptStable.Etag);
        Assert.Equal(stable.CompressedDocument, keptStable.CompressedDocument);
        Assert.Equal(draftEtag, keptDraft.Etag);
        Assert.Equal(draftBytes, keptDraft.CompressedDocument);
        Assert.Equal(legacyBytes, keptLegacy.CompressedDocument);
        Assert.Empty(fixture.Writes.ProtectedKeys);
    }

    [Theory]
    [InlineData("absent-legacy", true)]
    [InlineData("wrong-legacy", false)]
    [InlineData("absent-dense", false)]
    public async Task OptionalLegacyNamespaceStillRequiresVerifiedMappingWhenPresent(string mutation, bool accepted)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.EditComponent = node =>
        {
            if (node["audio"] is not JsonObject audio) return;
            var legacy = audio["legacyAudioAnalysis"]!["referenceTrack"]!.AsObject();
            if (mutation == "absent-legacy") legacy.Remove("jellyfinStreamIndex");
            if (mutation == "wrong-legacy") legacy["jellyfinStreamIndex"] = 7;
            if (mutation == "absent-dense") audio["audioAnalysis"]!["referenceTrack"]!.AsObject().Remove("jellyfinStreamIndex");
        };
        if (accepted) Assert.Equal(DraftCompositionResult.Stored, await fixture.RunAsync());
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RunAsync());
            Assert.Equal("audio-unverified-jellyfin-track", error.Message);
            Assert.Null(await fixture.Repository.GetAsync(fixture.TargetKey, default));
        }
        Assert.Empty(fixture.Writes.ProtectedKeys);
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("audio-only")]
    [InlineData("video-only")]
    public async Task StreamMetadataChangesAtAnyStageDiscardTheDraft(string stage)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AfterStage = value =>
        {
            if (value == stage) fixture.Current = fixture.Current with { StreamIdentity = "changed-track-declaration" };
        };
        Assert.Equal(DraftCompositionResult.MediaChanged, await fixture.RunAsync());
        Assert.Null(await fixture.Repository.GetAsync(fixture.TargetKey, default));
        Assert.NotNull(await fixture.Repository.GetAsync(fixture.SourceKey, default));
        Assert.Empty(fixture.Writes.ProtectedKeys);
        Assert.False(Directory.Exists(fixture.WorkDirectory));
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("audio-only")]
    [InlineData("video-only")]
    public async Task ConcurrentTargetUploadWinsAcrossTheWholePipeline(string stage)
    {
        await using var fixture = await Fixture.CreateAsync();
        AnalysisRecord? uploaded = null;
        fixture.AfterStage = value =>
        {
            if (value != stage) return;
            uploaded = StoredAnalysisTestData.Create(fixture.Current.Media, DraftAnalysisMasterBuilder.AlgorithmVersion);
            uploaded.Etag = "\"concurrent-upload\"";
            fixture.Repository.UpsertAsync(uploaded, null, default).GetAwaiter().GetResult();
        };
        Assert.Equal(DraftCompositionResult.AnalysisChanged, await fixture.RunAsync());
        Assert.Equal(uploaded!.Etag, (await fixture.Repository.GetAsync(fixture.TargetKey, default))!.Etag);
        Assert.Empty(fixture.Writes.ProtectedKeys);
        Assert.False(Directory.Exists(fixture.WorkDirectory));
    }

    [Fact]
    public async Task WrongBundleDigestFailsBeforeAnyProbeOrComponent()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings = fixture.Settings with { WorkerSha256 = new string('0', 64) };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RunAsync());
        Assert.Equal("draft-worker-bundle-digest", error.Message);
        Assert.Empty(fixture.Modes);
        Assert.Null(fixture.WorkDirectory);
        Assert.Empty(fixture.Writes.ProtectedKeys);
    }

    [Fact]
    public async Task SourceDeletionDuringProbeDoesNotRestoreOrPublishAnyAnalysis()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AfterStage = value =>
        {
            if (value == "probe") fixture.Repository.DeleteAsync(fixture.SourceKey, default).GetAwaiter().GetResult();
        };
        Assert.Equal(DraftCompositionResult.AnalysisChanged, await fixture.RunAsync());
        Assert.Null(await fixture.Repository.GetAsync(fixture.SourceKey, default));
        Assert.Null(await fixture.Repository.GetAsync(fixture.TargetKey, default));
        Assert.Empty(fixture.Modes);
        Assert.Empty(fixture.Writes.ProtectedKeys);
    }

    [Fact]
    public async Task CancellationAfterOneComponentReleasesProtectionAndStoresNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.AfterStage = value => { if (value == "audio-only") cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync(cancellation.Token));
        Assert.Null(await fixture.Repository.GetAsync(fixture.TargetKey, default));
        Assert.Empty(fixture.Writes.ProtectedKeys);
        Assert.False(Directory.Exists(fixture.WorkDirectory));
    }

    [Fact]
    public async Task WorkerFailureAfterAudioKeepsTheOldSource()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AfterStage = value => { if (value == "video-only") throw new InvalidDataException("fixture-worker-error"); };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RunAsync());
        Assert.Null(await fixture.Repository.GetAsync(fixture.TargetKey, default));
        Assert.NotNull(await fixture.Repository.GetAsync(fixture.SourceKey, default));
        Assert.Empty(fixture.Writes.ProtectedKeys);
        Assert.False(Directory.Exists(fixture.WorkDirectory));
    }

    [Fact]
    public async Task SharedExecutionLeasePreventsConcurrentExperimentalWorkers()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        using var lease = await fixture.Execution.AcquireAsync(default);
        var pending = fixture.RunAsync(cancellation.Token);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await fixture.Resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default, default!, default);
    }

    [Fact]
    public async Task DisposalCancelsWaitersOnTheSharedExecutionLease()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var lease = await fixture.Execution.AcquireAsync(default);
        var pending = fixture.RunAsync();
        fixture.Runner.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(fixture.Writes.ProtectedKeys);
    }

    [Fact]
    public async Task JobTimeoutIncludesWaitingForTheExecutionLease()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings = fixture.Settings with { Timeout = TimeSpan.FromMilliseconds(100) };
        using var lease = await fixture.Execution.AcquireAsync(default);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RunAsync());
        Assert.Equal("draft-job-timeout", error.Message);
        await fixture.Resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default, default!, default);
        Assert.Empty(fixture.Writes.ProtectedKeys);
    }

    [Fact]
    public async Task ServerAnalysisRunnerRoutesExplicitStableRequestThroughSynchronizedEngine()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Settings = fixture.Settings with { TargetVersion = "1.2.0" };
        var item = Substitute.For<BaseItem>();
        item.Id = fixture.Current.Media.ItemId;
        item.Name = "Stable synchronized fixture";
        var source = new MediaSourceInfo
        {
            Id = fixture.Current.Media.MediaSourceId,
            Path = fixture.Current.InputPath,
            RunTimeTicks = TimeSpan.FromMilliseconds(fixture.Current.Media.DurationMs).Ticks
        };
        item.GetMediaSources(false).Returns([source]);
        var library = Substitute.For<ILibraryManager>();
        library.GetItemById<BaseItem>(item.Id).Returns(item);
        var legacyWorker = Substitute.For<IServerAnalysisWorkerRunner>();
        var activity = new ServerAnalysisActivity();
        using var runner = new ServerAnalysisRunner(
            library,
            fixture.Repository,
            new AnalysisDocumentValidator(),
            new MediaFingerprintService(),
            new AnalysisRepresentationService(),
            fixture.Writes,
            legacyWorker,
            activity,
            NullLogger<ServerAnalysisRunner>.Instance,
            fixture.Execution,
            fixture.Runner);
        var progress = new ProgressCapture();

        var result = await runner.AnalyzeItemAsync(item.Id, progress, default, source.Id, recalculate: true);

        Assert.True(result.AnyStored);
        Assert.Equal(SourceAnalysisStatus.Created, Assert.Single(result.Sources).Status);
        Assert.Equal(new[] { "audio-only", "video-only" }, fixture.Modes);
        Assert.Equal(1.0, Assert.Single(progress.Values));
        Assert.Null(activity.Snapshot().Current);
        Assert.Equal("stored", Assert.Single(activity.Snapshot().Recent).Outcome);
        Assert.NotNull(await fixture.Repository.GetAsync(fixture.TargetKey, default));
        await legacyWorker.DidNotReceiveWithAnyArgs().AnalyzeAsync(default!, 0, 0, 0, default, default);
        Assert.Empty(fixture.Writes.ProtectedKeys);
    }

    [DraftNativeTheory]
    [InlineData("available", "1.2.0-draft")]
    [InlineData("no-track", "1.2.0-draft")]
    [InlineData("second-default", "1.2.0-draft")]
    [InlineData("available", "1.2.0")]
    [InlineData("no-track", "1.2.0")]
    [InlineData("second-default", "1.2.0")]
    public async Task RealSharedBundleProducesValidatedCompositionThroughTheHostAdapter(string profile, string targetVersion)
    {
        // Explicit opt-in offline smoke. Host metadata is synthetic, not a live Jellyfin claim.
        var manifestPath = Environment.GetEnvironmentVariable("AETHER_DRAFT_NATIVE_PROOF_MANIFEST")!;
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var media = manifest["media"]![profile]!.AsObject();
        await using var fixture = await Fixture.CreateAsync(profile == "no-track");
        fixture.Settings = fixture.Settings with { TargetVersion = targetVersion };
        var streams = media["streams"]!.AsArray().Select(node => new DraftHostStreamDescriptor(
            node!["index"]!.GetValue<int>(), node["type"]!.GetValue<string>(), node["codec"]?.GetValue<string>(),
            node["channels"]?.GetValue<int>(), node["sampleRate"]?.GetValue<int>(), false, false)).ToArray();
        var path = media["path"]!.GetValue<string>();
        var file = new FileInfo(path);
        var source = new MediaBrowser.Model.Dto.MediaSourceInfo
        {
            Id = fixture.Current.Media.MediaSourceId,
            Path = path,
            RunTimeTicks = TimeSpan.FromSeconds(2).Ticks,
            MediaStreams = streams.Select(value => new MediaBrowser.Model.Entities.MediaStream
            {
                Index = value.Index,
                Type = Enum.Parse<MediaBrowser.Model.Entities.MediaStreamType>(value.Type, true),
                Codec = value.Codec,
                Channels = value.Channels,
                SampleRate = value.SampleRate
            }).ToList(),
            DefaultAudioStreamIndex = media["selectedIndex"]?.GetValue<int>()
        };
        var item = Substitute.For<MediaBrowser.Controller.Entities.BaseItem>();
        item.Id = fixture.Current.Media.ItemId;
        item.GetMediaSources(false).Returns([source]);
        var library = Substitute.For<MediaBrowser.Controller.Library.ILibraryManager>();
        library.GetItemById<MediaBrowser.Controller.Entities.BaseItem>(item.Id).Returns(item);
        var hostResolver = new DraftJellyfinSourceResolver(library, new MediaFingerprintService());
        var host = await hostResolver.ResolveAsync(item.Id, source.Id, default);
        Assert.NotNull(host);
        await fixture.Repository.DeleteAsync(fixture.SourceKey, default);
        await fixture.Repository.UpsertAsync(StoredAnalysisTestData.Create(host.Media, "1.1.0"), null, default);
        var settings = fixture.Settings with
        {
            WorkerPath = manifest["workerPath"]!.GetValue<string>(),
            WorkerSha256 = manifest["workerSha256"]!.GetValue<string>(),
            ProducerRevision = manifest["producerRevision"]!.GetValue<string>(),
            FfmpegPath = manifest["ffmpegPath"]!.GetValue<string>(),
            FfprobePath = manifest["ffprobePath"]!.GetValue<string>(),
            Timeout = TimeSpan.FromMinutes(2)
        };
        using var runner = new DraftServerAnalysisRunner(hostResolver, new DraftWorkerProcessRunner(), fixture.Repository,
            fixture.Writes, fixture.Execution, () => settings);
        Assert.Equal(DraftCompositionResult.Stored, await runner.AnalyzeAsync(item.Id, source.Id, default));
        var record = await fixture.Repository.GetAsync(fixture.TargetKey, default);
        Assert.NotNull(record);
        var bytes = CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes);
        using var document = JsonDocument.Parse(bytes);
        var state = document.RootElement.GetProperty("audioAnalysis").GetProperty("state").GetString();
        Assert.Equal(profile == "no-track" ? "no-track" : "available", state);
        if (profile != "no-track") Assert.Equal(source.DefaultAudioStreamIndex,
            document.RootElement.GetProperty("audioAnalysis").GetProperty("referenceTrack").GetProperty("jellyfinStreamIndex").GetInt32());
        foreach (var detail in new[] { "full", "balanced", "compact" })
        {
            var representation = DraftAnalysisMasterBuilder.Create(bytes, detail, 32 * 1024 * 1024);
            var output = Path.Combine(manifest["exportDirectory"]!.GetValue<string>(), targetVersion);
            Directory.CreateDirectory(output);
            await File.WriteAllBytesAsync(Path.Combine(output, profile + "-" + detail + ".json"), representation.Json);
        }

        Assert.Equal(DraftCompositionResult.AlreadyCurrent, await runner.AnalyzeIfNeededAsync(item.Id, source.Id, default));
        var unchanged = (await fixture.Repository.GetAsync(fixture.TargetKey, default))!;
        Assert.Equal(record.Etag, unchanged.Etag);
        Assert.Equal(record.CompressedDocument, unchanged.CompressedDocument);
        Assert.Equal(host.Media.Fingerprint, record.MediaFingerprint);
        Assert.Empty(fixture.Writes.ProtectedKeys);
        Assert.True(file.Exists);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Revision = "sha256:8ddc99b791acc18b70aba15237c909024a69364637f2b776fb9a48f96a758e3b";
        private readonly string _directory;
        public DraftHostSource Current { get; set; }
        public DraftWorkerSettings Settings { get; set; }
        public IDraftSourceResolver Resolver { get; } = Substitute.For<IDraftSourceResolver>();
        public IDraftWorkerProcessRunner Worker { get; } = Substitute.For<IDraftWorkerProcessRunner>();
        public AnalysisWriteCoordinator Writes { get; } = new();
        public AnalysisWorkerExecutionGate Execution { get; } = new();
        public AnalysisRepository Repository { get; }
        public DraftServerAnalysisRunner Runner { get; }
        public AnalysisRecord Source { get; private set; } = null!;
        public List<string> Modes { get; } = [];
        public Action<string>? AfterStage { get; set; }
        public Action<JsonObject>? EditComponent { get; set; }
        public string? WorkDirectory { get; private set; }
        public AnalysisKey SourceKey => new(Current.Media.ItemId, Current.Media.MediaSourceId, "aether-visual", "1.1.0");
        public AnalysisKey TargetKey => SourceKey with { AlgorithmVersion = Settings.TargetVersion };

        private Fixture(string directory, bool noTrack)
        {
            _directory = directory;
            var path = Path.Combine(directory, "input.mkv");
            File.WriteAllBytes(path, [0]);
            var bundle = Path.Combine(directory, "draft.cjs");
            File.WriteAllText(bundle, "module.exports = {};");
            var media = new MediaFingerprint(Guid.NewGuid(), "source-1", "sha256:" + new string('a', 64), "strong", 2000);
            Current = new DraftHostSource(media, path, "stable-streams", noTrack
                ? [new DraftHostStreamDescriptor(0, "video", "h264", null, null, false, false)]
                : [new DraftHostStreamDescriptor(0, "video", "h264", null, null, false, false),
                    new DraftHostStreamDescriptor(1, "audio", "flac", 1, 22050, false, false)], noTrack ? null : 1);
            Settings = new DraftWorkerSettings("node", bundle, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundle))).ToLowerInvariant(),
                noTrack ? "sha256:c7ed93c561f2988a3496b93df6ab9f514707b23be8fdf6b59a5ab73e101e5308" : Revision,
                "ffmpeg", "ffprobe", Enabled: true);
            var factory = new Factory(new DbContextOptionsBuilder<AnalysisDbContext>().UseSqlite("Data Source=" + Path.Combine(directory, "store.sqlite")).Options);
            using (var context = factory.CreateDbContext()) context.Database.Migrate();
            Repository = new AnalysisRepository(factory, Writes);
            Resolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Current;
            });
            Worker.ProbeAsync(Arg.Any<DraftHostSource>(), Arg.Any<DraftWorkerSettings>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Assert.Contains(SourceKey, Writes.ProtectedKeys);
                AfterStage?.Invoke("probe");
                return JsonSerializer.SerializeToUtf8Bytes(new
                {
                    streams = noTrack
                    ? new object[] { new { index = 0, codec_type = "video" } }
                    : [new { index = 0, codec_type = "video" }, new { index = 1, codec_type = "audio", codec_name = "flac", channels = 1, sample_rate = "22050" }]
                });
            });
            Worker.ProduceAsync(Arg.Any<DraftHostSource>(), Arg.Any<DraftTrackSelection>(), Arg.Any<JsonElement>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<DraftWorkerSettings>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                var mode = call.ArgAt<string>(3);
                WorkDirectory = call.ArgAt<string>(4);
                if (AfterStage is null) Assert.Null(await Repository.GetAsync(TargetKey, default));
                Modes.Add(mode);
                var file = noTrack ? Path.Combine("consumer-profiles", "missing-audio-track-" + mode + ".json")
                    : (mode == "audio-only" ? "audio-full-v2.json" : "video-full-v2.json");
                var node = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "draft", file)))!.AsObject();
                node["snapshot"] = JsonSerializer.SerializeToNode(call.ArgAt<JsonElement>(2));
                if (node["video"] is JsonObject video) video["mediaFingerprintAtStart"] = Current.Media.Fingerprint;
                foreach (var group in node["signalProvenance"]!.AsObject()) group.Value!["sourceFingerprint"] = Current.Media.Fingerprint;
                if (node["video"] is JsonObject videoComponent) videoComponent["signalProvenance"] = node["signalProvenance"]!.DeepClone();
                AddTrackIndex(node, call.ArgAt<DraftTrackSelection>(1).JellyfinStreamIndex);
                EditComponent?.Invoke(node);
                var output = Path.Combine(WorkDirectory, mode + ".json");
                await File.WriteAllBytesAsync(output, JsonSerializer.SerializeToUtf8Bytes(node), call.ArgAt<CancellationToken>(6));
                AfterStage?.Invoke(mode);
                return output;
            });
            Runner = new DraftServerAnalysisRunner(Resolver, Worker, Repository, Writes, Execution, () => Settings);
        }

        public static async Task<Fixture> CreateAsync(bool noTrack = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), "aether-draft-runner-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var fixture = new Fixture(directory, noTrack);
            fixture.Source = StoredAnalysisTestData.Create(fixture.Current.Media, "1.1.0");
            await fixture.Repository.UpsertAsync(fixture.Source, null, default);
            return fixture;
        }

        public Task<DraftCompositionResult> RunAsync(CancellationToken cancellationToken = default) =>
            Runner.AnalyzeAsync(Current.Media.ItemId, Current.Media.MediaSourceId, cancellationToken);

        public Task<DraftCompositionResult> RunIfNeededAsync(CancellationToken cancellationToken = default) =>
            Runner.AnalyzeIfNeededAsync(Current.Media.ItemId, Current.Media.MediaSourceId, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Runner.Dispose();
            Writes.Dispose();
            await Task.CompletedTask;
            Directory.Delete(_directory, true);
        }

        private static void AddTrackIndex(JsonNode? node, int? index)
        {
            if (node is JsonObject obj)
            {
                if (obj.ContainsKey("ffmpegStreamIndex") && index.HasValue) obj["jellyfinStreamIndex"] = index.Value;
                foreach (var value in obj.ToArray()) AddTrackIndex(value.Value, index);
            }
            else if (node is JsonArray array) foreach (var value in array) AddTrackIndex(value, index);
        }
    }

    private sealed class Factory(DbContextOptions<AnalysisDbContext> options) : IDbContextFactory<AnalysisDbContext>
    {
        public AnalysisDbContext CreateDbContext() => new(options);
        public Task<AnalysisDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class ProgressCapture : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }
}

internal sealed class DraftNativeTheoryAttribute : TheoryAttribute
{
    public DraftNativeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("AETHER_DRAFT_NATIVE_PROOF_MANIFEST") is null)
            Skip = "Requires an explicit standalone bundle and generated media proof manifest.";
    }
}
