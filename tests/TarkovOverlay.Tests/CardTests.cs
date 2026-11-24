using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Economics;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Core.Settings;
using TarkovOverlay.Tests.Fixtures;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>Card presentation (confirmed vs possible match, preview parity). FIXTURE KIND: recorded live catalog + synthetic items.</summary>
public class CardTests
{
    static CatalogContext Ctx => LiveCatalog.RegularContext;
    static CardBuilder Builder(Tracker? t = null) => new(Ctx, t, () => LiveCatalog.FetchedAt);

    [Fact]
    public void ConfirmedHit_ShowsFullCard()
    {
        var m = Ctx.Items.Match("AR-15 5.56x45 SureFire MAG5-60 STANAG 60-round magazine");
        var card = Builder().ForMatch(m);
        Assert.Equal(CardKind.Full, card.Kind);
        Assert.Null(card.Heading);
        Assert.Equal("AR-15 5.56x45 SureFire MAG5-60 STANAG 60-round magazine", card.Title);
        Assert.Contains(card.Rows, r => r.Label == "Flea 24h avg");
        Assert.Contains(card.Status, new[] { "KEEP", "FLEA IF ELIGIBLE", "TRADER", "CHECK" });
        Assert.Equal("PvP", card.ModeCue);
        Assert.All(card.Sections, s => Assert.True(s.Rows.Count <= 2));
    }

    [Fact]
    public void StrongPossibleMatch_ShowsOnlyFleaAndTrader_SameAmountsAsFullCard()
    {
        var m = Ctx.Items.Match("Molot Arms VPO-209 .366 TKM carbne");
        Assert.Equal(MatchKind.PossibleStrong, m.Kind);
        var preview = Builder().ForMatch(m);
        Assert.Equal("Possible match", preview.Heading);
        Assert.All(preview.Rows, r => Assert.Contains(r.Label, new[] { "Flea 24h avg", "Trader" }));
        Assert.InRange(preview.Rows.Count, 1, 2);
        var full = Builder().Full(m.Items[0], compact: true);
        foreach (var row in preview.Rows)
            Assert.Equal(full.Rows.Single(r => r.Label == row.Label).Value, row.Value);
        Assert.Empty(preview.Sections);
    }

    [Fact]
    public void WeakPossibleMatch_ShowsPriceUnderPossibleHeading()
    {
        // 1.2.4: a weak possible match used to show the name only, so the card had no price at all.
        var item = Ctx.Snapshot.Items["59e6687d86f77411d949b251"];
        var card = Builder().Preview(item, strong: false);
        Assert.Equal("Possible match", card.Heading);
        Assert.Equal(CardKind.PossibleWeak, card.Kind);
        Assert.All(card.Rows, r => Assert.Contains(r.Label, new[] { "Flea 24h avg", "Trader" }));
        Assert.NotEmpty(card.Rows);
        Assert.Contains(card.Messages, m => m.Contains("Verify in the main window"));
        Assert.Equal(item.Name, card.Title);
    }

    [Fact]
    public void SeveralCandidates_HeadingAndList()
    {
        var m = new MatchResult(MatchKind.Several, Ctx.Items.Search("PMAG").Take(5).ToList(), "pmag");
        var card = Builder().ForMatch(m);
        Assert.Equal("Several possible matches", card.Heading);
        Assert.Equal(5, card.CandidateIds.Count);
    }

    [Fact]
    public void NoResult_IsCompactAndUseful()
    {
        var card = Builder().ForMatch(MatchResult.NoMatch("qqq zzz"));
        Assert.Equal(CardKind.NoMatch, card.Kind);
        Assert.Empty(card.Rows);
        Assert.True(card.Messages.Count <= 2);
        var nt = CardBuilder.NoTooltip("Item tooltip not visible", "Wait for Tarkov’s item name and retry.");
        Assert.Equal(CardKind.NoTooltip, nt.Kind);
    }

    [Fact]
    public void PerSlotRow_HasSymbolTierAndRouteLabel()
    {
        var calc = new PriceCalculator(new MarketSettings(true, 15, 0.05, 0.05), () => LiveCatalog.FetchedAt);
        var item = new CatalogItem { Id = "x", Name = "X", Width = 2, Height = 1, BasePrice = 100_000, Avg24hPrice = 100_000, LastScan = LiveCatalog.FetchedAt };
        var row = CardBuilder.PerSlotRow(calc.Summarize(item))!;
        Assert.Equal("Per slot · flea net", row.Label);
        Assert.Equal("▲ ₽45,000", row.Value);
        Assert.Equal(ValueTier.Good, row.Tier);
        var traderItem = new CatalogItem { Id = "y", Name = "Y", Width = 1, Height = 1, SellToTrader = new[] { new TraderSale("t", 4_000, 4_000, "RUB") } };
        var row2 = CardBuilder.PerSlotRow(calc.Summarize(traderItem))!;
        Assert.Equal("Per slot · trader", row2.Label);
        Assert.StartsWith("▼", row2.Value);
    }

    [Fact]
    public void Rig_ShowsStorageFootprintGainAndLargestPocket()
    {
        var card = Builder().Full(Ctx.Snapshot.Items["59e7643b86f7742cbf2c109a"], compact: true);
        var rig = Ctx.Snapshot.Items["59e7643b86f7742cbf2c109a"];
        Assert.Contains(card.Facts, f => f.StartsWith("Storage 10 slots") && f.Contains($"{rig.Width}×{rig.Height} outside"));
        Assert.Contains("Largest pocket 2×2", card.Facts);
    }

    [Fact]
    public void LooseRound_VsAmmoPack_FactsDiffer()
    {
        var round = Builder().Full(Ctx.Snapshot.Items["64b7bbb74b75259c590fa897"], true);
        var pack = Builder().Full(Ctx.Snapshot.Items["64aceafcb5bf5e95f50a4c20"], true);
        Assert.Contains(round.Facts, f => f.StartsWith("Per-round price"));
        Assert.Contains(pack.Facts, f => f.StartsWith("Pack:"));
    }

    [Fact]
    public void TrackedNeed_TakesStatusPriority_AndShowsLeftCount()
    {
        var s = new AppSettings();
        var t = new Tracker(s);
        var id = "590c2e1186f77425357b6124";
        var need = Ctx.Hideout.For(id).First();
        t.Track(new TrackedGoal { GoalKey = need.GoalKey, Mode = "regular", Kind = "hideout", Title = need.StationName, ItemIds = { id }, Required = need.Count, Remaining = 1 });
        var card = Builder(t).Full(Ctx.Snapshot.Items[id], compact: true);
        Assert.Equal("KEEP", card.Status);
        Assert.Contains(card.Sections.Single(x => x.Title.StartsWith("Hideout")).Rows, r => r.Contains("1 left"));
    }

    [Fact]
    public void Sections_CompactShowsTwoRowsAndMoreCount()
    {
        // an item used in many places
        var busy = Ctx.Snapshot.Items.Values.OrderByDescending(i => Ctx.Recipes.CraftsUsing(i.Id).Count + Ctx.Recipes.BartersUsing(i.Id).Count).First();
        var card = Builder().Full(busy, compact: true);
        var sec = card.Sections.Single(s => s.Title == "Crafts and barters");
        Assert.Equal(2, sec.Rows.Count);
        Assert.True(sec.MoreCount > 0);
        var main = Builder().Full(busy, compact: false);
        var all = main.Sections.Single(s => s.Title == "Crafts and barters");
        Assert.Equal(sec.Rows.Count + sec.MoreCount, all.Rows.Count);
    }
}

public class ExpandedCardTests
{
    [Fact]
    public void SecondPress_Expanded_ListsAllQuestsFirst_ThenRecipes_NoMore()
    {
        var ctx = TarkovOverlay.Tests.Fixtures.LiveCatalog.RegularContext;
        // an item with more than 2 quest hand-ins (e.g. the Grizzly kit) so the compact card truncates
        var item = ctx.Snapshot.Items.Values.Where(i => !i.IsPreset).OrderByDescending(i => ctx.Quests.For(i.Id).Count).First();
        int quests = ctx.Quests.For(item.Id).Count;
        Assert.True(quests > 2);
        var b = new TarkovOverlay.Core.Presentation.CardBuilder(ctx, null);
        var m = new TarkovOverlay.Core.Matching.MatchResult(TarkovOverlay.Core.Matching.MatchKind.Verified, new[] { item }, item.Name);

        var compact = b.ForMatch(m);
        Assert.False(compact.Expanded);
        Assert.True(compact.HasMore);
        Assert.Equal(2, compact.Sections[0].Rows.Count);

        var full = b.ForMatch(m, expanded: true);
        Assert.True(full.Expanded);
        Assert.False(full.HasMore);
        Assert.StartsWith("Quests", full.Sections[0].Title);
        Assert.Equal(quests, full.Sections[0].Rows.Count);
        // each quest row carries its count and FIR flag where required
        var needs = ctx.Quests.For(item.Id);
        Assert.All(full.Sections[0].Rows, r => Assert.Matches(@"\d+×", r));
        Assert.Equal(needs.Count(n => n.FoundInRaid), full.Sections[0].Rows.Count(r => r.Contains("· FIR")));
        // prices are identical to the compact card
        Assert.Equal(compact.Rows.Select(r => r.Label + r.Value), full.Rows.Select(r => r.Label + r.Value));
        // crafts/barters (if any) come after quests, all listed
        var recipes = full.Sections.FirstOrDefault(s => s.Title.StartsWith("Crafts"));
        if (recipes != null) Assert.True(full.Sections.ToList().IndexOf(recipes) > 0 && recipes.MoreCount == 0);
    }

    [Fact]
    public void AlwaysShowFullList_DefaultsOff_AndPersists()
    {
        var root = Path.Combine(Path.GetTempPath(), "tpo-" + Guid.NewGuid());
        var store = new TarkovOverlay.Core.Settings.SettingsStore(new TarkovOverlay.Core.AppPaths(root));
        var s = store.Load();
        Assert.False(s.AlwaysShowFullList);
        s.AlwaysShowFullList = true;
        store.Save(s);
        Assert.True(new TarkovOverlay.Core.Settings.SettingsStore(new TarkovOverlay.Core.AppPaths(root)).Load().AlwaysShowFullList);
        Directory.Delete(root, true);
    }
}
