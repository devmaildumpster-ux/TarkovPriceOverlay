using System.Windows;
using System.Windows.Controls;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Views;
using TarkovOverlay.Maps.Logs;
using TarkovOverlay.Maps.Position;

namespace TarkovOverlay.App.Map;

/// <summary>
/// Game folder, screenshot folder and log-reading options for the map and quest tracker. 1.1.4: a panel on the
/// "Map &amp; Screenshots" tab of Settings… (was its own Map settings dialog). Every change applies at once: ticks and
/// the cleanup choice on click, folders when the box loses focus / after Browse….
/// </summary>
public sealed class MapSettingsPanel : StackPanel
{
    public MapSettingsPanel(MapFeature f)
    {
        var s = f.Settings;
        var p = this;
        bool loading = true;
        p.Margin = new Thickness(0);

        // game folder
        p.Children.Add(Ui.Text("Escape from Tarkov folder (for its Logs: map, raid start/end, PMC/Scav, quest updates)", 12, Theme.Text, FontWeights.SemiBold, wrap: true));
        var gameBox = new TextBox { Text = s.EftInstallDir ?? "", Width = 470, Margin = new Thickness(0, 4, 4, 4), Background = Theme.Panel, Foreground = Theme.Text };
        var detected = Ui.Text(f.Install is { } i ? $"Found: {i.LogsDir} (from {i.Source})" : "Not found yet. Leave empty to auto-detect (launcher settings, registry, Steam, running game), or pick the folder.", 11, Theme.Muted, wrap: true);
        p.Children.Add(Ui.Row(gameBox, Ui.Button("Browse…", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick the Escape from Tarkov folder (the one with EscapeFromTarkov.exe)" };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            {
                gameBox.Text = dlg.FolderName;
                detected.Text = EftInstallLocator.LogsDirFor(dlg.FolderName) is { } logs ? $"Logs folder: {logs}" : "No Logs folder there yet (it appears after the game has been started once).";
            }
        })));
        p.Children.Add(detected);
        var watchLogs = Check("Read the game's logs (map, raid, PMC/Scav, quest started/finished messages)", s.WatchGameLogs);
        var questLogs = Check("Update quest progress automatically from the game's quest messages", s.UpdateQuestsFromLogs);
        p.Children.Add(watchLogs); p.Children.Add(questLogs);

        // screenshots
        p.Children.Add(new Border { Height = 10 });
        p.Children.Add(Ui.Text("Position dot (Tarkov puts your position in each in-raid screenshot's file name)", 12, Theme.Text, FontWeights.SemiBold, wrap: true));
        var shotBox = new TextBox { Text = s.ScreenshotsDir ?? "", Width = 470, Margin = new Thickness(0, 4, 4, 4), Background = Theme.Panel, Foreground = Theme.Text };
        p.Children.Add(Ui.Row(shotBox, Ui.Button("Browse…", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick Tarkov's Screenshots folder" };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) shotBox.Text = dlg.FolderName;
        })));
        var cands = ScreenshotFolders.Candidates(s.ScreenshotsDir);
        var pick = ScreenshotFolders.Resolve(cands);
        p.Children.Add(Ui.Text($"Empty = find it automatically. Using: {pick.Path} ({pick.Source}, {(pick.Exists ? "found" : "not found yet")})", 11, pick.Exists ? Theme.Muted : Theme.Warn, wrap: true));
        p.Children.Add(Ui.Text("Checked: " + string.Join(" · ", cands.Select(c => $"{c.Path} ({(c.Exists ? "found" : "missing")})")), 10.5, Theme.Muted, wrap: true));
        p.Children.Add(Ui.Text(TarkovOverlay.Maps.Logs.ScreenshotKeyCheck.Describe(f.ScreenshotKeyStatus, f.ScreenshotKeys) +
            ". Windows 11: if PrtScn opens the Snipping Tool, Tarkov may not get the key; turn off Settings › Accessibility › Keyboard › \"Use the Print screen key to open screen capture\", or bind Tarkov's screenshot to another key.", 10.5,
            f.ScreenshotKeyStatus is "unbound" or "sysreq" ? Theme.Warn : Theme.Muted, wrap: true));
        var watchShots = Check("Watch for new screenshots (press PrtScn in raid to update the dot)", s.WatchScreenshots);
        p.Children.Add(watchShots);
        var keep = s.ScreenshotKeepCount;
        var modes = new (ScreenshotCleanupMode Mode, string Label)[]
        {
            (ScreenshotCleanupMode.KeepNewest, $"Keep newest {keep}"),
            (ScreenshotCleanupMode.DeleteImmediately, "Delete right after reading"),
            (ScreenshotCleanupMode.MoveToRecycleBin, $"Move to Recycle Bin (keep newest {keep})"),
            (ScreenshotCleanupMode.Off, "Off (keep all)"),
        };
        var cleanup = new ComboBox { Width = 260, Margin = new Thickness(0, 4, 0, 2) };
        foreach (var m in modes) cleanup.Items.Add(m.Label);
        cleanup.SelectedIndex = Math.Max(0, Array.FindIndex(modes, m => m.Mode == s.CleanupMode));
        p.Children.Add(Ui.Row(Ui.Text("Position screenshot cleanup: ", 12, Theme.Text), cleanup));
        p.Children.Add(Ui.Text("Only touches Tarkov's position screenshots (files named like 2026-11-23[14-05]_123.4, 5.6, -78.9_…png) directly in the folder above. " +
            "Other screenshots, other files and sub-folders are never touched. Your last position is saved separately, so the dot still shows after a restart.", 10.5, Theme.Muted, wrap: true));

        // map behaviour
        p.Children.Add(new Border { Height = 10 });
        p.Children.Add(Ui.Text("Map", 12, Theme.Text, FontWeights.SemiBold));
        var autoSwitch = Check("Switch to the raid's map automatically", s.AutoSwitchMap);
        var ocr = Check("Experimental: read the exit list when I press O in raid (dims exits that aren't on it)", s.ReadExitListWithOcr);
        ocr.ToolTip = "Takes a screenshot of the top-right of the game's screen about 0.7 s and 1.5 s after O, only while in raid, and reads it with the same OCR as the price cards.";
        p.Children.Add(autoSwitch); p.Children.Add(ocr);
        // 1.2.1: map style
        var sat = Check("Default to the satellite map where available (tarkov.dev's rendered top-down tiles; vector drawing otherwise)", s.PreferSatelliteMap);
        sat.ToolTip = "Satellite + vector: Customs, Woods, Shoreline, Interchange, Reserve, Factory, Ground Zero. Satellite only: The Lab, The Labyrinth, Icebreaker. Vector only: Streets of Tarkov, Lighthouse, Terminal.";
        sat.Checked += (_, _) => { if (!loading) f.SetPreferSatellite(true, "settings"); };
        sat.Unchecked += (_, _) => { if (!loading) f.SetPreferSatellite(false, "settings"); };
        Action sync = () => { loading = true; sat.IsChecked = s.PreferSatelliteMap; loading = false; };
        f.MapStyleChanged += sync;
        Unloaded += (_, _) => f.MapStyleChanged -= sync;
        Loaded += (_, _) => { f.MapStyleChanged -= sync; f.MapStyleChanged += sync; sync(); };
        p.Children.Add(sat);
        p.Children.Add(StyleBox);
        // 1.2.2: auto layer switching
        var layerModes = new (string Id, string Label)[] { ("all", "All floors & underground"), ("underground", "Underground only"), ("off", "Off (manual)") };
        var autoLayer = new ComboBox { Width = 200, Margin = new Thickness(0, 4, 0, 2) };
        foreach (var m in layerModes) autoLayer.Items.Add(m.Label);
        bool layerLoading = true;
        Action syncLayer = () => { layerLoading = true; autoLayer.SelectedIndex = Math.Max(0, Array.FindIndex(layerModes, m => m.Id == s.MapAutoLayerMode)); layerLoading = false; };
        syncLayer();
        autoLayer.SelectionChanged += (_, _) => { if (!layerLoading && autoLayer.SelectedIndex >= 0) f.SetAutoLayerMode(layerModes[autoLayer.SelectedIndex].Id, "settings"); };
        f.AutoLayerModeChanged += syncLayer;
        Unloaded += (_, _) => f.AutoLayerModeChanged -= syncLayer;
        Loaded += (_, _) => { f.AutoLayerModeChanged -= syncLayer; f.AutoLayerModeChanged += syncLayer; syncLayer(); };
        autoLayer.ToolTip = "Uses the height in each new position screenshot (tarkov.dev's floor extents) to show the floor you're on: e.g. Reserve Bunkers, Customs Underground, Factory Tunnels, Labs Technical. Picking a floor by hand sticks until the next screenshot.";
        p.Children.Add(Ui.Row(Ui.Text("Auto-switch map layer on screenshot: ", 12, Theme.Text), autoLayer));

        var saved = Ui.Text("", 10.5, Theme.Muted);
        p.Children.Add(saved);
        void Apply()
        {
            if (loading) return;
            var newGame = string.IsNullOrWhiteSpace(gameBox.Text) ? null : gameBox.Text.Trim();
            var newShots = string.IsNullOrWhiteSpace(shotBox.Text) ? null : shotBox.Text.Trim();
            var cleanupMode = modes[Math.Max(0, cleanup.SelectedIndex)].Mode;
            bool restart = newGame != s.EftInstallDir || newShots != s.ScreenshotsDir || watchLogs.IsChecked != s.WatchGameLogs || watchShots.IsChecked != s.WatchScreenshots;
            bool any = restart || s.UpdateQuestsFromLogs != (questLogs.IsChecked == true) || s.CleanupMode != cleanupMode ||
                       s.AutoSwitchMap != (autoSwitch.IsChecked == true) || s.ReadExitListWithOcr != (ocr.IsChecked == true);
            if (!any) return;
            s.EftInstallDir = newGame; s.ScreenshotsDir = newShots;
            s.WatchGameLogs = watchLogs.IsChecked == true; s.UpdateQuestsFromLogs = questLogs.IsChecked == true;
            s.WatchScreenshots = watchShots.IsChecked == true; s.CleanupMode = cleanupMode;
            s.AutoSwitchMap = autoSwitch.IsChecked == true; s.ReadExitListWithOcr = ocr.IsChecked == true;
            if (restart) f.ApplyWatchers();
            f.SettingsChanged();
            f.SaveNow();
            saved.Text = $"Saved {DateTime.Now:HH:mm:ss}" + (restart ? " · log/screenshot watchers restarted" : "");
            Services.Log.Write("settings: map settings applied" + (restart ? " (watchers restarted)" : ""));
        }
        ApplyNow = Apply;
        foreach (var c in new[] { watchLogs, questLogs, watchShots, autoSwitch, ocr }) { c.Checked += (_, _) => Apply(); c.Unchecked += (_, _) => Apply(); }
        cleanup.SelectionChanged += (_, _) => Apply();
        gameBox.LostFocus += (_, _) => Apply();
        shotBox.LostFocus += (_, _) => Apply();
        gameBox.TextChanged += (_, _) => { if (!gameBox.IsKeyboardFocused) Apply(); };   // Browse… result
        shotBox.TextChanged += (_, _) => { if (!shotBox.IsKeyboardFocused) Apply(); };
        loading = false;
    }

    /// <summary>Settings… puts the Satellite/Vector shortcut row here (it owns the key recorder).</summary>
    public StackPanel StyleBox { get; } = new() { Margin = new Thickness(0, 2, 0, 0) };

    /// <summary>Applies whatever is typed now (Settings… calls this when it closes, for a folder box still focused).</summary>
    public Action ApplyNow { get; private set; } = () => { };

    private static CheckBox Check(string label, bool on) => new()
    {
        Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 580 }, IsChecked = on, Foreground = Theme.Text, Margin = new Thickness(0, 3, 0, 3),
    };
}
