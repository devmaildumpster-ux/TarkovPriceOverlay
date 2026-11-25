using TarkovOverlay.Core;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Core.Progress;
using TarkovOverlay.Core.Settings;
using TarkovOverlay.Maps.Logs;
using TarkovOverlay.Maps.Quests;
using TarkovOverlay.Tests.Fixtures;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>1.4.0 quest &amp; hideout flags on price/item cards (PvE + PvP, log-driven quest state, hideout levels, toggle).</summary>
public class Release140Tests
{
    private static readonly Lazy<CatalogContext> _pveCtx = new(() => new CatalogContext(LiveCatalog.Pve, () => LiveCatalog.FetchedAt));
    private static CatalogContext RegularCtx => LiveCatalog.RegularContext;
    private static CatalogContext PveCtx => _pveCtx.Value;

    private static CatalogItem FindByName(CatalogContext ctx, string name) =>
        ctx.Snapshot.Items.Values.First(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static ProgressView MakeProgress(QuestProgressStore quests, HideoutProgressStore? hideout = null) =>
        new((m, q) => quests.For(m).Get(q) == QuestState.Complete, hideout,
            (m, q) => quests.For(m).Get(q) switch
            {
                QuestState.Active => QuestProgressState.Active,
                QuestState.Complete => QuestProgressState.Complete,
                QuestState.Failed => QuestProgressState.Failed,
                _ => QuestProgressState.None,
            });

    [Fact]
    public void ActiveQuest_FlaggedFirst_WithCountAndFir_AndSortedFirstInSection()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tpo-r140-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var qp = new QuestProgressStore(Path.Combine(dir, "quest-progress.json"));
            var salewa = FindByName(PveCtx, "Salewa first aid kit");
            var salewaQuests = PveCtx.Quests.For(salewa.Id).Select(q => (q.QuestId, q.QuestName, q.Count, q.FoundInRaid)).Distinct().ToList();
            Assert.True(salewaQuests.Count >= 3);

            var shortage = salewaQuests.First(q => q.QuestName == "Shortage");
            var firstInLine = salewaQuests.First(q => q.QuestName == "First in Line");
            qp.For("pve").Set(firstInLine.QuestId, QuestState.Complete);
            qp.For("pve").Set(shortage.QuestId, QuestState.Active);

            var card = new CardBuilder(PveCtx, null, () => LiveCatalog.FetchedAt, new CardOptions { Progress = MakeProgress(qp) })
                .Full(salewa, compact: true);

            Assert.True(card.Flags.Count >= 2);
            var active = card.Flags[0];
            Assert.Equal(NeedFlagKind.ActiveQuest, active.Kind);
            Assert.Equal(shortage.Count, active.Count);
            Assert.Equal(shortage.Count, active.FirCount);
            Assert.Contains("ACTIVE QUEST", active.Label);
            Assert.Contains("FIR", active.Label);
            Assert.Contains("Shortage", active.Detail);

            var later = card.Flags[1];
            Assert.Equal(NeedFlagKind.Quest, later.Kind);
            Assert.Contains("LATER QUEST", later.Label);
            Assert.Contains("+", later.Label);
            Assert.Contains("FIR", later.Label);
            // First in Line was completed, so its count is excluded
            int expectedLater = salewaQuests.Where(q => q.QuestId != firstInLine.QuestId && q.QuestId != shortage.QuestId).Sum(q => q.Count);
            Assert.Equal(expectedLater, later.Count);

            var qSec = card.Sections.First(s => s.Title.StartsWith("Quests", StringComparison.Ordinal));
            Assert.StartsWith("Shortage · Active", qSec.Rows[0]);
            Assert.DoesNotContain(qSec.Rows, r => r.StartsWith("First in Line", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void HideoutFlags_ShowCountsAndFir_AndHideBuiltLevels()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tpo-r140-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var qp = new QuestProgressStore(Path.Combine(dir, "quest-progress.json"));
            var hp = new HideoutProgressStore(Path.Combine(dir, "hideout-progress.json"));
            var sparkPlug = FindByName(PveCtx, "Spark plug");
            var genId = PveCtx.Hideout.For(sparkPlug.Id).First(h => h.StationName == "Generator").StationId;

            CardModel Build() => new CardBuilder(PveCtx, null, () => LiveCatalog.FetchedAt, new CardOptions { Progress = MakeProgress(qp, hp) })
                .Full(sparkPlug, compact: true);

            // Level 0: Generator L1 (1x) + Generator L3 (16x) = 17x
            var c0 = Build();
            var h0 = Assert.Single(c0.Flags, f => f.Kind == NeedFlagKind.Hideout);
            Assert.Equal(17, h0.Count);
            Assert.Equal("HIDEOUT · 17×", h0.Label);
            Assert.Contains("Generator L1", h0.Detail);

            // Built Generator L1 -> only L3 (16x) remains
            hp.SetLevel("pve", genId, 1, 3);
            var c1 = Build();
            var h1 = Assert.Single(c1.Flags, f => f.Kind == NeedFlagKind.Hideout);
            Assert.Equal(16, h1.Count);
            Assert.Equal("HIDEOUT · 16×", h1.Label);
            Assert.Equal("Generator L3", h1.Detail);

            // Built Generator L3 -> no hideout flag
            hp.SetLevel("pve", genId, 3, 3);
            var c3 = Build();
            Assert.DoesNotContain(c3.Flags, f => f.Kind == NeedFlagKind.Hideout);

            // Item with a Found-in-Raid hideout requirement reports FIR in the flag
            var firHideoutItem = PveCtx.Snapshot.Items.Values.First(i => PveCtx.Hideout.For(i.Id).Any(h => h.FoundInRaid));
            var cFir = new CardBuilder(PveCtx, null, () => LiveCatalog.FetchedAt).Full(firHideoutItem, compact: true);
            var hFir = Assert.Single(cFir.Flags, f => f.Kind == NeedFlagKind.Hideout);
            Assert.True(hFir.FirCount > 0);
            Assert.Contains("FIR", hFir.Label);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GameLogs_DriveActiveAndCompletedQuestFlags_PerMode_PvpAndPve()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tpo-r140-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var qp = new QuestProgressStore(Path.Combine(dir, "quest-progress.json"));
            var sparkPvp = FindByName(RegularCtx, "Spark plug");
            var sparkPve = FindByName(PveCtx, "Spark plug");
            var carRepairPvp = RegularCtx.Quests.For(sparkPvp.Id).First(q => q.QuestName == "Car Repair").QuestId;
            var carRepairPve = PveCtx.Quests.For(sparkPve.Id).First(q => q.QuestName == "Car Repair").QuestId;

            // Simulate Tarkov game-log events: started in PvE, finished in PvP
            QuestLog.Apply(new QuestLogEntry("pve", carRepairPve, "started", DateTimeOffset.UtcNow), qp.For("pve"), feed: null);
            QuestLog.Apply(new QuestLogEntry("regular", carRepairPvp, "finished", DateTimeOffset.UtcNow), qp.For("regular"), feed: null);

            var pv = MakeProgress(qp);
            var pveCard = new CardBuilder(PveCtx, null, () => LiveCatalog.FetchedAt, new CardOptions { Progress = pv }).Full(sparkPve, compact: true);
            var pvpCard = new CardBuilder(RegularCtx, null, () => LiveCatalog.FetchedAt, new CardOptions { Progress = pv }).Full(sparkPvp, compact: true);

            // PvE: Car Repair is active + hideout still needed
            Assert.Contains(pveCard.Flags, f => f.Kind == NeedFlagKind.ActiveQuest && f.Detail.Contains("Car Repair") && f.Label.Contains("8× FIR"));
            Assert.Contains(pveCard.Flags, f => f.Kind == NeedFlagKind.Hideout && f.Count == 17);

            // PvP: Car Repair is finished (filtered out), only hideout flag remains
            Assert.DoesNotContain(pvpCard.Flags, f => f.Kind is NeedFlagKind.ActiveQuest or NeedFlagKind.Quest);
            Assert.Contains(pvpCard.Flags, f => f.Kind == NeedFlagKind.Hideout && f.Count == 17);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SettingsToggle_HidesAndShowsFlags_AndRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tpo-r140-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SettingsStore(new AppPaths(dir));
            var s = new AppSettings();
            Assert.True(s.CardShowNeedFlags);
            Assert.True(CardOptions.From(s, null).ShowNeedFlags);

            s.CardShowNeedFlags = false;
            store.Save(s);
            var loaded = store.Load();
            Assert.False(loaded.CardShowNeedFlags);
            var opts = CardOptions.From(loaded, null);
            Assert.False(opts.ShowNeedFlags);

            var spark = FindByName(PveCtx, "Spark plug");
            var withFlags = new CardBuilder(PveCtx, null, () => LiveCatalog.FetchedAt).Full(spark, compact: true);
            var withoutFlags = new CardBuilder(PveCtx, null, () => LiveCatalog.FetchedAt, opts).Full(spark, compact: true);
            Assert.NotEmpty(withFlags.Flags);
            Assert.Empty(withoutFlags.Flags);
            // Sections and price rows stay controlled by their own toggles
            Assert.Equal(withFlags.Rows.Count, withoutFlags.Rows.Count);
            Assert.Equal(withFlags.Sections.Count, withoutFlags.Sections.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PreviewAndMergedCards_AlsoShowFlags_AndRespectToggle()
    {
        var spark = FindByName(PveCtx, "Spark plug");
        var previewOn = new CardBuilder(PveCtx, null, () => LiveCatalog.FetchedAt).Preview(spark, strong: true);
        Assert.Contains(previewOn.Flags, f => f.Kind == NeedFlagKind.Quest && f.Detail.Contains("Car Repair"));
        Assert.Contains(previewOn.Flags, f => f.Kind == NeedFlagKind.Hideout && f.Count == 17);

        var previewOff = new CardBuilder(PveCtx, null, () => LiveCatalog.FetchedAt, new CardOptions { ShowNeedFlags = false }).Preview(spark, strong: true);
        Assert.Empty(previewOff.Flags);

        var merged = new CardBuilder(PveCtx, null, () => LiveCatalog.FetchedAt).MergedSameName(new[] { spark, spark });
        Assert.NotEmpty(merged.Flags);
    }

    [Fact]
    public void TrackerRemaining_And_FailedQuests_UpdateFlags()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tpo-r140-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var spark = FindByName(PveCtx, "Spark plug");
            var carRepair = PveCtx.Quests.For(spark.Id).First(q => q.QuestName == "Car Repair");
            var tracker = new Tracker(new AppSettings());
            tracker.Track(new TrackedGoal
            {
                GoalKey = carRepair.GoalKey, Mode = "pve", Kind = "quest", Title = carRepair.QuestName,
                ItemIds = carRepair.AcceptedItemIds.ToList(), ItemName = spark.Name, Required = carRepair.Count,
                Remaining = 3, FoundInRaid = carRepair.FoundInRaid,
            });

            var reduced = new CardBuilder(PveCtx, tracker, () => LiveCatalog.FetchedAt).Full(spark, compact: true);
            var qFlag = Assert.Single(reduced.Flags, f => f.Kind == NeedFlagKind.Quest);
            Assert.Equal(3, qFlag.Count);
            Assert.Contains("3× FIR", qFlag.Label);

            tracker.Adjust(carRepair.GoalKey, -3); // 0 left
            var zeroed = new CardBuilder(PveCtx, tracker, () => LiveCatalog.FetchedAt).Full(spark, compact: true);
            Assert.DoesNotContain(zeroed.Flags, f => f.Kind == NeedFlagKind.Quest);

            // Failed quest is excluded when FilterByProgress is true
            var qp = new QuestProgressStore(Path.Combine(dir, "quest-progress.json"));
            qp.For("pve").Set(carRepair.QuestId, QuestState.Failed);
            var failed = new CardBuilder(PveCtx, null, () => LiveCatalog.FetchedAt, new CardOptions { Progress = MakeProgress(qp) }).Full(spark, compact: true);
            Assert.DoesNotContain(failed.Flags, f => f.Kind is NeedFlagKind.Quest or NeedFlagKind.ActiveQuest);
        }
        finally { Directory.Delete(dir, true); }
    }
}
