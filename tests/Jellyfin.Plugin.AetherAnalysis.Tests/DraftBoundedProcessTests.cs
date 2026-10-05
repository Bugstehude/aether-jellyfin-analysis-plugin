using System.Text;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class DraftBoundedProcessTests
{
    [Fact]
    public async Task CapturesNodeOutputAndPreservesArgumentsContainingSpaces()
    {
        using var fixture = new NodeFixture("process.stdout.write(JSON.stringify(process.argv.slice(2)))");

        var result = await Run(fixture, ["alpha beta", "--value", "two words"]);

        Assert.Equal("[\"alpha beta\",\"--value\",\"two words\"]", Encoding.UTF8.GetString(result.StandardOutput));
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task RejectsStdoutBeyondConfiguredBoundAndDoesNotExposeCommandDetails()
    {
        using var fixture = new NodeFixture("process.stdout.write('x'.repeat(100000))");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run(fixture, maxOutputBytes: 128));

        Assert.Equal("draft-process-output-budget", error.Message);
        Assert.DoesNotContain(fixture.ScriptPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsStderrBeyondConfiguredBound()
    {
        using var fixture = new NodeFixture("process.stderr.write('secret detail '.repeat(10000))");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run(fixture, maxErrorBytes: 64));

        Assert.Equal("draft-process-error-budget", error.Message);
        Assert.DoesNotContain("secret detail", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnforcesTimeoutAndStopsNode()
    {
        using var fixture = new NodeFixture("setInterval(() => {}, 1000)");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run(fixture, timeout: TimeSpan.FromMilliseconds(150)));

        Assert.Equal("draft-process-timeout", error.Message);
    }

    [Fact]
    public async Task DoesNotWaitOnCompletedStdoutWhileChildContinuesRunning()
    {
        using var fixture = new NodeFixture("process.stdout.end(); setInterval(() => {}, 1000)");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run(fixture, timeout: TimeSpan.FromMilliseconds(150)));

        Assert.Equal("draft-process-timeout", error.Message);
    }

    [Fact]
    public async Task TimesOutWhenExitedParentLeavesDescendantHoldingPipeOpen()
    {
        using var fixture = new NodeFixture("const { spawn } = require('child_process'); spawn(process.execPath, ['-e', 'setTimeout(() => process.exit(0), 900)'], { stdio: 'inherit' }); process.exit(0)");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run(fixture, timeout: TimeSpan.FromMilliseconds(150)));

        Assert.Equal("draft-process-timeout", error.Message);
    }

    [Fact]
    public async Task ExternalCancellationRemainsOperationCanceledException()
    {
        using var fixture = new NodeFixture("setInterval(() => {}, 1000)");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(fixture, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ReportsNonzeroExitWithStableCodeOnly()
    {
        using var fixture = new NodeFixture("console.error('private detail'); process.exit(9)");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run(fixture));

        Assert.Equal("draft-process-exit", error.Message);
        Assert.DoesNotContain("private detail", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonzeroExitCarriesOnlyTheWorkersFixedErrorCode()
    {
        using var fixture = new NodeFixture(
            "process.stderr.write('ffmpeg: /mnt/private media/title.mp4: Invalid data\\n');"
            + "process.stderr.write(JSON.stringify({error: 'AUDIO_TIMING_UNAVAILABLE'}) + '\\n'); process.exit(3)");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run(fixture));

        Assert.Equal("draft-process-exit", error.Message);
        Assert.Equal(3, error.Data[DraftArtifactDiagnostics.ExitCodeKey]);
        Assert.Equal("AUDIO_TIMING_UNAVAILABLE", error.Data[DraftArtifactDiagnostics.WorkerErrorKey]);
        Assert.Equal("-", DraftArtifactDiagnostics.WorkerErrorCode("only /mnt/private text"u8));
        Assert.Equal("-", DraftArtifactDiagnostics.WorkerErrorCode("{\"error\":\"/mnt/x y\"}"u8));
    }

    [Fact]
    public async Task MonitorsOutputFileSize()
    {
        using var fixture = new NodeFixture("require('fs').writeFileSync(process.argv[2], Buffer.alloc(4096)); setInterval(() => {}, 1000)");
        var outputPath = Path.Combine(fixture.DirectoryPath, "result.bin");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run(
            fixture,
            arguments: [outputPath],
            outputFilePath: outputPath,
            maxOutputFileBytes: 128));

        Assert.Equal("draft-process-output-file-budget", error.Message);
    }

    [Fact]
    public async Task ChecksOutputFileSizeWhenChildExitsBeforePollingInterval()
    {
        using var fixture = new NodeFixture("require('fs').writeFileSync(process.argv[2], Buffer.alloc(4096))");
        var outputPath = Path.Combine(fixture.DirectoryPath, "result.bin");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Run(
            fixture,
            arguments: [outputPath],
            outputFilePath: outputPath,
            maxOutputFileBytes: 128));

        Assert.Equal("draft-process-output-file-budget", error.Message);
    }

    [Fact]
    public async Task ValidatesBudgetsBeforeStartingProcess()
    {
        using var fixture = new NodeFixture("require('fs').writeFileSync(process.argv[2], 'started')");
        var outputPath = Path.Combine(fixture.DirectoryPath, "result.bin");
        var request = new DraftProcessRequest(
            "node", [fixture.ScriptPath, outputPath], new Dictionary<string, string>(), TimeSpan.FromSeconds(2),
            10, 10, 0, outputPath, null);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DraftBoundedProcess.RunAsync(request));

        Assert.Equal("draft-process-invalid-request", error.Message);
        Assert.False(File.Exists(outputPath));
    }

    private static Task<DraftProcessResult> Run(
        NodeFixture fixture,
        IReadOnlyList<string>? arguments = null,
        int maxOutputBytes = 1024 * 1024,
        int maxErrorBytes = 1024 * 1024,
        TimeSpan? timeout = null,
        string? outputFilePath = null,
        int? maxOutputFileBytes = null,
        CancellationToken cancellationToken = default)
    {
        var processArguments = new List<string> { fixture.ScriptPath };
        if (arguments is not null)
        {
            processArguments.AddRange(arguments);
        }

        var request = new DraftProcessRequest(
            "node",
            processArguments,
            new Dictionary<string, string>(),
            timeout ?? TimeSpan.FromSeconds(10),
            maxOutputBytes,
            maxErrorBytes,
            0,
            outputFilePath,
            maxOutputFileBytes);
        return DraftBoundedProcess.RunAsync(request, cancellationToken);
    }

    private sealed class NodeFixture : IDisposable
    {
        public NodeFixture(string source)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "aether-draft-process-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            ScriptPath = Path.Combine(DirectoryPath, "fixture.js");
            File.WriteAllText(ScriptPath, source, Encoding.UTF8);
        }

        public string DirectoryPath { get; }

        public string ScriptPath { get; }

        public void Dispose()
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
