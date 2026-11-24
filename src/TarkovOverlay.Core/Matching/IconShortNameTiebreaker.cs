using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Text;

namespace TarkovOverlay.Core.Matching;

/// <summary>
/// 1.3.1: uses the short name painted on the item icon to resolve a "Possible match" / "Several"
/// tooltip result (or a failed tooltip read) without asking the user to pick. A verified full-name
/// match is never changed. If the icon text doesn't agree with the tooltip candidates, the tooltip
/// result is kept (a bad icon read must not override a better name).
/// </summary>
public static class IconShortNameTiebreaker
{
    public const string Repair = "icon short name";

    public static MatchResult Apply(ItemIndex index, MatchResult tooltip, IEnumerable<string>? iconTexts)
    {
        if (tooltip.Kind == MatchKind.Verified) return tooltip;
        if (iconTexts == null) return tooltip;
        MatchResult best = tooltip;
        foreach (var raw in iconTexts)
        {
            var next = ApplyOne(index, tooltip, raw);
            if (Better(next, best)) best = next;
            if (best.Kind == MatchKind.Verified) return best;
        }
        return best;
    }

    public static MatchResult ApplyOne(ItemIndex index, MatchResult tooltip, string? iconText)
    {
        if (tooltip.Kind == MatchKind.Verified) return tooltip;
        var hits = Hits(index, iconText);
        if (hits.Count == 0) return tooltip;

        var pool = Pool(tooltip);
        if (pool != null)
        {
            hits = hits.Where(h => pool.Contains(h.Id)).ToList();
            if (hits.Count == 0) return tooltip; // icon disagrees with the tooltip — keep the tooltip
        }

        string repair = string.IsNullOrEmpty(tooltip.Repair) ? Repair : tooltip.Repair + "; " + Repair;
        if (hits.Count == 1)
            return new MatchResult(MatchKind.Verified, hits, tooltip.Text.Length > 0 ? tooltip.Text : hits[0].Name, repair, tooltip.Stage);
        // Narrowed, but still shared (e.g. loose ammo and its packs all say "M995").
        if (pool != null && hits.Count >= tooltip.Items.Count && tooltip.Kind != MatchKind.None)
            return tooltip;
        return new MatchResult(MatchKind.Several, hits.Take(12).ToList(), tooltip.Text, repair, tooltip.Stage);
    }

    /// <summary>Catalog items whose short name matches the icon text (exact compact, or a unique 1-glyph fold).</summary>
    public static IReadOnlyList<CatalogItem> Hits(ItemIndex index, string? iconText)
    {
        var seen = new Dictionary<string, CatalogItem>(StringComparer.Ordinal);
        foreach (var variant in Variants(iconText))
        {
            foreach (var it in index.ExactShort(variant))
                seen.TryAdd(it.Id, it);
        }
        if (seen.Count > 0) return seen.Values.ToList();
        // One-character OCR slip, only when it points at a single short name.
        var close = index.CloseShort(iconText, maxDistance: 1);
        return close.Count == 1 ? close : Array.Empty<CatalogItem>();
    }

    private static HashSet<string>? Pool(MatchResult tooltip)
    {
        if (tooltip.Items.Count == 0 && tooltip.Alternatives.Count == 0) return null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in tooltip.Items) ids.Add(it.Id);
        foreach (var it in tooltip.Alternatives) ids.Add(it.Id);
        return ids;
    }

    /// <summary>The raw label, plus a copy without a trailing stack-count if the remainder still looks like a name.</summary>
    public static IEnumerable<string> Variants(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        yield return text;
        var c = NameNormalizer.Compact(text);
        int i = c.Length;
        while (i > 0 && char.IsDigit(c[i - 1])) i--;
        if (i >= 3 && i < c.Length) yield return c[..i];
    }

    private static bool Better(MatchResult a, MatchResult b)
    {
        int Sa(MatchResult m) => m.Kind == MatchKind.Verified ? 3 : m.Kind == MatchKind.Several && m.Repair?.Contains(Repair) == true ? 2 : m.Kind != b.Kind && m.Items.Count > 0 && m.Items.Count < Math.Max(2, b.Items.Count) ? 2 : 0;
        if (Sa(a) != Sa(b)) return Sa(a) > Sa(b);
        if (a.Kind == MatchKind.Several && b.Kind == MatchKind.Several) return a.Items.Count < b.Items.Count;
        return false;
    }
}
