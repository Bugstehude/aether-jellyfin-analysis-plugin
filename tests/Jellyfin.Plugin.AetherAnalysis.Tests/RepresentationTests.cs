using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AetherAnalysis.Application;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class RepresentationTests
{
    [Fact]
    public void CompactRepresentationPreservesPeakSceneCutAndReducesCadence()
    {
        var service = new AnalysisRepresentationService();
        var master = JsonNode.Parse(
            """
            {
              "schemaVersion": 2,
              "sampling": { "intervalMs": 250, "frameWidth": 480, "frameHeight": 270, "colorSpace": "srgb" },
              "representation": { "detail": "full", "sourceIntervalMs": 250, "derived": false, "reductionVersion": "aether-reduction-1" },
              "frames": [
                { "timestampMs": 0, "luminance": 0.1, "contrast": 0.2, "saturation": 0.3, "motionEnergy": 0.4, "sceneCutProbability": 0.1, "palette": [] },
                { "timestampMs": 250, "luminance": 0.3, "contrast": 0.4, "saturation": 0.5, "motionEnergy": 0.6, "sceneCutProbability": 0.95, "palette": [] },
                { "timestampMs": 1000, "luminance": 0.5, "contrast": 0.6, "saturation": 0.7, "motionEnergy": 0.8, "sceneCutProbability": 0.2, "palette": [] }
              ]
            }
            """)!.ToJsonString();

        var representation = service.Create(System.Text.Encoding.UTF8.GetBytes(master), "compact");
        using var document = JsonDocument.Parse(representation.Json);
        var frames = document.RootElement.GetProperty("frames");

        Assert.Equal(2, frames.GetArrayLength());
        Assert.Equal(0.95, frames[0].GetProperty("sceneCutProbability").GetDouble(), precision: 8);
        Assert.Equal(1000, document.RootElement.GetProperty("sampling").GetProperty("intervalMs").GetInt32());
        Assert.True(document.RootElement.GetProperty("representation").GetProperty("derived").GetBoolean());
    }

    [Fact]
    public void RepresentationEtagChangesWithDetail()
    {
        var service = new AnalysisRepresentationService();
        var master = System.Text.Encoding.UTF8.GetBytes(
            """{"sampling":{"intervalMs":250},"frames":[{"timestampMs":0,"luminance":0,"contrast":0,"saturation":0,"motionEnergy":0,"sceneCutProbability":0,"palette":[]}]}""");

        var compact = service.Create(master, "compact");
        var full = service.Create(master, "full");

        Assert.NotEqual(compact.Etag, full.Etag);
    }

    [Fact]
    public void RepresentationEtagCanBeResolvedWithoutMaterializingRepresentation()
    {
        var service = new AnalysisRepresentationService();
        var master = System.Text.Encoding.UTF8.GetBytes(
            """{"sampling":{"intervalMs":250},"frames":[]}""");
        var masterEtag = AnalysisRepresentationService.CreateEtag(master);

        var representation = service.Create(master, "balanced", masterEtag);
        var metadataOnlyEtag = AnalysisRepresentationService.CreateRepresentationEtag(
            masterEtag,
            "balanced");

        Assert.Equal(metadataOnlyEtag, representation.Etag);
    }
}
