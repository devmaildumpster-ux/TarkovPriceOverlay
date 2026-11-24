using System.Globalization;
using System.Text;

namespace TarkovOverlay.Core.Text;

/// <summary>Case, space, Unicode punctuation and dash normalization shared by the catalog index and OCR text.</summary>
public static class NameNormalizer
{
    /// <summary>Lowercase, NFKC, unified dashes/quotes, single spaces. Keeps punctuation (used by repairs).</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var n = s.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(n.Length);
        bool space = false;
        foreach (var ch0 in n)
        {
            char ch = ch0 switch
            {
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' or '\uFE58' or '\uFE63' or '\uFF0D' or '\u00AD' => '-',
                '\u2018' or '\u2019' or '\u201A' or '\u201B' or '\u2032' or '`' or '\u00B4' => '\'',
                '\u201C' or '\u201D' or '\u201E' or '\u201F' or '\u2033' => '"',
                '\u00A0' or '\u2007' or '\u202F' or '\t' or '\r' or '\n' => ' ',
                _ => ch0,
            };
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) space = true;
                continue;
            }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>Only meaningful letters and digits; the key used for complete-name comparison.</summary>
    public static string Compact(string? s)
    {
        var n = Normalize(s);
        var sb = new StringBuilder(n.Length);
        foreach (var ch in n)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Joins OCR lines in reading order with single spaces, as required before matching.</summary>
    // "P98", "₽ 12 450", "12,450 P", "R 7578", "7578" (OCR reads ₽ as P or R)
    private static readonly System.Text.RegularExpressions.Regex PriceOnly = new(@"^[PpRr₽$€£]?\s*\d[\d\s.,]*\s*[PpRr₽$€£]?\.?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex SecondaryWords = new(
        @"\b(trader|traders|cannot|can't|can not|buy|sell|sold|price)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// True for a tooltip line that is not part of an item name: a trader price ("₽ 12 450", "7578 P") or a
    /// trader-screen message ("The trader cannot buy this item.").
    /// </summary>
    public static bool IsSecondaryLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var t = line.Trim();
        if (t.IndexOfAny(new[] { '₽', '$', '€', '£' }) >= 0) return true;
        if (PriceOnly.IsMatch(t)) return true;
        int digits = t.Count(char.IsDigit), letters = t.Count(char.IsLetter);
        if (digits >= 3 && letters <= 2) return true;
        return SecondaryWords.IsMatch(t);
    }

    /// <summary>The lines after the name (see <see cref="TitleLines"/>).</summary>
    public static IReadOnlyList<string> SecondaryLines(IReadOnlyList<string> lines)
    {
        var kept = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return kept.Skip(TitleLines(kept).Count).ToList();
    }

    private static readonly System.Text.RegularExpressions.Regex RublePrice = new(
        @"^(?:[₽PpRr£]\s*)?(\d{1,3}(?:[ ,.]\d{3})+|\d{1,9})(?:\s*[₽PpRr£])?\.?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A trader offer line such as "₽ 35 118" (OCR may read ₽ as P or R). Null unless the whole line is one
    /// clean rouble amount with properly grouped digits.
    /// </summary>
    public static long? ParseRublePrice(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var m = RublePrice.Match(line.Trim().Trim('(', ')', '[', ']', '{', '}', '|', ':', ';', '\'', '"', '`', ' '));
        if (!m.Success) return null;
        var digits = new string(m.Groups[1].Value.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var v) && v > 0 && v < 1_000_000_000 ? v : null;
    }

    /// <summary>The name lines of a tooltip: the first line always, then following lines up to the first secondary line.</summary>
    public static IReadOnlyList<string> TitleLines(IReadOnlyList<string> lines)
    {
        var kept = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (kept.Count <= 1) return kept;
        int n = 1;
        while (n < kept.Count && !IsSecondaryLine(kept[n])) n++;
        return kept.Take(n).ToList();
    }

    public static string JoinLines(IEnumerable<string> lines) =>
        string.Join(" ", lines.Select(l => l?.Trim() ?? "").Where(l => l.Length > 0));

    public static IReadOnlyList<string> Tokens(string? s) =>
        Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
