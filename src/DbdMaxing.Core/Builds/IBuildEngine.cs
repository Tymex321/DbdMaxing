using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Builds;

public interface IBuildEngine
{
    /// <summary>
    /// Returns up to <paramref name="count"/> distinct 4-perk builds made only from <paramref name="owned"/> perks of
    /// <paramref name="role"/>, best first. <paramref name="killerId"/> tunes killer builds for a specific killer.
    /// </summary>
    IReadOnlyList<Build> Recommend(Role role, IEnumerable<Perk> owned, int count = 5, string? killerId = null);
}
