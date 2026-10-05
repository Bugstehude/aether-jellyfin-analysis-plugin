using System.Security.Cryptography;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Configuration;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class Analysis12WorkerBundleTests
{
    [Fact]
    public async Task ReleasedSettingsPreserveDecoderCapAndPinActualPackagedBytes()
    {
        var configuration = new PluginConfiguration { AnalysisFfmpegThreads = 4 };
        var settings = Analysis12WorkerBundle.CreateSettings(configuration, "ffmpeg", "ffprobe");
        Assert.Equal("1.2.0", settings.TargetVersion);
        Assert.Equal(4, settings.FfmpegThreads);
        var actual = await File.ReadAllBytesAsync(settings.WorkerPath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(actual)), settings.WorkerSha256);
        Assert.Contains("AETHER_FFMPEG_THREADS", System.Text.Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public void DisablingServerAnalysisCannotStartTheReleasedBundle()
    {
        Assert.Throws<InvalidDataException>(() => Analysis12WorkerBundle.CreateSettings(
            new PluginConfiguration { ServerAnalysisEnabled = false }, "ffmpeg", "ffprobe"));
    }
}
