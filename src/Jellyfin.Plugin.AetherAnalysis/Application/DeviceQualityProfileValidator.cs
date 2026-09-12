using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>
/// Prüft den Körper von <c>PUT /device-profiles/{client}/{ladder}/{deviceIdentifier}</c> gegen
/// den Vertrag <c>aether.device-quality-profile</c> v1 (siehe
/// <c>contracts/schemas/device-quality-profile-v1.schema.json</c>).
///
/// ## Warum keine vollständige JSON-Schema-Validierung
/// Das kanonische Schema liegt unverändert im Contract-Repository (Draft-07,
/// <c>additionalProperties: false</c>, verschachtelte Enums). Es hier vollständig
/// nachzubilden würde bei jeder Vertragsänderung eine zweite, driftende Kopie der Regeln
/// bedeuten. Diese Klasse prüft deshalb nur, wovon die Ablage tatsächlich abhängt: dass die
/// Pflichtfelder da sind und ihren erwarteten JSON-Typ haben, und die wenigen Felder, die
/// der Server selbst durchsetzt (Pfad-Identität, „release"-Build, kein abgebrochener Lauf).
/// Anders als <see cref="AnalysisDocumentValidator"/> — der rechnet aus den Werten weiter
/// (Fingerprints, Frame-Grenzen) und muss deshalb streng sein — zeigt der Server ein
/// Geräteprofil nur an; ein Client, der zusätzliche oder leicht abweichend geformte Felder
/// schickt, soll daran nicht scheitern (wie schon beim Regler-Preset in
/// <see cref="Jellyfin.Plugin.AetherAnalysis.Api.PresetsController"/>).
/// </summary>
public sealed class DeviceQualityProfileValidator
{
    /// <summary>
    /// Obergrenze für den Profil-Körper. Großzügig genug für Dutzende Erlebnis-Einträge mit
    /// Beweisdaten je Stufe, eng genug, dass ein fehlerhafter Client nicht unbegrenzt Platz
    /// im Katalog belegt.
    /// </summary>
    public const int MaxDocumentBytes = 256 * 1024;

    private static readonly string[] AllowedClients = ["tvos", "web"];
    private static readonly string[] AllowedBuilds = ["debug", "release", "benchmark"];
    private static readonly string[] OutputRequiredFields =
        ["width", "height", "refreshHz", "pixelClass", "refreshClass", "frameBudgetMs"];

    private static readonly string[] SourceRequiredFields =
        ["label", "codec", "width", "height", "framesPerSecond", "hdrClass", "pixelClass", "origin"];

    /// <summary>Validiert den rohen Körper gegen Pflichtform und Pfad-Identität.</summary>
    /// <param name="body">Der ungeprüfte Anfragekörper.</param>
    /// <param name="client">Der Client-Anteil der Route, bereits als gültig geprüft.</param>
    /// <param name="ladder">Der Treppen-Anteil der Route, bereits als gültig geprüft.</param>
    /// <param name="deviceIdentifier">Der Geräte-Anteil der Route, bereits als gültig geprüft.</param>
    public DeviceQualityProfileValidationResult Validate(JsonElement body, string client, string ladder, string deviceIdentifier)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return DeviceQualityProfileValidationResult.Invalid("Body must be a JSON object.");
        }

        if (Encoding.UTF8.GetByteCount(body.GetRawText()) > MaxDocumentBytes)
        {
            return DeviceQualityProfileValidationResult.Invalid("Profile exceeds the configured size limit.");
        }

        if (!TryGetString(body, "schema", out var schema) || schema != "aether.device-quality-profile")
        {
            return DeviceQualityProfileValidationResult.Invalid("schema must equal 'aether.device-quality-profile'.");
        }

        if (!body.TryGetProperty("schemaVersion", out var schemaVersionElement)
            || schemaVersionElement.ValueKind != JsonValueKind.Number
            || !schemaVersionElement.TryGetInt32(out var schemaVersion)
            || schemaVersion != 1)
        {
            return DeviceQualityProfileValidationResult.Invalid("schemaVersion must equal 1.");
        }

        if (!TryGetString(body, "client", out var bodyClient) || !AllowedClients.Contains(bodyClient))
        {
            return DeviceQualityProfileValidationResult.Invalid("client must be 'tvos' or 'web'.");
        }

        if (!string.Equals(bodyClient, client, StringComparison.Ordinal))
        {
            return DeviceQualityProfileValidationResult.Invalid("client does not match the route.");
        }

        if (!TryGetString(body, "ladder", out var bodyLadder) || bodyLadder.Length == 0)
        {
            return DeviceQualityProfileValidationResult.Invalid("ladder is missing.");
        }

        if (!string.Equals(bodyLadder, ladder, StringComparison.Ordinal))
        {
            return DeviceQualityProfileValidationResult.Invalid("ladder does not match the route.");
        }

        if (!body.TryGetProperty("ladderSteps", out var ladderSteps) || ladderSteps.ValueKind != JsonValueKind.Number)
        {
            return DeviceQualityProfileValidationResult.Invalid("ladderSteps is missing.");
        }

        if (!body.TryGetProperty("device", out var device) || device.ValueKind != JsonValueKind.Object)
        {
            return DeviceQualityProfileValidationResult.Invalid("device is missing.");
        }

        if (!TryGetString(device, "identifier", out var bodyDeviceIdentifier))
        {
            return DeviceQualityProfileValidationResult.Invalid("device.identifier is missing.");
        }

        if (!string.Equals(bodyDeviceIdentifier, deviceIdentifier, StringComparison.Ordinal))
        {
            return DeviceQualityProfileValidationResult.Invalid("device.identifier does not match the route.");
        }

        if (!TryGetString(device, "description", out var deviceDescription))
        {
            return DeviceQualityProfileValidationResult.Invalid("device.description is missing.");
        }

        if (!TryGetString(device, "operatingSystem", out _) || !TryGetString(device, "appVersion", out _))
        {
            return DeviceQualityProfileValidationResult.Invalid("device is missing required fields.");
        }

        if (!TryGetString(device, "build", out var build) || !AllowedBuilds.Contains(build))
        {
            return DeviceQualityProfileValidationResult.Invalid("device.build is missing or invalid.");
        }

        // Nur „release" gelangt in den serverweiten Katalog: ein Debug- oder Benchmark-Build
        // misst planmäßig anders (Instrumentierung, Overlays, keine Optimierung) — würde er
        // aufgenommen, zöge er die Startstufe künftiger echter Selbstläufe nach unten.
        if (!string.Equals(build, "release", StringComparison.Ordinal))
        {
            return DeviceQualityProfileValidationResult.Invalid("device.build must equal 'release'.");
        }

        if (!HasRequiredObject(body, "output", OutputRequiredFields))
        {
            return DeviceQualityProfileValidationResult.Invalid("output is missing required fields.");
        }

        if (!HasRequiredObject(body, "source", SourceRequiredFields))
        {
            return DeviceQualityProfileValidationResult.Invalid("source is missing required fields.");
        }

        if (!body.TryGetProperty("run", out var run) || run.ValueKind != JsonValueKind.Object)
        {
            return DeviceQualityProfileValidationResult.Invalid("run is missing.");
        }

        if (!TryGetString(run, "id", out _)
            || !TryGetString(run, "mode", out var mode)
            || !TryGetString(run, "startedAt", out _)
            || !TryGetString(run, "finishedAt", out var finishedAt))
        {
            return DeviceQualityProfileValidationResult.Invalid("run is missing required fields.");
        }

        if (!IsNumber(run, "warmupSeconds")
            || !IsNumber(run, "measurementSeconds")
            || !IsNumber(run, "plannedCombinations")
            || !IsNumber(run, "measuredCombinations")
            || !IsNumber(run, "failedCombinations"))
        {
            return DeviceQualityProfileValidationResult.Invalid("run is missing required numeric fields.");
        }

        if (!run.TryGetProperty("policy", out var policy) || policy.ValueKind != JsonValueKind.Object)
        {
            return DeviceQualityProfileValidationResult.Invalid("run.policy is missing.");
        }

        if (!run.TryGetProperty("interrupted", out var interrupted)
            || interrupted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return DeviceQualityProfileValidationResult.Invalid("run.interrupted is missing.");
        }

        // Ein abgebrochener Lauf beschreibt keine verlässlich erreichte Startstufe — in den
        // Katalog aufgenommen, würde er künftigen Geräten derselben Klasse eine Stufe
        // vorschlagen, die nie zu Ende gemessen wurde.
        if (interrupted.ValueKind == JsonValueKind.True)
        {
            return DeviceQualityProfileValidationResult.Invalid("run.interrupted must be false.");
        }

        if (!body.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return DeviceQualityProfileValidationResult.Invalid("entries is missing.");
        }

        int? fallbackStartStepIndex = null;
        if (body.TryGetProperty("fallbackStartStepIndex", out var fallback)
            && fallback.ValueKind == JsonValueKind.Number
            && fallback.TryGetInt32(out var parsedFallback))
        {
            fallbackStartStepIndex = parsedFallback;
        }

        return DeviceQualityProfileValidationResult.Valid(deviceDescription, mode, finishedAt, entries.GetArrayLength(), fallbackStartStepIndex);
    }

    private static bool IsNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number;

    private static bool HasRequiredObject(JsonElement body, string name, string[] requiredProperties)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in requiredProperties)
        {
            if (!value.TryGetProperty(property, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }
}

/// <summary>Result of device-profile body validation.</summary>
public sealed record DeviceQualityProfileValidationResult(
    bool IsValid,
    string? Error,
    string? DeviceDescription,
    string? Mode,
    string? FinishedAt,
    int EntryCount,
    int? FallbackStartStepIndex)
{
    /// <summary>Creates a valid result carrying the summary fields the repository needs.</summary>
    public static DeviceQualityProfileValidationResult Valid(
        string deviceDescription, string mode, string finishedAt, int entryCount, int? fallbackStartStepIndex) =>
        new(true, null, deviceDescription, mode, finishedAt, entryCount, fallbackStartStepIndex);

    /// <summary>Creates an invalid result.</summary>
    public static DeviceQualityProfileValidationResult Invalid(string error) =>
        new(false, error, null, null, null, 0, null);
}
