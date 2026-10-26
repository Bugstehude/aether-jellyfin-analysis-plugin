using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Api;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class PresetsControllerTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"aether-preset-api-{Guid.NewGuid():N}.sqlite");
    private readonly AnalysisWriteCoordinator coordinator = new();
    private readonly Guid userId = Guid.NewGuid();
    private const string Snapshot = """{"knobs":{"grain":0.4},"rimColor":[1,0.5,0]}""";

    private sealed class ContextFactory(DbContextOptions<AnalysisDbContext> options)
        : IDbContextFactory<AnalysisDbContext>
    {
        public AnalysisDbContext CreateDbContext() => new(options);
    }

    public void Dispose()
    {
        coordinator.Dispose();
        File.Delete(path);
    }

    private async Task<ManualPresetRepository> CreateRepository(int count, DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AnalysisDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new ContextFactory(options);
        await using var context = factory.CreateDbContext();
        await context.Database.MigrateAsync();
        context.ManualPresets.AddRange(Enumerable.Range(0, count).Select(index => new ManualPreset
        {
            UserId = userId,
            Id = $"preset-{index}",
            Name = "original",
            SnapshotJson = Snapshot
        }));
        await context.SaveChangesAsync();
        return new ManualPresetRepository(interceptor is null ? factory : new ContextFactory(
            new DbContextOptionsBuilder<AnalysisDbContext>(options).AddInterceptors(interceptor).Options));
    }

    private PresetsController CreateController(ManualPresetRepository repository, Guid owner)
    {
        using var services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(coordinator)
            .AddSingleton<ILogger<PresetsController>>(NullLogger<PresetsController>.Instance)
            .BuildServiceProvider();
        var controller = ActivatorUtilities.CreateInstance<PresetsController>(services);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("Jellyfin-UserId", owner.ToString())], "test"))
            }
        };
        return controller;
    }

    private static JsonElement Body(string name = "updated") =>
        JsonSerializer.SerializeToElement(new { name, snapshot = JsonSerializer.Deserialize<JsonElement>(Snapshot) });

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
                && command.CommandText.Contains("manual_presets", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref count);
                await release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task ConcurrentCreatesRespectTheQuota()
    {
        var inserts = new PausedInserts();
        var repository = await CreateRepository(ManualPresetRepository.MaxPresetsPerUser - 1, inserts);
        var first = CreateController(repository, userId).PutPreset("new-a", Body(), CancellationToken.None);
        var second = CreateController(repository, userId).PutPreset("new-b", Body(), CancellationToken.None);
        Console.WriteLine($"Overlapping preset inserts before release: {inserts.Count}");
        inserts.Release();
        Assert.InRange(inserts.Count, 1, 2);

        var results = await Task.WhenAll(first, second);
        var count = await repository.CountAsync(userId, CancellationToken.None);
        Console.WriteLine($"Stored presets after concurrent requests: {count}");
        Assert.Single(results.OfType<NoContentResult>());
        Assert.Single(results.OfType<ObjectResult>(), result => result.StatusCode == StatusCodes.Status507InsufficientStorage);
        Assert.Equal(ManualPresetRepository.MaxPresetsPerUser, count);
    }

    [Fact]
    public async Task UpdatingAtTheQuotaAndUserIsolationStillWork()
    {
        var repository = await CreateRepository(ManualPresetRepository.MaxPresetsPerUser);
        var controller = CreateController(repository, userId);
        Assert.IsType<NoContentResult>(await controller.PutPreset("preset-0", Body(), CancellationToken.None));
        var rejected = Assert.IsType<ObjectResult>(await controller.PutPreset("new", Body(), CancellationToken.None));
        Assert.Equal(StatusCodes.Status507InsufficientStorage, rejected.StatusCode);
        var stored = await repository.ListAsync(userId, CancellationToken.None);
        Assert.Equal(ManualPresetRepository.MaxPresetsPerUser, stored.Count);
        Assert.Equal("updated", Assert.Single(stored, value => value.Id == "preset-0").Name);

        var otherUser = Guid.NewGuid();
        var otherController = CreateController(repository, otherUser);
        Assert.IsType<NoContentResult>(await otherController.DeletePreset("preset-0", CancellationToken.None));
        Assert.Equal(ManualPresetRepository.MaxPresetsPerUser, await repository.CountAsync(userId, CancellationToken.None));
        Assert.Equal(0, await repository.CountAsync(otherUser, CancellationToken.None));
    }
}
