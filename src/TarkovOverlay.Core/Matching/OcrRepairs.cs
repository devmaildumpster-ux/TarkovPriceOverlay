using System.Text;
using System.Text.RegularExpressions;

namespace TarkovOverlay.Core.Matching;

/// <summary>
/// Narrow, pattern-specific OCR glyph repairs. Each repair only fires in a known context (between digits,
/// inside a caliber, a numeric token, or a measurement suffix) and a repaired text is only accepted when it
/// equals a complete catalog name. Repairs never add or remove digits, so 60 vs 100 rounds, 30mm vs 38mm,
/// and different calibers stay distinct.
/// </summary>
public static class OcrRepairs
{
    public sealed record Variant(string Text, string Description, int Stage);

    private static readonly (Regex Pattern, string Replacement, string Name)[] UnitRules =
    {
        // "5.45<39", "5.45×39", "5.45 x 39" -> 5.45x39 (x between digits)
        (new Regex(@"(?<=\d)\s*[<×✕✗хx\*]\s*(?=\d)", RegexOptions.Compiled), "x", "x between digits"),
        // "5S6x45" / "S.56x45" -> 5.56x45 (S as 5 inside a decimal caliber)
        (new Regex(@"(?<=\d)s(?=[\.,]?\d*x\d)|(?<![a-z0-9])s(?=[\.,]\d+x\d)", RegexOptions.Compiled), "5", "S as 5 in caliber"),
        // "6Ø-round", "3ømm", "6o" -> 0 inside a numeric token
        (new Regex(@"(?<=\d)[øo°](?=$|[\døo°\s\-x\.,/)]|mm|m|rn|round)", RegexOptions.Compiled), "0", "Ø/O as 0 in number"),
        (new Regex(@"(?<![a-z0-9])ø(?=\d)", RegexOptions.Compiled), "0", "Ø as 0 in number"),
        // "1l0", "2|5" -> 1 between digits
        (new Regex(@"(?<=\d)[l|i](?=\d)", RegexOptions.Compiled), "1", "l/I as 1 in number"),
        // "30rnrn", "30mrn", "30rnm" -> 30mm (measurement suffix)
        (new Regex(@"(?<=\d)(?:rn|m)(?:rn|m)(?![a-z])", RegexOptions.Compiled), "mm", "rn as m in mm"),
    };

    private static readonly Dictionary<char, char> ModelLetterSubs = new()
    {
        ['0'] = 'o', ['1'] = 'i', ['5'] = 's', ['8'] = 'b', ['6'] = 'g', ['2'] = 'z',
    };

    /// <summary>
    /// Stage 1: unit/number repairs only (literal model codes kept). Every combination of the applicable rules
    /// is produced, so one rule (e.g. l→1 between digits) can never block another from reaching the real name
    /// (e.g. "6L31" must stay literal while "5.45&lt;39" is repaired).
    /// </summary>
    public static IReadOnlyList<Variant> UnitVariants(string normalized)
    {
        // A rule may only become applicable after another (e.g. "3ømrn": Ø→0 enables rn→m), so all rules take part.
        var applicable = UnitRules;
        var result = new List<Variant>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { normalized };
        int n = applicable.Length;
        for (int mask = 1; mask < (1 << n); mask++)
        {
            var text = normalized;
            var names = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                var next = ApplyRule(applicable[i].Pattern, applicable[i].Replacement, text);
                if (next != text) { names.Add(applicable[i].Name); text = next; }
            }
            if (names.Count > 0 && seen.Add(text)) result.Add(new Variant(text, string.Join(", ", names), 1));
        }
        return result;
    }

    /// <summary>All applicable unit repairs applied together (used as the base for suggestions).</summary>
    public static Variant? UnitRepair(string normalized)
    {
        var s = normalized;
        var applied = new List<string>();
        foreach (var (p, r, name) in UnitRules)
        {
            var next = ApplyRule(p, r, s);
            if (next != s) { applied.Add(name); s = next; }
        }
        return applied.Count == 0 ? null : new Variant(s, string.Join(", ", applied), 1);
    }

    /// <summary>Applies a rule until stable (so "1ØØ" becomes "100").</summary>
    private static string ApplyRule(Regex p, string r, string s)
    {
        for (int i = 0; i < 4; i++)
        {
            var next = p.Replace(s, r);
            if (next == s) return s;
            s = next;
        }
        return s;
    }

    private static readonly Dictionary<char, char[]> LeadingDigitSubs = new()
    {
        ['8'] = new[] { 'b' }, ['0'] = new[] { 'o' }, ['5'] = new[] { 's' }, ['6'] = new[] { 'g' }, ['1'] = new[] { 'l', 'i' }, ['2'] = new[] { 'z' },
    };

    private static readonly Dictionary<char, char> LetterToDigit = new()
    {
        ['o'] = '0', ['g'] = '6', ['s'] = '5', ['i'] = '1', ['l'] = '1', ['b'] = '8', ['z'] = '2',
    };

    /// <summary>
    /// Stage 2: model-letter substitutions inside model-code-like tokens (alphanumeric, ≥4 chars, starting with
    /// two letters): a digit read for a letter (TANG06T → TANGO6T) or a letter read for a digit (TANGOGT → TANGO6T,
    /// MAGS → MAG5). A letter→digit swap is only considered when the token ends up mixing letters and digits.
    /// At most two substitutions, both within one token. Every variant must still equal a complete catalog name.
    /// </summary>
    public static IEnumerable<Variant> ModelLetterVariants(string text)
    {
        int produced = 0;
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
            int start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
            int end = i;
            // Word-initial digit read for a capital ("8olts" → Bolts, "0x bleach"): a short name's first letter is
            // the glyph most often clipped by the pointer or the frame. Only when the rest of the word is letters.
            if (end - start >= 4 && char.IsDigit(text[start]) && LeadingDigitSubs.TryGetValue(text[start], out var leads))
            {
                bool rest = true;
                for (int k = start + 1; k < end; k++) if (!char.IsLetter(text[k])) { rest = false; break; }
                if (rest)
                    foreach (var l in leads)
                    {
                        if (++produced > 400) yield break;
                        yield return new Variant(Sub(text, start, l), $"leading {text[start]}→{l}", 2);
                    }
            }
            if (end - start < 4 || !char.IsLetter(text[start]) || !char.IsLetter(text[start + 1])) continue;

            bool hasDigit = false;
            for (int k = start; k < end; k++) if (char.IsDigit(text[k])) { hasDigit = true; break; }
            var subs = new List<(int Pos, char To)>();
            for (int k = start + 2; k < end; k++)
            {
                char c = text[k];
                if (char.IsDigit(c) && ModelLetterSubs.TryGetValue(c, out var l) && char.IsLetter(text[k - 1])) subs.Add((k, l));
                else if (char.IsLetter(c) && LetterToDigit.TryGetValue(c, out var d)) subs.Add((k, d));
            }
            if (subs.Count == 0 || subs.Count > 8) continue;

            foreach (var a in subs)
            {
                var t1 = Sub(text, a.Pos, a.To);
                if (IsMixedAfter(t1, start, end) || hasDigit)
                {
                    if (++produced > 400) yield break;
                    yield return new Variant(t1, $"model letter {text[a.Pos]}→{a.To}", 2);
                }
            }
            for (int x = 0; x < subs.Count; x++)
                for (int y = x + 1; y < subs.Count; y++)
                {
                    var t2 = Sub(Sub(text, subs[x].Pos, subs[x].To), subs[y].Pos, subs[y].To);
                    if (!IsMixedAfter(t2, start, end)) continue;
                    if (++produced > 400) yield break;
                    yield return new Variant(t2, "two model letters", 2);
                }
        }
    }

    // Matching runs on lower-cased text: a roman "II" is "ii" here.
    private static bool IsRomanLike(char c) => c is 'i' or 'l' or '1' or '|' or '!' or 't' or 'v' or 'x' or 'I' or 'T' or 'V' or 'X';
    private static bool IsIConfusable(char c) => c is 'l' or '1' or '|' or '!' or 't' or 'T';

    /// <summary>
    /// Roman numerals in model names ("ComTac II", "Mk III", "Class IV"): OCR reads a thin "I" as l, 1, |, ! or
    /// the second I of "II" as T ("ComTac IT"). Short standalone tokens (2-4 chars) made only of numeral-like
    /// glyphs, with at least one real I or V, get every confusable turned into I. Like every repair, a variant
    /// only counts when it equals a complete catalog name.
    /// </summary>
    public static IEnumerable<Variant> RomanNumeralVariants(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            int end = i;
            int len = end - start;
            if (len is < 2 or > 4) continue;
            bool ok = true, anchor = false, conf = false;
            for (int k = start; k < end; k++)
            {
                char c = text[k];
                if (!IsRomanLike(c)) { ok = false; break; }
                if (c is 'i' or 'I' or 'v' or 'V' or 'x' or 'X') anchor = true;
                if (IsIConfusable(c)) conf = true;
            }
            // "ll"/"11"/"||" (no real i/v/x) are accepted only when every glyph is a thin stroke (no T)
            if (!ok || !conf) continue;
            if (!anchor)
            {
                bool allThin = true;
                for (int k = start; k < end; k++) if (text[k] is 'T' or 't') { allThin = false; break; }
                if (!allThin) continue;
            }
            var sb = new StringBuilder(text);
            for (int k = start; k < end; k++)
            {
                if (IsIConfusable(text[k])) sb[k] = char.IsUpper(text[start]) ? 'I' : 'i';
            }
            yield return new Variant(sb.ToString(), $"roman numeral {text.Substring(start, len)}→{sb.ToString(start, len)}", 2);
        }
    }

    private static bool IsMixedAfter(string t, int start, int end)
    {
        bool l = false, d = false;
        for (int k = start; k < end; k++) { if (char.IsLetter(t[k])) l = true; else if (char.IsDigit(t[k])) d = true; }
        return l && d;
    }

    private static string Sub(string s, int pos, char to)
    {
        var sb = new StringBuilder(s);
        sb[pos] = to;
        return sb.ToString();
    }

}
