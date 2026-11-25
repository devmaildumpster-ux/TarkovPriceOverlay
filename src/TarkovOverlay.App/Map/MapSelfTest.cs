using System.Diagnostics;
using System.Windows;
using TarkovOverlay.App.Overlay;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Maps;
using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Definitions;
using TarkovOverlay.Maps.Extracts;
using TarkovOverlay.Maps.Quests;

namespace TarkovOverlay.App.Map;

/// <summary>
/// --map-selftest [outDir] [map ...]: downloads map data + drawings into a temp cache, renders each map with its
/// exits, quest spots and a sample position dot to PNGs in outDir. Exit 0 = every map drew.
/// </summary>
internal static class MapSelfTest
{
    public static int Run(string[] args)
    {
        int i = Array.IndexOf(args, "--map-selftest");
        var rest = args.Skip(i + 1).ToList();
        var outDir = rest.Count > 0 && !rest[0].StartsWith('-') ? rest[0] : Path.Combine(Path.GetTempPath(), "tpo-map-selftest");
        MapView.NoText = rest.Remove("--no-text");
        var style = MapStyle.Satellite;
        int si = rest.IndexOf("--style");
        if (si >= 0 && si + 1 < rest.Count) { style = rest[si + 1].StartsWith("vec", StringComparison.OrdinalIgnoreCase) ? MapStyle.Vector : MapStyle.Satellite; rest.RemoveRange(si, 2); }
        var only = rest.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(outDir);
        var paths = new MapPaths(Path.Combine(outDir, "cache"));
        using var feed = new TarkovDevFeedSource();
        using var http = new HttpClient();
        var data = new MapDataService(paths, feed, (u, ct) => http.GetStringAsync(u, ct), Console.WriteLine);
        var sw = Stopwatch.StartNew();
        var defs = data.GetDefinitionsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var maps = data.GetMapFeedAsync("regular", CancellationToken.None).GetAwaiter().GetResult();
        var tasks = data.GetTaskFeedAsync("regular", CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"data: {defs.Count} drawings, {maps?.Maps.Count ?? 0} maps, {tasks?.Tasks.Count ?? 0} quests in {sw.ElapsedMilliseconds} ms");
        var renderer = new MapImageRenderer(paths, http);
        int failed = 0;
        foreach (var def in defs)
        {
            if (only.Count > 0 && !only.Contains(def.Key)) continue;
            sw.Restart();
            try
            {
                var img = renderer.GetAsync(def, MapLayerChoice.Main, style, null, CancellationToken.None).GetAwaiter().GetResult();
                long render = sw.ElapsedMilliseconds;
                var info = maps?.ByNormalizedName(def.Key);
                var ex = info != null ? ExtractFilter.For(info, PlayerSide.Pmc) : new List<ExtractMarker>();
                var qs = new List<QuestMarker>();
                if (tasks != null && maps != null)
                {
                    var ids = maps.Maps.Where(m => MapDefinitionParser.Resolve(defs, m.NormalizedName)?.Key == def.Key).Select(m => m.Id).ToList();
                    foreach (var o in QuestLogic.ObjectivesOnMap(tasks, new ModeProgress(), ids, showAllAvailable: true))
                        foreach (var s in o.Spots) qs.Add(new QuestMarker(o.Task.Id, o.Task.Name, o.Task.TraderName, o.Objective.Description, s, Colors.Orange, o.Objective.Optional, false));
                }
                var view = new MapView();
                view.Measure(new Size(1400, 900)); view.Arrange(new Rect(0, 0, 1400, 900));
                view.Satellite = img.Style == MapStyle.Satellite;
                view.SetMap(def, img);
                view.Fit();
                view.SetMarkers(ex, qs, null);
                if (ex.FirstOrDefault() is { } first) view.SetPlayer(new PlayerMarker(first.Position, 45, DateTimeOffset.Now.AddMinutes(-2)));
                view.UpdateLayout();
                var rtb = new RenderTargetBitmap(1400, 900, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(view);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(Path.Combine(outDir, def.Key + ".png"))) enc.Save(fs);
                Console.WriteLine($"PASS {def.Key}: {MapStyles.Label(img.Style)}{(img.TileZoom >= 0 ? $" z{img.TileZoom}" : "")} {img.Bitmap.PixelWidth}×{img.Bitmap.PixelHeight} ({img.Source}, {render} ms), {ex.Count} PMC exits, {qs.Count} quest spots");
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"FAIL {def.Key}: {e.GetType().Name}: {e.Message}");
            }
        }
        Console.WriteLine(failed == 0 ? "RESULT: OK" : $"RESULT: {failed} map(s) failed");
        return failed == 0 ? 0 : 1;
    }
}

/// <summary>--map-ui-preview out.png [map]: opens the real main window with the map panel, loads a map and saves a
/// picture of the window (layout check without a Windows PC).</summary>
internal static class MapUiPreview
{
    public static int Run(string[] args)
    {
        int i = Array.IndexOf(args, "--map-ui-preview");
        var outFile = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "tpo-ui.png");
        var mapName = i + 2 < args.Length ? args[i + 2] : "customs";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var services = new Services.AppServices();
        var map = new MapFeature(services, app.Dispatcher);
        map.Start(); // log + screenshot watchers, as in the real app
        var lookupCard = new Overlay.CursorCardWindow();
        var hotkey = new Native.HotkeyManager();
        var lookup = new Services.LookupController(services.Catalog, services.Recognizer, lookupCard, () => services.Settings, () => services.Tracker, app.Dispatcher, services.StackReader, services.OfferReader);
        var auto = new Services.AutoReader(() => services.Settings, lookup, lookupCard);
        var main = new Views.MainWindow(services, hotkey, lookup, lookupCard, auto) { Width = 1280, Height = 860, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        int code = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await map.Panel.LoadMapAsync(mapName);
                await Task.Delay(1500);
                Console.WriteLine("position: " + (map.Panel.LastPositionNote ?? "(no fix)"));
                if (args.Contains("--test-position")) Console.WriteLine("test: " + map.TestLatestScreenshot());
                await Task.Delay(300);
                main.UpdateLayout();
                var root = (FrameworkElement)main.Content;
                var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(outFile)) enc.Save(fs);
                Console.WriteLine($"saved {outFile} ({rtb.PixelWidth}×{rtb.PixelHeight})");
                // 1.2.7: exit list as read by OCR ("--exit-list 'EXFIL01 Scav Camp (Co-Op)|EXFIL02 …'"): same matching as the O reader
                int el = Array.IndexOf(args, "--exit-list");
                if (el >= 0 && el + 1 < args.Length)
                {
                    var lines = args[el + 1].Split('|');
                    var exits = map.Panel.CurrentExtracts.Where(e => e.Kind != Maps.Extracts.ExtractKind.Transit).Select(e => (e.Id, e.Name)).ToList();
                    var ms = Maps.Extracts.ExitListMatcher.MatchLines(lines, exits);
                    map.SetListedExtracts(ms.Select(m => m.ExtractId).ToHashSet());
                    foreach (var e in map.Panel.CurrentExtracts)
                        Console.WriteLine($"exit list: {(e.Kind == Maps.Extracts.ExtractKind.Transit ? "transit " : ms.Any(m => m.ExtractId == e.Id) ? "LISTED  " : "crossed ")} {e.Name} ({e.KindLabel})");
                    await Task.Delay(600);
                    main.UpdateLayout();
                    Save(root, Path.ChangeExtension(outFile, null) + "-exitlist.png", null);
                }
                // 1.3.1: Quests window (search "postman pat" and record list size so an empty-but-Showing-N layout bug is visible)
                if (args.Contains("--quests"))
                {
                    var questSearch = "postman pat";
                    int qs = Array.IndexOf(args, "--quests-search");
                    if (qs >= 0 && qs + 1 < args.Length) questSearch = args[qs + 1];
                    string? modeName = null;
                    int qm = Array.IndexOf(args, "--quests-mode");
                    if (qm >= 0 && qm + 1 < args.Length) modeName = args[qm + 1];
                    map.OpenQuests();
                    var qw = app.Windows.OfType<QuestsWindow>().FirstOrDefault() ?? throw new InvalidOperationException("Quests window did not open");
                    for (int w = 0; w < 200 && (qw.SummaryText.Length == 0 || qw.SummaryText.Contains("Loading", StringComparison.OrdinalIgnoreCase)); w++)
                        await Task.Delay(100);
                    if (modeName != null)
                    {
                        var want = Core.Catalog.GameMode.All.FirstOrDefault(m => m.Id.Equals(modeName, StringComparison.OrdinalIgnoreCase) || m.DisplayName.Equals(modeName, StringComparison.OrdinalIgnoreCase));
                        if (want != null && !Equals(qw.ModeBox.SelectedItem, want))
                        {
                            qw.ModeBox.SelectedItem = want;
                            for (int w = 0; w < 200 && (qw.SummaryText.Length == 0 || qw.SummaryText.Contains("Loading", StringComparison.OrdinalIgnoreCase)); w++)
                                await Task.Delay(100);
                        }
                    }
                    qw.UpdateLayout();
                    Console.WriteLine($"quests list {qw.List.ActualWidth:0}×{qw.List.ActualHeight:0} items={qw.List.Items.Count} containers={System.Windows.Media.VisualTreeHelper.GetChildrenCount(qw.List)} summary={qw.SummaryText}");
                    Save((FrameworkElement)qw.Content, Path.ChangeExtension(outFile, null) + "-quests.png", Theme.Window);
                    qw.SearchBox.Text = questSearch;
                    await Task.Delay(300);
                    qw.UpdateLayout();
                    var names = string.Join(" | ", qw.Rows.Select(r => $"{r.Name} ({r.Trader}, {r.Status})"));
                    Console.WriteLine($"quests search '{questSearch}': list {qw.List.ActualWidth:0}×{qw.List.ActualHeight:0} items={qw.List.Items.Count} names={names} summary={qw.SummaryText}");
                    Save((FrameworkElement)qw.Content, Path.ChangeExtension(outFile, null) + "-quests-postman.png", Theme.Window);
                    qw.Close();
                }
                // 1.2.1: Satellite/Vector toggle (button state, shortcut path, single-style map)
                if (args.Contains("--style-toggle"))
                {
                    var pnl = map.Panel;
                    bool before = map.Settings.PreferSatelliteMap;
                    Console.WriteLine($"style: shown {pnl.ShownStyle}, button '{pnl.StyleButtonText}' enabled={pnl.StyleButtonEnabled}");
                    map.ToggleMapStyle("selftest");
                    for (int w = 0; w < 100 && pnl.ShownStyle == (before ? Maps.Definitions.MapStyle.Satellite : Maps.Definitions.MapStyle.Vector); w++) await Task.Delay(100);
                    Console.WriteLine($"style after toggle: shown {pnl.ShownStyle}, button '{pnl.StyleButtonText}', hint '{pnl.HintText}', setting PreferSatellite={map.Settings.PreferSatelliteMap}");
                    main.UpdateLayout();
                    Save(root, Path.ChangeExtension(outFile, null) + "-toggled.png", null);
                    map.ToggleMapStyle("selftest");
                    for (int w = 0; w < 100 && map.Settings.PreferSatelliteMap == before && pnl.ShownStyle != (before ? Maps.Definitions.MapStyle.Satellite : Maps.Definitions.MapStyle.Vector); w++) await Task.Delay(100);
                    await Task.Delay(500);
                    Console.WriteLine($"style toggled back: shown {pnl.ShownStyle}, setting PreferSatellite={map.Settings.PreferSatelliteMap}");
                    await pnl.LoadMapAsync("streets-of-tarkov");
                    Console.WriteLine($"streets: shown {pnl.ShownStyle}, button '{pnl.StyleButtonText}' enabled={pnl.StyleButtonEnabled}");
                    map.ToggleMapStyle("selftest");
                    Console.WriteLine($"streets toggle: hint '{pnl.HintText}', setting PreferSatellite={map.Settings.PreferSatelliteMap}");
                    await pnl.LoadMapAsync(mapName);
                    await Task.Delay(800);
                }
                // 1.2.3: Controls & hotkeys window (normal size + the whole list)
                if (args.Contains("--controls"))
                {
                    var cw = Views.ControlsWindow.Open(services, main);
                    await Task.Delay(600);
                    cw.UpdateLayout();
                    Save((FrameworkElement)cw.Content, Path.ChangeExtension(outFile, null) + "-controls.png", Theme.Window);
                    var sv = Find<System.Windows.Controls.ScrollViewer>(cw).First();
                    var body = (FrameworkElement)sv.Content;
                    body.Measure(new Size(sv.ActualWidth, double.PositiveInfinity));
                    Console.WriteLine($"controls: {cw.Sections.Count} sections, {cw.Sections.Sum(x => x.Rows.Count)} rows, list height {body.DesiredSize.Height:0} (view {sv.ActualHeight:0})");
                    cw.Height = Math.Min(4000, body.DesiredSize.Height + (cw.ActualHeight - sv.ActualHeight) + 40);
                    await Task.Delay(600);
                    cw.UpdateLayout();
                    Console.WriteLine($"controls full: window {cw.ActualWidth:0}×{cw.ActualHeight:0}, scrollable {sv.ScrollableHeight:0}");
                    Save((FrameworkElement)cw.Content, Path.ChangeExtension(outFile, null) + "-controls-full.png", Theme.Window);
                    foreach (var sec in cw.Sections) foreach (var r in sec.Rows.Where(r => r.HotkeyId != null)) Console.WriteLine($"  hotkey row [{r.HotkeyId}] '{r.Keys}'");
                    cw.Close();
                }
                // 1.2.2: auto layer switching end-to-end through the real screenshot watcher
                if (args.Contains("--auto-layer")) await AutoLayerE2E(map, main, root, outFile);
                // 1.1.4: Settings… tabs (Map › "Map settings…" opens the Map tab of the same window)
                foreach (var tab in new[] { Views.SettingsTab.Overlay, Views.SettingsTab.Map, Views.SettingsTab.Troubleshooting })
                {
                    var w = Views.SettingsWindow.Open(tab, main)!;
                    await Task.Delay(400);
                    w.UpdateLayout();
                    var r2 = (FrameworkElement)w.Content;
                    var b2 = new RenderTargetBitmap((int)r2.ActualWidth, (int)r2.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    b2.Render(r2);
                    var e2 = new PngBitmapEncoder();
                    e2.Frames.Add(BitmapFrame.Create(b2));
                    var f2 = Path.ChangeExtension(outFile, null) + "-settings-" + tab.ToString().ToLowerInvariant() + ".png";
                    using (var fs = File.Create(f2)) e2.Save(fs);
                    Console.WriteLine($"settings tab requested {tab} -> showing {w.CurrentTab}; saved {f2}");
                    Console.WriteLine("  ticks: " + string.Join(" | ", Checks(r2).Select(c => $"{((c.Content as System.Windows.Controls.TextBlock)?.Text ?? c.Content?.ToString() ?? "?")[..Math.Min(28, ((c.Content as System.Windows.Controls.TextBlock)?.Text ?? c.Content?.ToString() ?? "?").Length)]}={c.IsChecked}")));
                }
                // 1.2.0: full Overlay settings page (the window scrolls), in-game search bar, hideout tracker
                {
                    var w = Views.SettingsWindow.Open(Views.SettingsTab.Overlay, main)!;
                    var sv = Find<System.Windows.Controls.ScrollViewer>(w).First();
                    var body = (FrameworkElement)sv.Content;
                    body.Measure(new Size(sv.ActualWidth > 0 ? sv.ActualWidth : 640, double.PositiveInfinity));
                    Console.WriteLine($"overlay page height {body.DesiredSize.Height:0}");
                    w.Height = Math.Min(4000, body.DesiredSize.Height + 200);
                    await Task.Delay(400);
                    w.UpdateLayout();
                    Save((FrameworkElement)w.Content, Path.ChangeExtension(outFile, null) + "-settings-overlay-full.png", Theme.Window);
                }
                try
                {
                    var load = services.Catalog.SwitchModeAsync(Core.Catalog.GameMode.FromId(services.Settings.ModeId));
                    await Task.WhenAny(load, Task.Delay(45000));
                    Console.WriteLine("catalog: " + (services.Catalog.Context is { } c ? $"{c.Snapshot.Items.Count} items" : "not loaded"));
                }
                catch (Exception e) { Console.WriteLine("catalog load failed: " + e.Message); }
                Console.WriteLine("search: create"); 
                var search = new Overlay.SearchOverlayWindow(services);
                Console.WriteLine("search: show");
                search.Show();
                Console.WriteLine("search: shown");
                search.Query = args.SkipWhile(a => a != "--search").Skip(1).FirstOrDefault() ?? "graphics";
                await Task.Delay(400);
                search.UpdateLayout();
                Save((FrameworkElement)search.Content, Path.ChangeExtension(outFile, null) + "-search.png", null);
                Console.WriteLine("search rows: " + string.Join(" | ", search.ResultsText.Take(5)));
                search.OpenSelected();
                await Task.Delay(400);
                search.UpdateLayout();
                Save((FrameworkElement)search.Content, Path.ChangeExtension(outFile, null) + "-search-card.png", null);
                search.CloseForGood();
                var hw = Views.HideoutWindow.Open(services, main);
                await Task.Delay(500);
                hw.UpdateLayout();
                Save((FrameworkElement)hw.Content, Path.ChangeExtension(outFile, null) + "-hideout.png", Theme.Window);
                hw.Close();
                code = 0;
            }
            catch (Exception e) { Console.WriteLine("FAILED: " + e); }
            finally { map.Dispose(); lookupCard.Close(); main.Close(); app.Shutdown(); }
        };
        app.Run(main);
        return code;
    }

    private static async Task AutoLayerE2E(MapFeature map, Window main, FrameworkElement root, string outFile)
    {
        var pnl = map.Panel;
        var folder = map.ScreenshotFolder;
        Console.WriteLine($"autolayer: mode {map.Settings.MapAutoLayerMode}, folder {folder}, floors [{string.Join(", ", pnl.FloorNames)}]");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("autolayer: FAILED (no screenshot folder)"); return; }
        await pnl.LoadMapAsync("reserve");
        await Task.Delay(800);
        var made = new List<string>();
        async Task<bool> Shot(string tag, double x, double y, double z, string expect)
        {
            var name = $"{DateTime.Now:yyyy-MM-dd[HH-mm]}_{x:0.00}, {y:0.00}, {z:0.00}_0.00000, 0.70711, 0.00000, 0.70711_{made.Count + 1}.11 (0).png";
            var file = Path.Combine(folder, name);
            var bmp = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgr32, null, new byte[64], 16);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(file)) enc.Save(fs);
            made.Add(file);
            var t0 = DateTime.Now;
            while ((DateTime.Now - t0).TotalSeconds < 30 && !(pnl.FloorName == expect && map.LastFix?.File == file)) await Task.Delay(100);
            await Task.Delay(2500); // let the floor image load
            bool ok = pnl.FloorName == expect;
            Console.WriteLine($"autolayer {tag}: y {y} -> floor '{pnl.FloorName}' (expected '{expect}') shown {pnl.ShownStyle}, hint '{pnl.HintText}', note '{pnl.LastPositionNote}', active window={main.IsActive} => {(ok ? "OK" : "FAILED")}");
            return ok;
        }
        bool all = true;
        all &= await Shot("bunker", 60, -10, -100, "Bunkers");
        pnl.CenterOnPlayer(10);
        await Task.Delay(1500);
        main.UpdateLayout();
        Save(root, Path.ChangeExtension(outFile, null) + "-reserve-bunkers.png", null);
        // a hand pick is kept while no newer screenshot arrives
        pnl.PickFloorByHand("3rd Floor");
        await Task.Delay(2500);
        Console.WriteLine($"autolayer manual: picked 3rd Floor -> floor '{pnl.FloorName}' (redraws for the same screenshot keep it) => {(pnl.FloorName == "3rd Floor" ? "OK" : "FAILED")}");
        all &= pnl.FloorName == "3rd Floor";
        all &= await Shot("surface", 60, 2, -100, "Main");
        Console.WriteLine($"autolayer surface style: {pnl.ShownStyle} (PreferSatellite={map.Settings.PreferSatelliteMap})");
        all &= pnl.ShownStyle == (map.Settings.PreferSatelliteMap ? Maps.Definitions.MapStyle.Satellite : Maps.Definitions.MapStyle.Vector);
        // underground-only mode ignores upper floors
        var before = map.Settings.MapAutoLayerMode;
        map.SetAutoLayerMode("underground", "selftest");
        all &= await Shot("pawns-2nd (underground only)", -100, -2, 50, "Main");
        all &= await Shot("command-bunker (underground only)", -100, -8, 50, "Bunkers");
        map.SetAutoLayerMode("off", "selftest");
        all &= await Shot("surface (off)", 60, 2, -100, "Bunkers");
        map.SetAutoLayerMode(before, "selftest");
        all &= await Shot("surface (all)", 60, 2, -100, "Main");
        // Shoreline: vector-only underground and back to satellite
        await pnl.LoadMapAsync("shoreline");
        await Task.Delay(800);
        all &= await Shot("shoreline west wing", -180, -8, -80, "Underground");
        all &= await Shot("shoreline surface", -180, -3, -80, "Main");
        Console.WriteLine($"autolayer shoreline back on Main: shown {pnl.ShownStyle}");
        await Task.Delay(1000);
        foreach (var f in made) try { File.Delete(f); } catch (IOException) { }
        Console.WriteLine($"autolayer: removed {made.Count} synthetic screenshots; RESULT {(all ? "OK" : "FAILED")}");
        await pnl.LoadMapAsync("customs");
    }

    private static void Save(FrameworkElement el, string file, Brush? bg)
    {
        double w = Math.Max(el.ActualWidth, 1), h = Math.Max(el.ActualHeight, 1);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(bg ?? new SolidColorBrush(Color.FromRgb(30, 30, 30)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(file)) enc.Save(fs);
        Console.WriteLine($"saved {file} ({rtb.PixelWidth}×{rtb.PixelHeight})");
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (c is T t) yield return t;
            foreach (var x in Find<T>(c)) yield return x;
        }
    }

    private static IEnumerable<System.Windows.Controls.Primitives.ToggleButton> Checks(DependencyObject root)
    {
        foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (c is System.Windows.Controls.Primitives.ToggleButton t) yield return t;
            foreach (var x in Checks(c)) yield return x;
        }
    }
}

/// <summary>
/// 1.2.1 --map-style-preview outPrefix [map] [x z scale]: renders the same view of a map in Satellite and in Vector
/// (exits, area labels, a sample "You" dot), whole map and zoomed in, as outPrefix-sat.png / -vec.png /
/// -sat-zoom.png / -vec-zoom.png. Same view transform for both, so pins must sit on the same spots.
/// </summary>
internal static class MapStylePreview
{
    public static int Run(string[] args)
    {
        int i = Array.IndexOf(args, "--map-style-preview");
        var prefix = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "tpo-style");
        var mapName = i + 2 < args.Length ? args[i + 2] : "customs";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var services = new Services.AppServices();
        var paths = new MapPaths(services.Paths);
        using var http = new HttpClient();
        using var feed = new TarkovDevFeedSource();
        var data = new MapDataService(paths, feed, (u, ct) => http.GetStringAsync(u, ct), Console.WriteLine);
        int code = 1;
        var view = new MapView { Width = 1400, Height = 900 };
        var win = new Window { Content = view, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
        win.Loaded += async (_, _) =>
        {
            try
            {
                var defs = await data.GetDefinitionsAsync(CancellationToken.None);
                var maps = await data.GetMapFeedAsync("regular", CancellationToken.None);
                var def = MapDefinitionParser.Resolve(defs, mapName) ?? throw new InvalidOperationException("no map " + mapName);
                var info = maps?.ByNormalizedName(mapName);
                var ex = info != null ? ExtractFilter.For(info, null) : new List<ExtractMarker>();
                var renderer = new MapImageRenderer(paths, http);
                // sample dot: Customs dorms area by default
                double px = 180, pz = 160, zoomScale = 9;
                int k = Array.IndexOf(args, "--at");
                if (k >= 0 && k + 3 < args.Length) { px = double.Parse(args[k + 1], System.Globalization.CultureInfo.InvariantCulture); pz = double.Parse(args[k + 2], System.Globalization.CultureInfo.InvariantCulture); zoomScale = double.Parse(args[k + 3], System.Globalization.CultureInfo.InvariantCulture); }
                double py = 0;
                int yi = Array.IndexOf(args, "--y");
                if (yi >= 0 && yi + 1 < args.Length) py = double.Parse(args[yi + 1], System.Globalization.CultureInfo.InvariantCulture);
                var player = new PlayerMarker(new TarkovOverlay.Maps.Geometry.Vec3(px, py, pz), 120, DateTimeOffset.Now.AddMinutes(-1));
                // 1.2.2: --floor NAME, or --floor auto (the floor tarkov.dev's extents pick for the dot)
                var floor = MapLayerChoice.Main;
                int fi = Array.IndexOf(args, "--floor");
                if (fi >= 0 && fi + 1 < args.Length)
                {
                    var choices = MapLayerChoice.For(def);
                    var want = args[fi + 1] == "auto" ? AutoLayer.Target(def, player.Position, AutoLayerMode.All) : args[fi + 1];
                    floor = choices.FirstOrDefault(c => c.Name == want) ?? MapLayerChoice.Main;
                    Console.WriteLine($"floor: {floor.Name} (asked '{args[fi + 1]}', dot at {px},{py},{pz})");
                }
                foreach (var style in new[] { MapStyle.Satellite, MapStyle.Vector })
                {
                    var sw = Stopwatch.StartNew();
                    var img = await renderer.GetAsync(def, floor, style, null, CancellationToken.None);
                    var tag = style == MapStyle.Satellite ? "sat" : "vec";
                    Console.WriteLine($"{tag}: {MapStyles.Label(img.Style)}{(img.TileZoom >= 0 ? $" z{img.TileZoom}" : "")} {img.Bitmap.PixelWidth}×{img.Bitmap.PixelHeight} placement {img.Placement.X:0.###},{img.Placement.Y:0.###} {img.Placement.Width:0.###}×{img.Placement.Height:0.###} ({img.Source}, {sw.ElapsedMilliseconds} ms)");
                    view.Satellite = img.Style == MapStyle.Satellite;
                    view.SetMap(def, img, keepView: false);
                    view.SetMarkers(ex, Array.Empty<QuestMarker>(), null);
                    view.SetPlayer(player);
                    // identical view for both styles: fit the map bounds, not the picture
                    view.ShowWhole(def.BoundsRect);
                    await Task.Delay(200);
                    Save(view, prefix + "-" + tag + ".png");
                    view.CenterOn(def.Projection.ToMap(player.Position), zoomScale);
                    await Task.Delay(200);
                    Save(view, prefix + "-" + tag + "-zoom.png");
                    foreach (var probe in ex.Take(3))
                    {
                        var mp = def.Projection.ToMap(probe.Position);
                        var bp = ((mp.X - img.Placement.X) / img.Placement.Width * img.Bitmap.PixelWidth, (mp.Y - img.Placement.Y) / img.Placement.Height * img.Bitmap.PixelHeight);
                        Console.WriteLine($"  {probe.Name}: map px {mp} → picture px ({bp.Item1:0.#}, {bp.Item2:0.#}) of {img.Bitmap.PixelWidth}×{img.Bitmap.PixelHeight}");
                    }
                }
                code = 0;
            }
            catch (Exception e) { Console.WriteLine("FAILED: " + e); }
            finally { win.Close(); app.Shutdown(); }
        };
        app.Run(win);
        return code;
    }

    private static void Save(FrameworkElement el, string file)
    {
        el.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(file)) enc.Save(fs);
        Console.WriteLine($"saved {file} ({rtb.PixelWidth}×{rtb.PixelHeight})");
    }
}
