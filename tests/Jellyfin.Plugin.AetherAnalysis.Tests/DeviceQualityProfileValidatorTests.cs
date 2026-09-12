using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AetherAnalysis.Application;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>
/// Tests für den Geräteprofil-Validator — die Stelle, an der ein Client (Web oder tvOS) sein
/// Selbstvermessungsergebnis in den serverweiten Katalog einträgt. Geprüft wird für jede
/// einzelne Vertragsgrenze aus dem Auftrag (Pfad-Identität, „release"-Build, kein
/// abgebrochener Lauf, Pflichtfelder), ob sie hält UND ob sie den gültigen Fall durchlässt —
/// ein Validator, der alles ablehnt, wäre ebenso kaputt wie einer, der alles durchlässt.
/// </summary>
public sealed class DeviceQualityProfileValidatorTests
{
    private const string Client = "tvos";
    private const string Ladder = "tvos-7";
    private const string DeviceIdentifier = "AppleTV14,1";

    private static JsonElement Profile(string? overrides = null)
    {
        var json = """
            {
              "schema": "aether.device-quality-profile",
              "schemaVersion": 1,
              "client": "tvos",
              "ladder": "tvos-7",
              "ladderSteps": 7,
              "device": {
                "identifier": "AppleTV14,1",
                "description": "AppleTV14,1",
                "operatingSystem": "tvOS 26.6.0",
                "appVersion": "1.0 (1)",
                "build": "release"
              },
              "output": {
                "width": 1920,
                "height": 1080,
                "refreshHz": 50,
                "pixelClass": "hd",
                "refreshClass": "hz50",
                "frameBudgetMs": 20
              },
              "source": {
                "label": "probe.mov",
                "codec": "hevc",
                "width": 3840,
                "height": 2160,
                "framesPerSecond": 59.985,
                "hdrClass": "sdr",
                "pixelClass": "uhd",
                "origin": "http"
              },
              "run": {
                "id": "lab-2026-08-28T05:26:36Z",
                "mode": "schnell",
                "startedAt": "2026-08-28T05:26:36Z",
                "finishedAt": "2026-08-28T05:41:42Z",
                "warmupSeconds": 10,
                "measurementSeconds": 60,
                "policy": {
                  "minimumWarmupSeconds": 10,
                  "minimumMeasurementSeconds": 60,
                  "maximumFrameP95BudgetMultiplier": 1.5,
                  "maximumGPUP95BudgetMultiplier": 0.7,
                  "minimumDeliveredFrameRatio": 0.95,
                  "requireVisualAcceptance": false,
                  "requireFormalBuild": true
                },
                "plannedCombinations": 11,
                "measuredCombinations": 12,
                "failedCombinations": 6,
                "interrupted": false
              },
              "entries": [
                { "experienceID": "trance", "status": "measured", "testedSteps": [0, 3, 5], "visualVerdict": "notReviewed", "evidence": { "runIDs": ["003-trance-step-5"] } }
              ],
              "fallbackStartStepIndex": 5
            }
            """;

        if (overrides is not null)
        {
            var node = JsonNode.Parse(json)!.AsObject();
            var patch = JsonNode.Parse(overrides)!.AsObject();
            foreach (var property in patch)
            {
                node[property.Key] = property.Value?.DeepClone();
            }

            json = node.ToJsonString();
        }

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static DeviceQualityProfileValidationResult Validate(
        string? overrides = null, string client = Client, string ladder = Ladder, string deviceIdentifier = DeviceIdentifier) =>
        new DeviceQualityProfileValidator().Validate(Profile(overrides), client, ladder, deviceIdentifier);

    [Fact]
    public void AcceptsAContractConformProfile()
    {
        // Die Gegenprobe zu allem Folgenden: ohne sie könnte jeder Test unten grün sein,
        // nur weil der Validator inzwischen grundsätzlich alles ablehnt.
        var result = Validate();

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("AppleTV14,1", result.DeviceDescription);
        Assert.Equal("schnell", result.Mode);
        Assert.Equal("2026-08-28T05:41:42Z", result.FinishedAt);
        Assert.Equal(1, result.EntryCount);
        Assert.Equal(5, result.FallbackStartStepIndex);
    }

    [Fact]
    public void AcceptsAProfileWithoutTheOptionalFallbackStep()
    {
        // fallbackStartStepIndex fehlt, wenn kein Erlebnis eine Stufe bestand — das ist
        // laut Vertrag ein gültiger, nicht bloß ein leerer Zustand.
        var result = Validate("""{ "fallbackStartStepIndex": null }""");

        Assert.True(result.IsValid, result.Error);
        Assert.Null(result.FallbackStartStepIndex);
    }

    [Fact]
    public void RejectsANonObjectBody()
    {
        var result = new DeviceQualityProfileValidator().Validate(
            JsonDocument.Parse("[1,2,3]").RootElement.Clone(), Client, Ladder, DeviceIdentifier);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RejectsAnOversizedBody()
    {
        var padding = new string('x', 260 * 1024);
        var result = Validate($$"""{ "device": { "identifier": "AppleTV14,1", "description": "{{padding}}", "operatingSystem": "tvOS", "appVersion": "1.0", "build": "release" } }""");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RejectsAWrongSchemaName()
    {
        Assert.False(Validate("""{ "schema": "aether.something-else" }""").IsValid);
    }

    [Fact]
    public void RejectsAWrongSchemaVersion()
    {
        Assert.False(Validate("""{ "schemaVersion": 2 }""").IsValid);
    }

    [Fact]
    public void RejectsAClientMismatchedWithTheRoute()
    {
        // Der Körper behauptet "web", die Route sagt "tvos" — der Katalogeintrag würde
        // sonst unter dem falschen Schlüssel landen.
        var result = Validate(client: "web");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RejectsALadderMismatchedWithTheRoute()
    {
        Assert.False(Validate(ladder: "tvos-9").IsValid);
    }

    [Fact]
    public void RejectsADeviceIdentifierMismatchedWithTheRoute()
    {
        Assert.False(Validate(deviceIdentifier: "AppleTV14,2").IsValid);
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("benchmark")]
    public void RejectsANonReleaseBuild(string build)
    {
        // Ein Debug- oder Benchmark-Build misst planmäßig anders (Instrumentierung,
        // Overlays) und darf spätere Selbstläufe echter Nutzer nicht nach unten ziehen.
        var result = Validate($$"""{ "device": { "identifier": "AppleTV14,1", "description": "AppleTV14,1", "operatingSystem": "tvOS", "appVersion": "1.0", "build": "{{build}}" } }""");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RejectsAnInterruptedRun()
    {
        var result = Validate(
            """{ "run": { "id": "lab", "mode": "schnell", "startedAt": "2026-01-01T00:00:00Z", "finishedAt": "2026-01-01T00:10:00Z", "warmupSeconds": 10, "measurementSeconds": 60, "policy": {}, "plannedCombinations": 1, "measuredCombinations": 0, "failedCombinations": 1, "interrupted": true } }""");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RejectsAMissingDeviceObject()
    {
        Assert.False(Validate("""{ "device": null }""").IsValid);
    }

    [Fact]
    public void RejectsAMissingOutputField()
    {
        Assert.False(Validate("""{ "output": { "width": 1920, "height": 1080, "refreshHz": 50, "pixelClass": "hd", "refreshClass": "hz50" } }""").IsValid);
    }

    [Fact]
    public void RejectsAMissingSourceField()
    {
        Assert.False(Validate("""{ "source": { "label": "probe.mov", "codec": "hevc", "width": 3840, "height": 2160, "framesPerSecond": 59.985, "hdrClass": "sdr", "pixelClass": "uhd" } }""").IsValid);
    }

    [Fact]
    public void RejectsMissingEntries()
    {
        Assert.False(Validate("""{ "entries": null }""").IsValid);
    }
}
