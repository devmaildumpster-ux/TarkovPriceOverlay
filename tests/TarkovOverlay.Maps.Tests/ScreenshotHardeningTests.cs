using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Geometry;
using TarkovOverlay.Maps.Logs;
using TarkovOverlay.Maps.Position;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

public class ScreenshotHardeningTests
{
    // Real names from EFT players (TarkovMonitor issue #101, tarkov tools/blogs, 2023–2026) plus tolerant variants.
    [Theory]
    [InlineData("2025-03-30[21-04]_175.30, 1.37, 150.68_-0.01464, 0.98439, -0.14329, -0.10113_9.53 (0).png", 175.30, 1.37, 150.68)]
    [InlineData("2025-04-14[20-42]_14.31, 2.55, 57.88_0.17434, 0.81716, -0.31801, 0.44802_15.47 (0).png", 14.31, 2.55, 57.88)]
    [InlineData("2026-08-30[17-30]_388.36, 2.64, -114.48_0.00027, 0.98771, -0.00171, 0.15626_8.60 (0).png", 388.36, 2.64, -114.48)]
    [InlineData("2026-09-04[18-33]_7.86, 38.06, -27.57_-0.03307, -0.13322, 0.00384, -0.99053_21.87 (0).png", 7.86, 38.06, -27.57)]
    [InlineData("2026-01-03[16-20]_166.52, 17.87, 7.16_0.03673, 0.94566, -0.11506, 0.30189_14.37 (0).png", 166.52, 17.87, 7.16)]
    [InlineData("2024-11-23[14-25]_123.45, 2.34, -56.78_0.0, 0.7, 0.0, 0.7_12.34 (0).png", 123.45, 2.34, -56.78)]
    [InlineData("2024-11-23[14-25]_123,45, 2,34, -56,78_0,0, 0,7, 0,0, 0,7_12,34 (0).png", 123.45, 2.34, -56.78)]   // decimal commas
    [InlineData("2024-11-23[14-25]_123.45,2.34,-56.78_0.0,0.7,0.0,0.7_12.34 (0).png", 123.45, 2.34, -56.78)]        // no spaces
    [InlineData("2024-11-23[14-25]_ 123.45 ,  2.34 , -56.78 _0.0, 0.7, 0.0, 0.7_12.34 (3).png", 123.45, 2.34, -56.78)] // extra spaces
    [InlineData("2024-11-23[14-25]_123.45, 2.34, -56.78 0.0, 0.7, 0.0, 0.7_12.34 (0).png", 123.45, 2.34, -56.78)]   // underscore missing
    [InlineData("2024-11-23[14-25]_123.45, 2.34, -56.78_0.0, 0.7, 0.0, 0.7_12.34 (0).jpg", 123.45, 2.34, -56.78)]   // jpg
    [InlineData("2024-11-23[14-25]_123.45, 2.34, -56.78_0.0, 0.7, 0.0, 0.7_12.34 (0).PNG", 123.45, 2.34, -56.78)]
    [InlineData("2024-11-23[14-25-07]_123.45, 2.34, -56.78_0.0, 0.7, 0.0, 0.7_12.34.png", 123.45, 2.34, -56.78)]   // seconds, no counter
    [InlineData("2024-11-23[14-25]_123, 2, -56_0, 1, 0, 0_12 (0).png", 123, 2, -56)]                               // integers
    [InlineData("2024-11-23[14-25]_1.2e2, 2.34, -56.78_1.0e-05, 0.7, 0.0, 0.7_12.34 (0).png", 120, 2.34, -56.78)]  // exponent
    public void Parses_real_and_variant_names(string name, double x, double y, double z)
    {
        var p = ScreenshotName.TryParse(name);
        Assert.NotNull(p);
        Assert.Equal(x, p!.Position.X, 6);
        Assert.Equal(y, p.Position.Y, 6);
        Assert.Equal(z, p.Position.Z, 6);
        Assert.Equal("ok", ScreenshotName.Explain(name));
    }

    [Fact]
    public void Parsing_ignores_the_current_culture()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var p = ScreenshotName.TryParse("2025-03-30[21-04]_175.30, 1.37, 150.68_-0.01464, 0.98439, -0.14329, -0.10113_9.53 (0).png");
            Assert.Equal(175.30, p!.Position.X, 6);
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }

    [Theory]
    [InlineData("2024-11-23[14-25] (0).png", "no position")]
    [InlineData("2024-11-23[14-25]_0.png", "no position")]
    [InlineData("Screenshot 2024-11-23 142500.png", "not a Tarkov screenshot")]
    [InlineData("2024-11-23[14-25]_1.0, 2.0, 3.0_0.0, 0.0, 0.0, 1.0 (0).txt", "not an image")]
    [InlineData("2024-11-23[14-25]_a, b, c_d, e, f, g (0).png", "unrecognised")]
    public void Explains_why_a_name_has_no_position(string name, string expect) => Assert.Contains(expect, ScreenshotName.Explain(name));

    [Fact]
    public void Folder_candidates_cover_onedrive_and_profile()
    {
        var env = new Dictionary<string, string?> { ["OneDrive"] = @"C:\Users\Player\OneDrive", ["USERPROFILE"] = @"C:\Users\Player" };
        var existing = Norm(@"C:\Users\Player\OneDrive\Documents\Escape from Tarkov\Screenshots");
        var c = ScreenshotFolders.Candidates(null, k => env.GetValueOrDefault(k), myDocuments: @"D:\Docs", exists: p => Norm(p) == existing);
        Assert.Equal(Norm(@"D:\Docs\Escape from Tarkov\Screenshots"), Norm(c[0].Path));
        Assert.Contains(c, x => x.Source.Contains("OneDrive"));
        Assert.Contains(c, x => Norm(x.Path) == Norm(@"C:\Users\Player\Documents\Escape from Tarkov\Screenshots"));
        // %OneDrive%\Documents and %USERPROFILE%\OneDrive\Documents are the same folder: listed once
        Assert.Single(c, x => Norm(x.Path) == existing);
        var pick = ScreenshotFolders.Resolve(c);
        Assert.Equal(existing, Norm(pick.Path));
        Assert.True(pick.Exists);
    }

    [Fact]
    public void Folder_override_wins_even_when_missing()
    {
        var c = ScreenshotFolders.Candidates(@"E:\Shots", _ => null, myDocuments: @"D:\Docs", exists: _ => true);
        var pick = ScreenshotFolders.Resolve(c);
        Assert.Equal(@"E:\Shots", pick.Path);
        Assert.Equal("Map settings", pick.Source);
        var none = ScreenshotFolders.Resolve(ScreenshotFolders.Candidates(null, _ => null, myDocuments: @"D:\Docs", exists: _ => false));
        Assert.False(none.Exists);
        Assert.Contains("Escape from Tarkov", none.Path);
    }

    [Fact]
    public void Drive_scan_finds_documents_on_another_drive_when_nothing_else_exists()
    {
        var env = new Dictionary<string, string?> { ["USERPROFILE"] = @"C:\Users\Player", ["USERNAME"] = "Player" };
        var target = Norm(@"D:\Documents\Escape from Tarkov\Screenshots");
        var c = ScreenshotFolders.Candidates(null, k => env.GetValueOrDefault(k), myDocuments: @"C:\Users\Player\Documents",
            exists: p => Norm(p) == target, fixedDrives: () => new[] { @"C:\", @"D:\" });
        var pick = ScreenshotFolders.Resolve(c);
        Assert.Equal(target, Norm(pick.Path));
        Assert.Contains("drive scan", pick.Source);
        Assert.Contains(c, x => Norm(x.Path) == Norm(@"D:\Users\Player\Documents\Escape from Tarkov\Screenshots"));

        // not scanned when a normal candidate exists, or with an override; a failing drive list is ignored
        var normal = ScreenshotFolders.Candidates(null, k => env.GetValueOrDefault(k), myDocuments: @"C:\Users\Player\Documents",
            exists: _ => true, fixedDrives: () => throw new InvalidOperationException("must not be called"));
        Assert.DoesNotContain(normal, x => x.Source.Contains("drive scan"));
        var failing = ScreenshotFolders.Candidates(null, k => env.GetValueOrDefault(k), myDocuments: @"C:\Users\Player\Documents",
            exists: _ => false, fixedDrives: () => throw new UnauthorizedAccessException());
        Assert.False(ScreenshotFolders.Resolve(failing).Exists);
    }

    private static string Norm(string p) => p.Replace('/', '\\');

    private const string Shot = "2025-03-30[21-04]_175.30, 1.37, 150.68_-0.01464, 0.98439, -0.14329, -0.10113_9.53 (0).png";

    [Fact]
    public void Watcher_sees_renamed_files_once()
    {
        var dir = Directory.CreateTempSubdirectory("eftshots");
        try
        {
            var fixes = new List<PositionFix>();
            var log = new List<string>();
            using var w = new ScreenshotWatcher(f => { lock (fixes) fixes.Add(f); }, s => { lock (log) log.Add(s); });
            Assert.True(w.Start(dir.FullName));
            var tmp = Path.Combine(dir.FullName, "tmp_write.tmp");
            File.WriteAllBytes(tmp, new byte[100]);
            File.Move(tmp, Path.Combine(dir.FullName, Shot));
            File.AppendAllText(Path.Combine(dir.FullName, Shot), "more"); // Changed event for the same file
            Assert.True(SpinWait.SpinUntil(() => { lock (fixes) return fixes.Count > 0; }, 5000));
            w.Poll(); // poll finds the same file: still one fix
            Thread.Sleep(300);
            lock (fixes) Assert.Single(fixes);
            Assert.Equal(175.30, fixes[0].Data.Position.X, 6);
            lock (log) Assert.Contains(log, l => l.StartsWith("map: screenshot seen") && l.Contains("x 175.3"));
            Assert.Equal(Shot, w.LastFile);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Poll_finds_files_the_watcher_missed()
    {
        var dir = Directory.CreateTempSubdirectory("eftshots");
        try
        {
            var fixes = new List<PositionFix>();
            using var w = new ScreenshotWatcher(f => { lock (fixes) fixes.Add(f); });
            Assert.True(w.Start(dir.FullName));
            w.Stop(); // simulate a watcher that misses events
            var file = Path.Combine(dir.FullName, Shot);
            File.WriteAllBytes(file, new byte[10]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));
            w.Poll();
            lock (fixes) Assert.Single(fixes);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Logs_ignored_menu_screenshots_with_reason()
    {
        var dir = Directory.CreateTempSubdirectory("eftshots");
        try
        {
            var log = new List<string>();
            int fixes = 0;
            using var w = new ScreenshotWatcher(_ => Interlocked.Increment(ref fixes), s => { lock (log) log.Add(s); });
            Assert.True(w.Start(dir.FullName));
            File.WriteAllBytes(Path.Combine(dir.FullName, "2025-03-30[21-04] (0).png"), new byte[10]);
            Assert.True(SpinWait.SpinUntil(() => { lock (log) return log.Any(l => l.Contains("ignored")); }, 5000));
            Assert.Equal(0, fixes);
            lock (log) Assert.Contains(log, l => l.Contains("no position in the name"));
            lock (log) Assert.Contains(log, l => l.StartsWith("map: screenshot folder candidates:"));
            lock (log) Assert.Contains(log, l => l.StartsWith("map: watching screenshots in"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Missing_folder_is_picked_up_when_it_appears()
    {
        var root = Directory.CreateTempSubdirectory("eftroot");
        try
        {
            var dir = Path.Combine(root.FullName, "Screenshots");
            int fixes = 0;
            using var w = new ScreenshotWatcher(_ => Interlocked.Increment(ref fixes)) { MissingRetry = TimeSpan.FromMilliseconds(200) };
            Assert.False(w.Start(dir));
            Assert.False(w.FolderExists);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, Shot), new byte[10]); // written before the watcher noticed the folder
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref fixes) == 1, 5000));
            Assert.True(w.FolderExists);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Latest_and_newest_image()
    {
        var dir = Directory.CreateTempSubdirectory("eftshots");
        try
        {
            using var w = new ScreenshotWatcher(_ => { });
            w.Start(dir.FullName);
            var a = Path.Combine(dir.FullName, Shot);
            File.WriteAllBytes(a, new byte[1]);
            File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(-5));
            var menu = Path.Combine(dir.FullName, "2025-03-30[21-10] (0).png");
            File.WriteAllBytes(menu, new byte[1]);
            Assert.Equal(menu, w.NewestImage()!.Value.File);
            Assert.Equal(a, w.Latest(TimeSpan.FromHours(3))!.File);
        }
        finally { dir.Delete(true); }
    }

    // ---- screenshot key from the game's "Control settings" log entry ----
    private static string Controls(string variants) =>
        "{\n  \"keyBindings\": [\n    { \"keyName\": \"Jump\", \"variants\": [ { \"keyCode\": [\"Space\"], \"isAxis\": false } ] },\n" +
        "    { \"keyName\": \"MakeScreenshot\", \"variants\": [ " + variants + " ] }\n  ]\n}\n";

    [Theory]
    [InlineData("{ \"keyCode\": [\"Print\"], \"isAxis\": false }, { \"keyCode\": [], \"isAxis\": false }", "ok", "Print")]
    [InlineData("{ \"keyCode\": [], \"isAxis\": false }", "unbound", null)]
    [InlineData("{ \"keyCode\": [\"SysReq\"], \"isAxis\": false }", "sysreq", "SysReq")]
    [InlineData("{ \"keyCode\": [\"LeftAlt\", \"F12\"], \"isAxis\": false }", "ok", "LeftAlt+F12")]
    public void Reads_screenshot_key_binding(string variants, string status, string? keys)
    {
        var (s, k) = ScreenshotKeyCheck.FromControlSettings(Controls(variants));
        Assert.Equal(status, s);
        Assert.Equal(keys, k);
    }

    [Fact]
    public void Log_parser_emits_screenshot_key_from_multiline_control_settings()
    {
        var p = new EftLogParser();
        var text = "2026-11-23 14:00:00.000 -05:00|1.0.0.1|Info|application|Control settings:\n" + Controls("{ \"keyCode\": [\"SysReq\"], \"isAxis\": false }") +
                   "2026-11-23 14:00:01.000 -05:00|1.0.0.1|Info|application|Session mode: Pve\n";
        var events = p.Feed(text);
        var key = Assert.Single(events, e => e.Kind == LogEventKind.ScreenshotKey);
        Assert.Equal("sysreq", key.Value);
        Assert.Contains(events, e => e.Kind == LogEventKind.SessionMode && e.Value == "pve");
        Assert.Contains("SysReq", ScreenshotKeyCheck.Describe("sysreq", "SysReq"));
        Assert.Contains("PrtScn", ScreenshotKeyCheck.Describe("ok", "Print"));
    }

    // ---- which map a position belongs to ----
    [Fact]
    public void Customs_spawn_is_inside_customs_and_far_point_is_nowhere()
    {
        var defs = MapDataService.LoadFallbackDefinitions();
        var customs = defs.First(d => d.Key == "customs");
        var crossroads = new Vec3(-336.33, 0, -86.39);
        Assert.True(MapGuess.Inside(customs, crossroads, 0.1));
        Assert.Null(MapGuess.FromPosition(defs, new Vec3(9000, 0, 9000)));
        Assert.False(MapGuess.Inside(customs, new Vec3(9000, 0, 9000), 0.15));
    }
}
