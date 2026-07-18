using Jellyfin.Plugin.AetherAnalysis.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

/// <summary>Creates the plugin context for the repository-pinned EF migration tool.</summary>
public sealed class DesignTimeContextFactory : IDesignTimeDbContextFactory<AnalysisDbContext>
{
    /// <inheritdoc />
    public AnalysisDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AnalysisDbContext>()
            .UseSqlite("Data Source=aether-analysis-design.sqlite")
            .Options;
        return new AnalysisDbContext(options);
    }
}
