using System.Text;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;
using Xunit;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class DraftToolchainProbeTests
{
    [Fact]
    public void FirstLineKeepsTheReportedVersionAndBoundsUntrustedOutput()
    {
        Assert.Equal("ffmpeg version 8.1.3-Jellyfin Copyright (c) 2000-2026",
            DraftToolchainProbe.FirstLine(Encoding.UTF8.GetBytes(
                "\n  ffmpeg version 8.1.3-Jellyfin Copyright (c) 2000-2026\r\nbuilt with gcc\n")));
        Assert.Equal("a?b", DraftToolchainProbe.FirstLine(Encoding.UTF8.GetBytes("a\u001bb")));
        Assert.Equal(200, DraftToolchainProbe.FirstLine(Encoding.UTF8.GetBytes(new string('x', 500)))!.Length);
        Assert.Null(DraftToolchainProbe.FirstLine(Encoding.UTF8.GetBytes(" \n\t\n")));
    }

    [Fact]
    public async Task ReportsMissingExecutablesWithoutThrowing()
    {
        Assert.Equal("unavailable (not configured)",
            await DraftToolchainProbe.VersionAsync(" ", CancellationToken.None));
        var missing = Path.Combine(Path.GetTempPath(), "aether-missing-" + Guid.NewGuid().ToString("N"));
        Assert.StartsWith("unavailable (", await DraftToolchainProbe.VersionAsync(missing, CancellationToken.None),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadsTheVersionLineOfTheActualExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("aether-toolchain-");
        try
        {
            var script = Path.Combine(directory.FullName, "ffmpeg");
            await File.WriteAllTextAsync(script,
                "#!/bin/sh\n[ \"$1\" = \"-version\" ] || exit 2\necho 'ffmpeg version 7.1.4-Jellyfin'\necho 'configuration: x'\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal("ffmpeg version 7.1.4-Jellyfin",
                await DraftToolchainProbe.VersionAsync(script, CancellationToken.None));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void FailureLogsKeepValidatorCodesButNoFreeFormMessages()
    {
        Assert.Equal("artifact-coverageEndBasis",
            ServerAnalysisRunner.FailureCode(new InvalidDataException("artifact-coverageEndBasis")));
        Assert.Equal("-", ServerAnalysisRunner.FailureCode(new InvalidDataException("ffmpeg: /media/private/Film.mp4 failed")));
        Assert.Equal("-", ServerAnalysisRunner.FailureCode(new InvalidDataException(new string('a', 81))));
    }
}
