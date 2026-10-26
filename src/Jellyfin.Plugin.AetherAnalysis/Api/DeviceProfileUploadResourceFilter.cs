using Jellyfin.Plugin.AetherAnalysis.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.AetherAnalysis.Api;

/// <summary>Bounds the complete device-profile request before JSON model binding.</summary>
public sealed class DeviceProfileUploadResourceFilter : IAsyncResourceFilter
{
    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (request.ContentLength is > DeviceQualityProfileValidator.MaxDocumentBytes)
        {
            Reject(context);
            return;
        }

        var original = request.Body;
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        var maximumRead = DeviceQualityProfileValidator.MaxDocumentBytes + 1;
        while (bounded.Length < maximumRead)
        {
            var remaining = maximumRead - (int)bounded.Length;
            var read = await original.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                context.HttpContext.RequestAborted).ConfigureAwait(false);
            if (read == 0) break;
            bounded.Write(buffer, 0, read);
        }

        if (bounded.Length > DeviceQualityProfileValidator.MaxDocumentBytes)
        {
            Reject(context);
            return;
        }

        bounded.Position = 0;
        request.Body = bounded;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            request.Body = original;
        }
    }

    private static void Reject(ResourceExecutingContext context)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "invalid-device-profile",
            Type = "urn:aether:analysis:error:invalid-device-profile",
            Detail = "Profile exceeds the configured size limit."
        };
        problem.Extensions["code"] = "invalid-device-profile";
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.Result = new BadRequestObjectResult(problem);
    }
}
