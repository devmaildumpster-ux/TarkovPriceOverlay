using TarkovOverlay.Maps.Extracts;
using TarkovOverlay.Maps.Logs;
using TarkovOverlay.Maps.Position;
using TarkovOverlay.Maps.Settings;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

public class ScreenshotCleanupTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("eftclean");
    private readonly List<string> _log = new();
    public void Dispose()
    {
        try { foreach (var f in _dir.EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal; _dir.Delete(true); } catch { }
    }

    private static string Name(int i, string ext = "png") => $"2026-11-23[14-{i:00}]_{100 + i}.5, 2.0, -{50 + i}.25_0.0, 0.7, 0.0, 0.7_1.{i} (0).{ext}";

    private string Make(string name, int minutesAgo)
    {
        var p = Path.Combine(_dir.FullName, name);
        File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
        var t = DateTime.UtcNow.AddMinutes(-minutesAgo);
        File.SetCreationTimeUtc(p, t); File.SetLastWriteTimeUtc(p, t);
        return p;
    }

    /// <summary>Ten coordinate shots (0 = newest) plus files that must never be touched.</summary>
    private (List<string> Shots, List<string> Others) Seed()
    {
        var shots = Enumerable.Range(0, 10).Select(i => Make(Name(i, i % 3 == 1 ? "jpg" : "png"), i * 5)).ToList();
        var others = new List<string>
        {
            Make("2026-11-23[14-00] (0).png", 500),                // menu / hideout screenshot (no position)
            Make("holiday.png", 600), Make("notes.txt", 600),
            Make(Name(20, "bmp"), 700),                            // coordinate name but not png/jpg
            Make(Name(21, "png") + ".txt", 700),
        };
        var ro = Make(Name(22), 800); File.SetAttributes(ro, FileAttributes.ReadOnly); others.Add(ro);
        var sub = Directory.CreateDirectory(Path.Combine(_dir.FullName, "old"));
        var inSub = Path.Combine(sub.FullName, Name(30)); File.WriteAllBytes(inSub, new byte[] { 1 }); others.Add(inSub);
        Directory.CreateDirectory(Path.Combine(_dir.FullName, Name(31))); // a folder with a coordinate-looking name
        return (shots, others);
    }

    private void AssertOthersUntouched(List<string> others)
    {
        foreach (var o in others) Assert.True(File.Exists(o), "touched " + o);
        Assert.True(Directory.Exists(Path.Combine(_dir.FullName, Name(31))));
    }

    [Fact]
    public void KeepNewest_5_keeps_the_newest_five_and_deletes_older_ones()
    {
        var (shots, others) = Seed();
        var c = new ScreenshotCleaner(new PlainFileDisposer(), _log.Add) { Mode = ScreenshotCleanupMode.KeepNewest, KeepCount = 5 };
        Assert.Equal(5, c.Trim(_dir.FullName));
        for (int i = 0; i < 10; i++) Assert.Equal(i < 5, File.Exists(shots[i]));
        AssertOthersUntouched(others);
        Assert.Contains(_log, l => l.StartsWith("map: screenshot cleanup") && l.Contains("deleted 5") && l.Contains("5 kept"));
        // a new shot arrives → the oldest kept one goes
        var fresh = Make(Name(40), 0);
        Assert.Equal(1, c.AfterRead(_dir.FullName, fresh));
        Assert.False(File.Exists(shots[4])); Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Off_deletes_nothing()
    {
        var (shots, others) = Seed();
        var c = new ScreenshotCleaner(new PlainFileDisposer(), _log.Add) { Mode = ScreenshotCleanupMode.Off };
        Assert.Equal(0, c.Trim(_dir.FullName));
        Assert.Equal(0, c.AfterRead(_dir.FullName, shots[0]));
        Assert.All(shots, s => Assert.True(File.Exists(s)));
        AssertOthersUntouched(others);
    }

    [Fact]
    public void DeleteImmediately_deletes_and_last_position_survives_a_restart()
    {
        var (shots, others) = Seed();
        var saved = Path.Combine(_dir.FullName, "old", "last-position.json");
        var fix = new PositionFix(ScreenshotName.TryParse(Path.GetFileName(shots[0]))!, shots[0], DateTimeOffset.Now);
        SavedPosition.Save(saved, fix, "customs");
        var c = new ScreenshotCleaner(new PlainFileDisposer(), _log.Add) { Mode = ScreenshotCleanupMode.DeleteImmediately, RetryDelay = TimeSpan.Zero };
        c.AfterRead(_dir.FullName, shots[0]);
        Assert.All(shots, s => Assert.False(File.Exists(s)));
        AssertOthersUntouched(others);
        // "restart": nothing in the folder, the saved position still gives the dot
        Assert.Null(new ScreenshotWatcher(_ => { }).Latest(TimeSpan.FromHours(3)));
        var back = SavedPosition.Load(saved, TimeSpan.FromHours(3), _dir.FullName);
        Assert.NotNull(back);
        Assert.Equal("customs", back!.Value.Map);
        Assert.Equal(100.5, back.Value.Fix.Data.Position.X, 6);
        Assert.Equal(-50.25, back.Value.Fix.Data.Position.Z, 6);
        Assert.Null(SavedPosition.Load(saved, TimeSpan.FromHours(3).Negate())); // too old → ignored
        Assert.Null(SavedPosition.Load(Path.Combine(_dir.FullName, "missing.json"), TimeSpan.FromHours(3)));
    }

    private sealed class FakeDisposer : IFileDisposer
    {
        public List<string> Recycled = new(), Deleted = new();
        public Func<string, Exception?> Fail = _ => null;
        public void Delete(string path) { if (Fail(path) is { } e) throw e; Deleted.Add(path); }
        public void Recycle(string path) { if (Fail(path) is { } e) throw e; Recycled.Add(path); }
    }

    [Fact]
    public void MoveToRecycleBin_goes_through_the_recycler_and_keeps_the_newest()
    {
        var (shots, others) = Seed();
        var d = new FakeDisposer();
        var c = new ScreenshotCleaner(d, _log.Add) { Mode = ScreenshotCleanupMode.MoveToRecycleBin, KeepCount = 5 };
        Assert.Equal(5, c.Trim(_dir.FullName));
        Assert.Empty(d.Deleted);
        Assert.Equal(shots.Skip(5).Select(Path.GetFileName).OrderBy(x => x), d.Recycled.Select(Path.GetFileName).OrderBy(x => x));
        Assert.Contains(_log, l => l.Contains("recycled 5"));
        AssertOthersUntouched(others);
        // no Recycle Bin available (e.g. not Windows) → file left alone, no exception
        var c2 = new ScreenshotCleaner(new PlainFileDisposer(), _log.Add) { Mode = ScreenshotCleanupMode.MoveToRecycleBin, KeepCount = 5 };
        Assert.Equal(0, c2.Trim(_dir.FullName));
        Assert.All(shots, s => Assert.True(File.Exists(s)));
    }

    [Fact]
    public void Io_errors_are_logged_not_thrown()
    {
        var (shots, others) = Seed();
        var d = new FakeDisposer { Fail = p => p.EndsWith(Path.GetFileName(shots[6])) ? new IOException("in use") : p.EndsWith(Path.GetFileName(shots[7])) ? new UnauthorizedAccessException("denied") : null };
        var c = new ScreenshotCleaner(d, _log.Add) { Mode = ScreenshotCleanupMode.KeepNewest, KeepCount = 5, LockedRetries = 1, RetryDelay = TimeSpan.Zero };
        Assert.Equal(3, c.Trim(_dir.FullName));
        Assert.Contains(_log, l => l.Contains("2 skipped"));
        // DeleteImmediately on a locked file: retried, then left for later
        var c2 = new ScreenshotCleaner(new FakeDisposer { Fail = _ => new IOException("locked") }, _log.Add) { Mode = ScreenshotCleanupMode.DeleteImmediately, LockedRetries = 2, RetryDelay = TimeSpan.Zero };
        Assert.Equal(0, c2.AfterRead(_dir.FullName, shots[0]));
        Assert.Contains(_log, l => l.Contains("still in use"));
        // missing folder
        Assert.Equal(0, c.Trim(Path.Combine(_dir.FullName, "nope")));
        AssertOthersUntouched(others);
    }

    [Fact]
    public void Only_coordinate_files_in_the_folder_itself_are_cleanable()
    {
        var p = Make(Name(1), 0);
        Assert.True(ScreenshotCleaner.IsCleanable(_dir.FullName, p));
        Assert.False(ScreenshotCleaner.IsCleanable(_dir.FullName, Make("2026-11-23[14-00] (0).png", 0)));
        Assert.False(ScreenshotCleaner.IsCleanable(_dir.FullName, Make(Name(2, "bmp"), 0)));
        Assert.False(ScreenshotCleaner.IsCleanable(Path.Combine(_dir.FullName, "other"), p)); // not the watched folder
        Assert.False(ScreenshotCleaner.IsCleanable(_dir.FullName, Path.Combine(_dir.FullName, "missing", Name(3))));
    }

    [Fact]
    public void Setting_defaults_to_keep_newest_5_and_upgrades_the_old_checkbox()
    {
        var s = new MapSettings();
        Assert.Equal(ScreenshotCleanupMode.KeepNewest, s.CleanupMode);
        Assert.Equal(5, s.ScreenshotKeepCount);
        var file = Path.Combine(_dir.FullName, "map-settings.json");
        File.WriteAllText(file, "{\"Schema\":2,\"DeleteProcessedScreenshots\":false}");
        Assert.Equal(ScreenshotCleanupMode.KeepNewest, new MapSettingsStore(file).Settings.CleanupMode);
        File.WriteAllText(file, "{\"Schema\":2,\"DeleteProcessedScreenshots\":true}");
        Assert.Equal(ScreenshotCleanupMode.DeleteImmediately, new MapSettingsStore(file).Settings.CleanupMode);
        File.WriteAllText(file, "{\"ScreenshotCleanup\":\"Off\"}");
        Assert.Equal(ScreenshotCleanupMode.Off, new MapSettingsStore(file).Settings.CleanupMode);
    }
}

public class ExtractSideTests
{
    [Fact]
    public void Unknown_side_shows_every_customs_exit_with_shared_ones_blue()
    {
        var customs = Fx.Maps.ByNormalizedName("customs")!;
        var all = ExtractFilter.For(customs, null, includeTransits: false);
        Assert.Contains(all, e => e.Name == "Dorms V-Ex" && e.Kind == ExtractKind.Pmc);
        Assert.Contains(all, e => e.Name == "Old Road Gate" && e.Kind == ExtractKind.Scav);
        Assert.Single(all, e => e.Name == "ZB-1011");
        Assert.Equal(ExtractKind.Shared, all.Single(e => e.Name == "ZB-1011").Kind);
        Assert.Equal(ExtractKind.Shared, all.Single(e => e.Name == "Crossroads").Kind);
        // a user's screenshot: Scav view had ZB-1011 but no Dorm V-Ex / Smuggler's Boat / ZB-1012/1013
        var scav = ExtractFilter.For(customs, PlayerSide.Scav, includeTransits: false);
        Assert.Contains(scav, e => e.Name == "ZB-1011" && e.Kind == ExtractKind.Shared);
        Assert.DoesNotContain(scav, e => e.Name == "Dorms V-Ex");
        Assert.True(all.Count > scav.Count);
    }

    private static RaidTracker Raid(string mode, bool countdown, bool backToMenu)
    {
        var r = new RaidTracker();
        var t = DateTimeOffset.Now;
        r.Apply(new LogEvent(LogEventKind.SessionMode, t, mode));
        r.Apply(new LogEvent(LogEventKind.MapLoading, t, "maps/customs_preset.bundle"));
        r.Apply(new LogEvent(LogEventKind.MapLoaded, t, "bigmap", "ABC123"));
        if (countdown) r.Apply(new LogEvent(LogEventKind.RaidStarting, t.AddSeconds(1)));
        r.Apply(new LogEvent(LogEventKind.RaidStarted, t.AddSeconds(countdown ? 12 : 1.5)));
        if (backToMenu) r.Apply(new LogEvent(LogEventKind.RaidExited, t.AddMinutes(30)));
        return r;
    }

    [Fact]
    public void Side_is_used_in_regular_raids_but_not_in_menus_or_pve()
    {
        Assert.Equal(PlayerSide.Pmc, Raid("regular", true, false).ExtractSide);
        Assert.Equal(PlayerSide.Scav, Raid("regular", false, false).ExtractSide);
        var menu = Raid("regular", false, true);
        Assert.Equal(PlayerSide.Scav, menu.Side);   // still remembered…
        Assert.Null(menu.ExtractSide);              // …but not used for the exits in menus
        Assert.Null(Raid("pve", false, false).ExtractSide); // PvE: countdown heuristic not trusted
        Assert.Null(Raid("pve", true, false).ExtractSide);
        Assert.Null(new RaidTracker().ExtractSide);
    }
}
