using TarkovOverlay.Core;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Tests.Fixtures;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>Feed normalization, cache, offline startup, partial feeds, mode switching. FIXTURE KIND: recorded live catalog + simulated outages.</summary>
public class CatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tpo-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    CatalogService Service(FixtureFeed feed, Func<DateTimeOffset>? now = null) =>
        new(feed, new CatalogStore(new AppPaths(_root)), now ?? (() => LiveCatalog.FetchedAt));

    static async Task<DataState> WaitFor(CatalogService s, Func<DataState, bool> pred)
    {
        for (int i = 0; i < 600 && !pred(s.State); i++) await Task.Delay(25);
        return s.State;
    }

    [Fact]
    public void Normalization_ReadsRequiredFieldsAndKeepsMissingDistinctFromZero()
    {
        var snap = LiveCatalog.Regular;
        var m882 = snap.Items["64b7bbb74b75259c590fa897"];
        Assert.Equal("9x19mm FMJ M882", m882.Name);
        Assert.False(string.IsNullOrEmpty(m882.ShortName));
        Assert.Equal(1, m882.Width); Assert.Equal(1, m882.Height);
        Assert.True(m882.BasePrice > 0);
        Assert.Contains("ammo", m882.Types);
        Assert.True(m882.StackMaxSize > 1);
        Assert.NotEmpty(m882.SellToTrader);
        Assert.True(snap.Market!.Enabled);
        Assert.Equal(0.05, snap.Market.SellOfferFeeRate);
        Assert.Equal(0.05, snap.Market.SellRequirementFeeRate);
        Assert.True(snap.Market.MinPlayerLevel > 0);
        Assert.Contains(snap.Items.Values, i => i.Avg24hPrice == null); // missing stays null, not 0
        Assert.Contains(snap.Items.Values, i => i.IsNoFlea);
        Assert.Equal("Prapor", snap.TraderNames["54cb50c76803fa8b248b4571"]);
        var rig = snap.Items["59e7643b86f7742cbf2c109a"];
        Assert.Equal("rig", rig.Container!.Kind);
        Assert.Equal(10, rig.Container.Grids.Sum(g => g.Area));
        Assert.All(snap.Sections.All, s => Assert.Equal(SectionState.Fresh, s.State));
    }

    [Fact]
    public void Translation_KeysNeverLeakAsNames()
    {
        Assert.DoesNotContain(LiveCatalog.Regular.Items.Values, i => i.Name.EndsWith(" Name", StringComparison.Ordinal) && i.Name.Contains(i.Id));
        Assert.DoesNotContain(LiveCatalog.Regular.StationNames.Values, n => n.StartsWith("hideout_area_"));
    }

    [Fact]
    public async Task FreshStart_NoCache_DownloadsSavesAndIsFresh()
    {
        using var s = Service(new FixtureFeed());
        await s.SwitchModeAsync(GameMode.Regular);
        Assert.Equal(DataStateKind.Fresh, s.State.Kind);
        Assert.NotNull(s.Context);
        Assert.True(File.Exists(new AppPaths(_root).CatalogFile("regular")));
    }

    [Fact]
    public async Task OfflineStartup_WithValidCache_IsUsableAndMarkedCached()
    {
        using (var s1 = Service(new FixtureFeed())) await s1.SwitchModeAsync(GameMode.Regular);
        using var s2 = Service(new FixtureFeed { Offline = true });
        await s2.SwitchModeAsync(GameMode.Regular);
        Assert.Equal(DataStateKind.Cached, s2.State.Kind);
        Assert.Contains("Refresh failed", s2.State.Message);
        Assert.Equal("6567e7681265c8a131069b0f", s2.Context!.Items.Match("SIG Sauer TANGO6T 1-6x24 30mm riflescope").Single!.Id);
        Assert.Equal(SectionState.Cached, s2.Context.Snapshot.Sections.Items);
    }

    [Fact]
    public async Task OfflineStartup_ExpiredCache_StillUsable_StateVisible()
    {
        using (var s1 = Service(new FixtureFeed())) await s1.SwitchModeAsync(GameMode.Regular);
        using var s2 = Service(new FixtureFeed { Offline = true }, () => LiveCatalog.FetchedAt.AddDays(3));
        await s2.SwitchModeAsync(GameMode.Regular);
        Assert.Equal(DataStateKind.CachedExpired, s2.State.Kind);
        Assert.NotNull(s2.Context);
        Assert.Contains("Old cached data", s2.State.Describe(LiveCatalog.FetchedAt.AddDays(3)));
    }

    [Fact]
    public async Task OfflineStartup_AbsentCache_ReportsClearly()
    {
        using var s = Service(new FixtureFeed { Offline = true });
        await s.SwitchModeAsync(GameMode.Regular);
        Assert.Equal(DataStateKind.None, s.State.Kind);
        Assert.Null(s.Context);
        Assert.Contains("no cache", s.State.Message);
    }

    [Fact]
    public void CorruptCache_IsIgnored()
    {
        var p = new AppPaths(_root).CatalogFile("regular");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "{ not json");
        Assert.Null(new CatalogStore(new AppPaths(_root)).TryLoad("regular", out var problem));
        Assert.Contains("unreadable", problem);
    }

    [Fact]
    public async Task PartialFeed_QuestsHideoutRecipesDegradeIndependently()
    {
        var feed = new FixtureFeed();
        feed.Failing.UnionWith(new[] { "tasks", "crafts" });
        using var s = Service(feed);
        await s.SwitchModeAsync(GameMode.Regular);
        Assert.Equal(DataStateKind.Partial, s.State.Kind);
        Assert.Contains("quests", s.State.MissingSections);
        Assert.Contains("crafts", s.State.MissingSections);
        var ctx = s.Context!;
        Assert.False(ctx.Quests.Available);
        Assert.False(ctx.Recipes.CraftsAvailable);
        Assert.True(ctx.Recipes.BartersAvailable);
        Assert.True(ctx.Hideout.Available);
        Assert.NotNull(ctx.Items.Match("9x39mm PAB-9 gs").Single);
    }

    [Fact]
    public async Task PartialFeed_ReusesCachedSectionFromLastGoodSnapshot()
    {
        using (var s1 = Service(new FixtureFeed())) await s1.SwitchModeAsync(GameMode.Regular);
        var feed = new FixtureFeed();
        feed.Failing.Add("tasks");
        using var s2 = Service(feed);
        await s2.SwitchModeAsync(GameMode.Regular);
        Assert.Equal(DataStateKind.Partial, s2.State.Kind);
        Assert.Contains("quests (cached)", s2.State.MissingSections);
        Assert.True(s2.Context!.Quests.Available);
    }

    [Fact]
    public async Task MissingTraderLocalization_ReusesVerifiedCachedNames_ElseGenericLabel()
    {
        var feed = new FixtureFeed(); feed.Failing.Add("traders_en");
        using (var s = Service(feed))
        {
            await s.SwitchModeAsync(GameMode.Regular);
            Assert.Equal("Trader", s.Context!.Snapshot.TraderName("54cb50c76803fa8b248b4571"));
        }
        using (var s1 = Service(new FixtureFeed())) await s1.SwitchModeAsync(GameMode.Regular);
        using (var s2 = Service(feed))
        {
            await s2.SwitchModeAsync(GameMode.Regular);
            Assert.Equal("Prapor", s2.Context!.Snapshot.TraderName("54cb50c76803fa8b248b4571"));
        }
    }

    [Fact]
    public async Task ItemsFeedFailure_KeepsLastGoodSnapshot()
    {
        using (var s1 = Service(new FixtureFeed())) await s1.SwitchModeAsync(GameMode.Regular);
        var feed = new FixtureFeed(); feed.Failing.Add("items");
        using var s2 = Service(feed);
        await s2.SwitchModeAsync(GameMode.Regular);
        Assert.Equal(DataStateKind.Cached, s2.State.Kind);
        Assert.NotNull(s2.Context);
        // and the cache on disk was not overwritten by a bad snapshot
        Assert.NotNull(new CatalogStore(new AppPaths(_root)).TryLoad("regular", out _));
    }

    [Fact]
    public async Task ModeSwitch_UsesModeOnEveryRequest_AndReloadsPrices()
    {
        var feed = new FixtureFeed();
        using var s = Service(feed);
        await s.SwitchModeAsync(GameMode.Regular);
        var regPrice = s.Context!.Snapshot.Items["5c6592372e221600133e47d7"];
        await s.SwitchModeAsync(GameMode.Pve);
        Assert.Equal("pve", s.Context!.Snapshot.ModeId);
        Assert.Equal(GameMode.Pve, s.Context.Mode);
        Assert.All(feed.Requested.Skip(9), p => Assert.StartsWith("/pve/", p));
        var pvePrice = s.Context.Snapshot.Items["5c6592372e221600133e47d7"];
        var expectedPve = LiveCatalog.Pve.Items["5c6592372e221600133e47d7"];
        Assert.Equal(expectedPve.Avg24hPrice, pvePrice.Avg24hPrice);
        Assert.Equal(expectedPve.SellToTrader.Max(t => t.PriceRub), pvePrice.SellToTrader.Max(t => t.PriceRub));
        Assert.True(File.Exists(new AppPaths(_root).CatalogFile("pve")));
        _ = regPrice;
    }

    [Fact]
    public async Task ModeSwitch_DiscardsResultsFromPreviousMode()
    {
        var slow = new SlowFeed(new FixtureFeed(), "/regular/items", TimeSpan.FromMilliseconds(400));
        using var s = new CatalogService(slow, new CatalogStore(new AppPaths(_root)), () => LiveCatalog.FetchedAt);
        var t1 = s.SwitchModeAsync(GameMode.Regular);
        await Task.Delay(50);
        await s.SwitchModeAsync(GameMode.Pve);
        await t1;
        Assert.Equal("pve", s.Context!.Snapshot.ModeId);
        Assert.Equal("pve", s.State.ModeId);
    }

    [Fact]
    public async Task PriceHistory_LoadsForModeAndItem()
    {
        using var s = Service(new FixtureFeed());
        var h = await s.GetPriceHistoryAsync("regular", "64b7bbb74b75259c590fa897", default);
        Assert.NotNull(h);
        Assert.NotEmpty(h!);
        Assert.True(h.Zip(h.Skip(1)).All(p => p.First.Time <= p.Second.Time));
    }

    [Fact]
    public void GameModes_MatchEndpointIndex()
    {
        Assert.Equal(new[] { "regular", "pve", "pvp-season" }, GameMode.All.Select(m => m.Id));
        Assert.Equal(new[] { "Regular PvP", "PvE", "Seasonal PvP" }, GameMode.All.Select(m => m.DisplayName));
    }

    sealed class SlowFeed : IFeedSource
    {
        private readonly IFeedSource _inner; private readonly string _path; private readonly TimeSpan _delay;
        public SlowFeed(IFeedSource inner, string path, TimeSpan delay) { _inner = inner; _path = path; _delay = delay; }
        public async Task<System.Text.Json.JsonDocument> GetJsonAsync(string path, TimeSpan timeout, CancellationToken ct)
        {
            if (path == _path) await Task.Delay(_delay, ct);
            return await _inner.GetJsonAsync(path, timeout, ct);
        }
    }
}
