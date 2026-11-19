using DbdMaxing.Core.Models;

namespace DbdMaxing.App.ViewModels;

public sealed record BuildPerkViewModel(string Name, string? IconSource, string? Description);

public sealed class BuildViewModel
{
    public BuildViewModel(Build build, int rank, double bestScore, Func<Perk, string?> iconFor)
    {
        Rank = $"#{rank}";
        Name = build.Name.ToUpperInvariant();
        Score = build.Score.ToString("0.0");
        ScorePercent = bestScore <= 0 ? 0 : Math.Clamp(build.Score / bestScore * 100, 5, 100);
        Perks = build.Perks.Select(p => new BuildPerkViewModel(p.Name, iconFor(p), p.Description)).ToList();
        Reasons = build.Reasons;
    }

    public string Rank { get; }
    public string Name { get; }
    public string Score { get; }
    public double ScorePercent { get; }
    public IReadOnlyList<BuildPerkViewModel> Perks { get; }
    public IReadOnlyList<string> Reasons { get; }
}
