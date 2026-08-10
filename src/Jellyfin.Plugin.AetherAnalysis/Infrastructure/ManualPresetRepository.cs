using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Je-Nutzer-Vorrat der Regler-Panel-Presets (siehe <see cref="ManualPreset"/>).</summary>
public sealed class ManualPresetRepository(IDbContextFactory<AnalysisDbContext> contextFactory)
{
    /// <summary>Obergrenze je Nutzer — schützt die Plugin-Datenbank vor unbegrenztem Wachstum, ohne im Alltag zu stören.</summary>
    public const int MaxPresetsPerUser = 500;

    /// <summary>Listet die Presets eines Nutzers, neueste zuerst.</summary>
    public async Task<IReadOnlyList<ManualPreset>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ManualPresets
            .AsNoTracking()
            .Where(value => value.UserId == userId)
            .OrderByDescending(value => value.UpdatedAtUnixTimeMilliseconds)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Zählt die Presets eines Nutzers, ohne sie zu laden.</summary>
    public async Task<int> CountAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ManualPresets
            .Where(value => value.UserId == userId)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Legt ein Preset ab oder ersetzt es; liefert, ob es NEU angelegt wurde.</summary>
    public async Task<bool> UpsertAsync(
        Guid userId,
        string id,
        string name,
        string snapshotJson,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.ManualPresets
            .FirstOrDefaultAsync(value => value.UserId == userId && value.Id == id, cancellationToken)
            .ConfigureAwait(false);

        var created = existing is null;
        if (existing is null)
        {
            context.ManualPresets.Add(new ManualPreset
            {
                UserId = userId,
                Id = id,
                Name = name,
                SnapshotJson = snapshotJson,
                UpdatedAtUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
        else
        {
            existing.Name = name;
            existing.SnapshotJson = snapshotJson;
            existing.UpdatedAtUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <summary>Löscht ein Preset; idempotent.</summary>
    public async Task<bool> DeleteAsync(Guid userId, string id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await context.ManualPresets
            .Where(value => value.UserId == userId && value.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return deleted > 0;
    }
}
