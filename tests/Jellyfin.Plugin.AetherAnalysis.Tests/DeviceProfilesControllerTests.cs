using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AetherAnalysis.Api;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class DeviceProfilesControllerTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"aether-profile-api-{Guid.NewGuid():N}.sqlite");
    private readonly AnalysisWriteCoordinator coordinator = new();

    private sealed class ContextFactory(DbContextOptions<AnalysisDbContext> options)
        : IDbContextFactory<AnalysisDbContext>
    {
        public AnalysisDbContext CreateDbContext() => new(options);
    }

    private sealed class PausedInserts : DbCommandInterceptor
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int count;
        public int Count => Volatile.Read(ref count);
        public void Release() => release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("INSERT", StringComparison.Ordinal)
                && command.CommandText.Contains("device_quality_profiles", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref count);
                await release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    public void Dispose()
    {
        coordinator.Dispose();
        File.Delete(path);
    }

    private async Task<DeviceQualityProfileRepository> CreateRepository(PausedInserts? reads = null)
    {
        var options = new DbContextOptionsBuilder<AnalysisDbContext>().UseSqlite($"Data Source={path}");
        if (reads is not null) options.AddInterceptors(reads);
        var factory = new ContextFactory(options.Options);
        await using var context = factory.CreateDbContext();
        await context.Database.MigrateAsync();
        return new DeviceQualityProfileRepository(factory);
    }

    private static JsonElement Profile(string description)
    {
        var node = JsonNode.Parse(DeviceQualityProfileValidatorTests.Profile().GetRawText())!;
        node["device"]!["description"] = description;
        return JsonSerializer.SerializeToElement(node);
    }

    private DeviceProfilesController Controller(DeviceQualityProfileRepository repository)
    {
        using var services = new ServiceCollection().AddSingleton(repository)
            .AddSingleton(coordinator).AddSingleton<DeviceQualityProfileValidator>()
            .AddSingleton<ILogger<DeviceProfilesController>>(NullLogger<DeviceProfilesController>.Instance)
            .BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<DeviceProfilesController>(services);
    }

    [Fact]
    public async Task ConcurrentFirstUploadsKeepLastWriteWinsAndWholeDocuments()
    {
        var reads = new PausedInserts();
        var repository = await CreateRepository(reads);
        var firstBody = Profile("first");
        var secondBody = Profile("second");
        var first = Controller(repository).PutProfile("tvos", "tvos-7", "AppleTV14,1", firstBody, CancellationToken.None);
        var second = Controller(repository).PutProfile("tvos", "tvos-7", "AppleTV14,1", secondBody, CancellationToken.None);
        Console.WriteLine($"Overlapping initial profile inserts before release: {reads.Count}");
        reads.Release();
        Assert.InRange(reads.Count, 1, 2);

        var errors = await Task.WhenAll(new[] { first, second }.Select(task => Record.ExceptionAsync(async () => { await task; })));
        Assert.All(errors, error => Assert.Null(error));
        Assert.All(await Task.WhenAll(first, second), result => Assert.IsType<NoContentResult>(result));
        var stored = await repository.GetAsync("tvos", "tvos-7", "AppleTV14,1", CancellationToken.None);
        Assert.NotNull(stored);
        var expected = stored.DeviceDescription == "first" ? firstBody : secondBody;
        Assert.Equal(expected.GetProperty("device").GetProperty("description").GetString(), stored.DeviceDescription);
        Assert.Equal(expected.GetRawText(), stored.DocumentJson);
        Assert.Single(await repository.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsOversizedHttpBodiesBeforeBindingAndStillStoresValidProfile(bool chunked)
    {
        var repository = await CreateRepository();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(repository).AddSingleton(coordinator).AddSingleton<DeviceQualityProfileValidator>()
            .AddSingleton<DeviceProfileUploadResourceFilter>();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(DeviceProfilesController).Assembly);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
        };
        var body = Profile("valid").GetRawText();
        var route = "/AetherAnalysis/v1/device-profiles/tvos/tvos-7/AppleTV14,1";

        // Leading whitespace is part of the HTTP body even though GetRawText
        // removes it. The old post-binding check admitted this large request.
        const int contractLimitBytes = 256 * 1024;
        using var request = new HttpRequestMessage(HttpMethod.Put, route)
        {
            Content = new StringContent(new string(' ', contractLimitBytes) + body, Encoding.UTF8, "application/json")
        };
        if (chunked)
        {
            request.Headers.TransferEncodingChunked = true;
            request.Content.Headers.ContentLength = null;
        }
        using var oversized = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Empty(await repository.ListAsync(CancellationToken.None));
        var boundaryBody = new string(' ', contractLimitBytes - Encoding.UTF8.GetByteCount(body)) + body;
        using var valid = await client.PutAsync(route, new StringContent(boundaryBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
        Assert.Single(await repository.ListAsync(CancellationToken.None));
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "synthetic-test")], "test")), "test")));
    }
}
