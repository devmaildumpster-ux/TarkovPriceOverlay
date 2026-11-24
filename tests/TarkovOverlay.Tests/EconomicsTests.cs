using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Economics;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>Section 10B economics. FIXTURE KIND: synthetic item records (text/object fixtures).</summary>
public class EconomicsTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);
    static readonly MarketSettings Market = new(true, 15, 0.05, 0.05);
    static PriceCalculator Calc(MarketSettings? m = null) => new(m ?? Market, () => Now);

    static CatalogItem Item(long? basePrice = 100_000, long? avg = 100_000, int? w = 1, int? h = 1, DateTimeOffset? scan = null,
        TraderSale[]? sell = null, TraderBuy[]? buy = null, string[]? types = null, long? low = null, int? offers = null, bool noScan = false) => new()
    {
        Id = "test", Name = "Test item", Width = w, Height = h, BasePrice = basePrice, Avg24hPrice = avg,
        LastScan = noScan ? null : scan ?? Now.AddMinutes(-30), SellToTrader = sell ?? Array.Empty<TraderSale>(),
        BuyFromTrader = buy ?? Array.Empty<TraderBuy>(), Types = types ?? Array.Empty<string>(), LastLowPrice = low, LastOfferCount = offers,
    };

    [Fact]
    public void Fee_SyntheticCheck_100k_Base_100k_Ask()
    {
        var f = FleaFee.Estimate(100_000, 100_000, 0.05, 0.05)!;
        Assert.Equal(10_000, f.Fee);
        Assert.Equal(90_000, f.Net);
        var s = Calc().Summarize(Item(w: 2, h: 1));
        Assert.Equal(45_000, s.PerSlot!.PerSlotValue);
        Assert.Equal(SaleRoute.FleaNet, s.PerSlot.Route);
        Assert.Equal(ValueTier.Good, s.PerSlot.Tier);
    }

    [Fact]
    public void Fee_AskBelowAndAboveBase_FollowsFormula()
    {
        // ask < base: of = log10(b/a)^1.08, rf = log10(a/b)
        double b = 50_000, a = 20_000;
        double of = Math.Pow(Math.Log10(b / a), 1.08), rf = Math.Log10(a / b);
        long expected = (long)Math.Round(b * 0.05 * Math.Pow(4, of) + a * 0.05 * Math.Pow(4, rf), MidpointRounding.AwayFromZero);
        Assert.Equal(expected, FleaFee.Estimate(50_000, 20_000, 0.05, 0.05)!.Fee);
        // ask > base
        b = 10_000; a = 80_000;
        of = Math.Log10(b / a); rf = Math.Pow(Math.Log10(a / b), 1.08);
        expected = (long)Math.Round(b * 0.05 * Math.Pow(4, of) + a * 0.05 * Math.Pow(4, rf), MidpointRounding.AwayFromZero);
        Assert.Equal(expected, FleaFee.Estimate(10_000, 80_000, 0.05, 0.05)!.Fee);
        Assert.Equal(80_000 - expected, FleaFee.Estimate(10_000, 80_000, 0.05, 0.05)!.Net);
    }

    [Fact]
    public void Fee_RoundsHalfAwayFromZero()
    {
        // base=ask=10 at rates 0.025 each -> fee exactly 0.5 -> rounds to 1
        Assert.Equal(1, FleaFee.Estimate(10, 10, 0.025, 0.025)!.Fee);
        Assert.Equal(1, Rounding.HalfAwayFromZero(0.5));
        Assert.Equal(3, Rounding.HalfAwayFromZero(2.5));
        Assert.Equal(-3, Rounding.HalfAwayFromZero(-2.5));
    }

    [Theory]
    [InlineData(null, 100L)]
    [InlineData(100L, null)]
    [InlineData(0L, 100L)]
    [InlineData(100L, 0L)]
    public void Fee_InvalidInputs_AreUnavailable(long? b, long? a) => Assert.Null(FleaFee.Estimate(b, a, 0.05, 0.05));

    [Fact]
    public void Fee_MissingRates_Unavailable() => Assert.Null(FleaFee.Estimate(100, 100, null, 0.05));

    [Theory]
    [InlineData(4_999, ValueTier.Low)]
    [InlineData(5_000, ValueTier.Decent)]
    [InlineData(9_999, ValueTier.Decent)]
    [InlineData(10_000, ValueTier.Good)]
    [InlineData(14_999, ValueTier.Good)]
    [InlineData(15_000, ValueTier.Premium)]
    public void TraderThresholds(long v, ValueTier t) => Assert.Equal(t, ValueTiers.For(SaleRoute.Trader, v));

    [Theory]
    [InlineData(14_999, ValueTier.Low)]
    [InlineData(15_000, ValueTier.Decent)]
    [InlineData(29_999, ValueTier.Decent)]
    [InlineData(30_000, ValueTier.Good)]
    [InlineData(74_999, ValueTier.Good)]
    [InlineData(75_000, ValueTier.Premium)]
    public void FleaNetThresholds(long v, ValueTier t) => Assert.Equal(t, ValueTiers.For(SaleRoute.FleaNet, v));

    [Fact]
    public void Symbols_AndColors_Distinct()
    {
        Assert.Equal(new[] { "▼", "●", "▲", "★" }, Enum.GetValues<ValueTier>().Select(ValueTiers.Symbol));
        Assert.Equal(new[] { "#AAB9BA", "#AAC8F0", "#A4DFC1", "#E8C784" }, Enum.GetValues<ValueTier>().Select(ValueTiers.ColorHex));
    }

    [Fact]
    public void PerSlot_TierUsesRoundedDisplayedValue()
    {
        // 29,999.5 per slot rounds to 30,000 -> Good (consistent with displayed amount)
        var item = Item(basePrice: null, avg: null, w: 2, h: 1, sell: new[] { new TraderSale("t", 29_999, 29_999, "RUB") });
        var s = Calc().Summarize(item);
        Assert.Equal(15_000, s.PerSlot!.PerSlotValue); // 14,999.5 -> 15,000
        Assert.Equal(ValueTier.Premium, s.PerSlot.Tier);
    }

    [Fact]
    public void StaleFlea_OlderThan2h_UsesTraderForPerSlot()
    {
        var item = Item(scan: Now.AddHours(-2).AddMinutes(-1), sell: new[] { new TraderSale("t", 9_000, 9_000, "RUB") });
        var s = Calc().Summarize(item);
        Assert.Equal(FleaState.Stale, s.Flea.State);
        Assert.Equal(SaleRoute.Trader, s.PerSlot!.Route);
        Assert.Equal(9_000, s.PerSlot.PerSlotValue);
        Assert.NotNull(s.FleaNet); // estimate still shown, labelled stale
    }

    [Fact]
    public void Flea_ExactlyTwoHoursOld_IsStillFresh()
    {
        Assert.Equal(FleaState.Fresh, Calc().Flea(Item(scan: Now.AddHours(-2))).State);
    }

    [Fact]
    public void Flea_FutureDated_BeyondTolerance_IsStale_WithinTolerance_Fresh()
    {
        Assert.Equal(FleaState.Stale, Calc().Flea(Item(scan: Now.AddMinutes(11))).State);
        Assert.Equal(FleaState.Fresh, Calc().Flea(Item(scan: Now.AddMinutes(9))).State);
    }

    [Fact]
    public void Flea_MissingScan_IsStale()
    {
        Assert.Equal(FleaState.Stale, Calc().Flea(Item(noScan: true)).State);
    }

    [Fact]
    public void Flea_Absent_IsUnavailableNotZero_WithAndWithoutTrader()
    {
        var noTrader = Calc().Summarize(Item(avg: null));
        Assert.Equal(FleaState.Unavailable, noTrader.Flea.State);
        Assert.Equal(FleaUnavailableReason.NoPrice, noTrader.Flea.Reason);
        Assert.Null(noTrader.Flea.Average);
        Assert.Null(noTrader.PerSlot);
        var withTrader = Calc().Summarize(Item(avg: null, sell: new[] { new TraderSale("t", 1_000, 1_000, "RUB") }));
        Assert.Equal(SaleRoute.Trader, withTrader.PerSlot!.Route);
    }

    [Fact]
    public void Flea_RestrictedItem_And_DisabledMode_AreUnavailable()
    {
        var restricted = Calc().Summarize(Item(types: new[] { "noFlea" }, sell: new[] { new TraderSale("t", 7_000, 7_000, "RUB") }));
        Assert.Equal(FleaUnavailableReason.ItemRestricted, restricted.Flea.Reason);
        Assert.Null(restricted.FleaNet);
        Assert.Equal(SaleRoute.Trader, restricted.PerSlot!.Route);
        var disabled = Calc(new MarketSettings(false, 15, 0.05, 0.05)).Summarize(Item());
        Assert.Equal(FleaUnavailableReason.ModeDisabled, disabled.Flea.Reason);
        Assert.Null(disabled.PerSlot);
    }

    [Fact]
    public void Flea_LevelRequirement_UsesHighestOfItemAndMarket()
    {
        var item = new CatalogItem { Id = "x", Name = "x", MinLevelForFlea = 25, Avg24hPrice = 10, BasePrice = 10, LastScan = Now };
        Assert.Equal(25, Calc().Flea(item).MinLevel);
        Assert.Equal(15, Calc().Flea(Item()).MinLevel);
    }

    [Fact]
    public void TraderPayout_BeatsFreshFleaNet()
    {
        // flea net 90,000 vs trader 95,000
        var s = Calc().Summarize(Item(sell: new[] { new TraderSale("a", 95_000, 95_000, "RUB"), new TraderSale("b", 50_000, 50_000, "RUB") }));
        Assert.Equal(SaleRoute.Trader, s.PerSlot!.Route);
        Assert.Equal(95_000, s.PerSlot.Amount);
        Assert.Equal("a", s.BestTrader!.TraderId);
    }

    [Fact]
    public void BestTrader_ComparesRoubleEquivalent_KeepsOriginalCurrency()
    {
        var s = Calc().Summarize(Item(sell: new[] { new TraderSale("rub", 6_000, 6_000, "RUB"), new TraderSale("pk", 54, 6_622, "USD") }));
        Assert.Equal("pk", s.BestTrader!.TraderId);
        Assert.Equal(54, s.BestTrader.Price);
        Assert.Equal("USD", s.BestTrader.Currency);
        Assert.Equal("$54 (≈₽6,622)", Money.WithRub(54, "USD", 6_622));
        Assert.Equal("€12", Money.Format(12, "EUR"));
    }

    [Fact]
    public void BuyBack_LowestRoubleEquivalentCashOffer_WithCurrencyTraderLoyaltyAndQuest()
    {
        var buy = new[]
        {
            new TraderBuy("mech", 30_000, 30_000, "RUB", 2, null, 5),
            new TraderBuy("pk", 200, 28_000, "USD", 3, "quest1", 2),
            new TraderBuy("skier", 210, 31_000, "EUR", 1, null, 1),
        };
        var s = Calc().Summarize(Item(buy: buy));
        Assert.Equal("pk", s.BuyBack!.TraderId);
        Assert.Equal(28_000, s.BuyBack.PriceRub);
        Assert.Equal(200, s.BuyBack.Price);
        Assert.Equal("USD", s.BuyBack.Currency);
        Assert.Equal(3, s.BuyBack.LoyaltyLevel);
        Assert.Equal("quest1", s.BuyBack.TaskUnlockId);
        Assert.False(s.BuyBack.Approximate);
    }

    [Fact]
    public void BuyBack_FallsBackToFreshFleaLow_MarkedApproximate()
    {
        var s = Calc().Summarize(Item(low: 80_000, offers: 12));
        Assert.True(s.BuyBack!.Approximate);
        Assert.Equal(80_000, s.BuyBack.PriceRub);
        Assert.Equal("flea low", s.BuyBack.Source);
        // stale or no listings -> no fallback
        Assert.Null(Calc().Summarize(Item(low: 80_000, offers: 0)).BuyBack);
        Assert.Null(Calc().Summarize(Item(low: 80_000, offers: 3, scan: Now.AddHours(-5))).BuyBack);
        Assert.Null(Calc().Summarize(Item(low: 80_000, offers: 3, types: new[] { "noFlea" })).BuyBack);
    }

    [Fact]
    public void UnknownFootprint_OmitsPerSlot()
    {
        var s = Calc().Summarize(Item(w: null, h: 2, sell: new[] { new TraderSale("t", 9_000, 9_000, "RUB") }));
        Assert.Null(s.Footprint);
        Assert.Null(s.PerSlot);
    }
}
