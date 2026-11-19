using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Data;

/// <summary>All perks and characters in the game, plus local icon files for the vision scanner.</summary>
public interface IPerkCatalog
{
    IReadOnlyList<Perk> Perks { get; }
    IReadOnlyList<GameCharacter> Characters { get; }

    /// <summary>Loads from the local cache, refreshing from the network when stale; falls back to the bundled perks.json.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Downloads any missing perk icons into <see cref="AppPaths.IconDir"/> as "{perkId}.png".</summary>
    Task EnsureIconsAsync(IProgress<double>? progress = null, CancellationToken ct = default);

    Perk? Find(string perkId);

    /// <summary>Local icon file path for a perk, or null if not downloaded.</summary>
    string? GetIconPath(string perkId);
}
