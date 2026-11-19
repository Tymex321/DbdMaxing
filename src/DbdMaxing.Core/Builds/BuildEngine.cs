using System.Globalization;
using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Builds;

/// <summary>
/// Scores owned perks with meta.json and searches 4-perk combinations for the best builds.
/// The search prunes to the top-K perks (plus their synergy partners) and then evaluates every combination,
/// so it stays exhaustive over the candidates that matter while handling a full catalog in a few milliseconds.
/// Results are deterministic: every tie is broken by perk id (ordinal).
/// </summary>
public sealed class BuildEngine : IBuildEngine
{
    public const string FallbackName = "Balanced";

    /// <summary>A family of tags a well-rounded build wants at least one of.</summary>
    private sealed record CoverageGroup(Role Role, string Label, string[] Tags);

    private static readonly CoverageGroup[] CoverageGroups =
    {
        new(Role.Survivor, "chase", new[] { "chase", "exhaustion" }),
        new(Role.Survivor, "healing", new[] { "healing", "endurance" }),
        new(Role.Survivor, "info", new[] { "info", "aura" }),
        new(Role.Killer, "gen slowdown", new[] { "gen-slowdown", "slowdown", "regression" }),
        new(Role.Killer, "chase & info", new[] { "chase", "info", "aura", "tracking" }),
    };

    private readonly MetaData _meta;
    private readonly BuildEngineOptions _options;
    private readonly Dictionary<(string, string), List<SynergyRule>> _synergies = new();
    private readonly Dictionary<string, List<string>> _synergyPartners = new(StringComparer.Ordinal);
    private readonly (string Tag, TagLimit Limit)[] _tagLimits;

    public BuildEngine(MetaData meta) : this(meta, null) { }

    public BuildEngine(MetaData meta, BuildEngineOptions? options)
    {
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _options = options ?? BuildEngineOptions.Default;
        if (_options.BuildSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "BuildSize must be >= 1.");

        foreach (var rule in meta.Synergies ?? new())
        {
            if (string.IsNullOrEmpty(rule.A) || string.IsNullOrEmpty(rule.B) || rule.A == rule.B) continue;
            var key = PairKey(rule.A, rule.B);
            if (!_synergies.TryGetValue(key, out var list)) _synergies[key] = list = new();
            list.Add(rule);
            if (rule.Bonus > 0)
            {
                AddPartner(rule.A, rule.B);
                AddPartner(rule.B, rule.A);
            }
        }

        _tagLimits = (meta.TagLimits ?? new())
            .Where(l => !string.IsNullOrWhiteSpace(l.Tag))
            .Select(l => (NormalizeTag(l.Tag), l))
            .ToArray();
    }

    public IReadOnlyList<Build> Recommend(Role role, IEnumerable<Perk> owned, int count = 5, string? killerId = null)
    {
        ArgumentNullException.ThrowIfNull(owned);
        if (count <= 0) return Array.Empty<Build>();
        if (role != Role.Killer) killerId = null;

        // 1. Owned perks of this role, deduplicated, ranked individually (score desc, id asc).
        var all = owned
            .Where(p => p is not null && p.Role == role)
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(p => new Scored(p, PerkScore(p.Id, killerId), KillerBonus(p.Id, killerId)))
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Perk.Id, StringComparer.Ordinal)
            .ToList();
        if (all.Count == 0) return Array.Empty<Build>();

        // 2. Prune to the top K plus synergy partners of that set, then re-sort so indices follow rank.
        var candidates = SelectCandidates(all);
        var ctx = new SearchContext(this, role, candidates);

        // 3. Exhaustive search over the candidates.
        var size = Math.Min(_options.BuildSize, candidates.Count);
        var combos = new List<Combo>();
        var idx = new int[size];
        Enumerate(0, 0);

        void Enumerate(int start, int depth)
        {
            if (depth == size)
            {
                combos.Add(new Combo((int[])idx.Clone(), ctx.Score(idx)));
                return;
            }
            for (var i = start; i <= candidates.Count - (size - depth); i++)
            {
                idx[depth] = i;
                Enumerate(i + 1, depth + 1);
            }
        }

        combos.Sort(CompareCombos);

        // 4. Greedy diverse pick: each build must differ from all chosen ones by MinPerkDifference perks.
        var maxOverlap = size - _options.MinPerkDifference;
        var chosen = new List<Combo>();
        foreach (var combo in combos)
        {
            if (chosen.Count >= count) break;
            if (chosen.All(c => Overlap(c.Indices, combo.Indices) <= maxOverlap))
                chosen.Add(combo);
        }

        return chosen.Select(c => ctx.ToBuild(c, killerId)).ToList();
    }

    // ---------- scoring primitives ----------

    private double PerkScore(string id, string? killerId) =>
        (_meta.Perks.TryGetValue(id, out var pm) ? pm.Score : _options.DefaultPerkScore) + KillerBonus(id, killerId);

    private double KillerBonus(string id, string? killerId)
    {
        if (killerId is null || !_meta.Perks.TryGetValue(id, out var pm) || pm.KillerBonus is null) return 0;
        if (pm.KillerBonus.TryGetValue(killerId, out var b)) return b;
        foreach (var kv in pm.KillerBonus)
            if (string.Equals(kv.Key, killerId, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return 0;
    }

    private List<Scored> SelectCandidates(List<Scored> ranked)
    {
        if (ranked.Count <= _options.TopK) return ranked;

        var top = ranked.Take(_options.TopK).ToList();
        var inTop = top.Select(s => s.Perk.Id).ToHashSet(StringComparer.Ordinal);
        var room = Math.Max(0, _options.MaxCandidates - top.Count);
        if (room == 0) return top;

        // Rest of the owned perks that have a positive synergy with something in the top set, best potential first.
        var extras = ranked.Skip(_options.TopK)
            .Select(s => (s, best: BestSynergyInto(s.Perk.Id, inTop)))
            .Where(x => x.best > 0)
            .OrderByDescending(x => x.s.Score + x.best)
            .ThenBy(x => x.s.Perk.Id, StringComparer.Ordinal)
            .Take(room)
            .Select(x => x.s);

        return top.Concat(extras)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Perk.Id, StringComparer.Ordinal)
            .ToList();
    }

    private double BestSynergyInto(string id, HashSet<string> set)
    {
        if (!_synergyPartners.TryGetValue(id, out var partners)) return 0;
        var best = 0.0;
        foreach (var p in partners)
            if (set.Contains(p)) best = Math.Max(best, SynergyBonus(id, p));
        return best;
    }

    private double SynergyBonus(string a, string b) =>
        _synergies.TryGetValue(PairKey(a, b), out var rules) ? rules.Sum(r => r.Bonus) : 0;

    private void AddPartner(string a, string b)
    {
        if (!_synergyPartners.TryGetValue(a, out var list)) _synergyPartners[a] = list = new();
        if (!list.Contains(b)) list.Add(b);
    }

    private static (string, string) PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    internal static string NormalizeTag(string tag) =>
        tag.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');

    private static int CompareCombos(Combo x, Combo y)
    {
        var c = y.Score.CompareTo(x.Score);
        if (c != 0) return c;
        // Candidates are sorted by (score desc, id asc), so comparing index tuples is a stable id-based tie-break.
        for (var i = 0; i < x.Indices.Length; i++)
        {
            c = x.Indices[i].CompareTo(y.Indices[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static int Overlap(int[] a, int[] b)
    {
        var n = 0;
        foreach (var x in a)
            if (Array.IndexOf(b, x) >= 0) n++;
        return n;
    }

    private static string Fmt(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    private static string Signed(double v) => v.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

    private static string CountWord(int n) => n switch
    {
        1 => "One", 2 => "Two", 3 => "Three", 4 => "Four",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    private sealed record Scored(Perk Perk, double Score, double KillerBonus);

    private sealed record Combo(int[] Indices, double Score);

    /// <summary>Per-call precomputed lookup tables over the candidate list, so scoring a combo is a few array reads.</summary>
    private sealed class SearchContext
    {
        private readonly BuildEngine _engine;
        private readonly Role _role;
        private readonly List<Scored> _candidates;
        private readonly double[] _scores;
        private readonly double[,] _synergy;
        private readonly bool[][] _hasLimitTag;      // [limit][perk]
        private readonly CoverageGroup[] _groups;
        private readonly bool[][] _inGroup;          // [group][perk]
        private readonly HashSet<string>[] _tags;

        public SearchContext(BuildEngine engine, Role role, List<Scored> candidates)
        {
            _engine = engine;
            _role = role;
            _candidates = candidates;
            var n = candidates.Count;
            _scores = candidates.Select(c => c.Score).ToArray();
            _tags = candidates
                .Select(c => (c.Perk.Tags ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(NormalizeTag).ToHashSet(StringComparer.Ordinal))
                .ToArray();

            _synergy = new double[n, n];
            for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                _synergy[i, j] = _synergy[j, i] = engine.SynergyBonus(candidates[i].Perk.Id, candidates[j].Perk.Id);

            _hasLimitTag = engine._tagLimits
                .Select(l => _tags.Select(t => t.Contains(l.Tag)).ToArray())
                .ToArray();

            _groups = CoverageGroups.Where(g => g.Role == role).ToArray();
            _inGroup = _groups
                .Select(g => _tags.Select(t => g.Tags.Any(t.Contains)).ToArray())
                .ToArray();
        }

        public double Score(int[] idx)
        {
            var o = _engine._options;
            double total = 0;
            for (var a = 0; a < idx.Length; a++)
            {
                total += _scores[idx[a]];
                for (var b = a + 1; b < idx.Length; b++)
                    total += _synergy[idx[a], idx[b]];
            }

            for (var l = 0; l < _hasLimitTag.Length; l++)
            {
                var c = CountIn(_hasLimitTag[l], idx);
                var limit = _engine._tagLimits[l].Limit;
                if (c > limit.Max) total -= (c - limit.Max) * limit.PenaltyPerExtra;
            }

            for (var g = 0; g < _inGroup.Length; g++)
                if (CountIn(_inGroup[g], idx) > 0) total += o.CoverageBonusPerGroup;

            return total;
        }

        private static int CountIn(bool[] flags, int[] idx)
        {
            var c = 0;
            foreach (var i in idx)
                if (flags[i]) c++;
            return c;
        }

        public Build ToBuild(Combo combo, string? killerId)
        {
            // Indices are ascending and candidates are sorted by score desc / id asc, so this is already rank order.
            var perks = combo.Indices.Select(i => _candidates[i].Perk).ToList();
            return new Build(_role, perks, combo.Score, Reasons(combo.Indices, killerId), Name(combo.Indices), killerId);
        }

        private List<string> Reasons(int[] idx, string? killerId)
        {
            var o = _engine._options;
            var synergies = new List<(double Bonus, string Text)>();
            var warnings = new List<(double Weight, string Text)>();

            for (var a = 0; a < idx.Length; a++)
            for (var b = a + 1; b < idx.Length; b++)
            {
                var pa = _candidates[idx[a]].Perk;
                var pb = _candidates[idx[b]].Perk;
                if (!_engine._synergies.TryGetValue(PairKey(pa.Id, pb.Id), out var rules)) continue;
                foreach (var r in rules)
                {
                    var why = string.IsNullOrWhiteSpace(r.Reason) ? (r.Bonus >= 0 ? "synergy" : "anti-synergy") : r.Reason;
                    var text = $"{pa.Name} + {pb.Name}: {why} ({Signed(r.Bonus)})";
                    if (r.Bonus >= 0) synergies.Add((r.Bonus, text));
                    else warnings.Add((-r.Bonus, text));
                }
            }

            for (var l = 0; l < _hasLimitTag.Length; l++)
            {
                var c = CountIn(_hasLimitTag[l], idx);
                var (tag, limit) = _engine._tagLimits[l];
                if (c <= limit.Max) continue;
                var label = tag.Replace('-', ' ');
                var penalty = (c - limit.Max) * limit.PenaltyPerExtra;
                var text = limit.Max switch
                {
                    0 => $"{CountWord(c)} {label} perk{(c == 1 ? "" : "s")}: none recommended ({Signed(-penalty)})",
                    1 => $"{CountWord(c)} {label} perks: only one can be active ({Signed(-penalty)})",
                    _ => $"{CountWord(c)} {label} perks: more than {limit.Max} is overkill ({Signed(-penalty)})",
                };
                warnings.Add((penalty, text));
            }

            var killerNotes = new List<string>();
            if (killerId is not null)
                foreach (var i in idx)
                {
                    var s = _candidates[i];
                    if (s.KillerBonus > 0) killerNotes.Add($"{s.Perk.Name} suits {killerId} ({Signed(s.KillerBonus)})");
                    else if (s.KillerBonus < 0) killerNotes.Add($"{s.Perk.Name} is weaker on {killerId} ({Signed(s.KillerBonus)})");
                }

            var strong = new List<string>();
            var covered = new List<string>();
            for (var g = 0; g < _groups.Length; g++)
            {
                var c = CountIn(_inGroup[g], idx);
                if (c > 0) covered.Add(_groups[g].Label);
                if (c >= o.StrongGroupMinPerks) strong.Add($"Strong {_groups[g].Label} ({c} perks)");
            }

            var topTier = idx
                .Where(i => _scores[i] >= o.TopTierThreshold)
                .Select(i => $"Top-tier: {_candidates[i].Perk.Name} ({Fmt(_scores[i])})");

            var reasons = new List<string>();
            reasons.AddRange(synergies.OrderByDescending(s => s.Bonus).Select(s => s.Text));
            reasons.AddRange(warnings.OrderByDescending(w => w.Weight).Select(w => w.Text));
            reasons.AddRange(killerNotes);
            reasons.AddRange(strong);
            reasons.AddRange(topTier);
            if (covered.Count >= 2 && covered.Count == _groups.Length)
                reasons.Add("Covers " + JoinAnd(covered));

            return reasons.Distinct().Take(o.MaxReasons).ToList();
        }

        private static string JoinAnd(List<string> items) =>
            items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

        private string Name(int[] idx)
        {
            string? best = null;
            int bestPerks = 0, bestTags = 0;
            foreach (var arch in _engine._meta.Archetypes ?? new())
            {
                if (arch.Role != _role || arch.Tags is null || arch.Tags.Count == 0) continue;
                var archTags = arch.Tags.Select(NormalizeTag).ToHashSet(StringComparer.Ordinal);
                var perks = idx.Count(i => _tags[i].Overlaps(archTags));
                var tags = archTags.Count(t => idx.Any(i => _tags[i].Contains(t)));
                // Strictly greater keeps the first-listed archetype on ties, so naming is stable.
                if (perks > bestPerks || (perks == bestPerks && tags > bestTags))
                {
                    best = arch.Name;
                    bestPerks = perks;
                    bestTags = tags;
                }
            }

            var minMatches = Math.Min(_engine._options.MinArchetypeMatches, idx.Length);
            return best is not null && bestPerks >= minMatches && !string.IsNullOrWhiteSpace(best) ? best : FallbackName;
        }
    }
}
