using System.Globalization;
using System.IO.Compression;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Economics;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Core.Recognition;
using TarkovOverlay.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace TarkovOverlay.Tests;

/// <summary>
/// Stack count: cell location + digit isolation (pure pixels) and digits-only Tesseract reads.
/// FIXTURE KIND: synthetic rendered-font stash captures (tools/make_stack_fixtures.py: periodic grid, icon-like
/// cells, white short names, neighbour counts, DejaVu Sans Bold outlined count; 100/125/150%).
/// </summary>
public class StackCountTests
{
    private readonly ITestOutputHelper _out;
    public StackCountTests(ITestOutputHelper o) => _out = o;

    public sealed record Case(string File, double Scale, int ScreenHeight, int Cx, int Cy, PixelRect Tooltip, int Expected);

    public static IEnumerable<Case> All() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "stack", "manifest.tsv"))
            .Where(l => l.Length > 0).Select(l => l.Split('\t')).Select(p => new Case(p[0], double.Parse(p[1], CultureInfo.InvariantCulture),
                int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4]), new PixelRect(int.Parse(p[5]), int.Parse(p[6]), int.Parse(p[7]), int.Parse(p[8])), int.Parse(p[9])));

    public static IEnumerable<object[]> Files() => All().Select(c => new object[] { c.File });

    static Case Get(string file) => All().Single(c => c.File == file);

    static RgbImage Load(string file)
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "stack", file)), CompressionMode.Decompress);
        return Ppm.Read(gz);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Locator_FindsHoveredCell_AndIsolatesDigits_OrReportsHidden(string file)
    {
        var c = Get(file);
        var img = Load(file);
        var r = StackCountLocator.Locate(img, c.Cx, c.Cy, c.Tooltip, StackCountLocator.NominalPitch(c.ScreenHeight));
        _out.WriteLine($"{file}: {r.Status} cell {r.Cell} pitch {r.Pitch} digits {r.DigitBounds} {r.Note}");
        Assert.NotNull(r.Cell);
        Assert.True(r.Cell!.Value.Contains(c.Cx, c.Cy), "cell must contain the cursor");
        Assert.InRange(r.Pitch, (int)Math.Round(64 * c.Scale) - 1, (int)Math.Round(64 * c.Scale) + 1);
        if (c.Expected < 0) { Assert.Equal(StackLocateStatus.HiddenByTooltip, r.Status); return; }
        Assert.Equal(StackLocateStatus.Found, r.Status);
        // digits sit in the hovered cell's bottom-right
        var d = r.DigitBounds!.Value;
        Assert.True(d.X >= r.Cell.Value.X + r.Pitch / 4 && d.Right <= r.Cell.Value.Right && d.Bottom <= r.Cell.Value.Bottom);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Reader_ReadsCount_WithTesseractDigits_NeverWrong(string file)
    {
        var engine = TesseractTests.Engine.Value;
        Assert.NotNull(engine);
        var c = Get(file);
        var reader = new StackCountReader(engine);
        var r = await reader.ReadAsync(Load(file), c.Cx, c.Cy, c.Tooltip, c.ScreenHeight, 500, default);
        _out.WriteLine($"{file}: {r.Count?.ToString() ?? "null"} · {r.Note} · {string.Join(" | ", r.Reads)}");
        if (c.Expected < 0) Assert.Null(r.Count);
        else Assert.Equal(c.Expected, r.Count);
    }

    [Fact]
    public void Locator_FeaturelessImage_NoGrid()
    {
        var img = new RgbImage(670, 360);
        img.Fill(new PixelRect(0, 0, 670, 360), 20, 20, 20);
        Assert.Equal(StackLocateStatus.NoGrid, StackCountLocator.Locate(img, 245, 125, null, 64).Status);
    }

    private sealed class FakeEngine(string name, string text) : IOcrEngine
    {
        public string Name => name;
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public int? MaxImageDimension => null;
        public Task<OcrRead> RecognizeAsync(RgbImage panel, double scale, CancellationToken ct) =>
            Task.FromResult(new OcrRead(name, scale, text.Length == 0 ? Array.Empty<string>() : new[] { text }, panel.Width, panel.Height, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("70", "70", 80, 70)]
    [InlineData("70", "", 80, 70)]       // one engine silent: the other's valid read is used
    [InlineData("70", "78", 80, null)]   // disagreement → not read
    [InlineData("95", "95", 80, null)]   // above stackMaxSize → rejected
    [InlineData("0", "0", 80, null)]     // zero is never a stack count
    [InlineData("7O", "", 80, 70)]       // O→0 inside digits
    [InlineData("60/60", "", 80, null)]  // capacity style text is not a plain count
    [InlineData("abc", "", 80, null)]
    public async Task Reader_Validation(string a, string b, int max, int? expected)
    {
        var reader = new StackCountReader(new FakeEngine("A", a), new FakeEngine("B", b));
        var img = new RgbImage(20, 12);
        var r = await reader.ReadDigitsAsync(img, max, default);
        Assert.Equal(expected, r.Count);
    }

    // ---------------- economics + card ----------------
    private static CatalogContext Ctx() => LiveCatalog.RegularContext;

    [Fact]
    public void StackTotals_MultiplyUnitPrices_AndTierUsesStackTotal()
    {
        var ctx = Ctx();
        var ammo = ctx.Snapshot.Items.Values.First(i => i.IsAmmo && i.StackMaxSize is > 1 && PriceCalculator.BestTrader(i) is { PriceRub: > 20 });
        var s = ctx.Prices.Summarize(ammo);
        var v = StackValues.For(s, 70)!;
        Assert.Equal(s.BestTrader!.PriceRub * 70, v.TraderTotal);
        if (s.FleaNet is { Net: > 0 } n && s.Flea.State != FleaState.Unavailable) Assert.Equal(n.Net * 70, v.FleaNetTotal);
        Assert.NotNull(v.PerSlot);
        Assert.Equal(1, v.PerSlot!.Footprint);
        Assert.Equal(v.PerSlot.Amount, v.PerSlot.PerSlotValue);
        Assert.Equal(ValueTiers.For(v.PerSlot.Route, v.PerSlot.Amount), v.PerSlot.Tier);
        Assert.Null(StackValues.For(s, 0));
    }

    private static PriceSummary Synthetic(long traderUnit, long? fleaNetUnit, FleaState fleaState)
    {
        var item = new CatalogItem { Id = "x", Name = "Test round", Width = 1, Height = 1, StackMaxSize = 60, Types = new[] { "ammo" } };
        var flea = new FleaQuote(fleaNetUnit is null ? null : fleaNetUnit + 10, fleaState, fleaState == FleaState.Unavailable ? FleaUnavailableReason.ItemRestricted : FleaUnavailableReason.None, null, null);
        return new PriceSummary(item, flea, fleaNetUnit is { } n ? new FeeEstimate(10, n) : null,
            new TraderSale("t", traderUnit, traderUnit, "RUB"), null, 1, null);
    }

    [Theory]
    // trader-only (flea restricted): tiers at 5k/10k/15k on the stack total
    [InlineData(100, null, FleaState.Unavailable, 49, 4_900, SaleRoute.Trader, ValueTier.Low)]
    [InlineData(100, null, FleaState.Unavailable, 50, 5_000, SaleRoute.Trader, ValueTier.Decent)]
    [InlineData(100, null, FleaState.Unavailable, 99, 9_900, SaleRoute.Trader, ValueTier.Decent)]
    [InlineData(100, null, FleaState.Unavailable, 100, 10_000, SaleRoute.Trader, ValueTier.Good)]
    [InlineData(250, null, FleaState.Unavailable, 59, 14_750, SaleRoute.Trader, ValueTier.Good)]
    [InlineData(250, null, FleaState.Unavailable, 60, 15_000, SaleRoute.Trader, ValueTier.Premium)]
    // fresh flea net beats trader: flea cuts 15k/30k/75k
    [InlineData(100, 500, FleaState.Fresh, 29, 14_500, SaleRoute.FleaNet, ValueTier.Low)]
    [InlineData(100, 500, FleaState.Fresh, 30, 15_000, SaleRoute.FleaNet, ValueTier.Decent)]
    [InlineData(100, 1_000, FleaState.Fresh, 30, 30_000, SaleRoute.FleaNet, ValueTier.Good)]
    [InlineData(100, 1_250, FleaState.Fresh, 60, 75_000, SaleRoute.FleaNet, ValueTier.Premium)]
    // stale flea never drives the rating
    [InlineData(100, 1_250, FleaState.Stale, 60, 6_000, SaleRoute.Trader, ValueTier.Decent)]
    public void StackTiers_AtCutPoints(long traderUnit, int? fleaNet, FleaState state, int count, long amount, SaleRoute route, ValueTier tier)
    {
        var v = StackValues.For(Synthetic(traderUnit, fleaNet, state), count)!;
        if (fleaNet is { } fn && state != FleaState.Unavailable) Assert.Equal((long)fn * count, v.FleaNetTotal);
        Assert.Equal(traderUnit * count, v.TraderTotal);
        Assert.Equal(amount, v.PerSlot!.Amount);
        Assert.Equal(route, v.PerSlot.Route);
        Assert.Equal(tier, v.PerSlot.Tier);
    }

    [Fact]
    public void Card_WithCount_ShowsThisStackAndFullStack_PerUnitLabelled()
    {
        var ctx = Ctx();
        var ammo = ctx.Snapshot.Items.Values.First(i => i.IsAmmo && i.StackMaxSize is > 10 && PriceCalculator.BestTrader(i) is { PriceRub: > 20 });
        var b = new CardBuilder(ctx, null);
        var m = new MatchResult(MatchKind.Verified, new[] { ammo }, ammo.Name);
        var card = b.ForMatch(m, new StackReading(7, "read", Array.Empty<string>()));
        _out.WriteLine(string.Join("\n", card.Rows.Select(r => $"{r.Label} | {r.Value} | {r.Detail}")));
        Assert.StartsWith("This stack · 7", card.Rows[0].Label);
        Assert.Contains(Money.Rub(PriceCalculator.BestTrader(ammo)!.PriceRub * 7), card.Rows[0].Detail);
        Assert.StartsWith($"Full stack · {ammo.StackMaxSize}", card.Rows[1].Label);
        Assert.Contains(card.Rows, r => r.Label == "Trader · each");
        Assert.DoesNotContain(card.Rows, r => r.Label.StartsWith("Per slot"));
        Assert.NotNull(card.Rows[0].Tier);
    }

    [Fact]
    public void Card_CountNotRead_FallsBackToPerUnit_WithNote_AndSameItem()
    {
        var ctx = Ctx();
        var ammo = ctx.Snapshot.Items.Values.First(i => i.IsAmmo && i.StackMaxSize is > 10 && PriceCalculator.BestTrader(i) is { PriceRub: > 20 });
        var b = new CardBuilder(ctx, null);
        var m = new MatchResult(MatchKind.Verified, new[] { ammo }, ammo.Name);
        var plain = b.ForMatch(m);
        var notRead = b.ForMatch(m, StackReading.NotRead("count hidden by the name tooltip"));
        Assert.Equal(plain.Title, notRead.Title);
        Assert.Equal(plain.ItemId, notRead.ItemId);
        Assert.Equal(plain.Rows.Select(r => r.Value), notRead.Rows.Select(r => r.Value)); // identical per-unit numbers
        Assert.Contains(notRead.Rows, r => r.Label.StartsWith("Full stack"));
        Assert.Contains(notRead.Rows, r => r.Label.StartsWith("Per slot"));
        Assert.Contains("Stack count not read", notRead.Facts[0]);
        Assert.DoesNotContain(plain.Facts, f => f.Contains("not read"));
    }

    [Fact]
    public void NonStackable_IgnoresStackReading()
    {
        var ctx = Ctx();
        var gun = ctx.Snapshot.Items.Values.First(i => i.StackMaxSize is null or 1 && PriceCalculator.BestTrader(i) != null);
        var b = new CardBuilder(ctx, null);
        var m = new MatchResult(MatchKind.Verified, new[] { gun }, gun.Name);
        var a = b.ForMatch(m);
        var c = b.ForMatch(m, new StackReading(5, "read", Array.Empty<string>()));
        Assert.Equal(a.Rows.Select(r => r.Label + r.Value), c.Rows.Select(r => r.Label + r.Value));
    }
}
