using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AetherAnalysis.Api;

/// <summary>
/// Serverweiter Katalog vermessener Geräteprofile (Vertrag
/// <c>aether.device-quality-profile</c> v1, siehe
/// <c>contracts/schemas/device-quality-profile-v1.schema.json</c>). Ein Client (Web oder
/// tvOS) vermisst sich selbst und legt das Ergebnis hier ab, damit ein weiteres Gerät
/// derselben Klasse künftig sofort mit der richtigen Startstufe beginnen kann.
///
/// ## Warum serverweit statt je Nutzer (wie <see cref="PresetsController"/>)
/// Ein Profil gehört zur GERÄTEKLASSE (<c>device.identifier</c>, z. B. "AppleTV14,1" oder
/// ein Web-Fingerabdruck), nicht zum anmeldenden Menschen — genau wie Sprachpaket und
/// Reise-Tonspur in <see cref="AnalysisController"/>. Ein eigener Controller trotzdem, weil
/// der Schlüssel dreiteilig ist (Client, Treppe, Gerät) und inhaltlich nichts mit Medien
/// oder dem Sprachexperiment zu tun hat.
///
/// ## Warum keine Hochlade-Berechtigung geprüft wird
/// Anders als Sprachzeile und Reise-Tonspur (dort <c>CanUpload()</c>) ist ein Geräteprofil
/// kein redaktioneller Inhalt, den jemand für andere Nutzer gestaltet — es ist die
/// Selbstauskunft eines Clients über sich selbst, technisch nicht sensibler als der eigene
/// Medien-Fingerprint. Jeder angemeldete Client darf sein eigenes Geräteprofil ablegen;
/// <see cref="AuthorizeAttribute"/> allein reicht, wie im Auftrag vorgegeben.
/// </summary>
[ApiController]
[Authorize]
[Route("AetherAnalysis/v1/device-profiles")]
public sealed partial class DeviceProfilesController(
    DeviceQualityProfileRepository profiles,
    DeviceQualityProfileValidator validator,
    ILogger<DeviceProfilesController> logger) : ControllerBase
{
    private static readonly string[] AllowedClients = ["tvos", "web"];

    /// <summary>Lists the stored device-profile catalog, newest write first.</summary>
    [HttpGet]
    public async Task<ActionResult> ListProfiles(CancellationToken cancellationToken)
    {
        var stored = await profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(stored.Select(value => new
        {
            client = value.Client,
            ladder = value.Ladder,
            deviceIdentifier = value.DeviceIdentifier,
            deviceDescription = value.DeviceDescription,
            mode = value.Mode,
            finishedAt = value.FinishedAt,
            entries = value.EntryCount,
            fallbackStartStepIndex = value.FallbackStartStepIndex,
            storedAt = value.StoredAt
        }));
    }

    /// <summary>Gets one stored device profile; the response body is returned byte-identical to how it was stored.</summary>
    [HttpGet("{client}/{ladder}/{deviceIdentifier}")]
    public async Task<ActionResult> GetProfile(
        string client, string ladder, string deviceIdentifier, CancellationToken cancellationToken)
    {
        if (!IsValidRoute(client, ladder, deviceIdentifier))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-device-profile-route", "Route segment is invalid.");
        }

        var stored = await profiles.GetAsync(client, ladder, deviceIdentifier, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return ProblemResult(StatusCodes.Status404NotFound, "device-profile-not-found", "No device profile is stored for this key.");
        }

        // Roh als Bytes zurückgegeben, nicht neu über System.Text.Json serialisiert: der
        // Vertrag verlangt einen byteidentischen Körper, und jedes erneute Schreiben könnte
        // Feldreihenfolge oder Zahlformat unbemerkt verändern.
        return File(Encoding.UTF8.GetBytes(stored.DocumentJson), "application/json");
    }

    /// <summary>Stores or replaces one device profile. Last write wins; there is no history.</summary>
    [HttpPut("{client}/{ladder}/{deviceIdentifier}")]
    public async Task<ActionResult> PutProfile(
        string client,
        string ladder,
        string deviceIdentifier,
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        if (!IsValidRoute(client, ladder, deviceIdentifier))
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "invalid-device-profile-route", "Route segment is invalid.");
        }

        var result = validator.Validate(body, client, ladder, deviceIdentifier);
        if (!result.IsValid)
        {
            return ProblemResult(
                StatusCodes.Status400BadRequest, "invalid-device-profile", result.Error ?? "Device profile body is invalid.");
        }

        await profiles.UpsertAsync(
            client,
            ladder,
            deviceIdentifier,
            result.DeviceDescription!,
            result.Mode!,
            result.FinishedAt!,
            result.EntryCount,
            result.FallbackStartStepIndex,
            body.GetRawText(),
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "AETHER device profile stored for {Client}/{Ladder}/{DeviceIdentifier}.", client, ladder, deviceIdentifier);
        return NoContent();
    }

    private static bool IsValidRoute(string client, string ladder, string deviceIdentifier) =>
        AllowedClients.Contains(client) && IsValidLadder(ladder) && IsValidDeviceIdentifier(deviceIdentifier);

    private static bool IsValidLadder(string ladder) => ladder.Length is > 0 and <= 32 && LadderPattern().IsMatch(ladder);

    /// <summary>
    /// Erlaubt Komma, weil Apple seine Produktkennungen so schreibt (<c>AppleTV14,1</c>) — eine
    /// engere Regel hätte ausgerechnet den häufigsten Fall ausgeschlossen.
    /// </summary>
    private static bool IsValidDeviceIdentifier(string deviceIdentifier)
    {
        if (deviceIdentifier.Length is 0 or > 200)
        {
            return false;
        }

        foreach (var character in deviceIdentifier)
        {
            var allowed = character is >= 'a' and <= 'z'
                || character is >= 'A' and <= 'Z'
                || character is >= '0' and <= '9'
                || character is ',' or '.' or '_' or '-';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
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

    [GeneratedRegex("^[a-z]+-[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex LadderPattern();
}
