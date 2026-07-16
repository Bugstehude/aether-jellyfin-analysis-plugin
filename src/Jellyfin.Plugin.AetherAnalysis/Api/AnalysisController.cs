using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Contracts;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.AetherAnalysis.Api;

/// <summary>Canonical AETHER analysis API.</summary>
[ApiController]
[Authorize]
[EnableCors(AetherCorsPolicy.Name)]
[Route("AetherAnalysis/v1")]
public sealed class AnalysisController(
    ILibraryManager libraryManager,
    IAnalysisRepository repository,
    AnalysisDocumentValidator validator,
    MediaFingerprintService fingerprintService,
    AnalysisRepresentationService representationService,
    AnalysisWriteCoordinator writeCoordinator) : ControllerBase
{
    private const string AdministratorRole = "Administrator";
    private const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>Gets supported versions, limits and current-user permissions.</summary>
    [HttpGet("capabilities")]
    public ActionResult GetCapabilities()
    {
        ApplyCorsHeaders();
        var canUpload = CanUpload();
        var isAdministrator = User.IsInRole(AdministratorRole);
        var configuration = CurrentConfiguration;
        return Ok(new
        {
            apiVersion = "1.0",
            pluginVersion = Plugin.Instance?.Version.ToString() ?? "0.1.0",
            supportedAnalysisSchemas = new[] { 2 },
            supportedAlgorithms = new[] { new { id = "aether-visual", versions = new[] { "1.0.0" } } },
            supportedDetailLevels = new[] { "compact", "balanced", "full" },
            limits = new
            {
                maxUploadBytes = configuration.MaxUploadBytes,
                maxFramesPerAnalysis = 86400,
                maxBatchItems = configuration.MaxBatchItems
            },
            defaults = new
            {
                samplingIntervalMs = 250,
                frameWidth = 480,
                compression = "br",
                detail = "balanced"
            },
            permissions = new
            {
                canUpload,
                canDelete = isAdministrator,
                canViewStorageDetails = isAdministrator
            }
        });
    }

    /// <summary>Gets the current fingerprint for one accessible media source.</summary>
    [HttpGet("items/{itemId:guid}/media-sources/{mediaSourceId}/fingerprint")]
    public ActionResult GetFingerprint(Guid itemId, string mediaSourceId)
    {
        ApplyCorsHeaders();
        var media = GetAccessibleMedia(itemId, mediaSourceId);
        return media is null ? NotFoundProblem() : Ok(media);
    }

    /// <summary>Gets one current analysis representation.</summary>
    [HttpGet("items/{itemId:guid}/media-sources/{mediaSourceId}/analyses/{algorithmId}/{algorithmVersion}")]
    public async Task<ActionResult> GetAnalysis(
        Guid itemId,
        string mediaSourceId,
        string algorithmId,
        string algorithmVersion,
        [FromQuery] string detail = "balanced",
        CancellationToken cancellationToken = default)
    {
        ApplyCorsHeaders();
        var media = GetAccessibleMedia(itemId, mediaSourceId);
        if (media is null)
        {
            return NotFoundProblem();
        }

        var key = new AnalysisKey(itemId, mediaSourceId, algorithmId, algorithmVersion);
        var record = await repository.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.MediaFingerprint, media.Fingerprint, StringComparison.Ordinal))
        {
            return NotFoundProblem();
        }

        var master = CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes);
        var representation = representationService.Create(master, detail);
        Response.Headers.ETag = representation.Etag;
        Response.Headers.CacheControl = "private, no-cache";
        if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, representation.Etag, StringComparison.Ordinal)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return File(representation.Json, "application/json");
    }

    /// <summary>Checks whether one current analysis representation exists.</summary>
    [HttpHead("items/{itemId:guid}/media-sources/{mediaSourceId}/analyses/{algorithmId}/{algorithmVersion}")]
    public async Task<ActionResult> HeadAnalysis(
        Guid itemId,
        string mediaSourceId,
        string algorithmId,
        string algorithmVersion,
        [FromQuery] string detail = "balanced",
        CancellationToken cancellationToken = default)
    {
        ApplyCorsHeaders();
        var media = GetAccessibleMedia(itemId, mediaSourceId);
        if (media is null)
        {
            return NotFound();
        }

        var record = await repository.GetAsync(
            new AnalysisKey(itemId, mediaSourceId, algorithmId, algorithmVersion),
            cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.MediaFingerprint, media.Fingerprint, StringComparison.Ordinal))
        {
            return NotFound();
        }

        var master = CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes);
        var representation = representationService.Create(master, detail);
        Response.Headers.ETag = representation.Etag;
        Response.Headers["X-Aether-Analysis-Created-At"] = record.CreatedAt.ToString("O");
        return NoContent();
    }

    /// <summary>Creates or atomically replaces one analysis.</summary>
    [HttpPut("items/{itemId:guid}/media-sources/{mediaSourceId}/analyses/{algorithmId}/{algorithmVersion}")]
    public async Task<ActionResult> PutAnalysis(
        Guid itemId,
        string mediaSourceId,
        string algorithmId,
        string algorithmVersion,
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (!CanUpload())
        {
            return ProblemResult(StatusCodes.Status403Forbidden, "forbidden", "Upload permission required.");
        }

        var mediaBefore = GetAccessibleMedia(itemId, mediaSourceId);
        if (mediaBefore is null)
        {
            return NotFoundProblem();
        }

        var validation = validator.Validate(body, CurrentConfiguration.MaxUploadBytes);
        if (!validation.IsValid)
        {
            var status = validation.Code == "payload-too-large"
                ? StatusCodes.Status413PayloadTooLarge
                : StatusCodes.Status422UnprocessableEntity;
            return ProblemResult(status, validation.Code!, validation.Error!);
        }

        if (!string.Equals(
                validation.Value!.MediaFingerprintAtStart,
                mediaBefore.Fingerprint,
                StringComparison.Ordinal))
        {
            return ProblemResult(StatusCodes.Status409Conflict, "fingerprint-mismatch", "Media changed before upload.");
        }

        var storedAt = DateTimeOffset.UtcNow;
        var master = representationService.BuildMaster(
            validation.Json!,
            mediaBefore,
            algorithmId,
            algorithmVersion,
            storedAt);
        var mediaAfter = GetAccessibleMedia(itemId, mediaSourceId);
        if (mediaAfter is null || !string.Equals(mediaBefore.Fingerprint, mediaAfter.Fingerprint, StringComparison.Ordinal))
        {
            return ProblemResult(StatusCodes.Status409Conflict, "fingerprint-mismatch", "Media changed during upload.");
        }

        var key = new AnalysisKey(itemId, mediaSourceId, algorithmId, algorithmVersion);
        using var writeLease = await writeCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var existing = await repository.GetAsync(key, cancellationToken).ConfigureAwait(false);
        var etag = AnalysisRepresentationService.CreateEtag(master);
        var compressed = CompressionCodec.Compress(master);
        var stats = await repository.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        var projectedBytes = stats.StoredBytes - (existing?.CompressedDocument.Length ?? 0) + compressed.Length;
        if (projectedBytes > CurrentConfiguration.MaxStoredBytes)
        {
            return ProblemResult(
                StatusCodes.Status507InsufficientStorage,
                "storage-limit-exceeded",
                "Analysis storage limit would be exceeded.");
        }

        var record = new AnalysisRecord
        {
            ItemId = itemId,
            MediaSourceId = mediaSourceId,
            AlgorithmId = algorithmId,
            AlgorithmVersion = algorithmVersion,
            MediaFingerprint = mediaAfter.Fingerprint,
            FingerprintQuality = mediaAfter.FingerprintQuality,
            Etag = etag,
            CompressedDocument = compressed,
            UncompressedBytes = master.Length,
            FrameCount = validation.Value.Frames.Count,
            SourceIntervalMs = validation.Value.Sampling.IntervalMs,
            CreatedAt = validation.Value.CreatedAt,
            StoredAt = storedAt,
            LastAccessedAt = storedAt
        };
        var expectedEtag = Request.Headers.IfMatch.FirstOrDefault();
        var saved = await repository.UpsertAsync(record, expectedEtag, cancellationToken).ConfigureAwait(false);
        if (!saved)
        {
            return ProblemResult(StatusCodes.Status412PreconditionFailed, "precondition-failed", "If-Match did not match.");
        }

        Response.Headers.ETag = etag;
        return existing is null ? StatusCode(StatusCodes.Status201Created) : NoContent();
    }

    /// <summary>Deletes one analysis idempotently.</summary>
    [HttpDelete("items/{itemId:guid}/media-sources/{mediaSourceId}/analyses/{algorithmId}/{algorithmVersion}")]
    public async Task<ActionResult> DeleteAnalysis(
        Guid itemId,
        string mediaSourceId,
        string algorithmId,
        string algorithmVersion,
        CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (!User.IsInRole(AdministratorRole))
        {
            return ProblemResult(StatusCodes.Status403Forbidden, "forbidden", "Administrator permission required.");
        }

        using var writeLease = await writeCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await repository.DeleteAsync(
            new AnalysisKey(itemId, mediaSourceId, algorithmId, algorithmVersion),
            cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Gets status for an explicit bounded selection.</summary>
    [HttpPost("analyses/query")]
    public async Task<ActionResult> QueryAnalyses([FromBody] BatchSelection selection, CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (!IsValidBatch(selection))
        {
            return ProblemResult(StatusCodes.Status413PayloadTooLarge, "payload-too-large", "Batch selection exceeds limits.");
        }

        var items = new List<object>(selection.Items.Count);
        foreach (var selected in selection.Items)
        {
            var media = GetAccessibleMedia(selected.ItemId, selected.MediaSourceId);
            if (media is null)
            {
                items.Add(new { selected.ItemId, selected.MediaSourceId, status = "missing" });
                continue;
            }

            var record = await repository.GetAsync(
                new AnalysisKey(selected.ItemId, selected.MediaSourceId, selection.Algorithm.Id, selection.Algorithm.Version),
                cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                items.Add(new { selected.ItemId, selected.MediaSourceId, status = "missing" });
            }
            else if (!string.Equals(record.MediaFingerprint, media.Fingerprint, StringComparison.Ordinal))
            {
                items.Add(new { selected.ItemId, selected.MediaSourceId, status = "stale", reason = "media-changed" });
            }
            else
            {
                items.Add(new
                {
                    selected.ItemId,
                    selected.MediaSourceId,
                    status = "available",
                    createdAt = record.CreatedAt,
                    frameCount = record.FrameCount,
                    storedBytes = record.CompressedDocument.Length,
                    etag = record.Etag
                });
            }
        }

        return Ok(new { items });
    }

    /// <summary>Deletes an explicit bounded selection.</summary>
    [HttpPost("analyses/delete")]
    public async Task<ActionResult> DeleteSelected([FromBody] BatchSelection selection, CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (!User.IsInRole(AdministratorRole))
        {
            return ProblemResult(StatusCodes.Status403Forbidden, "forbidden", "Administrator permission required.");
        }

        if (!IsValidBatch(selection))
        {
            return ProblemResult(StatusCodes.Status413PayloadTooLarge, "payload-too-large", "Batch selection exceeds limits.");
        }

        using var writeLease = await writeCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        foreach (var selected in selection.Items)
        {
            if (await repository.DeleteAsync(
                    new AnalysisKey(selected.ItemId, selected.MediaSourceId, selection.Algorithm.Id, selection.Algorithm.Version),
                    cancellationToken).ConfigureAwait(false))
            {
                deleted++;
            }
        }

        return Ok(new { requested = selection.Items.Count, deleted, notFound = selection.Items.Count - deleted });
    }

    /// <summary>Gets plugin storage status without filesystem paths.</summary>
    [HttpGet("status")]
    public async Task<ActionResult> GetStatus(CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        var stats = await repository.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            service = "ready",
            databaseSchemaVersion = 1,
            stats.RecordCount,
            stats.StoredBytes,
            maxStoredBytes = CurrentConfiguration.MaxStoredBytes,
            retentionDays = CurrentConfiguration.RetentionDays,
            stats.OldestRecordAt,
            lastCleanupAt = (DateTimeOffset?)null,
            cleanup = new { orphanedRecords = 0, staleRecords = 0 }
        });
    }

    /// <summary>Handles positive browser preflight requests.</summary>
    [AllowAnonymous]
    [HttpOptions("{**path}")]
    public ActionResult Options()
    {
        var origin = Request.Headers.Origin.FirstOrDefault();
        if (origin is null || !IsAllowedOrigin(origin))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        ApplyCorsHeaders();
        Response.Headers.AccessControlAllowMethods = "GET, HEAD, PUT, DELETE, POST, OPTIONS";
        Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type, If-Match, If-None-Match";
        Response.Headers.AccessControlMaxAge = "600";
        return NoContent();
    }

    private MediaFingerprint? GetAccessibleMedia(Guid itemId, string mediaSourceId)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return null;
        }

        var item = libraryManager.GetItemById<BaseItem>(itemId, userId);
        return item is null ? null : fingerprintService.Create(item, mediaSourceId);
    }

    private Guid GetUserId()
    {
        var value = User.FindFirstValue(UserIdClaim);
        return Guid.TryParse(value, out var userId) ? userId : Guid.Empty;
    }

    private bool CanUpload()
    {
        if (User.IsInRole(AdministratorRole))
        {
            return true;
        }

        var userId = GetUserId();
        return userId != Guid.Empty && CurrentConfiguration.AllowedAnalyzerUserIds.Any(
            value => Guid.TryParse(value, out var allowed) && allowed == userId);
    }

    private bool IsValidBatch(BatchSelection selection) =>
        selection.Algorithm is not null
        && !string.IsNullOrWhiteSpace(selection.Algorithm.Id)
        && !string.IsNullOrWhiteSpace(selection.Algorithm.Version)
        && selection.Items.Count is > 0
        && selection.Items.Count <= CurrentConfiguration.MaxBatchItems;

    private ActionResult NotFoundProblem() =>
        ProblemResult(StatusCodes.Status404NotFound, "media-source-not-found", "Resource not found.");

    private ObjectResult ProblemResult(int status, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = detail,
            Type = $"urn:aether:analysis:error:{code}"
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return StatusCode(status, problem);
    }

    private void ApplyCorsHeaders()
    {
        var origin = Request.Headers.Origin.FirstOrDefault();
        if (origin is null || !IsAllowedOrigin(origin))
        {
            return;
        }

        Response.Headers.AccessControlAllowOrigin = origin;
        Response.Headers.AccessControlExposeHeaders = "ETag, X-Aether-Analysis-Created-At, Retry-After";
        Response.Headers.Append(HeaderNames.Vary, "Origin");
    }

    private bool IsAllowedOrigin(string origin) => CurrentConfiguration.AllowedOrigins.Any(
        configured => string.Equals(configured.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    private static Configuration.PluginConfiguration CurrentConfiguration =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
}
