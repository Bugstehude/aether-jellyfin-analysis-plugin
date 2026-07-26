using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AetherAnalysis.Application;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>
/// Tests für das Master-Dokument — das, was aus einem Upload tatsächlich
/// gespeichert und später an jeden Leser des Items ausgeliefert wird.
///
/// Der Anlass: bis vor Kurzem wurde JEDES unbekannte Top-Level-Feld eines
/// Uploads ungeprüft übernommen. Der Validator sieht solche Felder nicht an —
/// sie gingen also als ungeprüfte Fremddaten in die Datenbank und von dort
/// zurück an die Clients. Der Vertrag deckt das Verwerfen ausdrücklich
/// (plugin-concept.md 11.3: Autoren dürfen sich nicht darauf verlassen, dass
/// unbekannte Felder ein erneutes Speichern überleben).
/// </summary>
public sealed class MasterDocumentTests
{
    private static readonly MediaFingerprint Media = new(
        ItemId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
        MediaSourceId: "quelle-1",
        Fingerprint: "sha256:abc",
        FingerprintQuality: "strong",
        DurationMs: 60000);

    private static JsonObject Upload(string? extraProperties = null)
    {
        var node = JsonNode.Parse(
            """
            {
              "schemaVersion": 2,
              "createdAt": "2026-01-01T00:00:00Z",
              "durationMs": 60000,
              "sampling": { "intervalMs": 500, "frameWidth": 480, "frameHeight": 270, "colorSpace": "srgb" },
              "producer": { "name": "aether", "version": "1.0.0", "platform": "browser" },
              "mediaFingerprintAtStart": "sha256:abc",
              "frames": [
                { "timestampMs": 0, "luminance": 0.1, "contrast": 0.2, "saturation": 0.3, "motionEnergy": 0.4, "sceneCutProbability": 0.1, "palette": [] }
              ]
            }
            """)!.AsObject();
        if (extraProperties is not null)
        {
            foreach (var property in JsonNode.Parse(extraProperties)!.AsObject())
            {
                node[property.Key] = property.Value?.DeepClone();
            }
        }

        return node;
    }

    private static JsonElement BuildMaster(JsonObject upload)
    {
        var bytes = new AnalysisRepresentationService().BuildMaster(
            upload, Media, "aether-visual", "1.1.0", DateTimeOffset.UnixEpoch);
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    [Fact]
    public void DropsUnknownTopLevelFields()
    {
        var master = BuildMaster(Upload("""
            { "evilPayload": { "script": "<img onerror=alert(1)>" }, "randomNote": "beliebig" }
            """));

        Assert.False(master.TryGetProperty("evilPayload", out _));
        Assert.False(master.TryGetProperty("randomNote", out _));
    }

    [Fact]
    public void KeepsEverythingTheContractKnows()
    {
        // Gegenprobe: die Whitelist darf nicht versehentlich Vertragsfelder
        // mitverwerfen — sonst wäre die gespeicherte Analyse unbrauchbar.
        var master = BuildMaster(Upload("""
            { "clientContentFingerprint": { "algorithm": "sha256", "value": "sha256:def" },
              "audioFrames": [ { "timestampMs": 0, "rms": 0.2, "flux": 0.1 } ] }
            """));

        foreach (var name in new[]
                 {
                     "schemaVersion", "item", "algorithm", "createdAt", "storedAt",
                     "durationMs", "sampling", "producer", "representation", "frames",
                     "clientContentFingerprint", "audioFrames"
                 })
        {
            Assert.True(master.TryGetProperty(name, out _), $"Feld {name} fehlt im Master-Dokument.");
        }
    }

    [Fact]
    public void TakesIdentityFromTheServerNotFromTheUpload()
    {
        // Der Upload darf nicht bestimmen, zu welchem Item er gehört — sonst
        // könnte ein Client eine Analyse einem fremden Medium unterschieben.
        var master = BuildMaster(Upload("""
            { "item": { "id": "99999999-9999-9999-9999-999999999999", "fingerprint": "sha256:gefaelscht" } }
            """));

        var item = master.GetProperty("item");
        Assert.Equal(Media.ItemId.ToString(), item.GetProperty("id").GetString());
        Assert.Equal(Media.Fingerprint, item.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public void StampsTheAlgorithmTheServerWasCalledWith()
    {
        var algorithm = BuildMaster(Upload()).GetProperty("algorithm");
        Assert.Equal("aether-visual", algorithm.GetProperty("id").GetString());
        Assert.Equal("1.1.0", algorithm.GetProperty("version").GetString());
    }
}
