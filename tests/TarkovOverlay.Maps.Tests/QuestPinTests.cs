using TarkovOverlay.Maps.Quests;
using TarkovOverlay.Maps.Settings;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

/// <summary>1.1.2: quests on the map are an explicit selection (user report: "54 POIs" on a fresh tracker).</summary>
public class QuestPinTests
{
    private const string Customs = "56f40101d2720b2a4d8b45d6", Woods = "5704e3c2d2720bac5b8b4567";
    private static readonly string[] OnCustoms = { Customs };

    private static int Pois(Data.TaskFeed f, ModeProgress p, IReadOnlyCollection<string> map, bool browse = false) =>
        QuestLogic.ObjectivesOnMap(f, p, map, browse).Count; // objectives drawn/listed for the map

    [Fact]
    public void Fresh_tracker_draws_zero_quest_pois_although_dozens_are_available()
    {
        var f = Fx.Tasks();
        var p = new ModeProgress();
        Assert.NotEmpty(QuestLogic.ObjectivesOnMap(f, p, OnCustoms, showAllAvailable: true)); // what the old default drew
        Assert.Empty(QuestLogic.ObjectivesOnMap(f, p, OnCustoms));
        Assert.Equal(0, Pois(f, p, OnCustoms));
    }

    [Fact]
    public void Deselect_all_leaves_zero_pois_and_never_falls_back_to_all()
    {
        var f = Fx.Tasks();
        var p = new ModeProgress();
        var started = f.Tasks.Take(6).Select(t => t.Id).ToList();
        foreach (var id in started) p.Set(id, QuestState.Active);
        p.Pinned.Add(f.Named("Chemical - Part 1").Id);
        Assert.True(Pois(f, p, OnCustoms) > 0);
        int removed = QuestPins.DeselectAll(p);
        Assert.True(removed >= 6);
        Assert.Empty(p.Pinned);
        Assert.Equal(0, Pois(f, p, OnCustoms));
        Assert.Equal(0, Pois(f, p, new[] { Woods }));
        Assert.Empty(QuestLogic.ObjectivesOnMap(f, p, OnCustoms));
        // quest progress is untouched (the tracker still knows what's active)
        Assert.Equal(6, p.States.Count(kv => kv.Value == QuestState.Active));
        Assert.Equal((0, 0), QuestPins.Counts(f, p, OnCustoms));
    }

    [Fact]
    public void Ticking_individual_quests_shows_only_their_pois()
    {
        var f = Fx.Tasks();
        var p = new ModeProgress();
        var c1 = f.Named("Chemical - Part 1");
        QuestPins.Select(p, new[] { c1.Id });
        var shown = QuestLogic.ObjectivesOnMap(f, p, OnCustoms);
        Assert.NotEmpty(shown);
        Assert.All(shown, o => Assert.Equal(c1.Id, o.Task.Id));
        Assert.True(Pois(f, p, OnCustoms) > 0);
        Assert.False(QuestPins.Toggle(p, c1.Id));
        Assert.Equal(0, Pois(f, p, OnCustoms));
        Assert.True(QuestPins.Toggle(p, c1.Id));
        // a done quest draws nothing even if still picked
        p.States[c1.Id] = QuestState.Complete;
        Assert.Empty(QuestLogic.ObjectivesOnMap(f, p, OnCustoms));
    }

    [Fact]
    public void Quest_state_changes_pick_and_unpick()
    {
        var f = Fx.Tasks();
        var p = new ModeProgress();
        var c1 = f.Named("Chemical - Part 1");
        p.Set(c1.Id, QuestState.Active);                 // started in game / Mark active
        Assert.Contains(c1.Id, p.Pinned);
        p.Pinned.Remove(c1.Id);                          // user unticks it
        p.Set(c1.Id, QuestState.Active);                 // log replay of the same state: stays unticked
        Assert.DoesNotContain(c1.Id, p.Pinned);
        p.Pinned.Add(c1.Id);
        p.Set(c1.Id, QuestState.Complete);               // finished: off the map
        Assert.DoesNotContain(c1.Id, p.Pinned);
    }

    [Fact]
    public void Select_and_deselect_shown_respect_trader_search_and_map_filters()
    {
        var f = Fx.Tasks();
        var p = new ModeProgress();
        var trader = f.Named("Chemical - Part 1").TraderName;

        var byTrader = new QuestListFilter("Available now", trader);
        int n = QuestPins.SelectShown(f, p, byTrader);
        Assert.True(n > 0);
        Assert.All(p.Pinned, id => Assert.Equal(trader, f.Get(id)!.TraderName));
        Assert.Equal(n, QuestPins.Filter(f, p, byTrader).Count);

        QuestPins.DeselectAll(p);
        var bySearch = new QuestListFilter("All", Search: "chemical");
        QuestPins.SelectShown(f, p, bySearch);
        Assert.NotEmpty(p.Pinned);
        Assert.All(p.Pinned, id => Assert.Contains("chemical", f.Get(id)!.Name, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(p.Pinned, id => QuestLogic.Availability(f.Get(id)!, f, p) is QuestAvailability.Complete or QuestAvailability.Failed);

        QuestPins.DeselectAll(p);
        var customsOnly = new QuestListFilter("Available now", MapIds: OnCustoms);
        QuestPins.SelectShown(f, p, customsOnly);
        Assert.NotEmpty(p.Pinned);
        Assert.All(p.Pinned, id => Assert.True(QuestLogic.TouchesMap(f.Get(id)!, OnCustoms)));
        var (total, here) = QuestPins.Counts(f, p, OnCustoms);
        Assert.Equal(total, here);

        // deselect shown only touches the filtered list
        var woodsQuest = f.Tasks.First(t => !QuestLogic.TouchesMap(t, OnCustoms));
        p.Pinned.Add(woodsQuest.Id);
        int removed = QuestPins.DeselectShown(f, p, customsOnly);
        Assert.True(removed > 0);
        Assert.Equal(new[] { woodsQuest.Id }, p.Pinned.ToArray());

        // "On map" status filter lists exactly the picked quests
        Assert.Equal(new[] { woodsQuest.Id }, QuestPins.Filter(f, p, new QuestListFilter("On map")).Select(t => t.Id).ToArray());
    }

    [Fact]
    public void Upgrade_from_11_starts_with_active_quests_minus_hidden_not_all_available()
    {
        var f = Fx.Tasks();
        var file = new QuestProgressFile();
        var pve = new ModeProgress();
        var c1 = f.Named("Chemical - Part 1"); var hcp = f.Named("Health Care Privacy - Part 1");
        pve.States[c1.Id] = QuestState.Active;      // as loaded from a 1.1.1 file: OnMap missing
        pve.States[hcp.Id] = QuestState.Active;
        pve.OnMap = null;
        file.Modes["pve"] = pve;
        var s = new MapSettings { Schema = 1, ShowAllAvailableQuests = true, HiddenQuests = { hcp.Id } };
        Assert.True(QuestPins.MigrateFrom11(s, file));
        Assert.Equal(new[] { c1.Id }, pve.Pinned.ToArray());
        Assert.Empty(s.HiddenQuests);
        Assert.False(s.ShowAllAvailableQuests);
        Assert.Equal(2, s.Schema);
        Assert.False(QuestPins.MigrateFrom11(s, file)); // once
        Assert.Single(QuestLogic.ObjectivesOnMap(f, pve, OnCustoms).Select(o => o.Task.Id).Distinct());
    }

    [Fact]
    public void Selection_persists_through_the_store()
    {
        var dir = Directory.CreateTempSubdirectory("qp");
        try
        {
            var path = Path.Combine(dir.FullName, "quest-progress.json");
            var store = new QuestProgressStore(path);
            var p = store.For("pve");
            p.Pinned.Add("aaaaaaaaaaaaaaaaaaaaaaaa");
            store.Save();
            var again = new QuestProgressStore(path).For("pve");
            Assert.Equal(new[] { "aaaaaaaaaaaaaaaaaaaaaaaa" }, again.Pinned.ToArray());
            QuestPins.DeselectAll(again);
            Assert.Empty(again.Pinned);

            // an old 1.1.1 file without "OnMap": the selection starts from Active quests only
            File.WriteAllText(path, "{\"Schema\":1,\"Modes\":{\"pve\":{\"States\":{\"bbbbbbbbbbbbbbbbbbbbbbbb\":\"Active\",\"cccccccccccccccccccccccc\":\"Complete\"}}}}");
            var old = new QuestProgressStore(path).For("pve");
            Assert.Equal(new[] { "bbbbbbbbbbbbbbbbbbbbbbbb" }, old.Pinned.ToArray());
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Settings_from_11_load_without_browse_mode()
    {
        var dir = Directory.CreateTempSubdirectory("ms");
        try
        {
            var path = Path.Combine(dir.FullName, "map-settings.json");
            File.WriteAllText(path, "{\"Schema\":1,\"OnlyStartedQuests\":false,\"ShowQuests\":true,\"HiddenQuests\":[]}");
            var s = new MapSettingsStore(path).Settings;
            Assert.False(s.ShowAllAvailableQuests);
            Assert.True(s.ShowQuests);
            Assert.Equal(1, s.Schema);
            Assert.False(new MapSettings().ShowAllAvailableQuests);
            Assert.Equal(2, new MapSettings().Schema);
        }
        finally { dir.Delete(true); }
    }
}
