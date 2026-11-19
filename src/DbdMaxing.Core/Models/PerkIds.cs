using System.Globalization;
using System.Text;

namespace DbdMaxing.Core.Models;

public static class PerkIds
{
    /// <summary>"Barbecue &amp; Chili" -> "barbecue-chili", "Scourge Hook: Pain Resonance" -> "scourge-hook-pain-resonance".</summary>
    public static string Slug(string name)
    {
        var normalized = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var pendingDash = false;
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c == '\'' || c == '’')
                continue;
            if (char.IsLetterOrDigit(c))
            {
                if (pendingDash && sb.Length > 0)
                    sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }
        return sb.ToString();
    }
}
