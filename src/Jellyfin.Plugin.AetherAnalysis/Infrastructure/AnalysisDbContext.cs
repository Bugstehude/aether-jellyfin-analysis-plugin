using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Plugin-owned EF Core context. It never touches Jellyfin's main database.</summary>
public sealed class AnalysisDbContext(DbContextOptions<AnalysisDbContext> options) : DbContext(options)
{
    /// <summary>Gets stored analysis records.</summary>
    public DbSet<AnalysisRecord> Analyses => Set<AnalysisRecord>();

    /// <summary>Gets the singleton maintenance state.</summary>
    public DbSet<AnalysisMaintenanceState> MaintenanceStates => Set<AnalysisMaintenanceState>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var record = modelBuilder.Entity<AnalysisRecord>();
        record.ToTable("analysis_records");
        record.HasKey(value => new
        {
            value.ItemId,
            value.MediaSourceId,
            value.AlgorithmId,
            value.AlgorithmVersion
        });
        record.Property(value => value.MediaSourceId).HasMaxLength(128);
        record.Property(value => value.AlgorithmId).HasMaxLength(64);
        record.Property(value => value.AlgorithmVersion).HasMaxLength(32);
        record.Property(value => value.MediaFingerprint).HasMaxLength(71);
        record.Property(value => value.FingerprintQuality).HasMaxLength(16);
        record.Property(value => value.Etag).HasMaxLength(96);
        record.HasIndex(value => value.LastAccessedAtUnixTimeMilliseconds);
        record.HasIndex(value => value.StoredAtUnixTimeMilliseconds);

        var maintenance = modelBuilder.Entity<AnalysisMaintenanceState>();
        maintenance.ToTable("analysis_maintenance_state");
        maintenance.HasKey(value => value.Id);
        maintenance.Property(value => value.Id).ValueGeneratedNever();
        maintenance.Property(value => value.LastReason).HasMaxLength(32);
    }
}
