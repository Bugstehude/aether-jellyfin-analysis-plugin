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
    AnalysisJobDispatcher jobQueue,
    VoiceRecordingRepository voiceRecordings,
    JourneyTrackStore journeyTracks,
    ServerAnalysisActivity serverAnalysisActivity,
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
            supportedAlgorithms = new[]
            {
                new { id = AetherAlgorithm.Id, versions = new[] { AetherAlgorithm.Version } }
            },
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

    /// <summary>Requests an in-plugin server-side analysis run for one item (the AETHER "Server-Analyse" button).</summary>
    [HttpPost("items/{itemId:guid}/media-sources/{mediaSourceId}/analyze")]
    public ActionResult RequestServerAnalysis(Guid itemId, string mediaSourceId)
    {
        ApplyCorsHeaders();
        if (!CanUpload())
        {
            return ProblemResult(StatusCodes.Status403Forbidden, "forbidden", "Upload permission required.");
        }

        if (!IsValidIdentity(mediaSourceId, AetherAlgorithm.Id, AetherAlgorithm.Version))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-identity", "Route identity is invalid.");
        }

        if (!CurrentConfiguration.ServerAnalysisEnabled)
        {
            return ProblemResult(StatusCodes.Status409Conflict, "server-analysis-disabled", "Server-side analysis is disabled.");
        }

        if (GetAccessibleMedia(itemId, mediaSourceId) is null)
        {
            return NotFoundProblem();
        }

        var status = jobQueue.Enqueue(itemId);
        if (status is null)
        {
            Response.Headers.RetryAfter = "60";
            return ProblemResult(
                StatusCodes.Status429TooManyRequests,
                "analysis-queue-full",
                "Too many analyses are already queued; try again later.");
        }

        return Accepted(new { state = StateName(status.State), progress = status.Progress });
    }

    /// <summary>Gets the progress of a server-side analysis run for one item.</summary>
    [HttpGet("items/{itemId:guid}/media-sources/{mediaSourceId}/analyze/status")]
    public ActionResult GetServerAnalysisStatus(Guid itemId, string mediaSourceId)
    {
        ApplyCorsHeaders();
        if (!CanUpload())
        {
            return ProblemResult(StatusCodes.Status403Forbidden, "forbidden", "Upload permission required.");
        }

        if (!IsValidIdentity(mediaSourceId, AetherAlgorithm.Id, AetherAlgorithm.Version))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-identity", "Route identity is invalid.");
        }

        if (GetAccessibleMedia(itemId, mediaSourceId) is null)
        {
            return NotFoundProblem();
        }

        var status = jobQueue.GetStatus(itemId);
        return status is null
            ? Ok(new { state = "idle", progress = 0.0 })
            : Ok(new
            {
                state = StateName(status.State),
                progress = status.Progress,
                detail = status.Detail,
                updatedAt = status.UpdatedAt
            });
    }

    /// <summary>Gets status for an explicit bounded selection.</summary>
    [HttpPost("analyses/query")]
    public async Task<ActionResult> QueryAnalyses([FromBody] BatchSelection? selection, CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (IsOversizedBatch(selection))
        {
            return ProblemResult(StatusCodes.Status413PayloadTooLarge, "payload-too-large", "Batch selection exceeds limits.");
        }

        if (!IsValidBatch(selection))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-request", "Batch selection is invalid.");
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

        if (IsOversizedBatch(selection))
        {
            return ProblemResult(StatusCodes.Status413PayloadTooLarge, "payload-too-large", "Batch selection exceeds limits.");
        }

        if (!IsValidBatch(selection))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-request", "Batch selection is invalid.");
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

    /// <summary>Gets a live snapshot of what server-side analysis is doing (administrator only).</summary>
    [HttpGet("activity")]
    [Authorize(Roles = AdministratorRole)]
    public ActionResult GetActivity()
    {
        ApplyCorsHeaders();
        var snapshot = serverAnalysisActivity.Snapshot();
        return Ok(new
        {
            enabled = CurrentConfiguration.ServerAnalysisEnabled,
            autoAnalyzeOnScan = CurrentConfiguration.AutoAnalyzeOnScan,
            running = snapshot.Running,
            source = snapshot.Source,
            runStartedAt = snapshot.RunStartedAt,
            total = snapshot.TotalItems,
            checkedCount = snapshot.Checked,
            analyzed = snapshot.Analyzed,
            stored = snapshot.Stored,
            skipped = snapshot.Skipped,
            failed = snapshot.Failed,
            percent = snapshot.Percent,
            current = snapshot.Current is null
                ? null
                : new
                {
                    itemId = snapshot.Current.ItemId,
                    name = snapshot.Current.Name,
                    startedAt = snapshot.Current.StartedAt,
                    progress = snapshot.CurrentProgress
                },
            recent = snapshot.Recent.Select(entry => new
            {
                name = entry.Name,
                outcome = entry.Outcome,
                at = entry.At
            }),
            lastRun = snapshot.LastRun is null
                ? null
                : new
                {
                    source = snapshot.LastRun.Source,
                    finishedAt = snapshot.LastRun.FinishedAt,
                    cancelled = snapshot.LastRun.Cancelled,
                    analyzed = snapshot.LastRun.Analyzed,
                    stored = snapshot.LastRun.Stored,
                    skipped = snapshot.LastRun.Skipped,
                    failed = snapshot.LastRun.Failed,
                    elapsedSeconds = snapshot.LastRun.ElapsedSeconds
                },
            updatedAt = snapshot.UpdatedAt
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

    // --- Sprachpaket der „Sitzung" -----------------------------------------
    // Serverweit, nicht je Item: dieselben Sätze passen zu jedem Film. Der
    // Grund für die Ablage überhaupt ist die Quest — dort Dateien zuzuordnen
    // ist zäh, und die IndexedDB des Browsers ist je Gerät UND je Adresse
    // getrennt. Über den Server sind die Aufnahmen einfach da.

    /// <summary>Lists the server-wide spoken lines without their audio.</summary>
    [HttpGet("voice")]
    public async Task<ActionResult> ListVoiceRecordings(CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        var items = await voiceRecordings.ListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            lines = items.Select(item => new
            {
                lineId = item.LineId,
                contentType = item.ContentType,
                bytes = item.Bytes,
                updatedAt = item.UpdatedAt
            }),
            limits = new
            {
                maxRecordingBytes = VoiceRecordingRepository.MaxRecordingBytes,
                maxTotalBytes = VoiceRecordingRepository.MaxTotalBytes
            }
        });
    }

    /// <summary>Gets one spoken line's audio.</summary>
    [HttpGet("voice/{lineId}")]
    public async Task<ActionResult> GetVoiceRecording(string lineId, CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (!IsValidLineId(lineId))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-line-id", "Line id is invalid.");
        }

        var recording = await voiceRecordings.GetAsync(lineId.ToLowerInvariant(), cancellationToken)
            .ConfigureAwait(false);
        if (recording is null)
        {
            return NotFoundProblem();
        }

        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(recording.Content, recording.ContentType);
    }

    /// <summary>Stores or replaces one spoken line.</summary>
    [HttpPut("voice/{lineId}")]
    [RequestSizeLimit(VoiceRecordingRepository.MaxRecordingBytes)]
    public async Task<ActionResult> PutVoiceRecording(string lineId, CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (!CanUpload())
        {
            return ProblemResult(StatusCodes.Status403Forbidden, "forbidden", "Upload permission required.");
        }

        if (!IsValidLineId(lineId))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-line-id", "Line id is invalid.");
        }

        var contentType = NormalizeAudioContentType(Request.ContentType);
        if (contentType is null)
        {
            return ProblemResult(
                StatusCodes.Status415UnsupportedMediaType,
                "invalid-media-type",
                "The recording must declare an audio content type.");
        }

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var content = buffer.ToArray();
        if (content.Length == 0)
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "empty-body", "The recording is empty.");
        }

        if (content.Length > VoiceRecordingRepository.MaxRecordingBytes)
        {
            return ProblemResult(
                StatusCodes.Status413PayloadTooLarge, "payload-too-large", "The recording exceeds the per-line limit.");
        }

        var key = lineId.ToLowerInvariant();
        // Die Gesamtgrenze prüfen, aber OHNE die Zeile doppelt zu zählen, die
        // gerade ersetzt wird — sonst könnte man dieselbe Aufnahme irgendwann
        // nicht mehr überschreiben, obwohl sich am Gesamtumfang nichts ändert.
        using var writeLease = await writeCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var existing = await voiceRecordings.GetAsync(key, cancellationToken).ConfigureAwait(false);
        var total = await voiceRecordings.TotalBytesAsync(cancellationToken).ConfigureAwait(false);
        var projected = total - (existing?.ContentLength ?? 0) + content.Length;
        if (projected > VoiceRecordingRepository.MaxTotalBytes)
        {
            return ProblemResult(
                StatusCodes.Status507InsufficientStorage,
                "voice-storage-full",
                "The voice pack would exceed the configured total size.");
        }

        await voiceRecordings.UpsertAsync(key, contentType, content, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "AETHER voice line {LineId} stored ({Bytes} bytes).", key, content.Length);
        return NoContent();
    }

    // --- Reise-Tonspur -----------------------------------------------------
    // Eine EINZIGE lange Aufnahme, die ab Filmminute 1 mitläuft (ADR-016).
    // Eigener Endpunkt statt einer weiteren "Zeile": die Grenze von 8 MiB je
    // Zeile ist für gesprochene Sätze richtig und soll es bleiben — sie hier
    // anzuheben hätte jeder beliebigen Zeile erlaubt, den Vorrat zu sprengen.
    // Und die Ablage ist eine Datei neben der Datenbank, damit die Sicherung
    // der Plugin-Datenbank nicht fortan ein ganzes Musikstück mitträgt.

    /// <summary>Reports whether a journey track is stored, without loading it.</summary>
    [HttpGet("journey")]
    public ActionResult GetJourneyTrackInfo()
    {
        ApplyCorsHeaders();
        var info = journeyTracks.GetInfo();
        return Ok(new
        {
            present = info is not null,
            contentType = info?.ContentType,
            bytes = info?.Bytes,
            updatedAt = info?.UpdatedAt,
            limits = new { maxTrackBytes = JourneyTrackStore.MaxTrackBytes }
        });
    }

    /// <summary>Gets the journey track's audio.</summary>
    [HttpGet("journey/track")]
    public ActionResult GetJourneyTrack()
    {
        ApplyCorsHeaders();
        var info = journeyTracks.GetInfo();
        var stream = info is null ? null : journeyTracks.OpenRead();
        if (info is null || stream is null)
        {
            return NotFoundProblem();
        }

        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        // enableRangeProcessing: der Browser holt sich bei einer langen Datei
        // gern nur Ausschnitte. Ohne das lädt er bei jedem Sprung alles neu.
        return File(stream, info.ContentType, enableRangeProcessing: true);
    }

    /// <summary>Stores or replaces the journey track.</summary>
    [HttpPut("journey/track")]
    [RequestSizeLimit(JourneyTrackStore.MaxTrackBytes)]
    public async Task<ActionResult> PutJourneyTrack(CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (!CanUpload())
        {
            return ProblemResult(StatusCodes.Status403Forbidden, "forbidden", "Upload permission required.");
        }

        var contentType = NormalizeAudioContentType(Request.ContentType);
        if (contentType is null)
        {
            return ProblemResult(
                StatusCodes.Status415UnsupportedMediaType,
                "invalid-media-type",
                "The track must declare an audio content type.");
        }

        // Direkt in die Datei geschrieben, nicht erst in den Arbeitsspeicher:
        // bei 200 MiB kostet der Umweg das Doppelte davon an RAM, auf einem
        // Server, der nebenbei transkodiert.
        using var writeLease = await writeCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var written = await journeyTracks.WriteAsync(Request.Body, contentType, cancellationToken)
            .ConfigureAwait(false);
        if (written is null)
        {
            return ProblemResult(
                StatusCodes.Status413PayloadTooLarge, "payload-too-large", "The track exceeds the size limit.");
        }

        if (written == 0)
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "empty-body", "The track is empty.");
        }

        logger.LogInformation("AETHER journey track stored ({Bytes} bytes).", written);
        return NoContent();
    }

    /// <summary>Deletes the journey track; idempotent.</summary>
    [HttpDelete("journey/track")]
    public async Task<ActionResult> DeleteJourneyTrack(CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (!CanUpload())
        {
            return ProblemResult(StatusCodes.Status403Forbidden, "forbidden", "Upload permission required.");
        }

        using var writeLease = await writeCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        journeyTracks.Delete();
        return NoContent();
    }

    /// <summary>Deletes one spoken line; idempotent.</summary>
    [HttpDelete("voice/{lineId}")]
    public async Task<ActionResult> DeleteVoiceRecording(string lineId, CancellationToken cancellationToken)
    {
        ApplyCorsHeaders();
        if (!CanUpload())
        {
            return ProblemResult(StatusCodes.Status403Forbidden, "forbidden", "Upload permission required.");
        }

        if (!IsValidLineId(lineId))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-line-id", "Line id is invalid.");
        }

        using var writeLease = await writeCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await voiceRecordings.DeleteAsync(lineId.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Kennungen sind kleingeschriebene Wortmarken wie <c>fen-3</c>. Eng gefasst,
    /// weil sie als Schlüssel in die Datenbank gehen: kein Punkt, kein Schrägstrich,
    /// nichts, was einen Pfad ergeben könnte.
    /// </summary>
    private static bool IsValidLineId(string lineId)
    {
        if (lineId.Length is 0 or > 64)
        {
            return false;
        }

        foreach (var character in lineId)
        {
            var allowed = character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'
                || character == '-';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static string? NormalizeAudioContentType(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            // Legacy clients did not always send the header. Keep their established default.
            return "audio/mpeg";
        }

        var normalized = declared.Trim();
        if (normalized.Length > 64)
        {
            return null;
        }

        var separator = normalized.IndexOf(';');
        var mediaType = (separator < 0 ? normalized : normalized[..separator]).Trim();
        if (!mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Length == "audio/".Length
            || mediaType.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '/' or '.' or '+' or '-')))
        {
            return null;
        }

        return normalized;
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
        && selection.Items is { Count: > 0 }
        && selection.Items.Count <= EffectiveMaxBatchItems
        && selection.Items.All(item => item is not null
            && item.ItemId != Guid.Empty
            && IsValidIdentity(item.MediaSourceId, selection.Algorithm.Id, selection.Algorithm.Version));

    private static bool IsOversizedBatch(BatchSelection? selection) =>
        selection?.Items?.Count > EffectiveMaxBatchItems;

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

    private static string StateName(AnalysisJobState state) => state switch
    {
        AnalysisJobState.Queued => "queued",
        AnalysisJobState.Running => "running",
        AnalysisJobState.Completed => "completed",
        AnalysisJobState.Failed => "failed",
        _ => "idle"
    };

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
