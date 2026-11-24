using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Economics;
using TarkovOverlay.Core.Text;

namespace TarkovOverlay.Core.Search;

/// <summary>One line of the in-game search overlay.</summary>
public sealed record QuickSearchRow(CatalogItem Item, string Name, string? ShortName, string Flea, string Trader, string? PerSlot, ValueTier? Tier);

/// <summary>
/// 1.2.0 in-game search bar: ranks catalog items for a typed query (exact name / short name first, then prefix,
/// then word-prefix, then the app's fuzzy search) and formats each with flea, trader and per-slot value.
/// </summary>
public static class QuickSearch
{
    public static IReadOnlyList<QuickSearchRow> Run(CatalogContext ctx, string? query, int limit = 12)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<QuickSearchRow>();
        return Rank(ctx, query, limit).Select(i => Row(ctx, i)).ToList();
    }

    public static IReadOnlyList<CatalogItem> Rank(CatalogContext ctx, string query, int limit = 12)
    {
        var q = NameNormalizer.Normalize(query);
        if (q.Length == 0) return Array.Empty<CatalogItem>();
        var pool = ctx.Items.Search(query, Math.Max(limit * 4, 60));
        return pool
            .Select((item, i) => (item, i, score: Score(item, q)))
            .OrderBy(x => x.score).ThenBy(x => x.i)
            .Select(x => x.item)
            .GroupBy(x => x.Id).Select(g => g.First())
            .Take(limit)
            .ToList();
    }

    /// <summary>Lower is better: 0 exact short name, 1 exact name, 2 short-name prefix, 3 name prefix, 4 word prefix, 5 other.</summary>
    public static int Score(CatalogItem item, string normalizedQuery)
    {
        var name = NameNormalizer.Normalize(item.Name);
        var sn = item.ShortName is { } s ? NameNormalizer.Normalize(s) : "";
        if (sn.Length > 0 && sn == normalizedQuery) return 0;
        if (name == normalizedQuery) return 1;
        if (sn.Length > 0 && sn.StartsWith(normalizedQuery, StringComparison.Ordinal)) return 2;
        if (name.StartsWith(normalizedQuery, StringComparison.Ordinal)) return 3;
        if (name.Split(' ').Any(w => w.StartsWith(normalizedQuery, StringComparison.Ordinal))) return 4;
        return 5;
    }

    public static QuickSearchRow Row(CatalogContext ctx, CatalogItem item)
    {
        var s = ctx.Prices.Summarize(item);
        string flea = s.Flea.State == FleaState.Unavailable || s.Flea.Average is not { } f ? "flea —" : $"flea {Money.Rub(f)}{(s.Flea.State == FleaState.Stale ? " (stale)" : "")}";
        string trader = s.BestTrader is { } t ? $"{ctx.Snapshot.TraderName(t.TraderId)} {Money.Rub(t.PriceRub)}" : "trader —";
        string? perSlot = s.PerSlot is { } p ? $"{ValueTiers.Symbol(p.Tier)} {Money.Rub(p.PerSlotValue)}/slot" : null;
        return new QuickSearchRow(item, item.Name, item.ShortName, flea, trader, perSlot, s.PerSlot?.Tier);
    }
}
