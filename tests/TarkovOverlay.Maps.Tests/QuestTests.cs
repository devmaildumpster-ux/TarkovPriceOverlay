using TarkovOverlay.Maps.Logs;
using TarkovOverlay.Maps.Quests;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

public class QuestTests
{
    private const string Customs = "56f40101d2720b2a4d8b45d6", Factory = "55f2d3fd4bdc2d5f408b4567", Shoreline = "5704e554d2720bac5b8b456e", Woods = "5704e3c2d2720bac5b8b4567", Reserve = "5704e5fad2720bc05b8b4567";

    [Fact]
    public void Parses_tasks_with_names_traders_and_spots()
    {
        var f = Fx.Tasks();
        var hcp1 = f.Named("Health Care Privacy - Part 1");
        Assert.Equal("Therapist", hcp1.TraderName);
        Assert.Contains(hcp1.Objectives, o => o.Spots.Any(s => s.MapId == Shoreline));
        var mole = f.Named("Saving the Mole");
        Assert.Contains(mole.Objectives, o => o.Spots.Any(s => s.PossibleSpawn));
        // duplicate zones in the feed are collapsed
        foreach (var t in f.Tasks) foreach (var o in t.Objectives)
            Assert.Equal(o.Spots.Count, o.Spots.Select(s => (s.MapId, Math.Round(s.Position.X, 1), Math.Round(s.Position.Z, 1))).Distinct().Count());
    }

    [Fact]
    public void Availability_follows_prerequisites()
    {
        var f = Fx.Tasks();
        var p = new ModeProgress();
        var c1 = f.Named("Chemical - Part 1"); var c2 = f.Named("Chemical - Part 2"); var c3 = f.Named("Chemical - Part 3");
        Assert.Equal(QuestAvailability.Available, QuestLogic.Availability(c1, f, p));
        Assert.Equal(QuestAvailability.Locked, QuestLogic.Availability(c2, f, p));
        p.Set(c1.Id, QuestState.Complete);
        Assert.Equal(QuestAvailability.Available, QuestLogic.Availability(c2, f, p));
        // Part 3 accepts Part 2 "complete" or "active"
        p.Set(c2.Id, QuestState.Active);
        Assert.Equal(QuestAvailability.Available, QuestLogic.Availability(c3, f, p));
        Assert.Equal(QuestAvailability.Active, QuestLogic.Availability(c2, f, p));
    }

    [Fact]
    public void Failed_requirement_and_level_and_faction()
    {
        var f = Fx.Tasks();
        var p = new ModeProgress();
        var buyout = f.Named("Loyalty Buyout"); var c4 = f.Named("Chemical - Part 4");
        p.Set(c4.Id, QuestState.Complete);
        Assert.Equal(QuestAvailability.Locked, QuestLogic.Availability(buyout, f, p));
        p.Set(c4.Id, QuestState.Failed);
        Assert.Equal(QuestAvailability.Available, QuestLogic.Availability(buyout, f, p));

        var bunker = f.Named("The Bunker");
        p.PlayerLevel = 15;
        Assert.Equal(QuestAvailability.Locked, QuestLogic.Availability(bunker, f, p));
        p.PlayerLevel = 21;
        Assert.Equal(QuestAvailability.Available, QuestLogic.Availability(bunker, f, p));

        var bear = f.Tasks.First(t => t.Faction == "BEAR"); var usec = f.Tasks.First(t => t.Faction == "USEC");
        p.Faction = "USEC";
        Assert.Equal(QuestAvailability.Locked, QuestLogic.Availability(bear, f, p));
        Assert.Equal(QuestAvailability.Available, QuestLogic.Availability(usec, f, p));
    }

    [Fact]
    public void Mark_everything_before_a_quest()
    {
        var f = Fx.Tasks();
        var p = new ModeProgress();
        var c4 = f.Named("Chemical - Part 4");
        var closure = QuestLogic.PrerequisiteClosure(c4.Id, f).Select(t => t.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Chemical - Part 1", "Chemical - Part 2", "Chemical - Part 3" }, closure);
        int n = QuestLogic.MarkEverythingBefore(c4.Id, f, p);
        Assert.Equal(4, n);
        Assert.Equal(QuestState.Complete, p.Get(f.Named("Chemical - Part 1").Id));
        Assert.Equal(QuestState.Complete, p.Get(f.Named("Chemical - Part 3").Id));
        Assert.Equal(QuestState.Active, p.Get(c4.Id));
        // Loyalty Buyout needs Part 4 *failed*: marking before it sets Part 4 failed
        var p2 = new ModeProgress();
        QuestLogic.MarkEverythingBefore(f.Named("Loyalty Buyout").Id, f, p2);
        Assert.Equal(QuestState.Failed, p2.Get(c4.Id));
        Assert.Equal(QuestState.Complete, p2.Get(f.Named("Chemical - Part 3").Id));
    }

    [Fact]
    public void Browse_mode_shows_only_unlocked_incomplete_quests_for_that_map()
    {
        var f = Fx.Tasks();
        var p = new ModeProgress();
        var onCustoms = QuestLogic.ObjectivesOnMap(f, p, new[] { Customs }, showAllAvailable: true);
        Assert.Contains(onCustoms, o => o.Task.Name == "Chemical - Part 1");
        Assert.DoesNotContain(onCustoms, o => o.Task.Name == "Chemical - Part 2");       // locked
        Assert.DoesNotContain(onCustoms, o => o.Task.Name.StartsWith("Health Care"));    // other maps
        Assert.All(onCustoms, o => Assert.All(o.Spots, s => Assert.Equal(Customs, s.MapId)));

        p.Set(f.Named("Chemical - Part 1").Id, QuestState.Complete);
        onCustoms = QuestLogic.ObjectivesOnMap(f, p, new[] { Customs }, showAllAvailable: true);
        Assert.DoesNotContain(onCustoms, o => o.Task.Name == "Chemical - Part 1");       // done
        Assert.Contains(onCustoms, o => o.Task.Name == "Chemical - Part 2");             // now unlocked

        // default (no browse): only picked quests; starting a quest picks it
        Assert.Empty(QuestLogic.ObjectivesOnMap(f, p, new[] { Customs }));
        p.Set(f.Named("Chemical - Part 2").Id, QuestState.Active);
        var shown = QuestLogic.ObjectivesOnMap(f, p, new[] { Customs });
        Assert.NotEmpty(shown);
        Assert.All(shown, o => Assert.Equal("Chemical - Part 2", o.Task.Name));

        // unpicked quests and done objectives drop out
        p.Pinned.Remove(f.Named("Chemical - Part 2").Id);
        Assert.Empty(QuestLogic.ObjectivesOnMap(f, p, new[] { Customs }));
        p.Pinned.Add(f.Named("Chemical - Part 2").Id);
        var anyObj = f.Named("Chemical - Part 2").Objectives[0].Id;
        p.DoneObjectives.Add(anyObj);
        Assert.DoesNotContain(QuestLogic.ObjectivesOnMap(f, p, new[] { Customs }), o => o.Objective.Id == anyObj);
    }

    [Fact]
    public void Hand_in_objectives_are_not_put_on_maps()
    {
        var f = Fx.Tasks();
        var all = QuestLogic.ObjectivesOnMap(f, new ModeProgress(), new[] { Customs, Factory, Shoreline, Woods, Reserve }, showAllAvailable: true);
        Assert.DoesNotContain(all, o => o.Objective.Type is "giveItem" or "giveQuestItem" && o.Spots.Count == 0);
    }

    [Fact]
    public void Log_events_update_progress_and_infer_prerequisites()
    {
        var f = Fx.Tasks();
        var p = new ModeProgress();
        var c3 = f.Named("Chemical - Part 3");
        Assert.True(QuestLog.Apply(new QuestLogEntry("pve", c3.Id, "started", DateTimeOffset.UtcNow), p, f));
        Assert.Equal(QuestState.Active, p.Get(c3.Id));
        Assert.Equal(QuestState.Complete, p.Get(f.Named("Chemical - Part 2").Id));
        Assert.Equal(QuestState.Complete, p.Get(f.Named("Chemical - Part 1").Id));
        QuestLog.Apply(new QuestLogEntry("pve", c3.Id, "finished", null), p, f);
        Assert.Equal(QuestState.Complete, p.Get(c3.Id));
        // a later "started" for a finished quest does not undo it
        Assert.False(QuestLog.Apply(new QuestLogEntry("pve", c3.Id, "started", null), p, f));
        Assert.Equal(QuestState.Complete, p.Get(c3.Id));
    }

    [Fact]
    public void Reads_quest_history_from_log_folders_per_mode()
    {
        var dir = Directory.CreateTempSubdirectory("eftq");
        try
        {
            var a = Directory.CreateDirectory(Path.Combine(dir.FullName, "log_2025.07.28_10-00-00_1.0"));
            var b = Directory.CreateDirectory(Path.Combine(dir.FullName, "log_2025.07.29_10-00-00_1.0"));
            File.WriteAllText(Path.Combine(a.FullName, "x application.log"), "2025-07-28 10:00:01.000 +00:00|1.0|Info|application|Session mode: Regular\n");
            File.WriteAllText(Path.Combine(a.FullName, "x notifications.log"),
                "2025-07-28 10:05:00.000 +00:00|1.0|Info|notifications|Got notification | ChatMessageReceived\n{\n \"message\": { \"type\": 12, \"templateId\": \"5979f9ba86f7740f6c3fe9f2 successMessageText\" }\n}\n");
            File.WriteAllText(Path.Combine(b.FullName, "x application.log"), "2025-07-29 10:00:01.000 +00:00|1.0|Info|application|Session mode: Pve\n");
            File.WriteAllText(Path.Combine(b.FullName, "x notifications.log"),
                "2025-07-29 10:05:00.000 +00:00|1.0|Info|notifications|Got notification | ChatMessageReceived\n{\n \"message\": { \"type\": 10, \"templateId\": \"597a0b2986f77426d66c0633 description\" }\n}\n");
            var h = QuestLog.ReadHistory(dir.FullName, "pve");
            Assert.Equal(2, h.Count);
            Assert.Equal(("regular", "5979f9ba86f7740f6c3fe9f2", "finished"), (h[0].ModeId, h[0].TaskId, h[0].Status));
            Assert.Equal(("pve", "597a0b2986f77426d66c0633", "started"), (h[1].ModeId, h[1].TaskId, h[1].Status));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Progress_store_round_trips()
    {
        var file = Path.Combine(Path.GetTempPath(), "qp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new QuestProgressStore(file);
            s.For("pve").Set("abc", QuestState.Complete);
            s.For("pve").PlayerLevel = 12;
            s.For("regular").Set("def", QuestState.Active);
            s.Save();
            var t = new QuestProgressStore(file);
            Assert.Equal(QuestState.Complete, t.For("pve").Get("abc"));
            Assert.Equal(12, t.For("pve").PlayerLevel);
            Assert.Equal(QuestState.None, t.For("pve").Get("def"));
            Assert.Equal(QuestState.Active, t.For("regular").Get("def"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void TarkovTracker_progress_is_applied_read_only()
    {
        var json = "{\"data\":{\"tasksProgress\":[{\"id\":\"a\",\"complete\":true},{\"id\":\"b\",\"failed\":true},{\"id\":\"c\",\"complete\":false}]," +
                   "\"taskObjectivesProgress\":[{\"id\":\"o1\",\"complete\":true},{\"id\":\"o2\",\"complete\":false}],\"playerLevel\":23,\"pmcFaction\":\"USEC\",\"displayName\":\"Player\"},\"meta\":{\"self\":\"x\"}}";
        var pr = TarkovTracker.ParseProgress(json);
        var p = new ModeProgress();
        p.Set("c", QuestState.Complete);
        p.Set("d", QuestState.Active);
        TarkovTracker.Apply(pr, p);
        Assert.Equal(QuestState.Complete, p.Get("a"));
        Assert.Equal(QuestState.Failed, p.Get("b"));
        Assert.Equal(QuestState.None, p.Get("c"));   // tracker says not complete
        Assert.Equal(QuestState.Active, p.Get("d")); // untouched
        Assert.Contains("o1", p.DoneObjectives);
        Assert.DoesNotContain("o2", p.DoneObjectives);
        Assert.Equal(23, p.PlayerLevel);
        Assert.Equal("USEC", p.Faction);
        Assert.Equal("pve", TarkovTracker.ModeFromToken("PVE_0123456789abcdef01"));
        Assert.Equal(new Uri("https://api.tarkovtracker.org/progress"), TarkovTracker.ProgressUri("tarkovtracker.org"));
        Assert.Equal(new Uri("https://tarkovtracker.io/api/v2/progress"), TarkovTracker.ProgressUri("tarkovtracker.io"));
    }
}
