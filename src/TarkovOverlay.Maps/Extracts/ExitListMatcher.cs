using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TarkovOverlay.Maps.Extracts;

/// <summary>
/// Matches the extract names Tarkov lists after pressing O (read with OCR) to the map's extracts.
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
            var line = Normalize(StripDecorations(raw, out bool isTransitRow));
            if (line.Length < 3) continue;
            if (!isTransitRow && (line.StartsWith("transit to ", StringComparison.Ordinal) || line.StartsWith("iransit to ", StringComparison.Ordinal)))
                isTransitRow = true;
            var pool = isTransitRow ? ex.Where(e => e.Key.Contains("transit", StringComparison.Ordinal)).ToList() : ex;
            if (pool.Count == 0) continue;
            double bestScore = 0;
            foreach (var e in pool) bestScore = Math.Max(bestScore, Score(line, e.Key));
            if (bestScore < minScore) continue;
            // 1.2.7: every extract with the best score counts (the feed can list the same exit twice, e.g. a PMC and a
            // Scav "Railway Exfil" on Interchange; whichever copy the map draws must be marked as listed).
            foreach (var e in pool)
                if (Score(line, e.Key) >= bestScore - 1e-9 && !res.Any(r => r.ExtractId == e.Id))
                    res.Add(new Match(e.Id, e.Name, raw, bestScore));
        }
        return res;
    }

    // Row labels: "EXFIL01", "TRANSIT02" and OCR variants (case-insensitive, leading punctuation like "“EXFILO2",
    // OCR confusions like EXF1L, EXFIL@1, EXFILOL, EXFILOT, ExriLoa, IRANSIIO1, TRANSIN04, TRANSHO4, space-separated
    // "EXFIL 01", or glued to the name like "EXFIL01Scav" / "ExFilodPathtoLighthouse").
    // 1.2.7+: only the label is removed. Standalone "Exfil", "Railway Exfil", "Saferoom Exfil", "Exit to Woods",
    // "Expo Checkpoint", and "Transit to ..." (no row number) are kept intact.
    private static readonly Regex RowLabelPrefix = new(
        @"^[^\p{L}\p{N}]*(?:ex[frp][i1l|!u]{1,2}[l1|!i]?|(?<transit>[ti1|]?ran[s5][i1l|!h]{1,2}[t7ni1l|!]?))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RowNumberStandalone = new(
        @"^[0-9OoQDIlL|!@SsGgaqdT\]\?]{1,3}$",
        RegexOptions.CultureInvariant);

    private static readonly Regex RowNumberGlued = new(
        @"^[0-9OoQDIl|!@sd]{2}",
        RegexOptions.CultureInvariant);

    private static readonly Regex TimerToken = new(
        @"^[\(\[]?[0-9OoQDIl|!@?]{1,2}(?:[:.;][0-9OoQDIl|!@?B]{2}){1,2}[\)\]]?s?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex QuestionJunk = new(
        @"^[0-9A-Za-z|!@?]{2,}\?$|^[\?]{2,}$|^[27P\?]{4,}$",
        RegexOptions.CultureInvariant);

    /// <summary>Removes "EXFIL01" / "TRANSIT01", times ("0:00:28", "00:07:30", "0.00.28", "??:??:??"), "?????" noise, and status words around the name.</summary>
    internal static string StripDecorations(string s) => StripDecorations(s, out _);

    internal static string StripDecorations(string s, out bool isTransitRow)
    {
        isTransitRow = false;
        var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 1 && !parts[0].Any(char.IsLetterOrDigit)) parts.RemoveAt(0);

        for (int i = 0; i < parts.Count; i++)
        {
            var pm = RowLabelPrefix.Match(parts[i]);
            if (!pm.Success) continue;
            var after = parts[i][pm.Length..];
            bool transitHit = pm.Groups["transit"].Success;
            if (after.Length == 0)
            {
                if (i == 0 && i + 1 < parts.Count && RowNumberStandalone.IsMatch(parts[i + 1]))
                {
                    if (transitHit) isTransitRow = true;
                    parts.RemoveAt(i + 1);
                    parts.RemoveAt(i--);
                }
                continue;
            }
            if (RowNumberStandalone.IsMatch(after))
            {
                if (transitHit) isTransitRow = true;
                parts.RemoveAt(i--);
                continue;
            }
            if (after.Length >= 3 && RowNumberGlued.IsMatch(after))
            {
                if (transitHit) isTransitRow = true;
                parts[i] = after[2..];
            }
        }

        parts.RemoveAll(p => TimerToken.IsMatch(p) || (p.Contains(':') && (p.Count(c => char.IsDigit(c) || c is 'O' or 'o') >= 2 || p.Count(c => c == '?') >= 2)));

        // Split colon/dot timers at the end like "0 : 00 : 28"
        while (parts.Count >= 3 && (parts[^2] == ":" || parts[^2] == ".") && parts[^1].Length <= 2 && parts[^3].Length <= 2 &&
               parts[^1].All(c => char.IsDigit(c) || c is 'O' or 'o' or '?') &&
               parts[^3].All(c => char.IsDigit(c) || c is 'O' or 'o' or '?'))
        {
            parts.RemoveAt(parts.Count - 1);
            parts.RemoveAt(parts.Count - 1);
            if (parts.Count > 0 && parts[^1].Length <= 2 && parts[^1].All(c => char.IsDigit(c) || c is 'O' or 'o' or '?'))
                parts.RemoveAt(parts.Count - 1);
        }

        string[] status = { "pending...", "pending", "open", "closed", "requirement", "requirement...", "not", "available", "unavailable", "check", "requirements", "required" };
        while (parts.Count > 0)
        {
            var last = parts[^1];
            if (status.Contains(last.ToLowerInvariant())) { parts.RemoveAt(parts.Count - 1); continue; }
            if (parts.Count >= 2 && QuestionJunk.IsMatch(last) && !last.Equals("Labyrinth?", StringComparison.OrdinalIgnoreCase))
            {
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            if (parts.Count >= 2 && !last.Any(char.IsLetterOrDigit)) { parts.RemoveAt(parts.Count - 1); continue; }
            break;
        }
        while (parts.Count > 1 && !parts[0].Any(char.IsLetterOrDigit)) parts.RemoveAt(0);
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
        return sb.ToString().Trim();
    }

    /// <summary>1 − normalized Levenshtein distance, also tried against the extract's text without "(…)" parts.</summary>
    internal static double Score(string line, string name)
    {
        double s = ScoreTarget(line, name);
        var shortName = Normalize(Regex.Replace(name, @"\b(co op|flare)\b", "")).Trim();
        if (shortName.Length > 0 && shortName != name) s = Math.Max(s, ScoreTarget(line, shortName));
        return s;
    }

    private static double ScoreTarget(string line, string target)
    {
        double s = Similarity(line, target);
        // 1.2.7: a row cut off at the right edge ("Power Station V-", "Scav Camp (Co-0") still names the exit
        if (line.Length >= 8 && line.Length >= target.Length * 0.6 && line.Length < target.Length)
            s = Math.Max(s, 0.9 * Similarity(line, target[..line.Length]));

        var lineNoSp = line.Replace(" ", "");
        var targetNoSp = target.Replace(" ", "");
        if (lineNoSp.Length >= 6 && targetNoSp.Length >= 6)
            s = Math.Max(s, 0.95 * Similarity(lineNoSp, targetNoSp));

        if (target.Length >= 6)
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1)
            {
                for (int i = 0; i < words.Length; i++)
                {
                    var sb = new StringBuilder();
                    for (int j = i; j < words.Length; j++)
                    {
                        if (j > i) sb.Append(' ');
                        sb.Append(words[j]);
                        if (i == 0 && j == words.Length - 1) continue;
                        var span = sb.ToString();
                        if (span.Length < target.Length * 0.6 || span.Length > target.Length * 1.45) continue;
                        double spanSim = Similarity(span, target);
                        var spanNoSp = span.Replace(" ", "");
                        if (spanNoSp.Length >= 6 && targetNoSp.Length >= 6)
                            spanSim = Math.Max(spanSim, 0.95 * Similarity(spanNoSp, targetNoSp));
                        double weighted = spanSim * (0.72 + 0.26 * Math.Min(1.0, (double)span.Length / line.Length));
                        if (weighted > s) s = weighted;
                    }
                }
            }
        }
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
