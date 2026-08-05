using Jellyfin.Plugin.AetherAnalysis.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AetherAnalysis.Api;

/// <summary>
/// Admin-only API for generating Jellyfin chapter markers from already-stored scene-cut
/// analysis. Kept separate from <see cref="AnalysisController"/> (which is already large and
/// covers a different concern — storing/serving analysis documents) rather than folded in.
/// </summary>
[ApiController]
[Authorize(Roles = AdministratorRole)]
[Route("AetherAnalysis/v1/chapters")]
public sealed class ChapterController(ChapterGenerator generator) : ControllerBase
{
    private const string AdministratorRole = "Administrator";

    /// <summary>Starts a chapter-generation run in the background if one isn't already running; returns immediately.</summary>
    [HttpPost("generate")]
    public ActionResult Generate([FromBody] GenerateChaptersRequest request)
    {
        // Task.Run, not a bare fire-and-forget call — same reasoning as the sibling
        // duplicate-finder plugin's scan endpoint: GenerateAsync's semaphore acquisition and
        // initial item enumeration run synchronously on an uncontended gate, so a bare call
        // would otherwise block this request thread through the whole enumeration on a large
        // library.
        _ = Task.Run(() => generator.GenerateAsync(request.OverwriteExisting, CancellationToken.None));
        return Accepted();
    }

    /// <summary>Gets the current/last run's progress.</summary>
    [HttpGet("status")]
    public ActionResult GetStatus()
    {
        var status = generator.Status;
        // Explicit lowercase-keyed anonymous object, not the ChapterGenerationStatus POCO
        // directly: Jellyfin's server does not apply a camelCase naming policy, so a plain
        // Ok(status) would emit PascalCase keys that silently don't match the config page's
        // lowercase JS field reads — the same bug class fixed in the duplicate-finder plugin.
        return Ok(new
        {
            running = status.Running,
            checkedCount = status.Checked,
            total = status.Total,
            created = status.Created,
            skippedHadChapters = status.SkippedHadChapters,
            skippedNotAnalyzed = status.SkippedNotAnalyzed,
            lastFinishedAt = status.LastFinishedAt,
            lastError = status.LastError
        });
    }

    /// <summary>Request body for <see cref="Generate"/>.</summary>
    public sealed class GenerateChaptersRequest
    {
        /// <summary>Whether to replace chapters on items that already have some.</summary>
        public bool OverwriteExisting { get; set; }
    }
}
