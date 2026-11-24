using TarkovOverlay.Core;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Indexes;
using TarkovOverlay.Core.Settings;
using TarkovOverlay.Tests.Fixtures;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>Quests, crafts, barters, hideout, tracking, settings. FIXTURE KIND: recorded live catalog + synthetic snapshot records.</summary>
public class IndexAndSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tpo-set-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    static CatalogContext Ctx => LiveCatalog.RegularContext;

    [Fact]
    public void QuestObjective_AcceptingMultipleItemIds_SharesOneGoalKey()
    {
        // "First in Line" (Therapist): hand in 3 of any of many medical items, one objective.
        var q = LiveCatalog.Regular.Quests!.Single(x => x.Id == "657315ddab5a49b71f098853");
        var h = q.HandIns.Single(x => x.ObjectiveId == "65817bf31404f3565aef9fec");
        Assert.True(h.ItemIds.Count > 1);
        var keys = h.ItemIds.Select(id => Ctx.Quests.For(id).Single(n => n.ObjectiveId == h.ObjectiveId).GoalKey).Distinct().ToList();
        Assert.Single(keys);
        Assert.Equal("regular|quest|657315ddab5a49b71f098853|65817bf31404f3565aef9fec", keys[0]);
        var need = Ctx.Quests.For(h.ItemIds[0]).First(n => n.ObjectiveId == h.ObjectiveId);
        Assert.Equal("First in Line", need.QuestName);
        Assert.Equal("Therapist", need.TraderName);
        Assert.Equal(3, need.Count);
    }

    [Fact]
    public void RequiredVsOptional_HandIns_FindObjectivesNotDoubleCounted()
    {
        var snap = new CatalogSnapshot
        {
            ModeId = "regular",
            Items = { ["i1"] = new CatalogItem { Id = "i1", Name = "Thing" } },
            Quests = new()
            {
                new QuestInfo("q1", "Quest One", null, 10, Array.Empty<string>(), new[]
                {
                    new QuestHandIn("o1", new[] { "i1" }, 4, true, Optional: false),
                    new QuestHandIn("o2", new[] { "i1" }, 2, false, Optional: true),
                }, false),
            },
        };
        var idx = new QuestIndex(snap);
        Assert.Equal(2, idx.For("i1").Count);
        Assert.Equal(4, idx.RequiredTotal("i1")); // optional excluded from the keep count
    }

    [Fact]
    public void FeedParser_IgnoresFindItemObjectives()
    {
        // Every live hand-in comes from giveItem; findItem duplicates for the same item are not indexed.
        var all = LiveCatalog.Regular.Quests!.SelectMany(q => q.HandIns).ToList();
        Assert.NotEmpty(all);
        Assert.Equal(all.Count, all.Select(h => h.ObjectiveId).Distinct().Count());
    }

    [Fact]
    public void Prerequisites_ResolvedToQuestNames()
    {
        var withPrereq = Ctx.Snapshot.Quests!.First(q => q.PrerequisiteTaskIds.Count > 0 && q.HandIns.Count > 0);
        var need = Ctx.Quests.For(withPrereq.HandIns[0].ItemIds[0]).First(n => n.QuestId == withPrereq.Id);
        Assert.NotEmpty(need.PrerequisiteNames);
    }

    [Fact]
    public void Craft_WithReusableTools_AndMultiOutput_CostPerOutput()
    {
        var craft = Ctx.Snapshot.Crafts!.Single(c => c.Id == "63baedefe6ebc10fe0201083"); // 60× output, 2 tools
        Assert.Equal(60, craft.Output.Count);
        Assert.Equal(2, craft.Inputs.Count(i => i.Tool));
        Assert.Contains(craft, Ctx.Recipes.CraftsProducing(craft.Output.ItemId));
        foreach (var tool in craft.Inputs.Where(i => i.Tool)) Assert.Contains(craft, Ctx.Recipes.CraftsUsing(tool.ItemId));
        var est = Ctx.Costs.Estimate(craft.Inputs, craft.Output, craft.QuestItemInputs);
        Assert.Equal(2, est.Tools.Count);
        Assert.Equal(3, est.Consumed.Count);
        if (est.Known)
        {
            long sum = (long)Math.Round(est.Consumed.Sum(c => c.UnitRub!.Value * c.Count), MidpointRounding.AwayFromZero);
            Assert.Equal(sum, est.TotalRub);
            Assert.Equal((long)Math.Round(sum / 60.0, MidpointRounding.AwayFromZero), est.PerOutputRub);
        }
        else Assert.NotEmpty(est.Notes);
    }

    [Fact]
    public void CostEstimate_SyntheticRecipe_ToolsExcluded_RoublesFaceValue_UnknownMarked()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var snap = new CatalogSnapshot
        {
            ModeId = "regular",
            Market = new MarketSettings(true, 15, 0.05, 0.05),
            Items =
            {
                ["a"] = new CatalogItem { Id = "a", Name = "A", Avg24hPrice = 1000, LastScan = now,
                    BuyFromTrader = new[] { new TraderBuy("t", 800, 800, "RUB", 1, null, 5) } },
                ["b"] = new CatalogItem { Id = "b", Name = "B", Avg24hPrice = 500, LastScan = now },
                ["tool"] = new CatalogItem { Id = "tool", Name = "Tool" },
                ["c"] = new CatalogItem { Id = "c", Name = "C (no quote)" },
            },
        };
        var est = new RecipeCostEstimator(snap, new TarkovOverlay.Core.Economics.PriceCalculator(snap.Market, () => now));
        var r = est.Estimate(new[] { new RecipeItem("a", 2, false, false), new RecipeItem("b", 1, false, true), new RecipeItem("tool", 1, true, false), new RecipeItem(KnownIds.Roubles, 1000, false, false) },
            new RecipeItem("out", 4, false, false), 0);
        Assert.Equal(2 * 800 + 500 + 1000, r.TotalRub);       // LL1 cash cheaper than flea for A
        Assert.Equal(TarkovOverlay.Core.Economics.Rounding.HalfAwayFromZero(3100 / 4.0), r.PerOutputRub);
        Assert.Single(r.Tools);
        Assert.Contains(r.Notes, n => n.Contains("Found in Raid"));
        var unknown = est.Estimate(new[] { new RecipeItem("c", 1, false, false) }, new RecipeItem("out", 1, false, false), 1);
        Assert.False(unknown.Known);
        Assert.Contains(unknown.Notes, n => n.Contains("quest-only"));
    }

    [Fact]
    public void Barter_IndexedByOutputAndIngredient()
    {
        var b = Ctx.Snapshot.Barters!.First();
        Assert.Contains(b, Ctx.Recipes.BartersProducing(b.Output.ItemId));
        Assert.Contains(b, Ctx.Recipes.BartersUsing(b.Inputs[0].ItemId));
    }

    [Fact]
    public void Hideout_IndexesItemRequirements_WithPrereqsAndFir()
    {
        // Gym level 1 requires items with FIR flags plus other station levels.
        var needs = Ctx.Hideout.For("590c2e1186f77425357b6124");
        var gym = needs.FirstOrDefault(n => n.StationName == "Gym");
        Assert.NotNull(gym);
        Assert.True(gym!.FoundInRaid);
        Assert.Contains(gym.Prerequisites, p => p.Contains("level"));
        Assert.StartsWith("regular|hideout|6377a9b9a93bde8fa30eb79a|1|", gym.GoalKey);
    }

    [Fact]
    public void Hideout_KnownBonusesOnly()
    {
        Assert.Equal("+10 production slots", HideoutIndex.DescribeBonus(new HideoutBonus("AdditionalSlots", 10, null, true)));
        Assert.Null(HideoutIndex.DescribeBonus(new HideoutBonus("SomethingNew", 1, null, true)));
    }

    [Fact]
    public void Tracker_ClampsCounts_AndIsModeScoped()
    {
        var s = new AppSettings();
        var t = new Tracker(s);
        t.Track(new TrackedGoal { GoalKey = "regular|quest|q|o", Mode = "regular", Kind = "quest", Title = "Q", ItemIds = { "a", "b" }, Required = 3, Remaining = 3 });
        Assert.True(t.Adjust("regular|quest|q|o", +5));
        Assert.Equal(3, t.Get("regular|quest|q|o")!.Remaining);
        t.Adjust("regular|quest|q|o", -10);
        Assert.Equal(0, t.Get("regular|quest|q|o")!.Remaining);
        t.Adjust("regular|quest|q|o", +2);
        Assert.Equal(2, t.OpenFor("regular", "b").Single().Remaining); // any accepted item points to the same count
        Assert.Empty(t.OpenFor("pve", "a"));
        Assert.True(t.Remove("regular|quest|q|o"));
        Assert.Empty(s.Tracked);
    }

    [Fact]
    public void TrackedCounts_ModeAndScale_SurviveRestart()
    {
        var paths = new AppPaths(_root);
        var store = new SettingsStore(paths);
        var s = store.Load();
        Assert.Equal(2, s.EffectiveTooltipScale);   // fresh file default
        s.TooltipScale = 3;
        s.ModeId = "pve";
        new Tracker(s).Track(new TrackedGoal { GoalKey = "pve|hideout|st|2|r", Mode = "pve", Kind = "hideout", Title = "Lavatory L2", ItemIds = { "x" }, Required = 5, Remaining = 2 });
        store.Save(s);

        var reloaded = new SettingsStore(paths).Load();
        Assert.Equal(3, reloaded.EffectiveTooltipScale);
        Assert.Equal("pve", reloaded.ModeId);
        Assert.Equal(2, reloaded.Tracked.Single().Remaining);
    }

    [Fact]
    public void OlderSettingsFile_WithoutScale_DefaultsTo2x_AndBadValuesAreSanitized()
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(paths.SettingsFile, """{"SchemaVersion":1,"ModeId":"bogus","Tracked":[{"GoalKey":"k","Mode":"regular","Kind":"quest","Title":"t","Required":2,"Remaining":9}]}""");
        var s = new SettingsStore(paths).Load();
        Assert.Null(s.TooltipScale);
        Assert.Equal(2, s.EffectiveTooltipScale);
        Assert.Equal("regular", s.ModeId);
        Assert.Equal(2, s.Tracked[0].Remaining);
        File.WriteAllText(paths.SettingsFile, "garbage");
        Assert.Equal(2, new SettingsStore(paths).Load().EffectiveTooltipScale);
    }

    [Fact]
    public void DismissSettings_DefaultSurviveRestart_AndSanitize()
    {
        var paths = new AppPaths(_root);
        var store = new SettingsStore(paths);
        var s = store.Load();
        Assert.True(s.DismissOnMove);
        Assert.Equal(AppSettings.DefaultMoveTolerancePx, s.EffectiveMoveTolerancePx);
        s.DismissOnMove = false; s.MoveTolerancePx = 15;
        store.Save(s);
        var r = new SettingsStore(paths).Load();
        Assert.False(r.DismissOnMove);
        Assert.Equal(15, r.EffectiveMoveTolerancePx);
        File.WriteAllText(paths.SettingsFile, """{"SchemaVersion":1,"MoveTolerancePx":-4}""");
        var bad = new SettingsStore(paths).Load();
        Assert.Null(bad.MoveTolerancePx);
        Assert.True(bad.DismissOnMove);
        Assert.Equal(AppSettings.DefaultMoveTolerancePx, bad.EffectiveMoveTolerancePx);
    }
}
