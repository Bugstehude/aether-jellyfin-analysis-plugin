using System.ComponentModel.DataAnnotations.Schema;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Persistent summary of the most recent successful maintenance run.</summary>
public sealed class AnalysisMaintenanceState
{
    /// <summary>Gets the singleton row id.</summary>
    public int Id { get; init; } = 1;

    /// <summary>Gets or sets the completion timestamp as a SQLite-sortable integer.</summary>
    public long? LastCompletedAtUnixTimeMilliseconds { get; set; }

    /// <summary>Gets or sets the completion timestamp.</summary>
    [NotMapped]
    public DateTimeOffset? LastCompletedAt
    {
        get => LastCompletedAtUnixTimeMilliseconds.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(LastCompletedAtUnixTimeMilliseconds.Value)
            : null;
        set => LastCompletedAtUnixTimeMilliseconds = value?.ToUnixTimeMilliseconds();
    }

    /// <summary>Gets or sets the trigger for the latest run.</summary>
    public required string LastReason { get; set; }

    /// <summary>Gets or sets the number of retention deletions.</summary>
    public int LastRetentionDeletedRecords { get; set; }

    /// <summary>Gets or sets the number of capacity deletions.</summary>
    public int LastCapacityDeletedRecords { get; set; }

    /// <summary>Gets or sets the total compressed bytes deleted.</summary>
    public long LastDeletedBytes { get; set; }
}
