using TarkovOverlay.Core;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Display;
using TarkovOverlay.Core.Input;
using TarkovOverlay.Core.Lifetime;
using TarkovOverlay.Core.Links;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Core.Progress;
using TarkovOverlay.Core.Search;
using TarkovOverlay.Core.Settings;
using TarkovOverlay.Tests.Fixtures;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>1.2.0 (RatScanner-inspired features). FIXTURE KIND: recorded live catalog (regular) where data is needed.</summary>
public class ProgressFilterTests
{
    static CatalogContext Ctx => LiveCatalog.RegularContext;
    static CardBuilder B(CardOptions? o = null) => new(Ctx, null, () => LiveCatalog.FetchedAt, o);

    sealed class FakeProgress : IProgressView
    {
        public HashSet<string> Done { get; } = new();
        public Dictionary<string, int> Levels { get; } = new();
        public bool IsQuestCompleted(string modeId, string questId) => modeId == "regular" && Done.Contains(questId);
        public int StationLevel(string modeId, string stationId) => modeId == "regular" && Levels.TryGetValue(stationId, out var l) ? l : 0;
    }

    static string Collector => Ctx.Snapshot.Quests!.Single(t => t.Name == "Collector").Id;

    /// <summary>An item a Collector objective needs (one of the Kappa items).</summary>
    static CatalogItem KappaItem => Ctx.Snapshot.Items.Values.First(i => Ctx.Quests.For(i.Id).Any(n => n.QuestId == Collector));

    /// <summary>An item with at least two quest needs, one FIR and one not, and a hideout need.</summary>
    static CatalogItem Busy => Ctx.Snapshot.Items.Values
        .Where(i => Ctx.Hideout.For(i.Id).Count > 0 && Ctx.Quests.For(i.Id).Select(n => n.QuestId).Distinct().Count() >= 2)
        .OrderByDescending(i => Ctx.Quests.For(i.Id).Count).First();

    [Fact]
    public void KappaFlag_ComesFromTheFeed_CollectorChain()
    {
        var kappa = Ctx.Snapshot.Quests!.Where(t => t.KappaRequired).Select(t => t.Name).ToList();
        Assert.Contains("Collector", kappa);
        Assert.Contains(Ctx.Quests.For(KappaItem.Id), n => n.KappaRequired && n.FoundInRaid);
        int collectorItems = Ctx.Snapshot.Items.Values.Count(i => Ctx.Quests.For(i.Id).Any(n => n.QuestId == Collector));
        Assert.True(collectorItems >= 30, $"Collector items: {collectorItems}");
    }

    [Fact]
    public void FreshUser_SeesEverything_AndNothingHidden()
    {
        var p = new FakeProgress();
        var b = B(new CardOptions { Progress = p });
        var item = Busy;
        var (q, done, nf) = b.VisibleQuestNeeds(item.Id);
        Assert.Equal(Ctx.Quests.For(item.Id).Count, q.Count);
        Assert.Equal(0, done); Assert.Equal(0, nf);
        var (h, built, _) = b.VisibleHideoutNeeds(item.Id);
        Assert.Equal(Ctx.Hideout.For(item.Id).Count, h.Count);
        Assert.Equal(0, built);
        var card = b.Full(item, compact: false);
        Assert.DoesNotContain(card.Facts, f => f.Contains("already done"));
        // and the result equals the 1.1.4 behaviour (no options at all)
        Assert.Equal(B().VisibleQuestNeeds(item.Id).Shown.Count, q.Count);
    }

    [Fact]
    public void CompletedQuest_IsHidden_OnlyWhenFilterOn()
    {
        var item = Busy;
        var firstQuest = Ctx.Quests.For(item.Id)[0].QuestId;
        var p = new FakeProgress(); p.Done.Add(firstQuest);
        int perQuest = Ctx.Quests.For(item.Id).Count(n => n.QuestId == firstQuest);
        var on = B(new CardOptions { Progress = p });
        var (q, done, _) = on.VisibleQuestNeeds(item.Id);
        Assert.DoesNotContain(q, n => n.QuestId == firstQuest);
        Assert.Equal(perQuest, done);
        Assert.Contains(on.Full(item, compact: false).Facts, f => f.Contains("already done · hidden"));
        var off = B(new CardOptions { Progress = p, FilterByProgress = false });
        Assert.Contains(off.VisibleQuestNeeds(item.Id).Shown, n => n.QuestId == firstQuest);
        // another mode's progress never hides anything
        var pve = new CardBuilder(new CatalogContext(LiveCatalog.Pve, () => LiveCatalog.FetchedAt), null, () => LiveCatalog.FetchedAt, new CardOptions { Progress = p });
        Assert.Equal(0, pve.VisibleQuestNeeds(item.Id).DoneHidden);
    }

    [Fact]
    public void BuiltHideoutLevel_IsHidden_LowerLevelsDoNotCount()
    {
        var item = Busy;
        var need = Ctx.Hideout.For(item.Id)[0];
        var p = new FakeProgress();
        p.Levels[need.StationId] = need.Level - 1;
        Assert.Contains(B(new CardOptions { Progress = p }).VisibleHideoutNeeds(item.Id).Shown, n => n.GoalKey == need.GoalKey);
        p.Levels[need.StationId] = need.Level;
        var (h, built, _) = B(new CardOptions { Progress = p }).VisibleHideoutNeeds(item.Id);
        Assert.DoesNotContain(h, n => n.GoalKey == need.GoalKey);
        Assert.True(built >= 1);
    }

    [Fact]
    public void NonFirToggle_HidesOnlyNonFirNeeds()
    {
        var item = Ctx.Snapshot.Items.Values.First(i => Ctx.Quests.For(i.Id).Any(n => !n.FoundInRaid) && Ctx.Quests.For(i.Id).Any(n => n.FoundInRaid));
        var (q, _, nf) = B(new CardOptions { ShowNonFirNeeds = false }).VisibleQuestNeeds(item.Id);
        Assert.All(q, n => Assert.True(n.FoundInRaid));
        Assert.Equal(Ctx.Quests.For(item.Id).Count(n => !n.FoundInRaid), nf);
    }

    [Fact]
    public void KappaBadge_OnCollectorItem_GoneWhenCollectorDoneOrToggleOff()
    {
        var item = KappaItem;
        var p = new FakeProgress();
        var card = B(new CardOptions { Progress = p }).Full(item, compact: false);
        Assert.Contains("KAPPA", card.Badges);
        Assert.Contains(card.Sections, s => s.Rows.Any(r => r.Contains("Kappa")));
        Assert.DoesNotContain("KAPPA", B(new CardOptions { Progress = p, ShowKappaBadge = false }).Full(item, compact: false).Badges);
        p.Done.Add(Collector);
        bool otherKappa = Ctx.Quests.For(item.Id).Any(n => n.KappaRequired && n.QuestId != Collector);
        if (!otherKappa) Assert.DoesNotContain("KAPPA", B(new CardOptions { Progress = p }).Full(item, compact: false).Badges);
        // a plain item has no badge
        var plain = Ctx.Snapshot.Items.Values.First(i => Ctx.Quests.For(i.Id).Count == 0);
        Assert.Empty(B().Full(plain, compact: false).Badges);
    }

    [Fact]
    public void QuestSectionTitle_SaysFir()
    {
        var card = B().Full(KappaItem, compact: false);
        Assert.Contains(card.Sections, s => s.Title.StartsWith("Quests", StringComparison.Ordinal) && s.Title.Contains("FIR"));
    }

    [Fact]
    public void CardFieldToggles_DropRowsAndSections()
    {
        var item = Busy;
        var all = B().Full(item, compact: false);
        Assert.Contains(all.Rows, r => r.Label.StartsWith("Flea 24h", StringComparison.Ordinal));
        var none = B(new CardOptions { ShowFlea = false, ShowFleaNet = false, ShowTrader = false, ShowBuyBack = false, ShowPerSlot = false, ShowStack = false, ShowNeeds = false, ShowRecipes = false }).Full(item, compact: false);
        Assert.Empty(none.Rows);
        Assert.Empty(none.Sections);
        var noFlea = B(new CardOptions { ShowFlea = false }).Full(item, compact: false);
        Assert.DoesNotContain(noFlea.Rows, r => r.Label.StartsWith("Flea 24h", StringComparison.Ordinal));
        Assert.Equal(all.Sections.Count, noFlea.Sections.Count);
        var stackable = Ctx.Snapshot.Items.Values.Where(i => i.StackMaxSize is > 1)
            .First(i => B().Full(i, compact: false).Rows.Any(r => r.Label.StartsWith("Full stack", StringComparison.Ordinal)));
        Assert.DoesNotContain(B(new CardOptions { ShowStack = false }).Full(stackable, compact: false).Rows, r => r.Label.StartsWith("Full stack", StringComparison.Ordinal));
    }

    [Fact]
    public void CardOptions_FromSettings()
    {
        var s = new AppSettings { CardShowFlea = false, ShowKappaBadge = false, FilterNeedsByProgress = false, ShowNonFirNeeds = false };
        var o = CardOptions.From(s, null);
        Assert.False(o.ShowFlea); Assert.False(o.ShowKappaBadge); Assert.False(o.FilterByProgress); Assert.False(o.ShowNonFirNeeds);
        Assert.True(o.ShowTrader); Assert.True(o.ShowNeeds);
    }
}

public class HideoutStoreTests
{
    [Fact]
    public void RoundTrip_Clamp_Reset_PerMode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tpo-h-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "hideout-progress.json");
            var s = new HideoutProgressStore(f);
            string? changed = null; s.Changed += m => changed = m;
            s.SetLevel("regular", "stash", 3, 4);
            s.SetLevel("regular", "lavatory", 9, 3);   // clamps to max
            s.SetLevel("pve", "stash", 1, 4);
            s.SetLevel("regular", "medstation", -2, 3);// clamps to 0 → nothing stored
            Assert.Equal("regular", changed);
            s.Save();
            var r = new HideoutProgressStore(f);
            Assert.Equal(3, r.Level("regular", "stash"));
            Assert.Equal(3, r.Level("regular", "lavatory"));
            Assert.Equal(1, r.Level("pve", "stash"));
            Assert.Equal(0, r.Level("regular", "medstation"));
            Assert.Equal(0, r.Level("pvp-season", "stash"));
            r.Reset("regular");
            Assert.Equal(0, r.Level("regular", "stash"));
            Assert.Equal(1, r.Level("pve", "stash"));
            var v = new ProgressView((m, q) => m == "pve" && q == "q1", r);
            Assert.True(v.IsQuestCompleted("pve", "q1"));
            Assert.False(v.IsQuestCompleted("regular", "q1"));
            Assert.Equal(1, v.StationLevel("pve", "stash"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"Schema":1,"Modes":{"regular":{"stash":-5,"lav":999}}}""")]
    [InlineData("")]
    public void CorruptFile_StartsSafe(string text)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tpo-h-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "hideout-progress.json");
            File.WriteAllText(f, text);
            var s = new HideoutProgressStore(f);
            Assert.Equal(0, s.Level("regular", "stash"));
            Assert.InRange(s.Level("regular", "lav"), 0, 10);
            s.SetLevel("regular", "stash", 2, 4); s.Save();
            Assert.Equal(2, new HideoutProgressStore(f).Level("regular", "stash"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ModeAutoSwitchTests
{
    [Theory]
    [InlineData(true, "regular", "pve", "pve")]
    [InlineData(true, "pve", "regular", "regular")]
    [InlineData(true, "regular", "pvp-season", "pvp-season")]
    [InlineData(true, "pve", "pve", null)]
    [InlineData(true, "PVE", "pve", null)]
    [InlineData(false, "regular", "pve", null)]
    [InlineData(true, "regular", null, null)]
    [InlineData(true, "regular", "arena", null)]
    public void Decide(bool enabled, string app, string? logged, string? expected)
    {
        var d = ModeAutoSwitch.Decide(enabled, app, logged);
        Assert.Equal(expected, d.SwitchTo);
        Assert.False(string.IsNullOrWhiteSpace(d.Reason));
    }
}

public class ItemLinkTests
{
    static CatalogItem Item(string? wiki, string? link, string? norm = "m4a1") =>
        new() { Id = "x", Name = "Colt M4A1 & co", NormalizedName = norm, WikiLink = wiki, Link = link };

    [Fact]
    public void FeedLinks_UsedWhenSafe()
    {
        var i = Item("https://escapefromtarkov.fandom.com/wiki/Colt_M4A1", "https://tarkov.dev/item/colt-m4a1");
        Assert.Equal("https://escapefromtarkov.fandom.com/wiki/Colt_M4A1", ItemLinks.Wiki(i).AbsoluteUri);
        Assert.Equal("https://tarkov.dev/item/colt-m4a1", ItemLinks.TarkovDev(i).AbsoluteUri);
    }

    [Theory]
    [InlineData("http://escapefromtarkov.fandom.com/wiki/X")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/system32/calc.exe")]
    [InlineData("https://evil.example/wiki")]
    [InlineData("https://escapefromtarkov.fandom.com.evil.example/wiki")]
    [InlineData("https://user:pw@escapefromtarkov.fandom.com/wiki")]
    [InlineData("https://escapefromtarkov.fandom.com:8443/wiki")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a url")]
    public void UnsafeWiki_FallsBackToSearch(string? bad)
    {
        Assert.Null(ItemLinks.Safe(bad, ItemLinks.WikiHost));
        var u = ItemLinks.Wiki(Item(bad, null));
        Assert.Equal("https", u.Scheme);
        Assert.Equal(ItemLinks.WikiHost, u.Host);
        Assert.Contains("Special:Search", u.AbsoluteUri);
        Assert.Contains("Colt%20M4A1%20%26%20co", u.AbsoluteUri);
        Assert.True(ItemLinks.IsLaunchable(u));
    }

    [Fact]
    public void TarkovDev_Fallbacks()
    {
        Assert.Equal("https://tarkov.dev/item/m4a1", ItemLinks.TarkovDev(Item(null, "https://tarkov.dev.evil.io/item/x")).AbsoluteUri);
        Assert.StartsWith("https://tarkov.dev/?search=", ItemLinks.TarkovDev(Item(null, null, "Bad Slug/../x")).AbsoluteUri);
        Assert.NotNull(ItemLinks.Safe("https://www.tarkov.dev/item/x", ItemLinks.DevHost)); // subdomain ok
        Assert.False(ItemLinks.IsLaunchable(new Uri("https://example.com/")));
        Assert.False(ItemLinks.IsLaunchable(new Uri("http://tarkov.dev/")));
    }

    [Fact]
    public void LiveFeed_HasLinks()
    {
        var items = LiveCatalog.Regular.Items.Values.ToList();
        Assert.True(items.Count(i => ItemLinks.Safe(i.WikiLink, ItemLinks.WikiHost) != null) > items.Count / 2);
        Assert.True(items.Count(i => ItemLinks.Safe(i.Link, ItemLinks.DevHost) != null) > items.Count / 2);
        Assert.All(items.Take(200), i => { Assert.True(ItemLinks.IsLaunchable(ItemLinks.Wiki(i))); Assert.True(ItemLinks.IsLaunchable(ItemLinks.TarkovDev(i))); });
    }
}

public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Alt+W", ChordModifiers.Ctrl | ChordModifiers.Alt, 0x57u, "Ctrl+Alt+W")]
    [InlineData("alt+`", ChordModifiers.Alt, 0xC0u, "Alt+`")]
    [InlineData(" ctrl + shift + d ", ChordModifiers.Ctrl | ChordModifiers.Shift, 0x44u, "Ctrl+Shift+D")]
    [InlineData("F9", ChordModifiers.None, 0x78u, "F9")]
    [InlineData("Win+Alt+5", ChordModifiers.Win | ChordModifiers.Alt, 0x35u, "Ctrl+Alt+5")]
    public void ParseAndFormat(string text, ChordModifiers mods, uint vk, string _)
    {
        Assert.True(KeyChord.TryParse(text, out var c));
        Assert.Equal(mods, c.Modifiers);
        Assert.Equal(vk, c.Vk);
        Assert.True(KeyChord.TryParse(c.ToString(), out var again));
        Assert.Equal(c, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt+Nope")]
    [InlineData("Ctrl+W+Q")]
    public void Invalid(string? text) => Assert.False(KeyChord.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Alt+W", true)]
    [InlineData("Alt+`", true)]
    [InlineData("F10", true)]
    [InlineData("W", false)]
    [InlineData("Shift+W", false)]
    [InlineData("Ctrl+Escape", false)]
    public void Safety(string text, bool safe)
    {
        if (!KeyChord.TryParse(text, out var c)) { Assert.False(safe); return; }
        Assert.Equal(safe, c.IsSafe);
    }

    [Fact]
    public void Defaults_AreSafe()
    {
        foreach (var d in new[] { AppSettings.DefaultWikiHotkey, AppSettings.DefaultDevHotkey, AppSettings.DefaultSearchHotkey })
        {
            Assert.True(KeyChord.TryParse(d, out var c), d);
            Assert.True(c.IsSafe, d);
        }
    }
}

public class QuickSearchTests
{
    static CatalogContext Ctx => LiveCatalog.RegularContext;

    [Fact]
    public void ExactShortNameFirst()
    {
        var gpu = Ctx.Snapshot.Items.Values.Single(i => i.Name == "Graphics card");
        var r = QuickSearch.Run(Ctx, "gpu", 5);
        Assert.Equal(gpu.Id, r[0].Item.Id);
        Assert.Contains("flea", r[0].Flea);
        Assert.NotNull(r[0].PerSlot);
    }

    [Fact]
    public void PrefixAndWordMatches()
    {
        var r = QuickSearch.Run(Ctx, "graphics", 8);
        Assert.Equal("Graphics card", r[0].Name);
        var led = QuickSearch.Run(Ctx, "ledx", 3);
        Assert.Contains(led, x => x.Name.Contains("LEDX", StringComparison.OrdinalIgnoreCase));
        Assert.True(QuickSearch.Run(Ctx, "salewa", 5).Count > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_GivesNothing(string? q) => Assert.Empty(QuickSearch.Run(Ctx, q, 5));

    [Fact]
    public void Score_Order()
    {
        var i = new CatalogItem { Id = "1", Name = "Bottle of water", ShortName = "Water" };
        Assert.Equal(0, QuickSearch.Score(i, "water"));
        Assert.Equal(1, QuickSearch.Score(i, "bottle of water"));
        Assert.Equal(2, QuickSearch.Score(i, "wat"));
        Assert.Equal(3, QuickSearch.Score(i, "bott"));
        Assert.Equal(5, QuickSearch.Score(i, "xyz"));
        Assert.Equal(4, QuickSearch.Score(new CatalogItem { Id = "2", Name = "Bottle of water", ShortName = "H2O" }, "wat"));
    }

    [Fact]
    public void NoDuplicates_AndLimit()
    {
        var r = QuickSearch.Run(Ctx, "ammo", 7);
        Assert.True(r.Count <= 7);
        Assert.Equal(r.Count, r.Select(x => x.Item.Id).Distinct().Count());
    }
}

public class Settings120Tests
{
    [Fact]
    public void Defaults_And_114Migration()
    {
        var root = Path.Combine(Path.GetTempPath(), "tpo-" + Guid.NewGuid());
        var paths = new AppPaths(root);
        try
        {
            Directory.CreateDirectory(root);
            // a 1.1.4 settings file: none of the 1.2.0 keys
            File.WriteAllText(paths.SettingsFile, """{"SchemaVersion":1,"ModeId":"pve","HotkeyEnabled":false,"TooltipScale":3}""");
            var s = new SettingsStore(paths).Load();
            Assert.Equal("pve", s.ModeId); Assert.False(s.HotkeyEnabled);
            Assert.True(s.FilterNeedsByProgress); Assert.True(s.ShowNonFirNeeds); Assert.True(s.ShowKappaBadge);
            Assert.True(s.CardShowFlea && s.CardShowFleaNet && s.CardShowTrader && s.CardShowBuyBack && s.CardShowPerSlot && s.CardShowStack && s.CardShowNeeds && s.CardShowRecipes);
            Assert.Equal(95, s.EffectiveCardOpacityPercent);
            Assert.False(s.MinimizeToTray);
            Assert.True(s.AutoDetectMode);
            Assert.Equal("Ctrl+Alt+W", s.EffectiveWikiHotkey);
            Assert.Equal("Ctrl+Alt+D", s.EffectiveDevHotkey);
            Assert.Equal("Alt+`", s.EffectiveSearchHotkey);
            Assert.True(s.WikiHotkeyEnabled && s.DevHotkeyEnabled && s.SearchHotkeyEnabled);

            s.CardShowFlea = false; s.CardOpacityPercent = 60; s.MinimizeToTray = true; s.AutoDetectMode = false;
            s.WikiHotkey = "F8"; s.DevHotkey = ""; s.SearchHotkeyEnabled = false;
            new SettingsStore(paths).Save(s);
            var r = new SettingsStore(paths).Load();
            Assert.False(r.CardShowFlea); Assert.Equal(60, r.EffectiveCardOpacityPercent);
            Assert.True(r.MinimizeToTray); Assert.False(r.AutoDetectMode);
            Assert.Equal("F8", r.EffectiveWikiHotkey);
            Assert.Equal("", r.EffectiveDevHotkey);           // cleared stays cleared
            Assert.False(r.SearchHotkeyEnabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(10, 40)]
    [InlineData(250, 100)]
    [InlineData(70, 70)]
    public void Opacity_Sanitized(int saved, int effective)
    {
        var root = Path.Combine(Path.GetTempPath(), "tpo-" + Guid.NewGuid());
        var paths = new AppPaths(root);
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(paths.SettingsFile, $$"""{"SchemaVersion":1,"CardOpacityPercent":{{saved}},"SearchHotkey":"Ctrl+Bogus"}""");
            var s = new SettingsStore(paths).Load();
            Assert.Equal(effective, s.EffectiveCardOpacityPercent);
            Assert.Equal("Alt+`", s.EffectiveSearchHotkey); // unparsable → default
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

public class GraphicsIniTests
{
    const string ObjectStored = """
        {"DisplaySettings":{"Display":1,"FullScreenMode":2,"Resolution":{"Width":1920,"Height":1080}},
         "Stored":{"0":{"WindowResolution":{"Width":1920,"Height":1080}},"1":{"WindowResolution":{"Width":2560,"Height":1440}}}}
        """;
    const string ArrayStored = """
        {"DisplaySettings":{"Display":0},"Stored":[{"WindowResolution":{"Width":3440,"Height":1440}}]}
        """;

    [Fact]
    public void Parse_ObjectStored()
    {
        var i = GraphicsIni.Parse(ObjectStored)!;
        Assert.Equal(1, i.DisplayIndex); Assert.Equal(2560, i.Width); Assert.Equal(1440, i.Height); Assert.Equal(2, i.FullScreenMode);
    }

    [Fact]
    public void Parse_ArrayStored_AndOldResolutionFallback()
    {
        var a = GraphicsIni.Parse(ArrayStored)!;
        Assert.Equal((0, 3440, 1440), (a.DisplayIndex, a.Width!.Value, a.Height!.Value));
        var old = GraphicsIni.Parse("""{"DisplaySettings":{"Display":"0","Resolution":{"Width":1600,"Height":900}}}""")!;
        Assert.Equal((0, 1600, 900), (old.DisplayIndex, old.Width!.Value, old.Height!.Value));
        var noRes = GraphicsIni.Parse("""{"DisplaySettings":{"Display":2}}""")!;
        Assert.Null(noRes.Width);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[Graphics]\nDisplay=1")]
    [InlineData("{}")]
    [InlineData("""{"DisplaySettings":{}}""")]
    [InlineData("""{"DisplaySettings":{"Display":-1}}""")]
    [InlineData("""{"DisplaySettings":5}""")]
    [InlineData("[1,2,3]")]
    public void Parse_Malformed_IsNull(string text) => Assert.Null(GraphicsIni.Parse(text));

    [Fact]
    public void TryRead_MissingAndLockedFiles()
    {
        var (none, note) = GraphicsIni.TryRead(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid(), "Graphics.ini"));
        Assert.Null(none);
        Assert.Contains("not found", note);
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, ObjectStored);
            using var held = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); // the game holding it open
            var (info, n2) = GraphicsIni.TryRead(f);
            Assert.NotNull(info);
            Assert.StartsWith("read", n2);
            held.Dispose();
            File.WriteAllText(f, "garbage");
            Assert.Null(GraphicsIni.TryRead(f).Info);
        }
        finally { File.Delete(f); }
    }

    static MonitorInfo M(int i, int x, int w, int h, bool primary, double dpi = 1) =>
        new(i, new PixelRect(x, 0, w, h), new PixelRect(x, 0, w, h - 40), dpi, primary);

    [Fact]
    public void Resolver()
    {
        var mons = new[] { M(0, 0, 1920, 1080, true), M(1, 1920, 2560, 1440, false, 1.25) };
        var a = TarkovDisplayResolver.Resolve(mons, GraphicsIni.Parse(ObjectStored))!;
        Assert.Equal(1, a.Monitor.Index);
        Assert.Contains("2560×1440", a.Describe());
        Assert.Equal(0, TarkovDisplayResolver.Resolve(mons, null)!.Monitor.Index);
        // index says 0 but only monitor 1 matches the resolution
        var b = TarkovDisplayResolver.Resolve(mons, new GraphicsIniInfo(0, 2560, 1440, null, "t"))!;
        Assert.Equal(1, b.Monitor.Index);
        // index not present → primary
        Assert.Equal(0, TarkovDisplayResolver.Resolve(mons, new GraphicsIniInfo(5, 800, 600, null, "t"))!.Monitor.Index);
        // index present but resolution differs and nothing fits → by index (windowed)
        Assert.Equal(1, TarkovDisplayResolver.Resolve(mons, new GraphicsIniInfo(1, 1280, 720, null, "t"))!.Monitor.Index);
        Assert.Null(TarkovDisplayResolver.Resolve(Array.Empty<MonitorInfo>(), null));
    }
}

public class TrayShutdownTests
{
    [Fact]
    public void FailingTrayStep_DoesNotStopTheRest()
    {
        var log = new List<string>();
        var seq = new ShutdownSequence(log.Add);
        bool cardClosed = false, keysReleased = false;
        seq.Run("tray icon, extra shortcuts, search bar", () => throw new InvalidOperationException("Shell_NotifyIcon failed"));
        seq.Run("cursor card", () => cardClosed = true);
        seq.Run("hotkey", () => keysReleased = true);
        Assert.True(cardClosed); Assert.True(keysReleased);
        Assert.False(seq.Results[0].Ok);
        Assert.True(seq.Results.Skip(1).All(r => r.Ok));
        Assert.Contains(log, l => l.Contains("tray icon") && l.Contains("Shell_NotifyIcon failed"));
    }
}
