using System.ComponentModel.DataAnnotations.Schema;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>EF Core entity containing indexed identity and a compressed canonical document.</summary>
public sealed class AnalysisRecord
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the Jellyfin media-source id.</summary>
    public required string MediaSourceId { get; set; }

    /// <summary>Gets or sets the analysis algorithm id.</summary>
    public required string AlgorithmId { get; set; }

    /// <summary>Gets or sets the analysis algorithm version.</summary>
    public required string AlgorithmVersion { get; set; }

    /// <summary>Gets or sets the media fingerprint.</summary>
    public required string MediaFingerprint { get; set; }

    /// <summary>Gets or sets the fingerprint quality.</summary>
    public required string FingerprintQuality { get; set; }

    /// <summary>Gets or sets the strong master-document ETag.</summary>
    public required string Etag { get; set; }

    /// <summary>Gets or sets the Brotli-compressed canonical master document.</summary>
    public required byte[] CompressedDocument { get; set; }

    /// <summary>Gets or sets the uncompressed byte count.</summary>
    public int UncompressedBytes { get; set; }

    /// <summary>Gets or sets the frame count.</summary>
    public int FrameCount { get; set; }

    /// <summary>Gets or sets the source sampling interval.</summary>
    public int SourceIntervalMs { get; set; }

    /// <summary>Gets or sets the producer creation time.</summary>
    [NotMapped]
    public DateTimeOffset CreatedAt
    {
        get => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtUnixTimeMilliseconds);
        set => CreatedAtUnixTimeMilliseconds = value.ToUnixTimeMilliseconds();
    }

    /// <summary>Gets or sets the producer creation time as a SQLite-sortable integer.</summary>
    public long CreatedAtUnixTimeMilliseconds { get; set; }

    /// <summary>Gets or sets the server storage time.</summary>
    [NotMapped]
    public DateTimeOffset StoredAt
    {
        get => DateTimeOffset.FromUnixTimeMilliseconds(StoredAtUnixTimeMilliseconds);
        set => StoredAtUnixTimeMilliseconds = value.ToUnixTimeMilliseconds();
    }

    /// <summary>Gets or sets the storage time as a SQLite-sortable integer.</summary>
    public long StoredAtUnixTimeMilliseconds { get; set; }

    /// <summary>Gets or sets the last successful read time.</summary>
    [NotMapped]
    public DateTimeOffset LastAccessedAt
    {
        get => DateTimeOffset.FromUnixTimeMilliseconds(LastAccessedAtUnixTimeMilliseconds);
        set => LastAccessedAtUnixTimeMilliseconds = value.ToUnixTimeMilliseconds();
    }

    /// <summary>Gets or sets the access time as a SQLite-sortable integer.</summary>
    public long LastAccessedAtUnixTimeMilliseconds { get; set; }
}
