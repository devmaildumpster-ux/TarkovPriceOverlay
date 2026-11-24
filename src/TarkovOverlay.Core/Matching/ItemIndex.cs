using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Text;

namespace TarkovOverlay.Core.Matching;

public enum MatchKind
{
    /// <summary>One complete catalog name, literal or via a narrow repair.</summary>
    Verified,
    /// <summary>Several credible complete names (different reads, or identical catalog names). User must choose.</summary>
    Conflict,
    /// <summary>A single close suggestion (one character off or a unique exact short name). Preview: Flea + Trader.</summary>
    PossibleStrong,
    /// <summary>A single weaker suggestion. Preview: name only.</summary>
    PossibleWeak,
    /// <summary>Several approximate suggestions; populate the main window list.</summary>
    Several,
    None,
}

public sealed record MatchResult(MatchKind Kind, IReadOnlyList<CatalogItem> Items, string Text, string? Repair = null, int Stage = 0)
{
    /// <summary>1.2.4: runner-up suggestions for a possible match (best first is <see cref="Items"/>[0]).</summary>
    public IReadOnlyList<CatalogItem> Alternatives { get; init; } = Array.Empty<CatalogItem>();
    public bool IsCredibleFullName => Kind is MatchKind.Verified or MatchKind.Conflict;
    public CatalogItem? Single => Items.Count == 1 ? Items[0] : null;
    public static MatchResult NoMatch(string text) => new(MatchKind.None, Array.Empty<CatalogItem>(), text);
}

/// <summary>Index of full names and short names with stable item IDs. Built off the UI thread.</summary>
public sealed class ItemIndex
{
    private readonly Dictionary<string, List<CatalogItem>> _byFull = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CatalogItem>> _byShort = new(StringComparer.Ordinal);
    private readonly List<(CatalogItem Item, string Compact, string Normal, string ShortCompact)> _all = new();
    // 1.2.4: names folded over OCR look-alike glyphs (l/i/1, o/0/q, s/5, b/8, z/2, g/6, rn/m)
    private readonly Dictionary<string, List<CatalogItem>> _byFold = new(StringComparer.Ordinal);
    private readonly List<(CatalogItem Item, string Fold)> _folds = new();

    public int Count => _all.Count;

    public ItemIndex(IEnumerable<CatalogItem> items)
    {
        foreach (var it in items)
        {
            var c = NameNormalizer.Compact(it.Name);
            if (c.Length == 0) continue;
            Add(_byFull, c, it);
            var sc = NameNormalizer.Compact(it.ShortName);
            if (sc.Length > 0) Add(_byShort, sc, it);
            _all.Add((it, c, NameNormalizer.Normalize(it.Name), sc));
            var f = Fold(c);
            Add(_byFold, f, it);
            _folds.Add((it, f));
        }
    }

    /// <summary>
    /// 1.2.4: a compact name with every OCR look-alike glyph class collapsed to one symbol (l/i/1 → 1, o/0/q → 0,
    /// s/5 → 5, b/8 → 8, z/2 → 2, g/6 → 6, "rn" → m). A read that differs from a catalog name only by such glyphs
    /// folds to the same key; two catalog names that fold alike are never told apart this way.
    /// </summary>
    public static string Fold(string compact)
    {
        var sb = new System.Text.StringBuilder(compact.Length);
        for (int i = 0; i < compact.Length; i++)
        {
            char ch = compact[i];
            if (ch == 'r' && i + 1 < compact.Length && compact[i + 1] == 'n') { sb.Append('m'); i++; continue; }
            sb.Append(ch switch
            {
                'l' or 'i' or '1' => '1', 'o' or '0' or 'q' => '0', 's' or '5' => '5', 'b' or '8' => '8', 'z' or '2' => '2', 'g' or '6' => '6',
                _ => ch,
            });
        }
        return sb.ToString();
    }

    /// <summary>Catalog entries whose folded name equals the folded read.</summary>
    public IReadOnlyList<CatalogItem> FoldedFull(string text) =>
        _byFold.TryGetValue(Fold(NameNormalizer.Compact(text)), out var l) ? l : Array.Empty<CatalogItem>();

    private static void Add(Dictionary<string, List<CatalogItem>> d, string k, CatalogItem it)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = new List<CatalogItem>(1);
        l.Add(it);
    }

    /// <summary>Complete-name lookup by compact key.</summary>
    public IReadOnlyList<CatalogItem> ExactFull(string text) =>
        _byFull.TryGetValue(NameNormalizer.Compact(text), out var l) ? l : Array.Empty<CatalogItem>();

    /// <summary>1.3.1: items whose short name compact-equals <paramref name="text"/> (presets excluded).</summary>
    public IReadOnlyList<CatalogItem> ExactShort(string text)
    {
        var q = NameNormalizer.Compact(text);
        if (q.Length < 2 || !_byShort.TryGetValue(q, out var l)) return Array.Empty<CatalogItem>();
        return l.Where(s => !s.IsPreset).ToList();
    }

    /// <summary>1.3.1: short names within <paramref name="maxDistance"/> folded glyphs (presets excluded).</summary>
    public IReadOnlyList<CatalogItem> CloseShort(string? text, int maxDistance = 1)
    {
        var q = Fold(NameNormalizer.Compact(text));
        if (q.Length < 3) return Array.Empty<CatalogItem>();
        var hits = new Dictionary<string, CatalogItem>(StringComparer.Ordinal);
        foreach (var (item, _, _, sc) in _all)
        {
            if (item.IsPreset || sc.Length == 0) continue;
            var f = Fold(sc);
            if (Math.Abs(f.Length - q.Length) > maxDistance) continue;
            if (Levenshtein.Distance(f, q, maxDistance) <= maxDistance) hits.TryAdd(item.Id, item);
        }
        return hits.Values.ToList();
    }

    /// <summary>
    /// Matches one OCR read (lines already joined with spaces). Order: literal full name, then unit/number
    /// repairs, then model-letter substitutions. Approximate search is only used for suggestions.
    /// </summary>
    public MatchResult Match(string ocrText)
    {
        var norm = NameNormalizer.Normalize(ocrText);
        if (NameNormalizer.Compact(norm).Length < 3) return MatchResult.NoMatch(ocrText);

        var literal = ExactFull(norm);
        if (literal.Count > 0) return Full(literal, ocrText, null, 0);

        // Stage 1: unit/number repairs (every combination of applicable rules).
        var unitVariants = OcrRepairs.UnitVariants(norm);
        var stage1 = new Dictionary<string, (CatalogItem Item, string Desc)>();
        foreach (var v in unitVariants)
            foreach (var hit in ExactFull(v.Text))
                stage1.TryAdd(hit.Id, (hit, v.Description));
        if (stage1.Count > 0)
            return Full(stage1.Values.Select(x => x.Item).ToList(), ocrText, stage1.Values.First().Desc, 1);

        // Stage 2: model-letter substitution on the literal text and on each unit-repaired variant.
        var found = new Dictionary<string, (CatalogItem Item, string Desc)>();
        foreach (var basis in unitVariants.Prepend(new OcrRepairs.Variant(norm, "", 0)))
            foreach (var v in OcrRepairs.ModelLetterVariants(basis.Text).Concat(OcrRepairs.RomanNumeralVariants(basis.Text)))
                foreach (var hit in ExactFull(v.Text))
                    found.TryAdd(hit.Id, (hit, basis.Description.Length > 0 ? basis.Description + ", " + v.Description : v.Description));
        if (found.Count > 0)
            return Full(found.Values.Select(x => x.Item).ToList(), ocrText, found.Values.First().Desc, 2);

        // Stage 3 (1.2.4): differs from exactly one catalog name only by OCR look-alike glyphs ("7.62x5l", "AR-l5",
        // "TOZ-1O6", "QDC"→"ODC"). Safe because a misread of one name can only fold onto another name that already
        // folds alike, and then the key is shared and it is not used.
        foreach (var basis in unitVariants.Select(v => v.Text).Prepend(norm))
        {
            var folded = FoldedFull(basis);
            if (folded.Count == 0) continue;
            var names = folded.Select(i => NameNormalizer.Compact(i.Name)).Distinct(StringComparer.Ordinal).Count();
            if (names == 1) return Full(folded, ocrText, "look-alike glyphs (" + GlyphDiff(NameNormalizer.Compact(basis), NameNormalizer.Compact(folded[0].Name)) + ")", 3);
            break;
        }

        var baseText = OcrRepairs.UnitRepair(norm)?.Text ?? norm;
        return Suggest(baseText, ocrText);
    }

    /// <summary>"l→1, o→0": the glyphs that differ between a read and the name it folded onto (same length only).</summary>
    private static string GlyphDiff(string read, string name)
    {
        if (read.Length != name.Length) return "rn→m";
        var d = new List<string>();
        for (int i = 0; i < read.Length && d.Count < 4; i++) if (read[i] != name[i]) d.Add($"{read[i]}→{name[i]}");
        return d.Count == 0 ? "same" : string.Join(", ", d.Distinct());
    }

    /// <summary>
    /// 1.2.4: matches the name lines of one read. Joined in reading order first; when that gives no full name, a
    /// 2-3 line read is also tried in the other line orders (OCR engines sometimes return a short second line
    /// first). Only a complete catalog name counts for the reordered tries.
    /// </summary>
    /// <param name="expectedLines">
    /// Title lines the tooltip detector counted (0 = unknown). When OCR returned fewer lines and the read is itself a
    /// full name that is also the start of a longer name ("ELCAN SpecterDR 1x/4x scope" vs "… scope (FDE)"), the
    /// missing line could be the rest of the longer name, so the result is only a possible match.
    /// </param>
    public MatchResult MatchLines(IReadOnlyList<string> lines, int expectedLines = 0)
    {
        var kept = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
        var m0 = MatchLinesCore(kept);
        if (m0.Kind == MatchKind.Verified && expectedLines > kept.Count && m0.Single is { } v)
        {
            var longer = _all.Select(a => a.Item).Where(i => i.Id != v.Id && i.Name.Length > v.Name.Length && WordPrefix(v.Name, i.Name, 1)).Take(3).ToList();
            if (longer.Count > 0)
                return new MatchResult(MatchKind.PossibleStrong, new[] { v }, m0.Text,
                    $"{kept.Count} of {expectedLines} title lines read") { Alternatives = longer };
        }
        return m0;
    }

    private MatchResult MatchLinesCore(List<string> kept)
    {
        var first = Match(NameNormalizer.JoinLines(kept));
        if (first.IsCredibleFullName || kept.Count is < 2 or > 3) return first;
        foreach (var perm in Permutations(kept).Skip(1))
        {
            var m = Match(NameNormalizer.JoinLines(perm));
            if (m.IsCredibleFullName) return m with { Repair = (m.Repair is { } r ? r + ", " : "") + "lines reordered", Text = NameNormalizer.JoinLines(kept) };
        }
        return first;
    }

    private static IEnumerable<List<string>> Permutations(List<string> l)
    {
        if (l.Count <= 1) { yield return l; yield break; }
        for (int i = 0; i < l.Count; i++)
            foreach (var rest in Permutations(l.Where((_, j) => j != i).ToList()))
                yield return rest.Prepend(l[i]).ToList();
    }

    private static MatchResult Full(IReadOnlyList<CatalogItem> hits, string text, string? repair, int stage) =>
        hits.Count == 1
            ? new MatchResult(MatchKind.Verified, hits, text, repair, stage)
            : new MatchResult(MatchKind.Conflict, hits, text, repair ?? "identical catalog names", stage);

    /// <summary>Approximate suggestions only (never an automatic identification).</summary>
    public MatchResult Suggest(string normalized, string originalText)
    {
        var q = NameNormalizer.Compact(normalized);
        if (q.Length < 3) return MatchResult.NoMatch(originalText);

        // Exact unique short name -> strong possible match.
        if (_byShort.TryGetValue(q, out var shorts))
        {
            var distinct = shorts.Where(s => !s.IsPreset).ToList();
            if (distinct.Count == 1) return new MatchResult(MatchKind.PossibleStrong, distinct, originalText, "exact short name");
            if (distinct.Count > 1) return new MatchResult(MatchKind.Several, distinct.Take(12).ToList(), originalText, "shared short name");
        }

        // 1.2.4: distances on folded names, so OCR look-alikes (l/1, O/0, S/5…) don't count as differences.
        var fq = Fold(q);
        int maxD = Math.Max(2, (int)Math.Round(q.Length * 0.12));
        var scored = new List<(CatalogItem Item, int D)>();
        foreach (var (item, f) in _folds)
        {
            if (Math.Abs(f.Length - fq.Length) > maxD) continue;
            int d = Levenshtein.Distance(fq, f, maxD);
            if (d <= maxD) scored.Add((item, d));
        }
        // A title cut short (e.g. the second wrapped line lost) -> prefix suggestions.
        var prefix = new List<CatalogItem>();
        if (q.Length >= 12)
            foreach (var (item, f) in _folds)
                if (f.Length > fq.Length && f.StartsWith(fq, StringComparison.Ordinal) && !scored.Any(s => s.Item.Id == item.Id))
                {
                    prefix.Add(item);
                    scored.Add((item, maxD + 1));
                }

        if (scored.Count == 0) return MatchResult.NoMatch(originalText);
        scored.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.Item.IsPreset.CompareTo(b.Item.IsPreset));
        var best = scored[0];
        int second = scored.Count > 1 ? scored[1].D : int.MaxValue;
        IReadOnlyList<CatalogItem> Alts() => scored.Skip(1).Where(x => x.D <= maxD + 1).Take(3).Select(x => x.Item).ToList();

        // A difference in digits (caliber, capacity, tube size, magnification) is never a "strong" suggestion.
        bool digitsAgree = NumberDigits(normalized) == NumberDigits(best.Item.Name);
        if (best.D == 1 && second >= 3 && digitsAgree)
            return new MatchResult(MatchKind.PossibleStrong, new[] { best.Item }, originalText, "one character off") { Alternatives = Alts() };
        // The read is the start of exactly one name, ending at a word break: a wrapped title whose 2nd line was lost.
        if (best.D > maxD && prefix.Count == 1 && WordPrefix(normalized, prefix[0].Name))
            return new MatchResult(MatchKind.PossibleStrong, prefix, originalText, "first line of a longer name");
        if (best.D <= maxD && second >= best.D + 2 && best.D > 0)
            return new MatchResult(MatchKind.PossibleWeak, new[] { best.Item }, originalText, $"{best.D} characters off") { Alternatives = Alts() };
        if (scored.Count == 1)
            return new MatchResult(MatchKind.PossibleWeak, new[] { best.Item }, originalText, "partial title");
        return new MatchResult(MatchKind.Several, scored.Take(12).Select(s => s.Item).ToList(), originalText, "approximate");
    }

    /// <summary>True when the read covers whole words at the start of the name ("AR-10 KAC QDC 7.62x51 Flash").</summary>
    private static bool WordPrefix(string normalizedRead, string name, int minTokens = 2)
    {
        var rt = NameNormalizer.Tokens(normalizedRead).Select(t => Fold(NameNormalizer.Compact(t))).Where(t => t.Length > 0).ToList();
        var nt = NameNormalizer.Tokens(name).Select(t => Fold(NameNormalizer.Compact(t))).Where(t => t.Length > 0).ToList();
        if (rt.Count < minTokens || rt.Count >= nt.Count) return false;
        for (int i = 0; i < rt.Count; i++) if (rt[i] != nt[i]) return false;
        return true;
    }

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    /// <summary>Digits of the number-bearing words only, look-alikes folded ("7.62x5l" → 76251; "carbine" → "").</summary>
    private static string NumberDigits(string text) =>
        string.Concat(NameNormalizer.Tokens(text).Where(t => t.Any(char.IsDigit)).Select(t => Digits(Fold(NameNormalizer.Compact(t)))));

    /// <summary>Typed search: names or shorthand. Ranked exact > short name > prefix > all tokens > fuzzy.</summary>
    public IReadOnlyList<CatalogItem> Search(string query, int limit = 60)
    {
        var qn = NameNormalizer.Normalize(query);
        var qc = NameNormalizer.Compact(query);
        if (qc.Length == 0) return Array.Empty<CatalogItem>();
        var tokens = qn.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(NameNormalizer.Compact).Where(t => t.Length > 0).ToArray();
        var results = new List<(CatalogItem Item, int Rank, int Len)>();
        foreach (var (item, c, n, sc) in _all)
        {
            int rank;
            if (c == qc) rank = 0;
            else if (sc == qc) rank = 1;
            else if (c.StartsWith(qc, StringComparison.Ordinal)) rank = 2;
            else if (sc.Length > 0 && sc.StartsWith(qc, StringComparison.Ordinal)) rank = 3;
            else if (tokens.All(t => c.Contains(t, StringComparison.Ordinal) || sc.Contains(t, StringComparison.Ordinal))) rank = 4;
            else continue;
            if (item.IsPreset) rank += 5;
            results.Add((item, rank, c.Length));
        }
        if (results.Count == 0 && qc.Length >= 4)
        {
            int maxD = Math.Max(2, qc.Length / 6);
            foreach (var (item, c, _, sc) in _all)
            {
                int d = Math.Min(Levenshtein.Distance(qc, c, maxD), sc.Length > 0 ? Levenshtein.Distance(qc, sc, maxD) : int.MaxValue);
                if (d <= maxD) results.Add((item, 10 + d, c.Length));
            }
        }
        return results.OrderBy(r => r.Rank).ThenBy(r => r.Len).ThenBy(r => r.Item.Name, StringComparer.Ordinal)
            .Take(limit).Select(r => r.Item).ToList();
    }
}

public static class Levenshtein
{
    /// <summary>Bounded edit distance; returns max+1 when the distance exceeds max.</summary>
    public static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (cur[j] < rowMin) rowMin = cur[j];
            }
            if (rowMin > max) return max + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
