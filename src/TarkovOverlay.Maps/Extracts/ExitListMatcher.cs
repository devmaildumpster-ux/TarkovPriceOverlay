using System.Globalization;
using System.Text;

namespace TarkovOverlay.Maps.Extracts;

/// <summary>
/// Matches the extract names Tarkov lists after a double-tap of O (read with OCR) to the map's extracts.
/// Pure string logic; the capture/OCR side lives in the app.
/// </summary>
public static class ExitListMatcher
{
    public sealed record Match(string ExtractId, string ExtractName, string Line, double Score);

    public static List<Match> MatchLines(IEnumerable<string> ocrLines, IEnumerable<(string Id, string Name)> extracts, double minScore = 0.8)
    {
        var ex = extracts.Select(e => (e.Id, e.Name, Key: Normalize(e.Name))).Where(e => e.Key.Length > 0).ToList();
        var res = new List<Match>();
        foreach (var raw in ocrLines)
        {
            var line = Normalize(StripDecorations(raw));
            if (line.Length < 3) continue;
            Match? best = null;
            foreach (var e in ex)
            {
                double s = Score(line, e.Key);
                if (s >= minScore && (best == null || s > best.Score)) best = new Match(e.Id, e.Name, raw, s);
            }
            if (best != null && !res.Any(r => r.ExtractId == best.ExtractId)) res.Add(best);
        }
        return res;
    }

    /// <summary>Removes "EXFIL01", times ("12:34", "00:07:30"), and status words around the name.</summary>
    internal static string StripDecorations(string s)
    {
        var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        parts.RemoveAll(p => p.StartsWith("EXFIL", StringComparison.OrdinalIgnoreCase) || p.StartsWith("EXF1L", StringComparison.OrdinalIgnoreCase)
                             || p.Count(char.IsDigit) >= 2 && p.Contains(':'));
        string[] status = { "pending...", "pending", "open", "closed", "requirement", "requirement...", "not", "available", "unavailable", "check", "requirements" };
        while (parts.Count > 0 && status.Contains(parts[^1].ToLowerInvariant())) parts.RemoveAt(parts.Count - 1);
        return string.Join(' ', parts);
    }

    internal static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        var r = sb.ToString().Trim();
        // drop parenthetical decorations like "(co op)" / "(flare)" handled by tokens anyway
        return r;
    }

    /// <summary>1 − normalized Levenshtein distance, also tried against the extract's text without "(…)" parts.</summary>
    internal static double Score(string line, string name)
    {
        double s = Similarity(line, name);
        var shortName = Normalize(System.Text.RegularExpressions.Regex.Replace(name, @"\b(co op|flare)\b", "")).Trim();
        if (shortName.Length > 0 && shortName != name) s = Math.Max(s, Similarity(line, shortName));
        if (line.Contains(name, StringComparison.Ordinal) && name.Length >= 6) s = Math.Max(s, 0.95);
        return s;
    }

    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = Levenshtein(a, b);
        return 1.0 - (double)d / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
