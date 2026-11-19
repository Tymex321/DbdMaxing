using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbdMaxing.Core.Data;

/// <summary>Turns the API's templated HTML perk descriptions into plain text.</summary>
public static class PerkText
{
    private static readonly Regex Placeholder = new(@"\{(Tunable|Keyword|Input)\.([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ManyNewlines = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex CamelSplit = new(@"(?<=[a-z])(?=[A-Z])", RegexOptions.Compiled);

    /// <summary>
    /// Replaces {Tunable.X.Key} with tier values ("10/15/20"), {Keyword.DeepWound} with "Deep Wound",
    /// {Input.*} with "the Ability button", then strips HTML.
    /// </summary>
    public static string Clean(string? html, JsonElement? tunables = null)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var text = Placeholder.Replace(html, m => m.Groups[1].Value switch
        {
            "Tunable" => FormatTunable(m.Groups[2].Value, tunables),
            "Keyword" => KeywordText(m.Groups[2].Value),
            _ => "the Ability button",
        });
        return StripHtml(text);
    }

    public static string StripHtml(string html)
    {
        var s = html.Replace("\r", "");
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(?:ul|ol|p|div)>", "\n", RegexOptions.IgnoreCase);
        s = Tag.Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = Spaces.Replace(s, " ");
        s = string.Join('\n', s.Split('\n').Select(l => l.Trim()));
        s = ManyNewlines.Replace(s, "\n\n");
        return s.Trim();
    }

    private static string KeywordText(string keyword)
    {
        var words = CamelSplit.Replace(keyword, " ");
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string FormatTunable(string path, JsonElement? tunables)
    {
        var key = path[(path.LastIndexOf('.') + 1)..];
        if (tunables is not { ValueKind: JsonValueKind.Object } t)
            return "?";

        foreach (var prop in t.EnumerateObject())
        {
            if (!string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (prop.Value.ValueKind != JsonValueKind.Array)
                return FormatValue(prop.Value);
            var values = prop.Value.EnumerateArray().Select(FormatValue).Distinct().ToList();
            return string.Join('/', values);
        }
        return "?";
    }

    private static string FormatValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => Math.Round(v.GetDouble(), 2).ToString(CultureInfo.InvariantCulture),
        JsonValueKind.String => v.GetString() ?? "",
        _ => v.ToString(),
    };

    /// <summary>
    /// Best-guess icon URL on the DBD wiki (wiki.gg), which names files "IconPerks_" + lowerCamelCase(name) + ".png",
    /// e.g. "Hex: Ruin" -> IconPerks_hexRuin.png, "Self-Care" -> IconPerks_self-Care.png.
    /// The bundled perks.json holds verified URLs; this is only used for perks newer than the bundle.
    /// </summary>
    public static string WikiIconUrl(string perkName)
    {
        var cleaned = perkName.Replace("&", " And ").Replace("'", "").Replace("’", "").Replace(":", " ").Replace(".", " ");
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder("IconPerks_");
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            sb.Append(i == 0 ? char.ToLowerInvariant(w[0]) : char.ToUpperInvariant(w[0]));
            sb.Append(w, 1, w.Length - 1);
        }
        sb.Append(".png");
        return WikiImageBase + Uri.EscapeDataString(sb.ToString());
    }

    public const string WikiImageBase = "https://deadbydaylight.wiki.gg/images/";
}
