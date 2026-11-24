using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Economics;

namespace TarkovOverlay.Core.Ammo;

public enum AmmoSort { Penetration, Damage, ArmorDamage, FragChance, Speed, FleaPrice, TraderPrice, Name }

/// <summary>One round on the ammo chart (stats + this mode's flea / trader buy prices).</summary>
public sealed record AmmoRow(
    CatalogItem Item,
    AmmoStats Stats,
    string DisplayName,
    long? FleaPrice,
    TraderBuy? Trader,
    string? TraderLabel,
    IReadOnlyList<int> Armor)
{
    public string DamageText => Stats.IsMultiProjectile && Stats.Damage is { } d ? $"{Stats.Pellets}×{d}" : Stats.Damage?.ToString() ?? "—";
    public string PenText => Stats.Penetration?.ToString() ?? "—";
    public string ArmorDmgText => Stats.ArmorDamage is { } a ? a + "%" : "—";
    public string FragText => Stats.FragmentationChance is { } f ? $"{(int)Math.Round(f * 100, MidpointRounding.AwayFromZero)}%" : "—";
    public string SpeedText => Stats.Speed is { } s ? s + " m/s" : "—";
    public string TracerText => Stats.IsTracer ? (string.IsNullOrWhiteSpace(Stats.TracerColor) ? "Yes" : "Yes (" + Stats.TracerColor + ")") : "—";
}

/// <summary>
/// 1.3.0: builds the ammo chart from the already-cached catalog (no extra HTTP). Groups by caliber, sorts and
/// filters, and resolves ammo packs to the rounds inside them. Respects whatever PvP / PvE snapshot is current.
/// </summary>
public static class AmmoChart
{
    /// <summary>The item's own ammo, or the first ammo inside an ammo pack.</summary>
    public static AmmoStats? StatsOf(CatalogItem item, CatalogSnapshot snap)
    {
        if (item.Ammo != null) return item.Ammo;
        if (item.IsAmmoPack && item.ContainsItems.Count > 0 && snap.Item(item.ContainsItems[0].ItemId) is { Ammo: { } inner })
            return inner;
        return null;
    }

    public static CatalogItem? RoundOf(CatalogItem item, CatalogSnapshot snap) =>
        item.Ammo != null ? item
        : item.IsAmmoPack && item.ContainsItems.Count > 0 && snap.Item(item.ContainsItems[0].ItemId) is { Ammo: not null } inner ? inner
        : null;

    public static IReadOnlyList<(string Id, string Display, int Count)> Calibers(CatalogSnapshot snap) =>
        snap.Items.Values.Where(i => i.Ammo != null)
            .GroupBy(i => i.Ammo!.Caliber, StringComparer.Ordinal)
            .Select(g => (g.Key, AmmoCaliber.Display(g.Key), g.Count()))
            .OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<AmmoRow> Rows(CatalogSnapshot snap, string? caliber, string? search = null,
        bool tracersOnly = false, bool tradersOnly = false, AmmoSort sort = AmmoSort.Penetration, bool descending = true)
    {
        IEnumerable<CatalogItem> src = snap.Items.Values.Where(i => i.Ammo != null);
        if (!string.IsNullOrEmpty(caliber)) src = src.Where(i => i.Ammo!.Caliber == caliber);
        var rows = src.Select(i => ToRow(i, snap));
        if (tracersOnly) rows = rows.Where(r => r.Stats.IsTracer);
        if (tradersOnly) rows = rows.Where(r => r.Trader != null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            rows = rows.Where(r => r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Item.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.Item.ShortName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        IOrderedEnumerable<AmmoRow> ordered = sort switch
        {
            AmmoSort.Damage => Order(rows, r => r.Stats.TotalDamage ?? r.Stats.Damage, descending),
            AmmoSort.ArmorDamage => Order(rows, r => r.Stats.ArmorDamage, descending),
            AmmoSort.FragChance => Order(rows, r => r.Stats.FragmentationChance, descending),
            AmmoSort.Speed => Order(rows, r => r.Stats.Speed, descending),
            AmmoSort.FleaPrice => Order(rows, r => r.FleaPrice, descending),
            AmmoSort.TraderPrice => Order(rows, r => r.Trader?.PriceRub, descending),
            AmmoSort.Name => descending
                ? rows.OrderByDescending(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => Order(rows, r => r.Stats.Penetration, descending),
        };
        return ordered.ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IOrderedEnumerable<AmmoRow> Order<T>(IEnumerable<AmmoRow> rows, Func<AmmoRow, T?> key, bool desc) where T : struct =>
        desc ? rows.OrderByDescending(r => key(r).HasValue).ThenByDescending(r => key(r))
             : rows.OrderByDescending(r => key(r).HasValue).ThenBy(r => key(r));

    public static AmmoRow ToRow(CatalogItem item, CatalogSnapshot snap)
    {
        var st = item.Ammo ?? throw new ArgumentException("item has no ammo stats", nameof(item));
        var name = !string.IsNullOrWhiteSpace(item.ShortName) ? item.ShortName! : item.Name;
        long? flea = !item.IsNoFlea ? item.LastLowPrice ?? item.Avg24hPrice : null;
        var buy = item.BuyFromTrader.Where(b => b.PriceRub > 0)
            .OrderBy(b => b.PriceRub).ThenBy(b => b.MinTraderLevel).FirstOrDefault();
        string? trader = buy == null ? null : $"{snap.TraderName(buy.TraderId)} LL{buy.MinTraderLevel}";
        return new AmmoRow(item, st, name, flea, buy, trader, ArmorEffect.For(st.Penetration));
    }

    public static string FormatFrag(double? f) => f is { } v ? $"{(int)Math.Round(v * 100, MidpointRounding.AwayFromZero)}%" : "—";
    public static string FormatFlea(AmmoRow r) => r.FleaPrice is { } p ? Money.Rub(p) : "—";
    public static string FormatTrader(AmmoRow r) =>
        r.Trader is { } t ? $"{r.TraderLabel} {Money.Rub(t.PriceRub)}" : "—";
}
