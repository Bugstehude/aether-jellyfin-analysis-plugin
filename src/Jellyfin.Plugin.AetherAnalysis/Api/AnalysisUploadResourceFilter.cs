using Jellyfin.Plugin.AetherAnalysis.Application;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.AetherAnalysis.Api;

/// <summary>Serializes upload parsing and processing before model binding can amplify memory use.</summary>
public sealed class AnalysisUploadResourceFilter(AnalysisWriteCoordinator writeCoordinator)
    : IAsyncResourceFilter
{
    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        using var lease = await writeCoordinator.AcquireAsync(context.HttpContext.RequestAborted)
            .ConfigureAwait(false);
        await next().ConfigureAwait(false);
    }
}
