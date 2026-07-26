using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Application;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>
/// Tests für den Validator — die Stelle, an der fremde Daten den Server
/// betreten. Jeder Upload läuft hier durch, und was er durchlässt, wird
/// gespeichert und später an jeden Leser des Items ausgeliefert. Bis hierhin
/// hatte ausgerechnet diese Grenze keinen einzigen Test.
///
/// Geprüft wird deshalb nicht "kommt eine Fehlermeldung", sondern für jede
/// einzelne Vertragsgrenze: hält sie, und lässt sie den gültigen Fall durch?
/// Ein Validator, der ALLES ablehnt, wäre ebenso kaputt wie einer, der alles
/// durchlässt — nur unauffälliger.
/// </summary>
public sealed class AnalysisDocumentValidatorTests
{
    private const int MaxBytes = 50 * 1024 * 1024;
    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static JsonElement Upload(string? overrides = null)
    {
        var json = $$"""
            {
              "schemaVersion": 2,
              "createdAt": "2026-01-01T00:00:00Z",
              "durationMs": 60000,
              "sampling": { "intervalMs": 500, "frameWidth": 480, "frameHeight": 270, "colorSpace": "srgb" },
              "producer": { "name": "aether", "version": "1.0.0", "platform": "browser" },
              "mediaFingerprintAtStart": "{{Fingerprint}}",
              "frames": [
                { "timestampMs": 0, "luminance": 0.1, "contrast": 0.2, "saturation": 0.3, "motionEnergy": 0.4, "sceneCutProbability": 0.0, "palette": [] },
                { "timestampMs": 500, "luminance": 0.2, "contrast": 0.3, "saturation": 0.4, "motionEnergy": 0.5, "sceneCutProbability": 0.9, "palette": [] }
              ]
            }
            """;
        if (overrides is not null)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            var patch = System.Text.Json.Nodes.JsonNode.Parse(overrides)!.AsObject();
            foreach (var property in patch)
            {
                node[property.Key] = property.Value?.DeepClone();
            }

            json = node.ToJsonString();
        }

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static ValidationResult Validate(string? overrides = null) =>
        new AnalysisDocumentValidator().Validate(Upload(overrides), MaxBytes);

    [Fact]
    public void AcceptsAContractConformUpload()
    {
        // Die Gegenprobe zu allem Folgenden: ohne sie könnte jeder Test unten
        // grün sein, weil der Validator schlicht alles ablehnt.
        var result = Validate();
        Assert.True(result.IsValid, result.Error);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1 }""")]
    [InlineData("""{ "schemaVersion": 3 }""")]
    public void RejectsForeignSchemaVersions(string patch)
    {
        Assert.False(Validate(patch).IsValid);
    }

    [Theory]
    [InlineData("""{ "durationMs": 0 }""")]
    [InlineData("""{ "durationMs": -1 }""")]
    public void RejectsNonPositiveDuration(string patch)
    {
        Assert.False(Validate(patch).IsValid);
    }

    [Fact]
    public void RejectsATimestampFromTheFuture()
    {
        // Ein Dokument, das behauptet, morgen erstellt worden zu sein, würde
        // jede Alterung und jede Verdrängungsstrategie aushebeln.
        Assert.False(Validate($$"""{ "createdAt": "{{DateTimeOffset.UtcNow.AddDays(1):O}}" }""").IsValid);
    }

    [Theory]
    // Abtastung außerhalb der Vertragsgrenzen: zu dicht, zu grob, zu groß, falscher Farbraum.
    [InlineData("""{ "sampling": { "intervalMs": 100, "frameWidth": 480, "frameHeight": 270, "colorSpace": "srgb" } }""")]
    [InlineData("""{ "sampling": { "intervalMs": 20000, "frameWidth": 480, "frameHeight": 270, "colorSpace": "srgb" } }""")]
    [InlineData("""{ "sampling": { "intervalMs": 500, "frameWidth": 4096, "frameHeight": 270, "colorSpace": "srgb" } }""")]
    [InlineData("""{ "sampling": { "intervalMs": 500, "frameWidth": 480, "frameHeight": 270, "colorSpace": "p3" } }""")]
    public void RejectsSamplingOutsideTheContract(string patch)
    {
        Assert.False(Validate(patch).IsValid);
    }

    [Theory]
    [InlineData("""{ "producer": { "name": "", "version": "1.0.0", "platform": "browser" } }""")]
    [InlineData("""{ "producer": { "name": "aether", "version": "1.0.0", "platform": "toaster" } }""")]
    public void RejectsAnInvalidProducer(string patch)
    {
        Assert.False(Validate(patch).IsValid);
    }

    [Theory]
    [InlineData("""{ "mediaFingerprintAtStart": "nicht-wirklich-ein-hash" }""")]
    [InlineData("""{ "mediaFingerprintAtStart": "sha256:zzzz" }""")]
    [InlineData("""{ "mediaFingerprintAtStart": "" }""")]
    public void RejectsAMalformedFingerprint(string patch)
    {
        // Der Fingerabdruck entscheidet, ob eine gespeicherte Analyse noch zum
        // Medium gehört. Ein formloser Wert würde diese Prüfung entwerten.
        Assert.False(Validate(patch).IsValid);
    }

    [Fact]
    public void RejectsAnEmptyFrameList()
    {
        Assert.False(Validate("""{ "frames": [] }""").IsValid);
    }

    [Fact]
    public void RejectsNonAscendingTimestamps()
    {
        // Die gesamte Wiedergabe sucht Bilder über eine aufsteigende Zeitachse.
        var patch = """
            { "frames": [
              { "timestampMs": 500, "luminance": 0.1, "contrast": 0.1, "saturation": 0.1, "motionEnergy": 0.1, "sceneCutProbability": 0.1, "palette": [] },
              { "timestampMs": 0,   "luminance": 0.1, "contrast": 0.1, "saturation": 0.1, "motionEnergy": 0.1, "sceneCutProbability": 0.1, "palette": [] }
            ] }
            """;
        Assert.False(Validate(patch).IsValid);
    }

    [Fact]
    public void RejectsTimestampsBeyondTheStatedDuration()
    {
        var patch = """
            { "frames": [
              { "timestampMs": 999999, "luminance": 0.1, "contrast": 0.1, "saturation": 0.1, "motionEnergy": 0.1, "sceneCutProbability": 0.1, "palette": [] }
            ] }
            """;
        Assert.False(Validate(patch).IsValid);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public void RejectsMeasurementsOutsideZeroToOne(double value)
    {
        // Jeder Verbraucher rechnet mit 0..1; ein Wert daneben schlägt bis in
        // den Shader durch, wo ihn niemand mehr prüft.
        var patch = $$"""
            { "frames": [
              { "timestampMs": 0, "luminance": {{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "contrast": 0.1, "saturation": 0.1, "motionEnergy": 0.1, "sceneCutProbability": 0.1, "palette": [] }
            ] }
            """;
        Assert.False(Validate(patch).IsValid);
    }

    [Fact]
    public void RejectsAnAudioMetricOutsideZeroToOne()
    {
        var patch = """
            { "frames": [
              { "timestampMs": 0, "luminance": 0.1, "contrast": 0.1, "saturation": 0.1, "motionEnergy": 0.1, "sceneCutProbability": 0.1, "palette": [],
                "audio": { "rms": 4.2, "flux": 0.1 } }
            ] }
            """;
        Assert.False(Validate(patch).IsValid);
    }

    [Fact]
    public void RejectsAnUploadOverTheSizeLimit()
    {
        // Nicht über die tatsächliche Größe geprüft, sondern über ein kleines
        // Limit — der Zweck ist die Grenze, nicht das Erzeugen von 50 MB.
        var result = new AnalysisDocumentValidator().Validate(Upload(), maxUploadBytes: 32);
        Assert.False(result.IsValid);
        Assert.Equal("payload-too-large", result.Code);
    }

    [Fact]
    public void RejectsGarbageInsteadOfThrowing()
    {
        // Ein kaputter Upload darf eine Ausnahme ergeben, die als 500 endet —
        // er muss als saubere Ablehnung zurückkommen.
        var garbage = JsonDocument.Parse("""{ "schemaVersion": "zwei" }""").RootElement;
        var result = new AnalysisDocumentValidator().Validate(garbage, MaxBytes);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }
}
