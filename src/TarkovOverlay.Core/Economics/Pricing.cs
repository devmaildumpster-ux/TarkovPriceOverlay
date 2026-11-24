using TarkovOverlay.Core.Catalog;

namespace TarkovOverlay.Core.Economics;

public enum FleaState { Fresh, Stale, Unavailable }
public enum FleaUnavailableReason { None, ModeDisabled, ItemRestricted, NoPrice }
public enum SaleRoute { Trader, FleaNet }
public enum ValueTier { Low, Decent, Good, Premium }

public sealed record FleaQuote(long? Average, FleaState State, FleaUnavailableReason Reason, int? MinLevel, DateTimeOffset? LastScan);

public sealed record FeeEstimate(long Fee, long Net);

public sealed record BuyBack(long PriceRub, long Price, string Currency, string Source, string? TraderId, int? LoyaltyLevel, string? TaskUnlockId, bool Approximate);

public sealed record PerSlot(long Amount, long PerSlotValue, int Footprint, SaleRoute Route, ValueTier Tier);

public sealed record PriceSummary(
    CatalogItem Item, FleaQuote Flea, FeeEstimate? FleaNet, TraderSale? BestTrader, BuyBack? BuyBack, int? Footprint, PerSlot? PerSlot);

public static class Rounding
{
    public static long HalfAwayFromZero(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
}

/// <summary>Flea listing-fee estimate for one unit at the 24h average. Omits personal fee discounts.</summary>
public static class FleaFee
{
    public static FeeEstimate? Estimate(long? basePrice, long? ask, double? sellOfferFeeRate, double? sellRequirementFeeRate)
    {
        if (basePrice is not > 0 || ask is not > 0 || sellOfferFeeRate is not >= 0 || sellRequirementFeeRate is not >= 0) return null;
        double b = basePrice.Value, a = ask.Value;
        double of = Math.Log10(b / a);
        double rf = Math.Log10(a / b);
        if (a < b) of = Math.Pow(of, 1.08);
        else rf = Math.Pow(rf, 1.08);
        double fee = b * sellOfferFeeRate.Value * Math.Pow(4, of) + a * sellRequirementFeeRate.Value * Math.Pow(4, rf);
        if (!double.IsFinite(fee)) return null;
        long f = Rounding.HalfAwayFromZero(fee);
        return new FeeEstimate(f, ask.Value - f);
    }
}

public static class ValueTiers
{
    public static readonly long[] TraderCuts = { 5_000, 10_000, 15_000 };
    public static readonly long[] FleaCuts = { 15_000, 30_000, 75_000 };

    public static ValueTier For(SaleRoute route, long perSlot)
    {
        var cuts = route == SaleRoute.Trader ? TraderCuts : FleaCuts;
        if (perSlot < cuts[0]) return ValueTier.Low;
        if (perSlot < cuts[1]) return ValueTier.Decent;
        if (perSlot < cuts[2]) return ValueTier.Good;
        return ValueTier.Premium;
    }

    public static string Symbol(ValueTier t) => t switch
    {
        ValueTier.Low => "▼", ValueTier.Decent => "●", ValueTier.Good => "▲", _ => "★",
    };

    public static string Word(ValueTier t) => t switch
    {
        ValueTier.Low => "low", ValueTier.Decent => "decent", ValueTier.Good => "good", _ => "premium",
    };

    /// <summary>Palette: #AAB9BA gray, #AAC8F0 blue, #A4DFC1 green, #E8C784 gold.</summary>
    public static string ColorHex(ValueTier t) => t switch
    {
        ValueTier.Low => "#AAB9BA", ValueTier.Decent => "#AAC8F0", ValueTier.Good => "#A4DFC1", _ => "#E8C784",
    };
}

public sealed class PriceCalculator
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(10);

    private readonly MarketSettings? _market;
    private readonly Func<DateTimeOffset> _now;

    public PriceCalculator(MarketSettings? market, Func<DateTimeOffset>? now = null)
    {
        _market = market;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public FleaQuote Flea(CatalogItem item)
    {
        int? minLevel = MaxOrNull(item.MinLevelForFlea, _market?.MinPlayerLevel);
        if (_market is null || !_market.Enabled)
            return new FleaQuote(item.Avg24hPrice, FleaState.Unavailable, FleaUnavailableReason.ModeDisabled, minLevel, item.LastScan);
        if (item.IsNoFlea)
            return new FleaQuote(item.Avg24hPrice, FleaState.Unavailable, FleaUnavailableReason.ItemRestricted, minLevel, item.LastScan);
        if (item.Avg24hPrice is not > 0)
            return new FleaQuote(null, FleaState.Unavailable, FleaUnavailableReason.NoPrice, minLevel, item.LastScan);
        return new FleaQuote(item.Avg24hPrice, IsFresh(item.LastScan) ? FleaState.Fresh : FleaState.Stale, FleaUnavailableReason.None, minLevel, item.LastScan);
    }

    public bool IsFresh(DateTimeOffset? scan)
    {
        if (scan is null) return false;
        var now = _now();
        if (scan.Value > now + FutureTolerance) return false; // implausibly far in the future
        return now - scan.Value <= StaleAfter;
    }

    private static int? MaxOrNull(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>Highest positive sellToTrader by rouble equivalent.</summary>
    public static TraderSale? BestTrader(CatalogItem item) =>
        item.SellToTrader.Where(s => s.PriceRub > 0).OrderByDescending(s => s.PriceRub).ThenBy(s => s.TraderId, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>Lowest cash trader offer (rouble-equivalent comparison); else a fresh flea low with listings, marked approximate.</summary>
    public BuyBack? BuyBack(CatalogItem item, FleaQuote flea)
    {
        var cash = item.BuyFromTrader.Where(b => b.PriceRub > 0 && b.Currency is "RUB" or "USD" or "EUR")
            .OrderBy(b => b.PriceRub).ThenBy(b => b.MinTraderLevel).FirstOrDefault();
        if (cash != null)
            return new BuyBack(cash.PriceRub, cash.Price, cash.Currency, "trader", cash.TraderId, cash.MinTraderLevel, cash.TaskUnlockId, false);
        if (flea.State != FleaState.Unavailable || flea.Reason == FleaUnavailableReason.NoPrice)
        {
            if (_market is { Enabled: true } && !item.IsNoFlea && item.LastLowPrice is > 0 && item.LastOfferCount is > 0 && IsFresh(item.LastScan))
                return new BuyBack(item.LastLowPrice.Value, item.LastLowPrice.Value, "RUB", "flea low", null, null, null, true);
        }
        return null;
    }

    public PriceSummary Summarize(CatalogItem item)
    {
        var flea = Flea(item);
        FeeEstimate? net = flea.State == FleaState.Unavailable ? null
            : FleaFee.Estimate(item.BasePrice, flea.Average, _market?.SellOfferFeeRate, _market?.SellRequirementFeeRate);
        var trader = BestTrader(item);
        var buy = BuyBack(item, flea);
        var footprint = item.Footprint;

        PerSlot? per = null;
        if (footprint is > 0)
        {
            long? traderAmt = trader?.PriceRub;
            long? fleaAmt = flea.State == FleaState.Fresh && net is { Net: > 0 } ? net.Net : null; // stale flea never drives per-slot
            (long Amount, SaleRoute Route)? pick = null;
            if (traderAmt is > 0) pick = (traderAmt.Value, SaleRoute.Trader);
            if (fleaAmt is > 0 && (pick is null || fleaAmt.Value > pick.Value.Amount)) pick = (fleaAmt.Value, SaleRoute.FleaNet);
            if (pick is { } p)
            {
                long ps = Rounding.HalfAwayFromZero((double)p.Amount / footprint.Value);
                per = new PerSlot(p.Amount, ps, footprint.Value, p.Route, ValueTiers.For(p.Route, ps));
            }
        }
        return new PriceSummary(item, flea, net, trader, buy, footprint, per);
    }
}

/// <summary>Totals for a stack of <see cref="Count"/> units. PerSlot uses the stack total (a stack fills one cell).</summary>
public sealed record StackValue(int Count, long? TraderTotal, long? FleaNetTotal, bool FleaStale, PerSlot? PerSlot);

public static class StackValues
{
    /// <summary>
    /// Trader total = best trader unit price × count. Flea net total = per-unit net × count (the listing fee
    /// scales with quantity). Only fresh flea can drive the per-slot route, as for single items.
    /// </summary>
    public static StackValue? For(PriceSummary s, int count)
    {
        if (count < 1) return null;
        long? trader = s.BestTrader is { PriceRub: > 0 } t ? t.PriceRub * count : null;
        long? flea = s.Flea.State != FleaState.Unavailable && s.FleaNet is { Net: > 0 } n ? n.Net * count : null;
        bool stale = s.Flea.State == FleaState.Stale;
        int footprint = s.Footprint is > 0 ? s.Footprint.Value : 1;
        (long Amount, SaleRoute Route)? pick = null;
        if (trader is > 0) pick = (trader.Value, SaleRoute.Trader);
        if (!stale && flea is > 0 && (pick is null || flea.Value > pick.Value.Amount)) pick = (flea.Value, SaleRoute.FleaNet);
        PerSlot? per = null;
        if (pick is { } p)
        {
            long ps = Rounding.HalfAwayFromZero((double)p.Amount / footprint);
            per = new PerSlot(p.Amount, ps, footprint, p.Route, ValueTiers.For(p.Route, ps));
        }
        return new StackValue(count, trader, flea, stale, per);
    }
}
