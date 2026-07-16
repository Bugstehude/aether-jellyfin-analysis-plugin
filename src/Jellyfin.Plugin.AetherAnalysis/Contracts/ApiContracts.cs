namespace Jellyfin.Plugin.AetherAnalysis.Contracts;

/// <summary>Algorithm identity used by bounded batch operations.</summary>
public sealed record AlgorithmSelection(string Id, string Version);

/// <summary>One Jellyfin item and concrete media source.</summary>
public sealed record ItemSelection(Guid ItemId, string MediaSourceId);

/// <summary>Explicit bounded analysis selection.</summary>
public sealed record BatchSelection(AlgorithmSelection Algorithm, IReadOnlyList<ItemSelection> Items);
