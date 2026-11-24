using System.Text;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace TarkovOverlay.Tests;

/// <summary>
/// 1.2.4 regression sweep over the whole recorded tarkov.dev item list (PvE fixture): every name as Tarkov shows it,
/// wrapped on two lines at realistic tooltip widths, with the second line lost, with the lines out of order, and with
/// common OCR look-alike glyphs. Counts verified / possible (right item first) / wrong / missed per class and writes
/// a report to the test output folder (match-sweep.txt). Thresholds guard against regressions.
/// </summary>
public class MatchSweepTests
{
    private readonly ITestOutputHelper _out;
    public MatchSweepTests(ITestOutputHelper o) => _out = o;

    public sealed class Tally
    {
        public int Total, Verified, Possible, Listed, Wrong, Missed, WrongConfident;
        public List<string> Examples = new();
        public override string ToString() => $"{Total,6} · verified {Verified,5} · possible(right first) {Possible,5} · several(right listed) {Listed,5} · wrong {Wrong,4} (confident {WrongConfident}) · missed {Missed,4}";
    }

    /// <summary>Greedy word wrap at a character budget (Tarkov wraps the name tooltip at a fixed pixel width).</summary>
    public static List<string> Wrap(string name, int width)
    {
        var lines = new List<string>(); var cur = new StringBuilder();
        foreach (var w in name.Split(' '))
        {
            if (cur.Length > 0 && cur.Length + 1 + w.Length > width) { lines.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(w);
        }
        if (cur.Length > 0) lines.Add(cur.ToString());
        return lines;
    }

    /// <summary>OCR look-alikes: 1→l inside numbers, 0→O inside numbers, x→× between digits, Q→O.</summary>
    public static IEnumerable<(string Kind, string Text)> Confusions(string name)
    {
        string Rx(string pattern, string rep) => System.Text.RegularExpressions.Regex.Replace(name, pattern, rep);
        var a = Rx(@"(?<=\d)1|1(?=\d)", "l"); if (a != name) yield return ("1→l in numbers", a);
        var b = Rx(@"(?<=\d)0|0(?=\d)", "O"); if (b != name) yield return ("0→O in numbers", b);
        var c = Rx(@"(?<=\d)x(?=\d)", "×"); if (c != name) yield return ("x→× between digits", c);
        var d = name.Replace("Q", "O"); if (d != name) yield return ("Q→O", d);
        var e = Rx(@"(?<=\d)1(?=\D|$)", "I"); if (e != name) yield return ("trailing 1→I", e);
    }

    public static Dictionary<string, Tally> Run(Func<IReadOnlyList<string>, int, MatchResult> match, IReadOnlyCollection<CatalogItem> items)
    {
        var t = new Dictionary<string, Tally>();
        var nameCount = items.GroupBy(i => Core.Text.NameNormalizer.Compact(i.Name)).ToDictionary(g => g.Key, g => g.Count());
        void Count(string cls, CatalogItem item, IReadOnlyList<string> lines, int expected = 0)
        {
            if (!t.TryGetValue(cls, out var y)) t[cls] = y = new Tally();
            y.Total++;
            var m = match(lines, expected);
            bool same = nameCount[Core.Text.NameNormalizer.Compact(item.Name)] > 1;
            bool first = m.Items.Count > 0 && (m.Items[0].Id == item.Id || (same && m.Items.Any(i => i.Id == item.Id)));
            if ((m.Kind == MatchKind.Verified && m.Items[0].Id == item.Id) || (m.Kind == MatchKind.Conflict && same && m.Items.Any(i => i.Id == item.Id))) y.Verified++;
            else if (m.Kind is MatchKind.Verified) { y.Wrong++; y.WrongConfident++; if (y.Examples.Count < 12) y.Examples.Add($"WRONG {string.Join(" / ", lines)} -> {m.Items[0].Name}"); }
            else if (m.Kind is MatchKind.PossibleStrong or MatchKind.PossibleWeak && first) y.Possible++;
            else if (m.Kind is MatchKind.PossibleStrong or MatchKind.PossibleWeak or MatchKind.Conflict) { y.Wrong++; if (y.Examples.Count < 12) y.Examples.Add($"wrong-suggest {m.Kind} {string.Join(" / ", lines)} -> {m.Items[0].Name}"); }
            else if (m.Kind == MatchKind.Several && m.Items.Any(i => i.Id == item.Id)) y.Listed++;
            else { y.Missed++; if (y.Examples.Count < 12) y.Examples.Add($"missed {m.Kind} {string.Join(" / ", lines)}"); }
        }
        foreach (var item in items.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            Count("exact name", item, new[] { item.Name });
            foreach (var w in new[] { 30, 36, 42 })
            {
                var lines = Wrap(item.Name, w);
                if (lines.Count < 2) continue;
                Count($"wrapped@{w}", item, lines);
                Count($"wrapped@{w}, 2nd line lost", item, lines.Take(1).ToList(), 2);
                Count($"wrapped@{w}, lines swapped", item, lines.Skip(1).Append(lines[0]).ToList());
            }
            foreach (var (kind, text) in Confusions(item.Name)) Count(kind, item, new[] { text });
            // strictness: a letter that no look-alike rule covers is wrong (w for the first plain letter after the 3rd)
            var chars = item.Name.ToCharArray();
            int pos = Array.FindIndex(chars, 3, c => char.IsLetter(c) && "liosbzgqILIOSBZGQ".IndexOf(c) < 0 && c != 'w' && c != 'W');
            if (pos > 0) { chars[pos] = 'w'; Count("strict: one wrong letter", item, new[] { new string(chars) }); }
        }
        return t;
    }

    [Fact]
    public void Sweep_whole_catalog()
    {
        var items = LiveCatalog.Pve.Items.Values.Where(i => !i.IsPreset).ToList();
        var idx = new ItemIndex(items);
        var r = Run((lines, expected) => idx.MatchLines(lines, expected), items);
        var sb = new StringBuilder();
        foreach (var (k, v) in r.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.AppendLine($"{k,-30} {v}");
        sb.AppendLine();
        foreach (var (k, v) in r.OrderBy(k => k.Key, StringComparer.Ordinal)) foreach (var e in v.Examples) sb.AppendLine($"[{k}] {e}");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "match-sweep.txt"), sb.ToString());
        _out.WriteLine(sb.ToString());
        var env = Environment.GetEnvironmentVariable("TPO_SWEEP_OUT");
        if (!string.IsNullOrEmpty(env)) File.WriteAllText(env, sb.ToString());
        SweepAssertions.Check(r);
    }
}
