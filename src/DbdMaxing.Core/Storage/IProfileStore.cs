using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Storage;

public interface IProfileStore
{
    /// <summary>Loads owned perks for a Steam account; returns an empty set if none saved.</summary>
    OwnedPerkSet Load(string steamId64);

    void Save(string steamId64, OwnedPerkSet perks);
}
