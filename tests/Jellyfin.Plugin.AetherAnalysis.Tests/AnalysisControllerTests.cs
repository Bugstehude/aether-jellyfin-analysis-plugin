using System.Security.Claims;
using Jellyfin.Plugin.AetherAnalysis.Api;
using Jellyfin.Plugin.AetherAnalysis.Application;
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
        // Antwortet mit 413 statt 400 — der Endpunkt fasst „Auswahl ungültig"
        // und „Auswahl zu groß" in einer Prüfung zusammen. Inhaltlich ist die
        // Ablehnung richtig; der Statuscode ist hier bewusst festgehalten,
        // damit eine spätere Änderung daran auffällt statt still den Vertrag
        // zu verschieben.
        var controller = CreateController(EmptyLibrary(), userId: UserId);
        var result = await controller.QueryAnalyses(selection: null, CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, StatusOf(result));
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
}
