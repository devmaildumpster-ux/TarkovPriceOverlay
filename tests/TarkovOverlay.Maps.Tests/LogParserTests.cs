using TarkovOverlay.Maps.Extracts;
using TarkovOverlay.Maps.Logs;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

public class LogParserTests
{
    // Lines in the formats EFT writes (0.16+ with version column; 0.12-era; TarkovMonitor / community samples).
    private const string PveCustoms =
        "2025-07-29 03:54:40.001 +02:00|0.16.8.1.38114|Info|application|Session mode: Pve\n" +
        "2025-07-29 03:54:43.464 +02:00|0.16.8.1.38114|Info|application|scene preset path:maps/customs_preset.bundle rcid:bigmap.scenespreset.asset\n" +
        "2025-07-29 03:55:10.100 +02:00|0.16.8.1.38114|Info|application|LocationLoaded:12.3 real:15.2\n" +
        "2025-07-29 03:55:20.000 +02:00|0.16.8.1.38114|Info|application|GameStarting\n" +
        "2025-07-29 03:55:30.500 +02:00|0.16.8.1.38114|Info|application|GameStarted\n";

    [Fact]
    public void Parses_pve_raid_load_and_start()
    {
        var p = new EftLogParser();
        var ev = p.Feed(PveCustoms);
        Assert.Equal(new[] { LogEventKind.SessionMode, LogEventKind.MapLoading, LogEventKind.RaidStarting, LogEventKind.RaidStarted }, ev.Select(e => e.Kind));
        Assert.Equal("pve", ev[0].Value);
        Assert.Equal("maps/customs_preset.bundle", ev[1].Value);
        Assert.Equal("bigmap", ev[1].Extra);
        Assert.Equal(new DateTimeOffset(2025, 7, 29, 3, 54, 43, 464, TimeSpan.FromHours(2)), ev[1].Time);

        var raid = new RaidTracker();
        foreach (var e in ev) raid.Apply(e);
        Assert.Equal("customs", raid.Map);
        Assert.Equal(RaidPhase.InRaid, raid.Phase);
        Assert.Equal(PlayerSide.Pmc, raid.Side); // 10.5 s countdown
        Assert.Equal("pve", raid.SessionMode);
    }

    [Fact]
    public void Parses_online_network_game_create_line()
    {
        var line = "2021-01-10 13:26:41.734 +09:00|0.12.9.10532|Debug|application|TRACE-NetworkGameCreate profileStatus: 'Profileid: 5f00, Status: Busy, Ip: 192.0.2.10, Port: 17009, Location: RezervBase, Sid: 192.0.2.10-17009_PID_10.01.21_04.05.38, GameMode: deathmatch, shortId: KKNI'|\n";
        var ev = new EftLogParser().Feed(line);
        var e = Assert.Single(ev);
        Assert.Equal(LogEventKind.MapLoaded, e.Kind);
        Assert.Equal("RezervBase", e.Value);
        Assert.Equal("KKNI", e.Extra);
        var raid = new RaidTracker();
        raid.Apply(e);
        Assert.Equal("reserve", raid.Map);
    }

    [Fact]
    public void Old_dateless_format_uses_folder_date()
    {
        var p = new EftLogParser();
        p.AssumeDate(new DateOnly(2017, 8, 30));
        var ev = p.Feed("17:56:53.884|Info|application|scene preset path:maps/factory_preset.bundle rcid:factory.scenespreset.asset|\n" +
                        "17:58:04.955|Debug|application|TRACE-NetworkGameCreate profileStatus: 'Profileid: 59a6, Status: Busy, Ip: 192.0.2.20, Port: 17011, Location: factory4, Sid: x, GameMode: deathmatch'|\n");
        Assert.Equal(2, ev.Count);
        Assert.Equal("maps/factory_preset.bundle", ev[0].Value);
        Assert.Equal(2017, ev[0].Time!.Value.Year);
        var raid = new RaidTracker();
        foreach (var e in ev) raid.Apply(e);
        Assert.Equal("factory", raid.Map);
    }

    [Theory]
    [InlineData("maps/factory_night_preset.bundle", "factory_night", "night-factory")]
    [InlineData("maps/shopping_mall.bundle", "Shopping_Mall", "interchange")]
    [InlineData("maps/rezerv_base_preset.bundle", "Rezerv_Base", "reserve")]
    [InlineData("maps/city_preset.bundle", "city", "streets-of-tarkov")]
    [InlineData("maps/sandbox_high_preset.bundle", "sandbox_high", "ground-zero-21")]
    [InlineData("maps/laboratory_preset.bundle", "laboratory", "the-lab")]
    [InlineData("maps/unknown_new.bundle", "Shoreline", "shoreline")]
    public void Scene_bundles_map_to_tarkov_dev_names(string scene, string rcid, string expected)
    {
        Assert.Equal(expected, MapIds.FromScene(scene, rcid));
    }

    [Fact]
    public void Scav_raid_has_no_countdown()
    {
        var p = new EftLogParser();
        var ev = p.Feed(
            "2025-07-29 04:00:00.000 +02:00|0.16.8.1|Info|application|scene preset path:maps/woods_preset.bundle rcid:woods.scenespreset.asset\n" +
            "2025-07-29 04:01:00.000 +02:00|0.16.8.1|Info|application|GameStarting\n" +
            "2025-07-29 04:01:00.900 +02:00|0.16.8.1|Info|application|GameStarted\n");
        var raid = new RaidTracker();
        foreach (var e in ev) raid.Apply(e);
        Assert.Equal("woods", raid.Map);
        Assert.Equal(PlayerSide.Scav, raid.Side);
    }

    [Fact]
    public void Raid_end_via_profile_select_and_user_match_over()
    {
        var p = new EftLogParser();
        var raid = new RaidTracker();
        foreach (var e in p.Feed(PveCustoms)) raid.Apply(e);
        foreach (var e in p.Feed("2025-07-29 04:20:00.000 +02:00|0.16.8.1|Info|application|SelectProfile ProfileId:abc123 AccountId:1234567\n")) raid.Apply(e);
        Assert.Equal(RaidPhase.Menu, raid.Phase);
        Assert.NotNull(raid.EndedAt);

        // Online: notifications.log JSON payload
        var p2 = new EftLogParser();
        var ev = p2.Feed("2025-07-29 04:30:00.000 +02:00|0.16.8.1|Info|notifications|Got notification | UserMatchOver\n{\n  \"type\": \"userMatchOver\",\n  \"location\": \"bigmap\",\n  \"shortId\": \"ABC123\"\n}\n");
        var e2 = Assert.Single(ev);
        Assert.Equal(LogEventKind.RaidExited, e2.Kind);
        Assert.Equal("bigmap", e2.Value);
    }

    [Fact]
    public void Quest_status_messages_from_notifications_log()
    {
        var text =
            "2025-07-29 05:00:00.000 +02:00|0.16.8.1|Info|notifications|Got notification | ChatMessageReceived\n" +
            "{\n  \"type\": \"new_message\",\n  \"eventId\": \"1\",\n  \"dialogId\": \"5ac3b934156ae10c4430e83c\",\n  \"message\": {\n    \"_id\": \"x\",\n    \"type\": 12,\n    \"text\": \"with a } brace\",\n    \"templateId\": \"5a68661a86f774500f48afb0 successMessageText\",\n    \"hasRewards\": true\n  }\n}\n" +
            "2025-07-29 05:01:00.000 +02:00|0.16.8.1|Info|notifications|Got notification | ChatMessageReceived\n" +
            "{\n  \"message\": { \"type\": 10, \"templateId\": \"5a68663e86f774501078f78a description\" }\n}\n" +
            "2025-07-29 05:02:00.000 +02:00|0.16.8.1|Info|notifications|Got notification | ChatMessageReceived\n" +
            "{\n  \"message\": { \"type\": 1, \"text\": \"hi\" }\n}\n" +
            "2025-07-29 05:03:00.000 +02:00|0.16.8.1|Info|notifications|Got notification | ChatMessageReceived\n" +
            "{\n  \"message\": { \"type\": 11, \"templateId\": \"597a0f5686f774273b74f676 failMessageText\" }\n}\n";
        var p = new EftLogParser();
        // feed in awkward chunks to prove the incremental parser
        var ev = new List<LogEvent>();
        for (int i = 0; i < text.Length; i += 37) ev.AddRange(p.Feed(text.Substring(i, Math.Min(37, text.Length - i))));
        ev.AddRange(p.Flush());
        Assert.Equal(3, ev.Count);
        Assert.All(ev, e => Assert.Equal(LogEventKind.TaskStatus, e.Kind));
        Assert.Equal(("5a68661a86f774500f48afb0", "finished"), (ev[0].Value, ev[0].Extra));
        Assert.Equal(("5a68663e86f774501078f78a", "started"), (ev[1].Value, ev[1].Extra));
        Assert.Equal(("597a0f5686f774273b74f676", "failed"), (ev[2].Value, ev[2].Extra));
    }

    [Fact]
    public void Matching_aborted_returns_to_menu()
    {
        var p = new EftLogParser();
        var raid = new RaidTracker();
        foreach (var e in p.Feed("2025-07-29 04:00:00.000 +02:00|0.16.8.1|Info|application|scene preset path:maps/woods_preset.bundle rcid:woods.scenespreset.asset\n" +
                                 "2025-07-29 04:00:05.000 +02:00|0.16.8.1|Info|application|Network game matching aborted\n")) raid.Apply(e);
        Assert.Equal(RaidPhase.Menu, raid.Phase);
    }

    [Fact]
    public void Folder_stamp_and_session_files()
    {
        Assert.Equal(new DateTime(2025, 7, 29, 1, 26, 18, DateTimeKind.Local).ToUniversalTime(), EftInstallLocator.FolderStamp(@"C:\EFT\Logs\log_2025.07.29_1-26-18_0.16.8.1.38114"));
        var dir = Directory.CreateTempSubdirectory("efttest");
        try
        {
            var logs = Directory.CreateDirectory(Path.Combine(dir.FullName, "Logs"));
            var old = Directory.CreateDirectory(Path.Combine(logs.FullName, "log_2025.07.28_22-00-00_0.16.8.1"));
            var cur = Directory.CreateDirectory(Path.Combine(logs.FullName, "log_2025.07.29_1-26-18_0.16.8.1"));
            File.WriteAllText(Path.Combine(cur.FullName, "2025.07.29_1-26-18_0.16.8.1 application_000.log"), PveCustoms);
            File.WriteAllText(Path.Combine(cur.FullName, "2025.07.29_1-26-18_0.16.8.1 notifications_000.log"), "");
            Assert.Equal(logs.FullName, EftInstallLocator.LogsDirFor(dir.FullName));
            Assert.Equal(cur.FullName, EftInstallLocator.NewestSessionFolder(logs.FullName));
            var (app, notes) = EftInstallLocator.SessionFiles(cur.FullName);
            Assert.EndsWith("application_000.log", app);
            Assert.EndsWith("notifications_000.log", notes);

            // The watcher replays the current folder and then picks up appended lines.
            var got = new List<LogEvent>();
            using var w = new EftLogWatcher(() => logs.FullName, (e, _) => { lock (got) got.Add(e); });
            w.Start();
            SpinWait.SpinUntil(() => { lock (got) return got.Count >= 4; }, 5000);
            File.AppendAllText(app!, "2025-07-29 04:20:00.000 +02:00|0.16.8.1|Info|application|SelectProfile ProfileId:abc AccountId:1\n");
            SpinWait.SpinUntil(() => { lock (got) return got.Count >= 5; }, 5000);
            lock (got) Assert.Equal(LogEventKind.ProfileSelected, got.Last().Kind);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Launcher_settings_game_root()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "{\"language\":\"en\",\"gameRootDir\":\"D:\\\\Battlestate Games\\\\EFT\",\"x\":1}");
            Assert.Equal(@"D:\Battlestate Games\EFT", Assert.Single(EftInstallLocator.FromLauncherSettings(f)));
        }
        finally { File.Delete(f); }
    }
}
