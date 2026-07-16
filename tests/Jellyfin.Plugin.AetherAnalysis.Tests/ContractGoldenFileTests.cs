using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Application;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class ContractGoldenFileTests
{
    private readonly AnalysisDocumentValidator _validator = new();

    [Fact]
    public void UploadGoldenFilesFollowApplicationInvariants()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "contracts", "examples");
        foreach (var path in Directory.GetFiles(Path.Combine(root, "valid"), "*upload*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var result = _validator.Validate(document.RootElement, 50 * 1024 * 1024);
            Assert.True(result.IsValid, $"{Path.GetFileName(path)}: {result.Error}");
        }

        foreach (var path in Directory.GetFiles(Path.Combine(root, "invalid"), "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var result = _validator.Validate(document.RootElement, 50 * 1024 * 1024);
            Assert.False(result.IsValid, Path.GetFileName(path));
        }
    }

    [Fact]
    public void EveryCanonicalSchemaIsSyntacticallyValidJson()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "contracts", "schemas");
        foreach (var path in Directory.GetFiles(root, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }
}
