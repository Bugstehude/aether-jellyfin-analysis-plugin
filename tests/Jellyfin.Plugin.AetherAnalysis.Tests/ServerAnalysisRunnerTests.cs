using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class ServerAnalysisRunnerTests
{
    [Fact]
    public async Task DisposalCancelsNewWorkWithoutTouchingDisposedGates()
    {
        var runner = new ServerAnalysisRunner(
            Substitute.For<ILibraryManager>(),
            Substitute.For<IAnalysisRepository>(),
            new AnalysisDocumentValidator(),
            new MediaFingerprintService(),
            new AnalysisRepresentationService(),
            new AnalysisWriteCoordinator(),
            worker: null!,
            new ServerAnalysisActivity(),
            NullLogger<ServerAnalysisRunner>.Instance);

        runner.Dispose();
        runner.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.AnalyzePendingAsync(null, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.AnalyzeItemAsync(Guid.NewGuid(), null, CancellationToken.None));
    }
}
