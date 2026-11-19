using System.Text.RegularExpressions;
using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Data;

/// <summary>
/// Rule-based tag inference from a perk's name and (cleaned) description.
/// Tags are deliberately coarse; meta.json and the build engine only need rough categories.
/// </summary>
public static class PerkTagger
{
    private sealed record Rule(string Tag, Role? Role, Regex Pattern, Regex? Unless = null);

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static Rule R(string tag, Role? role, string pattern, string? unless = null) =>
        new(tag, role, new Regex(pattern, Opts), unless is null ? null : new Regex(unless, Opts));

    // Patterns run against "name\ndescription".
    private static readonly Rule[] Rules =
    {
        // Name-prefix families.
        R("hex", null, @"^hex:"),
        R("boon", Role.Survivor, @"^boon:"),
        R("scourge-hook", Role.Killer, @"^scourge hook:|scourge hook"),
        R("totem", null, @"\btotems?\b|^hex:|^boon:"),

        // Status effects.
        R("exhaustion", Role.Survivor,
            @"\bgains? \W*exhausted|\b(?:causes?|inflicts?|become|suffer from) (?:the )?\W*exhausted|exhausted (?:status effect )?for [\d./]+\s?s|while not \W*exhausted|cannot be used (?:while|when) \W*exhausted",
            unless: @"ignoring \W*exhausted|whenever you gain \W*exhausted"),
        R("haste", null, @"\bhaste\b"),
        R("hindered", null, @"\bhindered\b"),
        R("exposed", Role.Killer, @"\bexposed\b"),
        R("endurance", Role.Survivor, @"\bendurance\b"),
        R("obsession", null, @"\bobsession\b"),

        // Generators.
        R("regression", Role.Killer, @"regress"),
        R("gen-slowdown", Role.Killer,
            @"regress|generators? (?:is|are) blocked|blocks? (?:the |that |all )?generators?|generators?.{0,40}\bblocked\b|loses? [\d./]+\s?% (?:more |of )?(?:its |their )?(?:current |total )?progress|repair(?:ing)? speed.{0,30}(?:decreas|reduc|penalty)|(?:decreas|reduc)\w* .{0,30}repair speed|repairs? (?:[\d./]+% )?slower|repair [\d./]+% slower|and repair [\d./]+% slower|generator explodes|instantly explodes|it explodes"),
        R("gen-speed", Role.Survivor,
            @"repair(?:ing)? speed|repairs? (?:generators? |them |it )?[\d./]+\s?% faster|repair .{0,30}[\d./]+% faster|progress(?:ion)? (?:bonus|to (?:the|a) generator)|great skill check.{0,60}(?:generator|repair)|(?:generator|repair).{0,80}bonus progress|skill checks?.{0,80}[\d./]+% (?:more )?progress|great.{0,20}[\d./]+% more progress"),

        // Information.
        R("aura", null, @"\bauras?\b"),
        R("info", null,
            @"\bauras?\b|notification|\breveal|\bscream|terror radius.{0,40}(?:is|are) (?:visible|shown)|killer is (?:looking|within)|is within \d+|you are being chased|look(?:ing)? (?:at|in) your direction"),
        R("tracking", Role.Killer,
            @"\bauras?\b|loud noise notification|notification|\bscream|scratch marks|pools? of blood|\breveal|grunts of pain|oblivious|\bkiller instinct\b"),

        // Chase.
        R("chase", Role.Survivor,
            @"\bchase\b|\bchased\b|fast vault|\bvault|\bpallets?\b|\bsprint\b|\bhaste\b|\bbreak(?:s|ing)? chase|\bdodge|\bdistance\b"),
        R("chase", Role.Killer,
            @"\bchase\b|\bchasing\b|\blunge\b|basic attack|\bhaste\b|breaking (?:a )?pallet|pallet|\bvault|\bhindered\b|movement speed|\bexposed\b"),

        // Healing and survival.
        R("healing", null, @"\bheal(?:s|ing|ed|th)?\b|\bmedkit|\bmending\b"),
        R("secondwind", Role.Survivor,
            @"^second wind|recover(?:ing)? from (?:the )?dying state|recovery (?:speed|rate)|pick yourself up|fully recover|\bunbreakable\b|recover(?:s|ed)? (?:to )?(?:the )?injured"),
        R("anti-tunnel", Role.Survivor,
            @"(?:after|when|once|whenever) (?:you are |you're |being |you get |a survivor is |another survivor is )?unhooked|(?:after|when) (?:being )?rescued|unhook yourself or are unhooked|unhooked by another survivor|after (?:being )?unhooked|are unhooked or unhook yourself|unhook(?:ing)? (?:a|another) survivor.{0,80}(?:endurance|haste|protection|killer)"),
        R("unhook", Role.Survivor, @"\bunhook"),
        R("altruism", Role.Survivor,
            @"unhook(?:ing|s)? (?:a|another|other)|heal(?:ing|s)? (?:a |another |other )?survivors?|heal(?:ing)? others|protection hits?|take(?:s)? a hit|altruis|rescu|the injured survivor|dying survivor|healing another|pick(?:ing)? up (?:a |another )?(?:dying )?survivor"),

        // Stealth.
        R("stealth", null,
            @"\bundetectable\b|scratch marks|pools? of blood|grunts? of pain|(?:suppress|silence|muffle|reduc)\w* (?:the )?(?:noise|sound)|\bnoiseless\b|\bhide\b|\bhiding\b|while crouching|\bcrouch"),

        // Endgame.
        R("endgame", null,
            @"exit gates?|endgame|end game collapse|all generators (?:are|have been) (?:completed|powered)|last generator|final generator|\bhatch\b|last survivor|egc"),
    };

    // Killer-side slowdown also covers perks that waste survivors' time without touching generators.
    private static readonly Regex KillerTimeWaste = new(
        @"heal(?:ing)? speed.{0,30}(?:decreas|reduc|penalty)|(?:mangled|hemorrhage|broken)\b|(?:penalty|decrease|reduce)\w* .{0,30}(?:repair|heal|cleans|unhook)|action speeds?.{0,30}(?:decreas|reduc|penalty)|totems? .{0,40}(?:blessed|cleanse).{0,40}(?:slower|penalty)|blocks? (?:the )?(?:exit gates?|pallets?|windows?|lockers?)|deep wound|heal(?:ing)? .{0,30}slower|heal [\d./]+% slower|skill checks?.{0,60}(?:smaller|more often|faster|more basic)|more basic skill checks|cleanse totems [\d./]+% slower",
        Opts);

    /// <summary>Infers tags (sorted, distinct) for a perk.</summary>
    public static IReadOnlyList<string> Infer(string name, Role role, string? description)
    {
        var text = name + "\n" + (description ?? "");
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            if (rule.Role is { } r && r != role)
                continue;
            if (!rule.Pattern.IsMatch(text))
                continue;
            if (rule.Unless is not null && rule.Unless.IsMatch(text))
                continue;
            tags.Add(rule.Tag);
        }

        if (role == Role.Killer && (tags.Contains("gen-slowdown") || KillerTimeWaste.IsMatch(text)))
            tags.Add("slowdown");

        return tags.ToList();
    }
}
