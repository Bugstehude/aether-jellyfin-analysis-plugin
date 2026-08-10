using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AetherAnalysis.Api;

/// <summary>
/// Synchronisiert benannte Presets des manuellen Regler-Panels über Geräte hinweg
/// (Desktop, Mac-App, Quest — alle sprechen denselben Plugin-Server an).
///
/// Eigener Controller statt ein Anhang an <see cref="AnalysisController"/>: Presets
/// hängen an nichts Medienbezogenem, sondern rein am angemeldeten Nutzer — die erste
/// je-Nutzer-Ablage dieses Plugins. Die Nutzerkennung kommt, wie in
/// <see cref="AnalysisController"/> bereits vorgemacht, aus dem
/// <c>Jellyfin-UserId</c>-Claim, den Jellyfins Auth-Middleware nach erfolgreicher
/// Anmeldung auf <see cref="ControllerBase.User"/> setzt.
/// </summary>
[ApiController]
[Authorize]
[Route("AetherAnalysis/v1/presets")]
public sealed class PresetsController(ManualPresetRepository presets, ILogger<PresetsController> logger)
    : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";
    private const int MaxNameLength = 200;
    private const int MaxSnapshotJsonBytes = 32 * 1024;

    /// <summary>Lists the calling user's presets.</summary>
    [HttpGet]
    public async Task<ActionResult> ListPresets(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var stored = await presets.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        return Ok(stored.Select(ToResponse));
    }

    /// <summary>Creates or replaces one preset, scoped to the calling user.</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult> PutPreset(string id, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (!IsValidPresetId(id))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-preset-id", "Preset id is invalid.");
        }

        if (!TryReadName(body, out var name) || !TryReadSnapshotJson(body, out var snapshotJson))
        {
            return ProblemResult(StatusCodes.Status422UnprocessableEntity, "invalid-preset", "Preset body is invalid.");
        }

        // Die Obergrenze nur beim NEUANLEGEN prüfen: ein bestehendes Preset zu
        // aktualisieren darf nicht daran scheitern, dass der Vorrat inzwischen
        // voll ist — sonst könnte man ein Preset irgendwann nicht mehr umbenennen.
        var existingCount = await presets.CountAsync(userId, cancellationToken).ConfigureAwait(false);
        if (existingCount >= ManualPresetRepository.MaxPresetsPerUser)
        {
            var current = await presets.ListAsync(userId, cancellationToken).ConfigureAwait(false);
            if (current.All(value => value.Id != id))
            {
                return ProblemResult(
                    StatusCodes.Status507InsufficientStorage,
                    "preset-storage-full",
                    "The preset count limit has been reached.");
            }
        }

        // Immer 204, wie beim Sprachpaket (siehe AnalysisController.PutVoiceRecording): anders
        // als bei Analysen gibt es hier kein knappes Speicherbudget, das eine Unterscheidung
        // zwischen Neuanlage und Ersetzen für den Aufrufer wichtig machen würde.
        await presets.UpsertAsync(userId, id, name, snapshotJson, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("AETHER manual preset {PresetId} stored for user {UserId}.", id, userId);
        return NoContent();
    }

    /// <summary>Deletes one preset, scoped to the calling user; idempotent.</summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> DeletePreset(string id, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (!IsValidPresetId(id))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-preset-id", "Preset id is invalid.");
        }

        await presets.DeleteAsync(userId, id, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private static object ToResponse(ManualPreset preset) => new
    {
        id = preset.Id,
        name = preset.Name,
        snapshot = JsonDocument.Parse(preset.SnapshotJson).RootElement,
        updatedAtUnixTimeMilliseconds = preset.UpdatedAtUnixTimeMilliseconds
    };

    private static bool TryReadName(JsonElement body, out string name)
    {
        name = string.Empty;
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = nameElement.GetString() ?? string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return false;
        }

        name = trimmed;
        return true;
    }

    /// <summary>
    /// Prüft nur die Form (Objekt mit `knobs`-Objekt aus Zahlen und `rimColor`
    /// als Drei-Zahlen-Feld), NICHT die Menge der Regler-Kennungen — der
    /// Reglersatz wächst mit der Zeit, und der Server soll dafür keine
    /// Allowlist pflegen müssen.
    /// </summary>
    private static bool TryReadSnapshotJson(JsonElement body, out string snapshotJson)
    {
        snapshotJson = string.Empty;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("snapshot", out var snapshot))
        {
            return false;
        }

        if (snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("knobs", out var knobs)
            || knobs.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("rimColor", out var rimColor)
            || rimColor.ValueKind != JsonValueKind.Array
            || rimColor.GetArrayLength() != 3)
        {
            return false;
        }

        foreach (var knob in knobs.EnumerateObject())
        {
            if (knob.Value.ValueKind != JsonValueKind.Number)
            {
                return false;
            }
        }

        foreach (var component in rimColor.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Number)
            {
                return false;
            }
        }

        var raw = snapshot.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(raw) > MaxSnapshotJsonBytes)
        {
            return false;
        }

        snapshotJson = raw;
        return true;
    }

    /// <summary>
    /// Kennungen sind schmal gefasst wie bei den Sprachzeilen (siehe
    /// <see cref="AnalysisController"/>), aber GROSS-/Kleinschreibung bleibt
    /// erlaubt: `crypto.randomUUID()` liefert Kleinbuchstaben, der lokale
    /// Ersatzweg im Client mischt Ziffern und Kleinbuchstaben — beides passt
    /// hier bereits, eine engere Regel brächte nichts.
    /// </summary>
    private static bool IsValidPresetId(string id)
    {
        if (id.Length is 0 or > 128)
        {
            return false;
        }

        foreach (var character in id)
        {
            var allowed = character is >= 'a' and <= 'z'
                || character is >= 'A' and <= 'Z'
                || character is >= '0' and <= '9'
                || character == '-';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private Guid GetUserId()
    {
        var value = User.FindFirstValue(UserIdClaim);
        return Guid.TryParse(value, out var userId) ? userId : Guid.Empty;
    }

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
}
