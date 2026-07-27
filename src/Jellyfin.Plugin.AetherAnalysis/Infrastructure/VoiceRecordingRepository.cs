using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Kurzbeschreibung einer abgelegten Zeile, ohne die Audiodaten zu laden.</summary>
public sealed record VoiceRecordingSummary(string LineId, string ContentType, int Bytes, DateTimeOffset UpdatedAt);

/// <summary>Serverweiter Vorrat der eingesprochenen Zeilen (siehe <see cref="VoiceRecording"/>).</summary>
public sealed class VoiceRecordingRepository(IDbContextFactory<AnalysisDbContext> contextFactory)
{
    /// <summary>
    /// Obergrenze für den gesamten Vorrat. Achtzehn Zeilen à wenige hundert
    /// Kilobyte sind das Übliche; die Grenze fängt den Fall ab, dass jemand
    /// unkomprimierte Studioaufnahmen ablegt, und hält die Plugin-Datenbank
    /// klein genug, um sie mitzusichern.
    /// </summary>
    public const int MaxTotalBytes = 64 * 1024 * 1024;

    /// <summary>Obergrenze je Zeile.</summary>
    public const int MaxRecordingBytes = 8 * 1024 * 1024;

    /// <summary>Listet den Vorrat, ohne die Audiodaten zu laden.</summary>
    public async Task<IReadOnlyList<VoiceRecordingSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.VoiceRecordings
            .AsNoTracking()
            .OrderBy(value => value.LineId)
            .Select(value => new
            {
                value.LineId,
                value.ContentType,
                value.ContentLength,
                value.UpdatedAtUnixTimeMilliseconds
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new VoiceRecordingSummary(
                row.LineId,
                row.ContentType,
                row.ContentLength,
                DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUnixTimeMilliseconds)))
            .ToList();
    }

    /// <summary>Holt eine Aufnahme, oder null.</summary>
    public async Task<VoiceRecording?> GetAsync(string lineId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.VoiceRecordings
            .AsNoTracking()
            .FirstOrDefaultAsync(value => value.LineId == lineId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Summe aller abgelegten Bytes, ohne die Daten zu laden.</summary>
    public async Task<long> TotalBytesAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.VoiceRecordings
            .SumAsync(value => (long)value.ContentLength, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Legt eine Aufnahme ab oder ersetzt sie.</summary>
    public async Task UpsertAsync(
        string lineId,
        string contentType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.VoiceRecordings
            .FirstOrDefaultAsync(value => value.LineId == lineId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            context.VoiceRecordings.Add(new VoiceRecording
            {
                LineId = lineId,
                ContentType = contentType,
                Content = content,
                ContentLength = content.Length,
                UpdatedAtUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
        else
        {
            existing.ContentType = contentType;
            existing.Content = content;
            existing.ContentLength = content.Length;
            existing.UpdatedAtUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Löscht eine Aufnahme; idempotent.</summary>
    public async Task<bool> DeleteAsync(string lineId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await context.VoiceRecordings
            .Where(value => value.LineId == lineId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return deleted > 0;
    }
}
