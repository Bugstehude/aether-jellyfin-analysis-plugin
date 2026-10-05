using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AetherAnalysis.Api;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;
using Jellyfin.Plugin.AetherAnalysis.Contracts;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>
/// Tests für die HTTP-Oberfläche — dreizehn Endpunkte, die bis hierher keinen
/// einzigen hatten. Das Plugin nennt in seinem README zwei Sicherheitszusagen,
/// und beide waren unbelegt: „404 ohne Existenz-Leak" und „Schreibzugriffe nur
/// für Berechtigte".
///
/// Geprüft wird die ZUGANGSSCHICHT, nicht das Speichern (das deckt
/// AnalysisRepositoryTests ab): Wer nichts sehen darf, bekommt nichts — und
/// zwar ununterscheidbar von „gibt es nicht", denn ein abweichender Statuscode
/// verrät bereits, dass ein Item existiert.
/// </summary>
public sealed class AnalysisControllerTests
{
    private static readonly Guid ItemId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static AnalysisController CreateController(
        ILibraryManager libraryManager,
        IAnalysisRepository? repository = null,
        Guid? userId = null,
        bool isAdministrator = false,
        AnalysisWriteCoordinator? writeCoordinator = null,
        JourneyTrackStore? journeyTracks = null)
    {
        var controller = new AnalysisController(
            libraryManager,
            repository ?? Substitute.For<IAnalysisRepository>(),
            new AnalysisDocumentValidator(),
            new MediaFingerprintService(),
            new AnalysisRepresentationService(),
            writeCoordinator ?? new AnalysisWriteCoordinator(),
            new AnalysisOperationalTelemetry(),
            new AnalysisJobDispatcher(runner: null!, NullLogger<AnalysisJobDispatcher>.Instance),
            // Kein Kontext-Factory nötig: keiner dieser Tests fasst das
            // Sprachpaket an, und ein Zugriff darauf würde sofort auffallen.
            new VoiceRecordingRepository(contextFactory: null!),
            // Ein Verzeichnis, das es nicht gibt: keiner dieser Tests fasst die
            // Reise-Tonspur an, und ein Zugriff darauf fiele sofort auf.
            journeyTracks ?? new JourneyTrackStore(Path.Combine(Path.GetTempPath(), "aether-tests-none")),
            new ServerAnalysisActivity(),
            NullLogger<AnalysisController>.Instance);

        var claims = new List<Claim>();
        if (userId is not null)
        {
            claims.Add(new Claim("Jellyfin-UserId", userId.Value.ToString()));
        }

        if (isAdministrator)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Administrator"));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            },
        };
        return controller;
    }

    /// <summary>Eine Bibliothek, die für JEDE Anfrage nichts findet.</summary>
    private static ILibraryManager EmptyLibrary()
    {
        var library = Substitute.For<ILibraryManager>();
        library.GetItemById<BaseItem>(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((BaseItem?)null);
        return library;
    }

    /// <summary>Eine Bibliothek, in der genau ein sichtbares Video liegt.</summary>
    private static ILibraryManager LibraryWithVisibleItem()
    {
        var item = Substitute.For<BaseItem>();
        item.GetMediaSources(false).Returns(new List<MediaSourceInfo>
        {
            new() { Id = "quelle-1", Path = "/media/film.mkv", RunTimeTicks = 600_000_000 },
        });
        var library = Substitute.For<ILibraryManager>();
        library.GetItemById<BaseItem>(ItemId, UserId).Returns(item);
        return library;
    }

    private static IAnalysisRepository RepositoryWith(AnalysisRecordMetadata metadata)
    {
        var repository = Substitute.For<IAnalysisRepository>();
        repository
            .GetMetadataAsync(Arg.Any<IReadOnlyCollection<AnalysisKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AnalysisKey, AnalysisRecordMetadata> { [metadata.Key] = metadata });
        return repository;
    }

    private static int StatusOf(ActionResult result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? 0,
        StatusCodeResult statusCode => statusCode.StatusCode,
        _ => 0,
    };

    private static object? Property(object value, string name) =>
        value.GetType().GetProperty(name)?.GetValue(value);

    private static object FirstBatchItem(ActionResult result)
    {
        var body = Assert.IsType<OkObjectResult>(result).Value!;
        var items = Assert.IsAssignableFrom<IEnumerable<object>>(Property(body, "items"));
        return Assert.Single(items);
    }

    private static AnalysisRecordMetadata Metadata(string version, string fingerprint) => new(
        new AnalysisKey(ItemId, "quelle-1", "aether-visual", version), fingerprint,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 12, 128, $"\"etag-{version}\"");

    private static BatchSelection OneItem(bool allowCompatible = false, string requestedVersion = "1.1.0") => new(
        new AlgorithmSelection("aether-visual", requestedVersion),
        [new ItemSelection(ItemId, "quelle-1")], allowCompatible);

    private static AnalysisRecord RecordFor(MediaFingerprint media, string version)
    {
        var upload = JsonNode.Parse(
            """
            {
              "schemaVersion": 2,
              "createdAt": "2026-01-01T00:00:00Z",
              "durationMs": 60000,
              "sampling": { "intervalMs": 500, "frameWidth": 480, "frameHeight": 270, "colorSpace": "srgb" },
              "producer": { "name": "aether", "version": "1.0.0", "platform": "browser" },
              "mediaFingerprintAtStart": "placeholder",
              "frames": [
                { "timestampMs": 0, "luminance": 0.1, "contrast": 0.2, "saturation": 0.3, "motionEnergy": 0.4, "sceneCutProbability": 0.1, "palette": [] }
              ]
            }
            """)!.AsObject();
        upload["mediaFingerprintAtStart"] = media.Fingerprint;
        var document = new AnalysisRepresentationService().BuildMaster(
            upload, media, "aether-visual", version, DateTimeOffset.UnixEpoch);
        var compressed = CompressionCodec.Compress(document);
        return new AnalysisRecord
        {
            ItemId = media.ItemId,
            MediaSourceId = media.MediaSourceId,
            AlgorithmId = "aether-visual",
            AlgorithmVersion = version,
            MediaFingerprint = media.Fingerprint,
            FingerprintQuality = media.FingerprintQuality,
            Etag = "\"legacy-master\"",
            CompressedDocument = compressed,
            UncompressedBytes = document.Length,
            FrameCount = 1,
            SourceIntervalMs = 500,
            CreatedAt = DateTimeOffset.UnixEpoch,
            StoredAt = DateTimeOffset.UnixEpoch,
            LastAccessedAt = DateTimeOffset.UnixEpoch
        };
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/javascript")]
    public async Task RefusesNonAudioContentForStoredMedia(string contentType)
    {
        var controller = CreateController(EmptyLibrary(), isAdministrator: true);
        controller.Request.ContentType = contentType;
        controller.Request.Body = new MemoryStream([1, 2, 3]);

        var voice = await controller.PutVoiceRecording("fen-3", CancellationToken.None);
        var journey = await controller.PutJourneyTrack(CancellationToken.None);

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, StatusOf(voice));
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, StatusOf(journey));
    }

    [Fact]
    public async Task SerializesJourneyWritesWithTheSharedWriteCoordinator()
    {
        var coordinator = new AnalysisWriteCoordinator();
        using var heldLease = await coordinator.AcquireAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var controller = CreateController(
            EmptyLibrary(),
            isAdministrator: true,
            writeCoordinator: coordinator);
        controller.Request.ContentType = "audio/webm; codecs=opus";
        controller.Request.Body = new MemoryStream([1, 2, 3]);

        var upload = controller.PutJourneyTrack(cancellation.Token);
        Assert.False(upload.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
    }

    [Fact]
    public async Task AnswersNotFoundForAnItemTheUserCannotSee()
    {
        var controller = CreateController(EmptyLibrary(), userId: UserId);
        var result = await controller.GetAnalysis(ItemId, "quelle-1", "aether-visual", "1.1.0");

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Fact]
    public async Task LeaksNothingThroughDifferingStatusCodes()
    {
        // Der Kern der Zusage: „darfst du nicht sehen" und „gibt es nicht"
        // müssen dieselbe Antwort ergeben. Ein 403 gegen ein 404 verriete
        // bereits, dass ein Item existiert.
        var controller = CreateController(EmptyLibrary(), userId: UserId);

        var unknown = await controller.GetAnalysis(
            Guid.NewGuid(), "quelle-1", "aether-visual", "1.1.0");
        var forbidden = await controller.GetAnalysis(
            ItemId, "quelle-1", "aether-visual", "1.1.0");

        Assert.Equal(StatusOf(unknown), StatusOf(forbidden));
    }

    [Fact]
    public async Task AnswersNotFoundWhenNoUserClaimIsPresent()
    {
        // Ohne Nutzer-Anspruch gibt es keinen Zugriff — und auch keinen
        // Serverfehler: der Pfad darf nicht in eine Ausnahme laufen.
        var controller = CreateController(EmptyLibrary());
        var result = await controller.GetAnalysis(ItemId, "quelle-1", "aether-visual", "1.1.0");

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Theory]
    // Ungültige Routen-Identität bzw. Detailstufe: muss VOR jedem Datenzugriff
    // abgewiesen werden, sonst wandern ungeprüfte Zeichenketten in den
    // Speicherschlüssel.
    [InlineData("", "aether-visual", "1.1.0", "balanced")]
    [InlineData("quelle-1", "", "1.1.0", "balanced")]
    [InlineData("quelle-1", "aether-visual", "", "balanced")]
    [InlineData("quelle-1", "aether-visual", "1.1.0", "ultra")]
    [InlineData("quelle-1", "AETHER-VISUAL", "1.1.0", "balanced")]
    public async Task RejectsAnInvalidRouteIdentity(
        string mediaSourceId, string algorithmId, string algorithmVersion, string detail)
    {
        var controller = CreateController(EmptyLibrary(), userId: UserId);
        var result = await controller.GetAnalysis(
            ItemId, mediaSourceId, algorithmId, algorithmVersion, detail);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public void RefusesServerAnalysisWithoutUploadPermission()
    {
        // Der teuerste Endpunkt des Plugins: er löst minuten- bis stundenlange
        // ffmpeg-Läufe aus. Ein Nutzer ohne Upload-Recht wird abgewiesen, BEVOR
        // irgendetwas geprüft oder gestartet wird — das ist die zweite
        // Sicherheitszusage des README ("Schreibzugriffe nur für Berechtigte").
        var controller = CreateController(EmptyLibrary(), userId: UserId);
        var result = controller.RequestServerAnalysis(ItemId, "quelle-1");

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
    }

    [Fact]
    public void ReportsCapabilitiesWithoutAnItem()
    {
        // Der einzige Endpunkt, der ohne Item auskommt — er nennt Versionen,
        // Grenzen und die Rechte des fragenden Nutzers.
        var controller = CreateController(EmptyLibrary(), userId: UserId);
        var result = controller.GetCapabilities();

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
    }

    [Fact]
    public void RefusesThePreflightFromAnUnknownOrigin()
    {
        // Der einzige unauthentifizierte Pfad des Plugins. Er liefert nie
        // Daten, aber er darf auch keine CORS-Freigabe an eine beliebige
        // Herkunft erteilen — sonst dürfte jede fremde Seite im Browser des
        // angemeldeten Nutzers mit dem Plugin sprechen.
        var controller = CreateController(EmptyLibrary());
        controller.HttpContext.Request.Headers.Origin = "https://fremde-seite.example";

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(controller.Options()));
    }

    [Fact]
    public void RefusesThePreflightWithoutAnyOrigin()
    {
        var controller = CreateController(EmptyLibrary());
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(controller.Options()));
    }

    [Fact]
    public async Task RejectsABatchQueryWithoutABody()
    {
        var controller = CreateController(EmptyLibrary(), userId: UserId);
        var result = await controller.QueryAnalyses(selection: null, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public async Task RejectsNullOrMalformedBatchItemsInsteadOfThrowing()
    {
        var controller = CreateController(EmptyLibrary(), userId: UserId);
        var algorithm = new AlgorithmSelection("aether-visual", "1.1.0");
        var nullItem = new BatchSelection(algorithm, [null!]);
        var invalidIdentity = new BatchSelection(
            algorithm,
            [new ItemSelection(UserId, new string('x', 129))]);

        var nullResult = await controller.QueryAnalyses(nullItem, CancellationToken.None);
        var invalidResult = await controller.QueryAnalyses(invalidIdentity, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(nullResult));
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(invalidResult));
    }

    // --- Preconditions (der letzte offene Teil des P0-Punkts) ----------------

    [Fact]
    public async Task AnswersNotModifiedWhenTheClientAlreadyHasTheCurrentVersion()
    {
        // Ohne diesen Pfad lädt jeder Client bei jedem Start die vollständige
        // Analyse neu — bei einem langen Film sind das Megabytes je Abruf.
        var library = LibraryWithVisibleItem();
        var key = new AnalysisKey(ItemId, "quelle-1", "aether-visual", "1.1.0");
        var fingerprint = new MediaFingerprintService()
            .Create(library.GetItemById<BaseItem>(ItemId, UserId)!, "quelle-1")!;
        var metadata = new AnalysisRecordMetadata(
            key, fingerprint.Fingerprint, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            FrameCount: 1, StoredBytes: 128, Etag: "\"sha256-abc\"");

        var controller = CreateController(library, RepositoryWith(metadata), userId: UserId);
        // Erst holen, um das ETag zu erfahren, das der Server für diese
        // Detailstufe bildet — es ist NICHT identisch mit dem gespeicherten.
        await controller.GetAnalysis(ItemId, "quelle-1", "aether-visual", "1.1.0");
        var serverEtag = controller.Response.Headers.ETag.ToString();
        Assert.NotEmpty(serverEtag);

        controller.HttpContext.Request.Headers.IfNoneMatch = serverEtag;
        var result = await controller.GetAnalysis(ItemId, "quelle-1", "aether-visual", "1.1.0");

        Assert.Equal(StatusCodes.Status304NotModified, StatusOf(result));
    }

    [Fact]
    public async Task IgnoresAnEtagFromADifferentDetailLevel()
    {
        // Die Detailstufen liefern verschiedene Dokumente. Würde ein ETag über
        // Stufen hinweg gelten, bekäme ein Client ein 304 auf etwas, das er
        // nie geladen hat — und behielte dauerhaft die falsche Auflösung.
        var library = LibraryWithVisibleItem();
        var key = new AnalysisKey(ItemId, "quelle-1", "aether-visual", "1.1.0");
        var fingerprint = new MediaFingerprintService()
            .Create(library.GetItemById<BaseItem>(ItemId, UserId)!, "quelle-1")!;
        var metadata = new AnalysisRecordMetadata(
            key, fingerprint.Fingerprint, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            FrameCount: 1, StoredBytes: 128, Etag: "\"sha256-abc\"");

        var controller = CreateController(library, RepositoryWith(metadata), userId: UserId);
        await controller.GetAnalysis(ItemId, "quelle-1", "aether-visual", "1.1.0", "compact");
        var compactEtag = controller.Response.Headers.ETag.ToString();

        controller.HttpContext.Request.Headers.IfNoneMatch = compactEtag;
        var result = await controller.GetAnalysis(ItemId, "quelle-1", "aether-visual", "1.1.0", "full");

        Assert.NotEqual(StatusCodes.Status304NotModified, StatusOf(result));
    }

    [Fact]
    public async Task AnswersNotFoundWhenTheStoredAnalysisBelongsToAnotherFile()
    {
        // Der Fingerabdruck bindet eine Analyse an die konkrete Datei. Wurde
        // die Datei ersetzt, darf die alte Analyse NICHT mehr ausgeliefert
        // werden — sonst beschreibt sie einen Film, der nicht mehr da ist.
        var library = LibraryWithVisibleItem();
        var key = new AnalysisKey(ItemId, "quelle-1", "aether-visual", "1.1.0");
        var metadata = new AnalysisRecordMetadata(
            key, "sha256:ein-ganz-anderer-fingerabdruck", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, FrameCount: 1, StoredBytes: 128, Etag: "\"sha256-abc\"");

        var controller = CreateController(library, RepositoryWith(metadata), userId: UserId);
        var result = await controller.GetAnalysis(ItemId, "quelle-1", "aether-visual", "1.1.0");

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Fact]
    public async Task BatchQueryUsesOnlyTheExactVersionByDefault()
    {
        var library = LibraryWithVisibleItem();
        var fingerprint = new MediaFingerprintService()
            .Create(library.GetItemById<BaseItem>(ItemId, UserId)!, "quelle-1")!.Fingerprint;
        var repository = RepositoryWith(Metadata("1.0.0", fingerprint));
        var controller = CreateController(library, repository, userId: UserId);

        var result = await controller.QueryAnalyses(OneItem(), CancellationToken.None);

        Assert.Equal("missing", Property(FirstBatchItem(result), "status"));
    }

    [Fact]
    public async Task BatchQueryReadsCompatibleVersionOnlyWhenRequestedAndReportsItsIdentity()
    {
        var library = LibraryWithVisibleItem();
        var fingerprint = new MediaFingerprintService()
            .Create(library.GetItemById<BaseItem>(ItemId, UserId)!, "quelle-1")!.Fingerprint;
        var repository = RepositoryWith(Metadata("1.0.0", fingerprint));
        var controller = CreateController(library, repository, userId: UserId);

        var result = await controller.QueryAnalyses(OneItem(allowCompatible: true), CancellationToken.None);
        var item = FirstBatchItem(result);

        Assert.Equal("available", Property(item, "status"));
        var algorithm = Property(item, "algorithm")!;
        Assert.Equal("aether-visual", Property(algorithm, "id"));
        Assert.Equal("1.0.0", Property(algorithm, "version"));
    }

    [Fact]
    public async Task OlderReaderCanOptInToReadingNewerStoredVersion()
    {
        var library = LibraryWithVisibleItem();
        var fingerprint = new MediaFingerprintService()
            .Create(library.GetItemById<BaseItem>(ItemId, UserId)!, "quelle-1")!.Fingerprint;
        var repository = RepositoryWith(Metadata("1.1.0", fingerprint));
        var controller = CreateController(library, repository, userId: UserId);

        var exact = await controller.QueryAnalyses(OneItem(requestedVersion: "1.0.0"), CancellationToken.None);
        var compatible = await controller.QueryAnalyses(
            OneItem(allowCompatible: true, requestedVersion: "1.0.0"), CancellationToken.None);

        Assert.Equal("missing", Property(FirstBatchItem(exact), "status"));
        Assert.Equal("1.1.0", Property(Property(FirstBatchItem(compatible), "algorithm")!, "version"));
    }

    [Fact]
    public async Task BatchQueryPrefersCurrentVersionWhenBothFingerprintsMatch()
    {
        var library = LibraryWithVisibleItem();
        var fingerprint = new MediaFingerprintService()
            .Create(library.GetItemById<BaseItem>(ItemId, UserId)!, "quelle-1")!.Fingerprint;
        var current = Metadata("1.1.0", fingerprint);
        var legacy = Metadata("1.0.0", fingerprint);
        var repository = Substitute.For<IAnalysisRepository>();
        repository.GetMetadataAsync(Arg.Any<IReadOnlyCollection<AnalysisKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AnalysisKey, AnalysisRecordMetadata> { [legacy.Key] = legacy, [current.Key] = current });
        var controller = CreateController(library, repository, userId: UserId);

        var result = await controller.QueryAnalyses(OneItem(allowCompatible: true), CancellationToken.None);

        Assert.Equal("1.1.0", Property(Property(FirstBatchItem(result), "algorithm")!, "version"));
    }

    [Fact]
    public async Task BatchQueryUsesMatchingLegacyWhenCurrentIsStale()
    {
        var library = LibraryWithVisibleItem();
        var fingerprint = new MediaFingerprintService()
            .Create(library.GetItemById<BaseItem>(ItemId, UserId)!, "quelle-1")!.Fingerprint;
        var current = Metadata("1.1.0", "sha256:stale");
        var legacy = Metadata("1.0.0", fingerprint);
        var repository = Substitute.For<IAnalysisRepository>();
        repository.GetMetadataAsync(Arg.Any<IReadOnlyCollection<AnalysisKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AnalysisKey, AnalysisRecordMetadata> { [legacy.Key] = legacy, [current.Key] = current });
        var controller = CreateController(library, repository, userId: UserId);

        var result = await controller.QueryAnalyses(OneItem(allowCompatible: true), CancellationToken.None);
        var item = FirstBatchItem(result);

        Assert.Equal("available", Property(item, "status"));
        Assert.Equal("1.0.0", Property(Property(item, "algorithm")!, "version"));
    }

    [Fact]
    public async Task BatchQueryDoesNotReadAnUnadvertisedVersion()
    {
        var library = LibraryWithVisibleItem();
        var fingerprint = new MediaFingerprintService()
            .Create(library.GetItemById<BaseItem>(ItemId, UserId)!, "quelle-1")!.Fingerprint;
        var repository = RepositoryWith(Metadata("0.9.0", fingerprint));
        var controller = CreateController(library, repository, userId: UserId);

        var result = await controller.QueryAnalyses(OneItem(allowCompatible: true), CancellationToken.None);

        Assert.Equal("missing", Property(FirstBatchItem(result), "status"));
    }

    [Fact]
    public async Task BatchQueryReportsStaleWhenCandidatesExistButFingerprintsDoNotMatch()
    {
        var library = LibraryWithVisibleItem();
        var repository = RepositoryWith(Metadata("1.0.0", "sha256:old"));
        var controller = CreateController(library, repository, userId: UserId);

        var result = await controller.QueryAnalyses(OneItem(allowCompatible: true), CancellationToken.None);
        var item = FirstBatchItem(result);

        Assert.Equal("stale", Property(item, "status"));
        Assert.Equal("media-changed", Property(item, "reason"));
    }

    [Fact]
    public async Task BatchQueryConcealsInaccessibleItemsAsMissing()
    {
        var repository = Substitute.For<IAnalysisRepository>();
        var controller = CreateController(EmptyLibrary(), repository, userId: UserId);

        var result = await controller.QueryAnalyses(OneItem(allowCompatible: true), CancellationToken.None);

        Assert.Equal("missing", Property(FirstBatchItem(result), "status"));
        await repository.DidNotReceive().GetMetadataAsync(
            Arg.Any<IReadOnlyCollection<AnalysisKey>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactHeadCanReadLegacyWhileCurrentGetRemainsMissing()
    {
        var library = LibraryWithVisibleItem();
        var fingerprint = new MediaFingerprintService()
            .Create(library.GetItemById<BaseItem>(ItemId, UserId)!, "quelle-1")!.Fingerprint;
        var repository = RepositoryWith(Metadata("1.0.0", fingerprint));
        var media = new MediaFingerprintService()
            .Create(library.GetItemById<BaseItem>(ItemId, UserId)!, "quelle-1")!;
        repository.GetAsync(Arg.Any<AnalysisKey>(), Arg.Any<CancellationToken>())
            .Returns(RecordFor(media, "1.0.0"));
        var controller = CreateController(library, repository, userId: UserId);

        var legacyGet = await controller.GetAnalysis(ItemId, "quelle-1", "aether-visual", "1.0.0");
        var legacyHead = await controller.HeadAnalysis(ItemId, "quelle-1", "aether-visual", "1.0.0");
        var currentGet = await controller.GetAnalysis(ItemId, "quelle-1", "aether-visual", "1.1.0");

        Assert.IsType<FileContentResult>(legacyGet);
        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(legacyHead));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(currentGet));
    }

    [Fact]
    public void CapabilitiesExposeCurrentAndCompatibleReadVersionsSeparately()
    {
        var controller = CreateController(EmptyLibrary(), userId: UserId);
        var body = Assert.IsType<OkObjectResult>(controller.GetCapabilities()).Value!;
        var algorithms = Assert.IsAssignableFrom<IEnumerable<object>>(Property(body, "supportedAlgorithms"));
        var algorithm = Assert.Single(algorithms);

        Assert.Equal("1.2.0", Property(algorithm, "preferredVersion"));
        Assert.Contains("1.2.0", Assert.IsAssignableFrom<IEnumerable<string>>(Property(algorithm, "versions")));
        Assert.Equal(new[] { "1.1.0", "1.0.0" }, Assert.IsAssignableFrom<IEnumerable<string>>(Property(algorithm, "compatibleReadVersions")));
        Assert.NotEmpty(Assert.IsAssignableFrom<IEnumerable<object>>(Property(algorithm, "readCompatibility")));
        Assert.DoesNotContain(AetherAlgorithm.ReadCompatibility.Where(row => row.ReaderVersion != "1.2.0"),
            row => row.AnalysisVersions.Contains("1.2.0"));
    }

    [Theory]
    [InlineData("full", 100)]
    [InlineData("balanced", 40)]
    [InlineData("compact", 20)]
    public async Task Stable12HttpRepresentationKeepsMeasuredGroupsAndMatchesHeadEtag(string detail, int expectedPoints)
    {
        var library = LibraryWithVisibleItem();
        var item = library.GetItemById<BaseItem>(ItemId, UserId)!;
        item.Id = ItemId;
        item.GetMediaSources(false)[0].RunTimeTicks = TimeSpan.FromSeconds(2).Ticks;
        var media = new MediaFingerprintService().Create(item, "quelle-1")!;
        JsonObject Component(string mode)
        {
            var node = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "draft", mode + "-full-v2.json")))!.AsObject();
            node["snapshot"]!["itemId"] = ItemId.ToString();
            node["snapshot"]!["mediaSourceId"] = media.MediaSourceId;
            node["snapshot"]!["sourceFingerprint"] = media.Fingerprint;
            if (node["video"] is JsonObject video) video["mediaFingerprintAtStart"] = media.Fingerprint;
            foreach (var group in node["signalProvenance"]!.AsObject()) group.Value!["sourceFingerprint"] = media.Fingerprint;
            if (node["video"] is JsonObject videoComponent) videoComponent["signalProvenance"] = node["signalProvenance"]!.DeepClone();
            return node;
        }

        var audio = Component("audio");
        var video = Component("video");
        var context = new DraftArtifactContext(ItemId, media.MediaSourceId, media.Fingerprint, media.DurationMs,
            1, null, audio["producerRevision"]!.GetValue<string>(), TargetVersion: "1.2.0");
        var master = DraftAnalysisMasterBuilder.Build(JsonSerializer.SerializeToUtf8Bytes(audio),
            JsonSerializer.SerializeToUtf8Bytes(video), context, media, DateTimeOffset.UtcNow);
        var record = RecordFor(media, "1.2.0");
        record.CompressedDocument = CompressionCodec.Compress(master);
        record.UncompressedBytes = master.Length;
        record.Etag = AnalysisRepresentationService.CreateEtag(master);
        var key = new AnalysisKey(ItemId, media.MediaSourceId, "aether-visual", "1.2.0");
        var repository = RepositoryWith(Metadata("1.2.0", media.Fingerprint) with { Etag = record.Etag });
        repository.GetAsync(key, Arg.Any<CancellationToken>()).Returns(record);
        var controller = CreateController(library, repository, userId: UserId);

        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await controller.HeadAnalysis(ItemId, media.MediaSourceId,
            "aether-visual", "1.2.0", detail)));
        var headEtag = controller.Response.Headers.ETag.ToString();
        var get = Assert.IsType<FileContentResult>(await controller.GetAnalysis(ItemId, media.MediaSourceId,
            "aether-visual", "1.2.0", detail));
        using var output = JsonDocument.Parse(get.FileContents);
        var root = output.RootElement;
        Assert.Equal("1.2.0", root.GetProperty("algorithm").GetProperty("version").GetString());
        Assert.Equal(detail, root.GetProperty("representation").GetProperty("detail").GetString());
        Assert.Equal(expectedPoints, root.GetProperty("packedDenseAudioFrames").GetProperty("pointCount").GetInt32());
        Assert.Equal(4, root.GetProperty("audioFrames").GetArrayLength());
        Assert.True(root.TryGetProperty("cutEvents", out _));
        Assert.Equal(headEtag, controller.Response.Headers.ETag.ToString());
        Assert.Equal("1.2.0-draft", root.GetProperty("signalProvenance").GetProperty("denseAudio").GetProperty("algorithmVersion").GetString());
    }

    [Fact]
    public async Task Stable12CannotBeCreatedByRelabelingAnUnverifiedUpload()
    {
        var controller = CreateController(EmptyLibrary(), userId: UserId, isAdministrator: true);
        var result = await controller.PutAnalysis(ItemId, "quelle-1", "aether-visual", "1.2.0", default, default);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, StatusOf(result));
    }

    [Fact]
    public async Task CompatibleReaderGoldenFilesMatchTheRuntimeContract()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "contracts", "examples", "valid");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<BatchSelection>(
            File.ReadAllText(Path.Combine(root, "compatible-reader-query-v1.json")), options)!;
        var library = LibraryWithVisibleItem();
        var fingerprint = new MediaFingerprintService()
            .Create(library.GetItemById<BaseItem>(ItemId, UserId)!, "quelle-1")!.Fingerprint;
        var controller = CreateController(library, RepositoryWith(Metadata("1.1.0", fingerprint)), userId: UserId);

        var response = Assert.IsType<OkObjectResult>(await controller.QueryAnalyses(request, CancellationToken.None));
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "compatible-reader-status-v1.json")));
        var actual = JsonSerializer.SerializeToNode(response.Value, options);
        Assert.True(JsonNode.DeepEquals(expected, actual));

        var capabilities = Assert.IsType<OkObjectResult>(controller.GetCapabilities()).Value!;
        var algorithm = Assert.Single(Assert.IsAssignableFrom<IEnumerable<object>>(Property(capabilities, "supportedAlgorithms")));
        var expectedAlgorithm = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "algorithm-read-compatibility-1.2-v1.json")));
        Assert.True(JsonNode.DeepEquals(expectedAlgorithm, JsonSerializer.SerializeToNode(algorithm, options)));
    }

    [Fact]
    public async Task DeleteRejectsCompatibleSelectionEvenForAdministrator()
    {
        var controller = CreateController(EmptyLibrary(), userId: UserId, isAdministrator: true);

        var result = await controller.DeleteSelected(OneItem(allowCompatible: true), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }
}
