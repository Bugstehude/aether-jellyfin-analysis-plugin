using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Contracts;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.AetherAnalysis.Api;

/// <summary>Canonical AETHER analysis API.</summary>
[ApiController]
[Authorize]
[Route("AetherAnalysis/v1")]
public sealed class AnalysisController(
    ILibraryManager libraryManager,
    IAnalysisRepository repository,
    AnalysisDocumentValidator validator,
    MediaFingerprintService fingerprintService,
    AnalysisRepresentationService representationService,
    AnalysisWriteCoordinator writeCoordinator,
    AnalysisOperationalTelemetry operationalTelemetry,
    ILogger<AnalysisController> logger) : ControllerBase
{
    private const int AbsoluteRequestSizeLimitBytes = 50 * 1024 * 1024;
    private const string AdministratorRole = "Administrator";
    private const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>Gets supported versions, limits and current-user permissions.</summary>
    [HttpGet("capabilities")]
    public ActionResult GetCapabilities()
    {
        ApplyCorsHeaders();
        var canUpload = CanUpload();
        var isAdministrator = User.IsInRole(AdministratorRole);
        return Ok(new
        {
            apiVersion = "1.0",
            pluginVersion = Plugin.Instance?.Version.ToString() ?? "0.1.0",
            supportedAnalysisSchemas = new[] { 2 },
            supportedAlgorithms = new[] { new { id = "aether-visual", versions = new[] { "1.0.0" } } },
            supportedDetailLevels = new[] { "compact", "balanced", "full" },
            limits = new
            {
                maxUploadBytes = EffectiveMaxUploadBytes,
                maxFramesPerAnalysis = 86400,
                maxBatchItems = EffectiveMaxBatchItems
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
        return media is null
            ? NotFoundProblem()
            : Ok(new
            {
                itemId = media.ItemId,
                mediaSourceId = media.MediaSourceId,
                fingerprint = media.Fingerprint,
                fingerprintQuality = media.FingerprintQuality,
                durationMs = media.DurationMs
            });
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
        if (!IsValidIdentity(mediaSourceId, algorithmId, algorithmVersion) || !IsValidDetail(detail))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-request", "Route identity or detail is invalid.");
        }

        var media = GetAccessibleMedia(itemId, mediaSourceId);
        if (media is null)
        {
            return NotFoundProblem();
        }

        var key = new AnalysisKey(itemId, mediaSourceId, algorithmId, algorithmVersion);
        var metadata = await GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (metadata is null || !string.Equals(metadata.MediaFingerprint, media.Fingerprint, StringComparison.Ordinal))
        {
            return NotFoundProblem();
        }

        var expectedEtag = AnalysisRepresentationService.CreateRepresentationEtag(metadata.Etag, detail);
        Response.Headers.ETag = expectedEtag;
        Response.Headers.CacheControl = "private, no-cache";
        if (HeaderMatches(Request.Headers.IfNoneMatch, expectedEtag))
        {
            await TouchIfDueAsync(key, metadata.LastAccessedAt, cancellationToken).ConfigureAwait(false);
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var record = await repository.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.MediaFingerprint, media.Fingerprint, StringComparison.Ordinal))
        {
            return NotFoundProblem();
        }

        AnalysisRepresentation representation;
        try
        {
            var master = CompressionCodec.Decompress(record.CompressedDocument, record.UncompressedBytes);
            representation = representationService.Create(master, detail, record.Etag);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            operationalTelemetry.RecordCorruptRead();
            logger.LogError(
                exception,
                "Stored AETHER analysis is corrupt for item {ItemId}, source {MediaSourceId}, algorithm {AlgorithmId}@{AlgorithmVersion}",
                itemId,
                mediaSourceId,
                algorithmId,
                algorithmVersion);
            return ProblemResult(
                StatusCodes.Status503ServiceUnavailable,
                "analysis-unavailable",
                "Stored analysis is temporarily unavailable.");
        }

        await TouchIfDueAsync(key, record.LastAccessedAt, cancellationToken).ConfigureAwait(false);
        Response.Headers.ETag = representation.Etag;
        if (HeaderMatches(Request.Headers.IfNoneMatch, representation.Etag))
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
        if (!IsValidIdentity(mediaSourceId, algorithmId, algorithmVersion) || !IsValidDetail(detail))
        {
            return BadRequest();
        }

        var media = GetAccessibleMedia(itemId, mediaSourceId);
        if (media is null)
        {
            return NotFound();
        }

        var key = new AnalysisKey(itemId, mediaSourceId, algorithmId, algorithmVersion);
        var record = await GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.MediaFingerprint, media.Fingerprint, StringComparison.Ordinal))
        {
            return NotFound();
        }

        var etag = AnalysisRepresentationService.CreateRepresentationEtag(record.Etag, detail);
        await TouchIfDueAsync(key, record.LastAccessedAt, cancellationToken).ConfigureAwait(false);
        Response.Headers.ETag = etag;
        Response.Headers["X-Aether-Analysis-Created-At"] = record.CreatedAt.ToString("O");
        if (HeaderMatches(Request.Headers.IfNoneMatch, etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return NoContent();
    }

    /// <summary>Creates or atomically replaces one analysis.</summary>
    [HttpPut("items/{itemId:guid}/media-sources/{mediaSourceId}/analyses/{algorithmId}/{algorithmVersion}")]
    [RequestSizeLimit(AbsoluteRequestSizeLimitBytes)]
    [ServiceFilter(typeof(AnalysisUploadResourceFilter))]
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

        if (!IsValidIdentity(mediaSourceId, algorithmId, algorithmVersion))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-identity", "Route identity is invalid.");
        }

        var mediaBefore = GetAccessibleMedia(itemId, mediaSourceId);
        if (mediaBefore is null)
        {
            return NotFoundProblem();
        }

        var validation = validator.Validate(body, EffectiveMaxUploadBytes);
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
        var ifMatchSupplied = Request.Headers.ContainsKey(HeaderNames.IfMatch);
        var etag = AnalysisRepresentationService.CreateEtag(master);
        var compressed = CompressionCodec.Compress(master);

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
        var now = DateTimeOffset.UtcNow;
        var retentionCutoff = EffectiveRetentionDays > 0
            ? now.AddDays(-EffectiveRetentionDays)
            : (DateTimeOffset?)null;
        var result = await repository.StoreBoundedAsync(
            new AnalysisStoreRequest(
                record,
                HeaderValues(Request.Headers.IfMatch).ToArray(),
                ifMatchSupplied,
                EffectiveMaxStoredBytes,
                retentionCutoff,
                now),
            cancellationToken).ConfigureAwait(false);
        if (result == AnalysisStoreResult.PreconditionFailed)
        {
            return ProblemResult(StatusCodes.Status412PreconditionFailed, "precondition-failed", "If-Match did not match.");
        }
        if (result == AnalysisStoreResult.StorageLimitExceeded)
        {
            return ProblemResult(
                StatusCodes.Status507InsufficientStorage,
                "storage-limit-exceeded",
                "Analysis storage limit would be exceeded.");
        }

        Response.Headers.ETag = etag;
        return result == AnalysisStoreResult.Created ? StatusCode(StatusCodes.Status201Created) : NoContent();
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

        if (!IsValidIdentity(mediaSourceId, algorithmId, algorithmVersion))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-identity", "Route identity is invalid.");
        }

        using var writeLease = await writeCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await repository.DeleteAsync(
            new AnalysisKey(itemId, mediaSourceId, algorithmId, algorithmVersion),
            cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Gets status for an explicit bounded selection.</summary>
    [HttpPost("analyses/query")]
    public async Task<ActionResult> QueryAnalyses([FromBody] BatchSelection? selection, CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (!IsValidBatch(selection))
        {
            return ProblemResult(StatusCodes.Status413PayloadTooLarge, "payload-too-large", "Batch selection exceeds limits.");
        }

        var lookups = selection.Items.Select(selected =>
        {
            var media = GetAccessibleMedia(selected.ItemId, selected.MediaSourceId);
            var key = new AnalysisKey(
                selected.ItemId,
                selected.MediaSourceId,
                selection.Algorithm.Id,
                selection.Algorithm.Version);
            return (Selected: selected, Media: media, Key: key);
        }).ToArray();
        var metadata = await repository.GetMetadataAsync(
            lookups.Where(value => value.Media is not null).Select(value => value.Key).ToArray(),
            cancellationToken).ConfigureAwait(false);
        var items = new List<object>(lookups.Length);
        foreach (var lookup in lookups)
        {
            var selected = lookup.Selected;
            if (lookup.Media is null)
            {
                items.Add(new
                {
                    itemId = selected.ItemId,
                    mediaSourceId = selected.MediaSourceId,
                    status = "missing"
                });
                continue;
            }

            if (!metadata.TryGetValue(lookup.Key, out var record))
            {
                items.Add(new
                {
                    itemId = selected.ItemId,
                    mediaSourceId = selected.MediaSourceId,
                    status = "missing"
                });
            }
            else if (!string.Equals(record.MediaFingerprint, lookup.Media.Fingerprint, StringComparison.Ordinal))
            {
                items.Add(new
                {
                    itemId = selected.ItemId,
                    mediaSourceId = selected.MediaSourceId,
                    status = "stale",
                    reason = "media-changed"
                });
            }
            else
            {
                items.Add(new
                {
                    itemId = selected.ItemId,
                    mediaSourceId = selected.MediaSourceId,
                    status = "available",
                    createdAt = record.CreatedAt,
                    frameCount = record.FrameCount,
                    storedBytes = record.StoredBytes,
                    etag = record.Etag
                });
            }
        }

        return Ok(new { items });
    }

    /// <summary>Deletes an explicit bounded selection.</summary>
    [HttpPost("analyses/delete")]
    public async Task<ActionResult> DeleteSelected([FromBody] BatchSelection? selection, CancellationToken cancellationToken)
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
    [Authorize(Roles = AdministratorRole)]
    public async Task<ActionResult> GetStatus(CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        var stats = await repository.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        var maintenance = await repository.GetMaintenanceSummaryAsync(cancellationToken).ConfigureAwait(false);
        var operational = operationalTelemetry.Snapshot();
        return Ok(new
        {
            service = operational.CorruptReadCount > 0 ? "degraded" : "ready",
            databaseSchemaVersion = 2,
            recordCount = stats.RecordCount,
            storedBytes = stats.StoredBytes,
            maxStoredBytes = EffectiveMaxStoredBytes,
            retentionDays = EffectiveRetentionDays,
            oldestRecordAt = stats.OldestRecordAt,
            lastCleanupAt = maintenance?.LastCompletedAt,
            cleanup = maintenance is null
                ? null
                : new
                {
                    reason = maintenance.LastReason,
                    retentionDeletedRecords = maintenance.LastRetentionDeletedRecords,
                    capacityDeletedRecords = maintenance.LastCapacityDeletedRecords,
                    deletedBytes = maintenance.LastDeletedBytes
                },
            operational = new
            {
                corruptReadCount = operational.CorruptReadCount,
                lastCorruptReadAt = operational.LastCorruptReadAt,
                touchFailureCount = operational.TouchFailureCount,
                lastTouchFailureAt = operational.LastTouchFailureAt
            }
        });
    }

    /// <summary>Runs retention and over-capacity cleanup immediately.</summary>
    [HttpPost("maintenance/cleanup")]
    public async Task<ActionResult> RunCleanup(CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (!User.IsInRole(AdministratorRole))
        {
            return ProblemResult(StatusCodes.Status403Forbidden, "forbidden", "Administrator permission required.");
        }

        var now = DateTimeOffset.UtcNow;
        var retentionCutoff = EffectiveRetentionDays > 0
            ? now.AddDays(-EffectiveRetentionDays)
            : (DateTimeOffset?)null;
        using var writeLease = await writeCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var result = await repository.CleanupAsync(
            new AnalysisCleanupRequest(
                retentionCutoff,
                EffectiveMaxStoredBytes,
                null,
                "manual",
                now),
            cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            retentionDeletedRecords = result.RetentionDeletedRecords,
            capacityDeletedRecords = result.CapacityDeletedRecords,
            deletedBytes = result.DeletedBytes,
            storedBytesAfter = result.StoredBytesAfter
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
        return userId != Guid.Empty && (CurrentConfiguration.AllowedAnalyzerUserIds ?? []).Any(
            value => Guid.TryParse(value, out var allowed) && allowed == userId);
    }

    private bool IsValidBatch([NotNullWhen(true)] BatchSelection? selection) =>
        selection is not null
        && selection.Algorithm is not null
        && !string.IsNullOrWhiteSpace(selection.Algorithm.Id)
        && !string.IsNullOrWhiteSpace(selection.Algorithm.Version)
        && selection.Items is { Count: > 0 }
        && selection.Items.Count <= EffectiveMaxBatchItems;

    private static bool IsValidIdentity(string mediaSourceId, string algorithmId, string algorithmVersion) =>
        !string.IsNullOrWhiteSpace(mediaSourceId)
        && mediaSourceId.Length <= 128
        && !string.IsNullOrWhiteSpace(algorithmId)
        && algorithmId.Length <= 64
        && (char.IsAsciiLetterLower(algorithmId[0]) || char.IsAsciiDigit(algorithmId[0]))
        && algorithmId.All(value => char.IsAsciiLetterLower(value) || char.IsAsciiDigit(value) || value is '.' or '_' or '-')
        && !string.IsNullOrWhiteSpace(algorithmVersion)
        && algorithmVersion.Length <= 32
        && char.IsAsciiLetterOrDigit(algorithmVersion[0])
        && algorithmVersion.All(value => char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-');

    private static bool IsValidDetail(string detail) => detail is "compact" or "balanced" or "full";

    private static IEnumerable<string> HeaderValues(IEnumerable<string?> values) => values
        .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Where(value => value.Length > 0);

    private static bool HeaderMatches(IEnumerable<string?> values, string etag) => HeaderValues(values)
        .Any(value => value == "*" || string.Equals(value, etag, StringComparison.Ordinal));

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

    private bool IsAllowedOrigin(string origin) => (CurrentConfiguration.AllowedOrigins ?? []).Any(
        configured => string.Equals(configured.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    private async Task TouchBestEffortAsync(AnalysisKey key, CancellationToken cancellationToken)
    {
        try
        {
            await repository.TouchAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            operationalTelemetry.RecordTouchFailure();
            logger.LogWarning(
                exception,
                "Could not update AETHER analysis access time for item {ItemId}, source {MediaSourceId}",
                key.ItemId,
                key.MediaSourceId);
        }
    }

    private Task TouchIfDueAsync(
        AnalysisKey key,
        DateTimeOffset lastAccessedAt,
        CancellationToken cancellationToken) => lastAccessedAt <= DateTimeOffset.UtcNow.AddHours(-1)
        ? TouchBestEffortAsync(key, cancellationToken)
        : Task.CompletedTask;

    private async Task<AnalysisRecordMetadata?> GetMetadataAsync(
        AnalysisKey key,
        CancellationToken cancellationToken)
    {
        var metadata = await repository.GetMetadataAsync([key], cancellationToken).ConfigureAwait(false);
        return metadata.GetValueOrDefault(key);
    }

    private static Configuration.PluginConfiguration CurrentConfiguration =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    private static int EffectiveMaxUploadBytes => Math.Clamp(
        CurrentConfiguration.MaxUploadBytes,
        1,
        AbsoluteRequestSizeLimitBytes);

    private static int EffectiveMaxBatchItems => Math.Clamp(
        CurrentConfiguration.MaxBatchItems,
        1,
        1000);

    private static int EffectiveRetentionDays => Math.Clamp(
        CurrentConfiguration.RetentionDays,
        0,
        36500);

    private static long EffectiveMaxStoredBytes => Math.Clamp(
        CurrentConfiguration.MaxStoredBytes,
        1024 * 1024,
        1024L * 1024 * 1024 * 1024);
}
