using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Zusammenfassung eines abgelegten Geräteprofils, ohne den Dokument-Text zu laden.</summary>
public sealed record DeviceQualityProfileSummary(
    string Client,
    string Ladder,
    string DeviceIdentifier,
    string DeviceDescription,
    string Mode,
    string FinishedAt,
    int EntryCount,
    int? FallbackStartStepIndex,
    DateTimeOffset StoredAt);

/// <summary>Serverweiter Katalog der vermessenen Geräteprofile (siehe <see cref="DeviceQualityProfile"/>).</summary>
public sealed class DeviceQualityProfileRepository(IDbContextFactory<AnalysisDbContext> contextFactory)
{
    /// <summary>Listet den Katalog, neueste Ablage zuerst, ohne die Dokument-Texte zu laden.</summary>
    public async Task<IReadOnlyList<DeviceQualityProfileSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.DeviceQualityProfiles
            .AsNoTracking()
            .OrderByDescending(value => value.StoredAtUnixTimeMilliseconds)
            .Select(value => new
            {
                value.Client,
                value.Ladder,
                value.DeviceIdentifier,
                value.DeviceDescription,
                value.Mode,
                value.FinishedAt,
                value.EntryCount,
                value.FallbackStartStepIndex,
                value.StoredAtUnixTimeMilliseconds
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new DeviceQualityProfileSummary(
                row.Client,
                row.Ladder,
                row.DeviceIdentifier,
                row.DeviceDescription,
                row.Mode,
                row.FinishedAt,
                row.EntryCount,
                row.FallbackStartStepIndex,
                DateTimeOffset.FromUnixTimeMilliseconds(row.StoredAtUnixTimeMilliseconds)))
            .ToList();
    }

    /// <summary>Holt ein Profil samt Dokument-Text, oder null.</summary>
    public async Task<DeviceQualityProfile?> GetAsync(
        string client,
        string ladder,
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.DeviceQualityProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                value => value.Client == client && value.Ladder == ladder && value.DeviceIdentifier == deviceIdentifier,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Legt ein Profil ab oder ersetzt es. Last-Write-Wins, keine Historie (siehe Klassenkommentar).</summary>
    public async Task UpsertAsync(
        string client,
        string ladder,
        string deviceIdentifier,
        string deviceDescription,
        string mode,
        string finishedAt,
        int entryCount,
        int? fallbackStartStepIndex,
        string documentJson,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.DeviceQualityProfiles
            .FirstOrDefaultAsync(
                value => value.Client == client && value.Ladder == ladder && value.DeviceIdentifier == deviceIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        var storedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (existing is null)
        {
            context.DeviceQualityProfiles.Add(new DeviceQualityProfile
            {
                Client = client,
                Ladder = ladder,
                DeviceIdentifier = deviceIdentifier,
                DeviceDescription = deviceDescription,
                Mode = mode,
                FinishedAt = finishedAt,
                EntryCount = entryCount,
                FallbackStartStepIndex = fallbackStartStepIndex,
                DocumentJson = documentJson,
                StoredAtUnixTimeMilliseconds = storedAt
            });
        }
        else
        {
            existing.DeviceDescription = deviceDescription;
            existing.Mode = mode;
            existing.FinishedAt = finishedAt;
            existing.EntryCount = entryCount;
            existing.FallbackStartStepIndex = fallbackStartStepIndex;
            existing.DocumentJson = documentJson;
            existing.StoredAtUnixTimeMilliseconds = storedAt;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
