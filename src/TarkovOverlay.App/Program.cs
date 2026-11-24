using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TarkovOverlay.App.Native;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Services;
using TarkovOverlay.App.Views;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Geometry;
using TarkovOverlay.Core.Recognition;

namespace TarkovOverlay.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke-test")) return Cli(() => SmokeTest());
        if (args.Contains("--update-check")) return Cli(() => UpdateCheckCli());
        int di = Array.IndexOf(args, "--diagnose");
        if (di >= 0) return Cli(() => Diagnose(args, di));
        if (args.Contains("--map-ui-preview")) return Cli(() => Map.MapUiPreview.Run(args));
        if (args.Contains("--map-selftest")) return Cli(() => Map.MapSelfTest.Run(args));
        if (args.Contains("--card-preview")) return Cli(() => CardPreview.Run(args));
        if (args.Contains("--map-style-preview")) return Cli(() => Map.MapStylePreview.Run(args));
        if (args.Contains("--lifetime-selftest")) return Cli(() => LifetimeSelfTest(legacy: args.Contains("legacy")));
        int ac = Array.IndexOf(args, "--auto-close-ms");
        return RunGui(ac >= 0 && ac + 1 < args.Length && int.TryParse(args[ac + 1], out var ms) ? ms : 0);
    }

    // ---------------- GUI ----------------
    private static int RunGui(int autoCloseMs = 0)
    {
        using var mutex = new Mutex(true, @"Local\TarkovPriceOverlay.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Tarkov Price Overlay is already running.", "Tarkov Price Overlay", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        AppServices? services = null;
        app.DispatcherUnhandledException += (_, e) =>
        {
            LogError(services, e.Exception);
            MessageBox.Show("Unexpected error: " + e.Exception.Message + "\n\nDetails were written to error.log in %LOCALAPPDATA%\\TarkovPriceOverlay.", "Tarkov Price Overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        services = new AppServices();
        var card = new CursorCardWindow();
        var hotkey = new HotkeyManager();
        var lookup = new LookupController(services.Catalog, services.Recognizer, card, () => services.Settings, () => services.Tracker, Dispatcher.CurrentDispatcher, services.StackReader, services.OfferReader);
        hotkey.Pressed += lookup.Trigger;
        var auto = new AutoReader(() => services.Settings, lookup, card);
        lookup.Snapshot = auto.SnapshotNear;
        lookup.ProbeFrame = auto.ProbeFrameNear;
        if (services.Settings.HotkeyEnabled) hotkey.Register();
        Log.Write($"start v{typeof(Program).Assembly.GetName().Version} · hotkey {hotkey.Active?.Label ?? "none"} ({string.Join("; ", hotkey.Attempts)}) · base {AppContext.BaseDirectory}");
        var map = new Map.MapFeature(services, Dispatcher.CurrentDispatcher); // 1.1: map + quest tracker
        lookup.Cards = services.Cards;            // 1.2.0: card fields / progress filter
        lookup.ItemShown = services.SetLastItem;  // 1.2.0: Wiki / tarkov.dev shortcuts
        var shell = new ShellFeatures(services);  // 1.2.0: extra shortcuts, search bar, tray icon
        var main = new MainWindow(services, hotkey, lookup, card, auto, shell);
        shell.ApplyHotkeys();
        shell.ApplyTray();
        AppLifetime.Wire(app, main, new (string, Action)[]
        {
            ("tray icon, extra shortcuts, search bar", shell.Dispose),
            ("map/quests (log + screenshot watchers)", map.Dispose),
            ("auto-read poller", auto.Stop),
            ("lookups", lookup.Close),
            ("cursor card", () => { card.HideCard("exit"); card.Close(); }),
            ("hotkey (UnregisterHotKey)", hotkey.Dispose),
            ("settings save, catalog refresh, Tesseract, HTTP", services.Dispose),
        }, Log.Write);
        main.Loaded += async (_, _) =>
        {
            // Tesseract is only the fallback: load it up front only when Windows OCR is missing (saves memory).
            if (!services.WindowsOcr.IsAvailable)
                _ = Task.Run(() => services.Tesseract.Warmup(out _)).ContinueWith(_ => main.Dispatcher.BeginInvoke(main.UpdateOcrState));
            auto.Apply();
            map.Start();
            if (autoCloseMs <= 0) TarkovOverlay.App.Views.ControlsWindow.ShowOnFirstLaunch(services, main); // 1.2.3: once, never activated
            await services.Catalog.SwitchModeAsync(GameMode.FromId(services.Settings.ModeId));
            TrimMemory("catalog loaded");
        };
        if (autoCloseMs > 0) CloseAfter(main, autoCloseMs);
        int code = app.Run(main);
        Log.Write($"shutdown: dispatcher stopped; exiting with code {code}");
        return code;
    }

    private static void CloseAfter(Window w, int ms)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); Log.Write($"auto-close: closing the main window after {ms} ms"); w.Close(); };
        t.Start();
    }

    /// <summary>
    /// --lifetime-selftest [legacy]: same window order as the real app (hidden cursor-card window first, then
    /// the main window), closes the main window after 1.5 s and reports whether the process gets past app.Run.
    /// "legacy" uses the 1.0.5 wiring (OnMainWindowClose only) to reproduce the lingering-process bug.
    /// </summary>
    private static int LifetimeSelfTest(bool legacy)
    {
        var sw = Stopwatch.StartNew();
        void Say(string m) { Console.WriteLine($"[{sw.ElapsedMilliseconds,5} ms] {m}"); Log.Write("selftest: " + m); }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var card = new CursorCardWindow();
        var main = new Window { Title = "lifetime selftest", Width = 200, Height = 120, ShowInTaskbar = false };
        Say($"windows created; Application.MainWindow is the {(app.MainWindow == card ? "hidden cursor card" : app.MainWindow == main ? "main window" : "nothing")}");
        ShellFeatures? shell = null;
        if (!legacy)
        {
            // 1.2.0: the tray icon, extra shortcuts and the (hidden) search bar must not keep the process alive either
            var services = new AppServices();
            services.Settings.MinimizeToTray = true; // in memory only, never saved here
            shell = new ShellFeatures(services);
            shell.ApplyHotkeys();
            shell.ApplyTray();
            _ = shell.SearchWindow;
            Say($"tray icon {(shell.TrayVisible ? "shown" : "not available on this system")}; extra shortcuts: {shell.HotkeyStatus(ShellFeatures.Wiki)} | {shell.HotkeyStatus(ShellFeatures.Dev)} | {shell.HotkeyStatus(ShellFeatures.Search)} | map style {shell.HotkeyStatus(ShellFeatures.MapStyle)}");
            AppLifetime.Wire(app, main, new (string, Action)[]
            {
                ("tray icon, extra shortcuts, search bar", shell.Dispose),
                ("cursor card", () => { card.HideCard("exit"); card.Close(); }),
            }, Say);
        }
        main.Closed += (_, _) => Say("main window closed");
        CloseAfter(main, 1500);
        int code = app.Run(main);
        if (shell != null) Say($"tray icon after shutdown: {(shell.TrayVisible ? "STILL SHOWN (FAIL)" : "removed")}");
        Say($"app.Run returned {code}: PASS, process is exiting");
        return 0;
    }

    /// <summary>Returns the large temporary buffers from JSON parsing / captures to the OS.</summary>
    internal static void TrimMemory(string why)
    {
        long before = Environment.WorkingSet;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Log.Write($"memory: trimmed after {why}: working set {before / 1048576} → {Environment.WorkingSet / 1048576} MB, managed {GC.GetTotalMemory(false) / 1048576} MB");
    }

    private static void LogError(AppServices? s, Exception ex)
    {
        try
        {
            var root = s?.Paths.Root ?? new Core.AppPaths().Root;
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "error.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------- CLI helpers ----------------
    private static int Cli(Func<int> body)
    {
        NativeMethods.AttachConsole(-1); // ATTACH_PARENT_PROCESS: print into the launching terminal
        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(stdout);
        }
        catch (IOException) { }
        try { return body(); }
        catch (Exception ex) { Console.WriteLine("FAILED: " + ex); return 1; }
    }

    /// <summary>
    /// --smoke-test: verifies the packaged model + native paths, Tesseract warmup and a real read, Windows OCR
    /// availability, and the VC++ runtime. Works from any working directory. Exit 0 = all good, 1 = bundled
    /// component broken, 2 = only Windows OCR unavailable (Tesseract fallback still works).
    /// </summary>
    /// <summary>1.2.6: --update-check prints what the launch check would say (no cache, nothing written).</summary>
    private static int UpdateCheckCli()
    {
        var u = new TarkovOverlay.Core.Updates.UpdateChecker(cacheFile: null);
        var r = u.CheckAsync(enabled: true, skippedVersion: null, force: true).GetAwaiter().GetResult();
        Console.WriteLine($"Tarkov Price Overlay update check · this version {r.Current} · URL {u.Api}");
        Console.WriteLine($"RESULT: {r.Status} · latest {r.Latest?.ToString() ?? "?"} · {r.Detail} · page {r.Url}");
        return r.Status is TarkovOverlay.Core.Updates.UpdateStatus.UpToDate or TarkovOverlay.Core.Updates.UpdateStatus.Available ? 0 : 2;
    }

    private static int SmokeTest()
    {
        var baseDir = AppContext.BaseDirectory;
        bool ok = true;
        void Check(string what, bool pass, string detail = "")
        {
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
            ok &= pass;
        }
        Console.WriteLine($"Tarkov Price Overlay {typeof(Program).Assembly.GetName().Version} smoke test · base {baseDir} · cwd {Environment.CurrentDirectory}");
        Check("64-bit process", Environment.Is64BitProcess);
        var model = Path.Combine(baseDir, "tessdata", "eng.traineddata");
        Check("tessdata\\eng.traineddata", File.Exists(model) && new FileInfo(model).Length > 1_000_000, model);
        foreach (var dll in new[] { "tesseract55.dll", "leptonica-1.85.0.dll" })
            Check("x64\\" + dll, File.Exists(Path.Combine(baseDir, "x64", dll)));
        var vc = Desktop.MissingVcRuntime();
        Check("Visual C++ 2015–2022 x64 runtime", vc == null, vc == null ? "" : "missing " + vc + " — install https://aka.ms/vs/17/release/vc_redist.x64.exe");

        // Embedded synthetic capture (rendered with DejaVu Sans by tools/make_ocr_fixtures.py), so the check does not
        // depend on installed fonts: exercises the tooltip detector plus both OCR engines.
        var scene = LoadSampleScene();
        var det = TooltipDetector.Detect(scene, 245, 125, 1.0, wideRetry: false);
        Check("Tooltip detector finds sample tooltip", det.Status == DetectionStatus.Found, det.Best is { } b ? DiagnosticsFormatter.Rect(b.Interior) : det.Status.ToString());
        var sample = det.Status == DetectionStatus.Found ? scene.Crop(det.Best!.TitlePanel) : scene;
        const string expect = "VPO-209";
        using (var tess = new global::TarkovOverlay.Ocr.Tesseract.TesseractOcrEngine(baseDir))
        {
            var warm = tess.Warmup(out var err);
            Check("Tesseract engine loads", warm, err ?? "");
            if (warm)
            {
                var read = tess.RecognizeAsync(sample, 3, CancellationToken.None).GetAwaiter().GetResult();
                Check("Tesseract reads sample", read.Text.Contains(expect, StringComparison.OrdinalIgnoreCase), $"\"{read.Text}\" in {read.Elapsed.TotalMilliseconds:0} ms");
            }
        }
        var win = new Ocr.WindowsOcrEngine();
        bool winOk = win.IsAvailable;
        Console.WriteLine($"{(winOk ? "PASS" : "WARN")}  Windows OCR {(winOk ? "(" + win.LanguageTag + ")" : "unavailable — " + win.UnavailableReason)}");
        if (winOk)
        {
            var read = win.RecognizeAsync(sample, 2, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"{(read.Text.Contains(expect, StringComparison.OrdinalIgnoreCase) ? "PASS" : "WARN")}  Windows OCR reads sample — \"{read.Text}\" in {read.Elapsed.TotalMilliseconds:0} ms");
        }
        Console.WriteLine(ok ? (winOk ? "RESULT: OK" : "RESULT: OK (Windows OCR missing; Tesseract fallback only)") : "RESULT: FAILED");
        return ok ? (winOk ? 0 : 2) : 1;
    }

    private static RgbImage LoadSampleScene()
    {
        using var res = typeof(Program).Assembly.GetManifestResourceStream("TarkovOverlay.App.smoke_sample.ppm.gz")
            ?? throw new InvalidOperationException("embedded smoke sample missing");
        using var gz = new System.IO.Compression.GZipStream(res, System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        ms.Position = 0;
        return Ppm.Read(ms);
    }

    /// <summary>
    /// --diagnose image [--mode regular|pve|pvp-season] [--cursor x,y] [--scale 1.25]: runs the same detection and
    /// OCR pipeline on a saved screenshot and writes image.diag.txt (also printed). With --cursor it emulates the
    /// hover crops around that point; otherwise it searches the whole image.
    /// </summary>
    private static int Diagnose(string[] args, int di)
    {
        if (di + 1 >= args.Length) { Console.WriteLine("usage: TarkovPriceOverlay --diagnose <image> [--mode regular|pve|pvp-season] [--cursor x,y] [--scale 1.0] [--screen-height 1080]"); return 1; }
        var path = Path.GetFullPath(args[di + 1]);
        string? Opt(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var mode = GameMode.FromId(Opt("--mode"));
        double scale = double.TryParse(Opt("--scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) && sc is >= 0.5 and <= 4 ? sc : 1.0;
        var img = ImageIo.Load(path);
        var sb = new StringBuilder();
        sb.AppendLine($"diagnose {path} ({img.Width}×{img.Height}) · mode {mode.DisplayName} · scale {scale}");
        var sw = Stopwatch.StartNew();

        var services = new AppServices();
        services.Catalog.SwitchModeAsync(mode, refresh: false).GetAwaiter().GetResult();
        if (services.Catalog.Context == null) services.Catalog.RefreshNowAsync().GetAwaiter().GetResult();
        var ctx = services.Catalog.Context;
        sb.AppendLine("catalog: " + services.Catalog.State.Describe(DateTimeOffset.UtcNow));
        if (ctx == null) { Finish(); return 1; }
        sb.AppendLine($"catalog load {sw.ElapsedMilliseconds} ms");
        sw.Restart();

        DetectionResult det;
        RgbImage source;
        (int X, int Y)? cursorInSource = null;
        var monitor = new MonitorSnapshot(new PixelRect(0, 0, img.Width, img.Height), new PixelRect(0, 0, img.Width, img.Height), scale);
        var cur = Opt("--cursor")?.Split(',');
        if (cur is { Length: 2 } && int.TryParse(cur[0], out var cx) && int.TryParse(cur[1], out var cy))
        {
            var first = CaptureGeometry.First(cx, cy, monitor);
            source = img.Crop(first);
            det = TooltipDetector.Detect(source, cx - first.X, cy - first.Y, scale, wideRetry: false);
            cursorInSource = (cx - first.X, cy - first.Y);
            sb.AppendLine($"first crop {DiagnosticsFormatter.Rect(first)}");
            DiagnosticsFormatter.AppendDetection(sb, det);
            if (det.Status != DetectionStatus.Found)
            {
                var wide = CaptureGeometry.Wide(cx, cy, monitor);
                source = img.Crop(wide);
                det = TooltipDetector.Detect(source, cx - wide.X, cy - wide.Y, scale, wideRetry: true);
                cursorInSource = (cx - wide.X, cy - wide.Y);
                sb.AppendLine($"wide crop {DiagnosticsFormatter.Rect(wide)}");
                DiagnosticsFormatter.AppendDetection(sb, det);
            }
        }
        else
        {
            source = img;
            det = TooltipDetector.Detect(img, img.Width / 2, img.Height / 2, scale, wideRetry: true);
            DiagnosticsFormatter.AppendDetection(sb, det);
        }
        sb.AppendLine($"detection {sw.ElapsedMilliseconds} ms");
        var panel = det.Status == DetectionStatus.Found ? TooltipDetector.CropPanel(source, det.Best!.TitlePanel, cursorInSource?.X, cursorInSource?.Y, scale) : source;
        if (det.Status != DetectionStatus.Found) sb.AppendLine("no tooltip detected — reading the whole crop as a fallback (the live overlay would show “Item tooltip not visible”)");
        if (panel.Width * panel.Height <= 4_000_000)
        {
            var outcome = services.Recognizer.RecognizeAsync(panel, ctx.Items, CancellationToken.None, det.Status == DetectionStatus.Found ? det.Best!.TitleLines : 0).GetAwaiter().GetResult();
            DiagnosticsFormatter.AppendOutcome(sb, outcome);
            if (cursorInSource is { } cis && det.Status == DetectionStatus.Found && outcome.Result.Kind == Core.Matching.MatchKind.Verified && outcome.Result.Single is { StackMaxSize: > 1 } si)
            {
                int screenH = int.TryParse(Opt("--screen-height"), out var shv) && shv > 100 ? shv : img.Height;
                var st = services.StackReader.ReadAsync(source, cis.X, cis.Y, det.Best!.Interior, screenH, si.StackMaxSize!.Value, CancellationToken.None).GetAwaiter().GetResult();
                sb.AppendLine($"stack count (screen height {screenH}): {(st.Count?.ToString() ?? "not read")} · {st.Note}{(st.Reads.Count > 0 ? " · " + string.Join(" | ", st.Reads) : "")}");
            }
            if (det.Status == DetectionStatus.Found && outcome.Result.Kind == Core.Matching.MatchKind.Verified && det.Best!.Lines > 1)
            {
                var nameLines = outcome.Attempts.LastOrDefault(x => x.Match.Kind == Core.Matching.MatchKind.Verified)?.Read.Lines;
                var sec = det.Best.SecondaryPanel is { } sp ? source.Crop(sp) : null;
                var (offer, note) = services.OfferReader.ReadAsync(nameLines, sec, CancellationToken.None).GetAwaiter().GetResult();
                sb.AppendLine($"trader offer: {(offer is { } o ? Core.Economics.Money.Rub(o) : "none")} · {note}");
            }
        }
        else sb.AppendLine("crop too large to OCR; pass --cursor x,y");
        sb.AppendLine($"pipeline total (excluding catalog load) {sw.ElapsedMilliseconds} ms");
        Finish();
        services.Dispose();
        return 0;

        void Finish()
        {
            var text = sb.ToString();
            Console.Write(text);
            try { File.WriteAllText(path + ".diag.txt", text, new UTF8Encoding(false)); Console.WriteLine("wrote " + path + ".diag.txt"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("could not write report: " + ex.Message); }
        }
    }
}
