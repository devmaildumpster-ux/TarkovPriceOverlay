using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TarkovOverlay.App.Native;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Services;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Economics;
using TarkovOverlay.Core.Input;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Presentation;

namespace TarkovOverlay.App.Views;

public enum SettingsTab { Overlay, Map, Troubleshooting }

/// <summary>What the Settings… window needs from the main window (kept out of the main window's layout since 1.1.4).</summary>
public sealed record SettingsHost(
    AppServices Services, HotkeyManager Hotkey, AutoReader Auto, CursorCardWindow Card,
    Func<CatalogItem?> SelectedItem, Func<GameMode, Task> SwitchMode, Action OpenSavedImage, Action Changed,
    ShellFeatures? Shell = null, Action? CardSettingsChanged = null);

/// <summary>
/// 1.1.4 Settings…: the rarely changed options that used to sit above the map. Tabs: "Overlay &amp; Card" (shortcut,
/// game mode, card size, dismiss rules, auto-read timing, value legend), "Map &amp; Screenshots" (the former Map
/// settings dialog) and "Troubleshooting" (OCR/price status, test card, log folder, saved image). Modeless, one
/// instance; every control applies and saves immediately.
/// </summary>
public sealed class SettingsWindow : Window
{
    internal static SettingsHost? Host;
    private static SettingsWindow? _current;

    private readonly SettingsHost _h;
    private readonly ContentControl _body = new();
    private readonly Dictionary<SettingsTab, (Border Tab, UIElement Page)> _tabs = new();
    private readonly TextBlock _hotkeyText = Ui.Text("", 12, Theme.Text, FontWeights.SemiBold);
    private readonly TextBlock _dataState = Ui.Text("", 11.5, Theme.Muted, wrap: true);
    private readonly TextBlock _ocrState = Ui.Text("", 11, Theme.Muted, wrap: true);
    private readonly TextBlock _testStatus = Ui.Text("", 11, Theme.Muted, wrap: true);
    private readonly ComboBox _mode = new() { Width = 170, Margin = new Thickness(0, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left };
    private Map.MapSettingsPanel? _mapPanel;

    /// <summary>Opens (or brings forward) Settings… on the given tab. Map › "Map settings…" lands on the Map tab.</summary>
    public static SettingsWindow? Open(SettingsTab tab, Window? owner)
    {
        if (Host == null) return null;
        if (_current is { IsLoaded: true } w) { w.Select(tab); w.Activate(); return w; }
        w = new SettingsWindow(Host, tab);
        if (owner != null && owner.IsLoaded) w.Owner = owner;
        _current = w;
        w.Closed += (_, _) => { if (_current == w) _current = null; };
        w.Show();
        return w;
    }

    public SettingsWindow(SettingsHost host, SettingsTab tab = SettingsTab.Overlay)
    {
        _h = host;
        Title = "Settings — Tarkov Price Overlay";
        Background = Theme.Window; Foreground = Theme.Text; FontFamily = Theme.Font;
        Width = 720; Height = 640; MinWidth = 560; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 10, 10, 0) };
        AddTab(strip, SettingsTab.Overlay, "Overlay & Card", OverlayPage());
        if (Map.MapFeature.Instance is { } f)
        {
            _mapPanel = new Map.MapSettingsPanel(f);
            var st = _h.Services.Settings;
            _mapPanel.StyleBox.Children.Add(HotkeyRow("Shortcut: switch the map Satellite ↔ Vector (works in game, never takes focus)", ShellFeatures.MapStyle,
                () => st.EffectiveMapStyleHotkey, v => st.MapStyleHotkey = v, AppSettingsDefaults.MapStyle, () => st.MapStyleHotkeyEnabled, v => st.MapStyleHotkeyEnabled = v));
            _mapPanel.StyleBox.Children.Add(Note("Also the Satellite/Vector button on the map toolbar (docked or popped out). Clear the key to turn the shortcut off."));
            AddTab(strip, SettingsTab.Map, "Map & Screenshots", _mapPanel);
        }
        AddTab(strip, SettingsTab.Troubleshooting, "Troubleshooting", TroubleshootingPage());

        var close = Ui.Button("Close", (_, _) => Close());
        close.IsCancel = true;
        var footer = new DockPanel { Margin = new Thickness(10, 6, 10, 10), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        var help = Ui.Button("Controls & hotkeys…", (_, _) => ControlsWindow.Open(_h.Services, this), "Every hotkey, mouse control and button, with your current keys"); // 1.2.3
        DockPanel.SetDock(help, Dock.Right);
        footer.Children.Add(help);
        footer.Children.Add(Ui.Text("Changes apply and save right away.", 11, Theme.Muted));

        var root = new DockPanel();
        DockPanel.SetDock(strip, Dock.Top); root.Children.Add(strip);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new Border
        {
            BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1), Background = Theme.Panel, Margin = new Thickness(10, 0, 10, 0),
            Child = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14, 10, 14, 10) },
        });
        Content = root;

        _h.Services.Catalog.StateChanged += OnState;
        Closing += (_, _) => _mapPanel?.ApplyNow();
        Closed += (_, _) => _h.Services.Catalog.StateChanged -= OnState;
        UpdateHotkeyText(); UpdateDataState(); UpdateOcrState();
        Select(tab);
    }

    public SettingsTab CurrentTab { get; private set; }

    public void Select(SettingsTab tab)
    {
        if (!_tabs.ContainsKey(tab)) tab = SettingsTab.Overlay;
        CurrentTab = tab;
        foreach (var (k, v) in _tabs)
        {
            bool on = k == tab;
            v.Tab.Background = on ? Theme.Panel : Theme.Window;
            v.Tab.BorderBrush = on ? Theme.CardEdge : Theme.Window;
            ((TextBlock)v.Tab.Child).Foreground = on ? Theme.Text : Theme.Muted;
        }
        _body.Content = _tabs[tab].Page;
    }

    private void AddTab(StackPanel strip, SettingsTab key, string label, UIElement page)
    {
        var t = new Border
        {
            Child = Ui.Text(label, 12.5, Theme.Muted, FontWeights.SemiBold), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 2, -1),
            BorderThickness = new Thickness(1, 1, 1, 0), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(3, 3, 0, 0),
        };
        t.MouseLeftButtonUp += (_, _) => Select(key);
        strip.Children.Add(t);
        _tabs[key] = (t, page);
    }

    private void OnState(DataState _) => Dispatcher.BeginInvoke(UpdateDataState);

    private void Saved(string what)
    {
        _h.Services.SaveSoon();
        Log.Write("settings: " + what);
        _h.Changed();
    }

    // ---------------- Overlay & Card ----------------
    private UIElement OverlayPage()
    {
        var s = _h.Services.Settings;
        var p = new StackPanel();

        p.Children.Add(Heading("Shortcut", first: true));
        var hotkeyOn = Check("Enabled", s.HotkeyEnabled, "Off: the global shortcut is released (other apps can use it). Auto-read on hover still works.");
        hotkeyOn.Margin = new Thickness(12, 0, 0, 0);
        hotkeyOn.Checked += (_, _) => { s.HotkeyEnabled = true; _h.Hotkey.Register(); UpdateHotkeyText(); Saved($"shortcut on ({_h.Hotkey.Active?.Label ?? "none available"})"); };
        hotkeyOn.Unchecked += (_, _) => { s.HotkeyEnabled = false; _h.Hotkey.Unregister(); UpdateHotkeyText(); Saved("shortcut off"); };
        p.Children.Add(Ui.Row(Ui.Text("Key: ", 12, Theme.Muted), _hotkeyText, hotkeyOn));
        p.Children.Add(Note("Over an item tooltip: shows the card (manual lookup). With a card showing: expands / collapses it."));

        p.Children.Add(Heading("Game mode"));
        foreach (var m in GameMode.All) _mode.Items.Add(m);
        _mode.SelectedItem = GameMode.FromId(s.ModeId);
        _mode.SelectionChanged += async (_, _) =>
        {
            if (_mode.SelectedItem is not GameMode gm || gm.Id == s.ModeId) return;
            Saved("mode -> " + gm.Id);
            await _h.SwitchMode(gm);
            _h.Changed();
        };
        p.Children.Add(_mode);
        p.Children.Add(Note("Prices, cards, tracked requirements and the map's data use this mode. During a raid the map follows the raid's own mode from the game log."));
        var autoMode = Check("Auto-detect PvP / PvE from Tarkov's logs", s.AutoDetectMode,
            "When Tarkov's log says \"Session mode: pve\" or \"regular\" (written when the game connects in that mode), the app switches to it. You can still pick a mode here any time; the next mode change in game switches again.");
        autoMode.Checked += (_, _) => { s.AutoDetectMode = true; Saved("auto-detect mode = on"); };
        autoMode.Unchecked += (_, _) => { s.AutoDetectMode = false; Saved("auto-detect mode = off"); };
        p.Children.Add(autoMode);

        p.Children.Add(Heading("Price card"));
        var size = new StackPanel { Orientation = Orientation.Horizontal };
        size.Children.Add(Ui.Text("Size  ", 12, Theme.Muted));
        foreach (var n in new[] { 1, 2, 3 })
        {
            var rb = new RadioButton { Content = $"{n}×", GroupName = "settings-scale", Margin = new Thickness(4, 0, 8, 0), Foreground = Theme.Text, IsChecked = s.EffectiveTooltipScale == n };
            rb.Checked += (_, _) => { s.TooltipScale = n; Saved($"card size {n}x"); };
            size.Children.Add(rb);
        }
        p.Children.Add(size);
        var fullList = Check("Always show the full list (every quest, craft and barter on the first press)", s.AlwaysShowFullList,
            "Off: the card shows 2 rows per section; press the shortcut again on the same item to expand/collapse.");
        fullList.Checked += (_, _) => { s.AlwaysShowFullList = true; Saved("always show full list = on"); };
        fullList.Unchecked += (_, _) => { s.AlwaysShowFullList = false; Saved("always show full list = off"); };
        p.Children.Add(fullList);
        var dismiss = Check("Hide the card when the mouse moves", s.DismissOnMove, "Off: the card stays until it times out or you press the shortcut.");
        dismiss.Checked += (_, _) => { s.DismissOnMove = true; Saved("dismiss on move = on"); };
        dismiss.Unchecked += (_, _) => { s.DismissOnMove = false; Saved("dismiss on move = off"); };
        p.Children.Add(dismiss);
        p.Children.Add(Ui.Row(Ui.Text("Mouse may drift ", 12, Theme.Muted),
            Choice(new[] { 2, 4, 6, 10, 15, 25, 40 }, s.EffectiveMoveTolerancePx, v => { s.MoveTolerancePx = v; Saved($"move tolerance {v}px"); }),
            Ui.Text(" px before the card hides", 12, Theme.Muted)));
        p.Children.Add(Note("Also how far the mouse may drift while an item is still being read."));

        p.Children.Add(Ui.Row(Ui.Text("Card opacity ", 12, Theme.Muted), OpacitySlider(s)));

        p.Children.Add(Heading("Card fields"));
        var fields = new WrapPanel();
        foreach (var (label, get, set) in new (string, Func<bool>, Action<bool>)[]
        {
            ("Flea price", () => s.CardShowFlea, v => s.CardShowFlea = v),
            ("Flea net after fee", () => s.CardShowFleaNet, v => s.CardShowFleaNet = v),
            ("Best trader", () => s.CardShowTrader, v => s.CardShowTrader = v),
            ("Buy-back price", () => s.CardShowBuyBack, v => s.CardShowBuyBack = v),
            ("Price per slot", () => s.CardShowPerSlot, v => s.CardShowPerSlot = v),
            ("Stack / full-stack value", () => s.CardShowStack, v => s.CardShowStack = v),
            ("Quest & hideout needs", () => s.CardShowNeeds, v => s.CardShowNeeds = v),
            ("Crafts & barters", () => s.CardShowRecipes, v => s.CardShowRecipes = v),
            ("Kappa badge", () => s.ShowKappaBadge, v => s.ShowKappaBadge = v),
        })
        {
            var c = Check(label, get());
            c.Width = 210;
            c.Checked += (_, _) => { set(true); CardSaved($"card field '{label}' on"); };
            c.Unchecked += (_, _) => { set(false); CardSaved($"card field '{label}' off"); };
            fields.Children.Add(c);
        }
        p.Children.Add(fields);

        p.Children.Add(Heading("Quest & hideout needs"));
        var filter = Check("Filter needs by my quest & hideout progress (hide quests marked done and hideout levels built)", s.FilterNeedsByProgress,
            "Uses Map › Quests… (the in-app quest tracker) and Map › Hideout…. Nothing is hidden until you mark quests done or set station levels.");
        filter.Checked += (_, _) => { s.FilterNeedsByProgress = true; CardSaved("filter needs by progress = on"); };
        filter.Unchecked += (_, _) => { s.FilterNeedsByProgress = false; CardSaved("filter needs by progress = off"); };
        p.Children.Add(filter);
        var nonFir = Check("Show non-FIR needs (off: only needs that require Found in Raid)", s.ShowNonFirNeeds);
        nonFir.Checked += (_, _) => { s.ShowNonFirNeeds = true; CardSaved("show non-FIR needs = on"); };
        nonFir.Unchecked += (_, _) => { s.ShowNonFirNeeds = false; CardSaved("show non-FIR needs = off"); };
        p.Children.Add(nonFir);
        p.Children.Add(Ui.Row(Ui.Button("Hideout levels…", (_, _) => HideoutWindow.Open(_h.Services, this), "Set the station levels you've built"),
            Ui.Button("Quests…", (_, _) => Map.MapFeature.Instance?.OpenQuests(), "Mark quests done / active (the map's quest tracker)")));
        p.Children.Add(Note("FIR = must be Found in Raid. KAPPA = needed for a quest tarkov.dev marks as required for the Kappa container (Collector and its chain)."));

        p.Children.Add(Heading("Shortcuts for the last item / search"));
        p.Children.Add(HotkeyRow("Open Tarkov Wiki for the last item", ShellFeatures.Wiki, () => s.EffectiveWikiHotkey, v => s.WikiHotkey = v, AppSettingsDefaults.Wiki,
            () => s.WikiHotkeyEnabled, v => s.WikiHotkeyEnabled = v));
        p.Children.Add(HotkeyRow("Open tarkov.dev for the last item", ShellFeatures.Dev, () => s.EffectiveDevHotkey, v => s.DevHotkey = v, AppSettingsDefaults.Dev,
            () => s.DevHotkeyEnabled, v => s.DevHotkeyEnabled = v));
        p.Children.Add(HotkeyRow("In-game search bar", ShellFeatures.Search, () => s.EffectiveSearchHotkey, v => s.SearchHotkey = v, AppSettingsDefaults.Search,
            () => s.SearchHotkeyEnabled, v => s.SearchHotkeyEnabled = v));
        p.Children.Add(Note("Ctrl, Alt or Win is required (or an F-key) so a shortcut never swallows normal typing. Ctrl+Shift+W/D aren't the defaults because browsers use them (close window / bookmark all). \"Last item\" = the last card, main-window selection or search result."));

        p.Children.Add(Heading("Window"));
        var tray = Check("Minimize to the system tray (icon next to the clock: click to open, right-click for Settings… / Quit)", s.MinimizeToTray);
        tray.Checked += (_, _) => { s.MinimizeToTray = true; _h.Shell?.ApplyTray(); Saved("minimize to tray = on"); };
        tray.Unchecked += (_, _) => { s.MinimizeToTray = false; _h.Shell?.ApplyTray(); Saved("minimize to tray = off"); };
        p.Children.Add(tray);

        p.Children.Add(Heading("Auto-read on hover"));
        var auto = Check("Show the card automatically when an item's name tooltip appears (no keypress)", s.AutoReadEnabled,
            "When the mouse rests on an item in Tarkov and its name tooltip appears, the card shows automatically.");
        auto.Checked += (_, _) => { s.AutoReadEnabled = true; _h.Auto.Apply(); Saved("auto-read = on"); };
        auto.Unchecked += (_, _) => { s.AutoReadEnabled = false; _h.Auto.Apply(); Saved("auto-read = off"); };
        p.Children.Add(auto);
        p.Children.Add(Ui.Row(Ui.Text("Mouse rest ", 12, Theme.Muted),
            Choice(new[] { 200, 250, 350, 500, 750, 1000 }, s.EffectiveAutoDwellMs, v => { s.AutoDwellMs = v; _h.Auto.Apply(); Saved($"auto-read rest {v}ms"); }),
            Ui.Text(" ms · wait for the tooltip up to ", 12, Theme.Muted),
            Choice(new[] { 300, 450, 600, 900, 1200, 1500 }, s.EffectiveAutoMaxTooltipWaitMs, v => { s.AutoMaxTooltipWaitMs = v; _h.Auto.Apply(); Saved($"auto-read tooltip wait {v}ms"); }),
            Ui.Text(" ms", 12, Theme.Muted)));

        p.Children.Add(Heading("Value colours (per inventory slot)"));
        foreach (var (tier, trader, flea) in new[]
        {
            (ValueTier.Low, "<5k", "<15k"), (ValueTier.Decent, "5k–10k", "15k–30k"), (ValueTier.Good, "10k–15k", "30k–75k"), (ValueTier.Premium, "15k+", "75k+"),
        })
            p.Children.Add(Ui.Text($"{ValueTiers.Symbol(tier)} {ValueTiers.Word(tier)} — trader {trader} · flea net {flea}", 11.5, Theme.ForTier(tier)));

        p.Children.Add(Heading("Game display mode"));
        p.Children.Add(Note("Cards can't draw over exclusive Fullscreen: set Tarkov to Borderless (or Windowed) if a lookup works in the app but no card shows in game."));
        return p;
    }

    private void CardSaved(string what)
    {
        Saved(what);
        _h.CardSettingsChanged?.Invoke();
    }

    private UIElement OpacitySlider(Core.Settings.AppSettings s)
    {
        var label = Ui.Text($"{s.EffectiveCardOpacityPercent}%", 12, Theme.Text);
        var slider = new Slider { Minimum = Core.Settings.AppSettings.MinCardOpacityPercent, Maximum = 100, Width = 220, Value = s.EffectiveCardOpacityPercent,
            TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        slider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            s.CardOpacityPercent = v;
            label.Text = $"{v}%";
            _h.Card.CardOpacity = v / 100.0;   // live, also on a card that's showing
            _h.Services.SaveSoon();
        };
        slider.LostMouseCapture += (_, _) => Log.Write($"settings: card opacity {s.EffectiveCardOpacityPercent}%");
        return Ui.Row(slider, label);
    }

    private KeyRecording? _recording;
    private sealed record KeyRecording(Button Button, Action<KeyChord> Done);

    private UIElement HotkeyRow(string label, string name, Func<string> get, Action<string?> set, string def, Func<bool> enabled, Action<bool> setEnabled)
    {
        var box = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
        var on = Check(label, enabled());
        var keyText = Ui.Text(get().Length == 0 ? "(none)" : get(), 12, Theme.Text, FontWeights.SemiBold);
        keyText.Width = 110; keyText.VerticalAlignment = VerticalAlignment.Center;
        var status = Ui.Text(_h.Shell?.HotkeyStatus(name) ?? "", 10.5, Theme.Muted);
        void Apply(string why)
        {
            Saved(why);
            _h.Shell?.ApplyHotkeys();
            var st = _h.Shell?.HotkeyStatus(name) ?? "";
            status.Text = st;
            status.Foreground = st.Contains("registered", StringComparison.Ordinal) || st == "off" ? Theme.Muted : Theme.Warn;
            keyText.Text = get().Length == 0 ? "(none)" : get();
        }
        on.Checked += (_, _) => { setEnabled(true); Apply($"{name} shortcut on"); };
        on.Unchecked += (_, _) => { setEnabled(false); Apply($"{name} shortcut off"); };
        Button? rec = null;
        rec = Ui.Button("Record…", (_, _) =>
        {
            _h.Shell?.SuspendHotkeys();
            rec!.Content = "Press keys… (Esc cancels)";
            _recording = new KeyRecording(rec, c => { set(c.ToString()); Apply($"{name} shortcut = {c}"); });
            Focus();
        }, "Click, then press the new combination (e.g. Ctrl+Alt+W)");
        var clear = Ui.Button("Clear", (_, _) => { set(""); Apply($"{name} shortcut cleared"); });
        var reset = Ui.Button("Default", (_, _) => { set(null); Apply($"{name} shortcut = default {def}"); }, "Default: " + def);
        box.Children.Add(on);
        var row = Ui.Row(Ui.Text("     Key ", 12, Theme.Muted), keyText, rec, clear, reset);
        box.Children.Add(row);
        status.Margin = new Thickness(38, 0, 0, 0);
        box.Children.Add(status);
        return box;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_recording is { } r)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            _recording = null;
            r.Button.Content = "Record…";
            if (key == Key.Escape) { _h.Shell?.ApplyHotkeys(); return; }
            var m = Keyboard.Modifiers;
            var mods = ChordModifiers.None;
            if (m.HasFlag(ModifierKeys.Control)) mods |= ChordModifiers.Ctrl;
            if (m.HasFlag(ModifierKeys.Alt)) mods |= ChordModifiers.Alt;
            if (m.HasFlag(ModifierKeys.Shift)) mods |= ChordModifiers.Shift;
            if (m.HasFlag(ModifierKeys.Windows)) mods |= ChordModifiers.Win;
            var chord = new KeyChord(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
            if (!chord.IsSafe || !KeyChord.TryParse(chord.ToString(), out var parsed))
            {
                MessageBox.Show(this, $"{chord} can't be used: add Ctrl, Alt or Win (or use an F-key) so normal typing isn't blocked.", Title);
                _h.Shell?.ApplyHotkeys();
                return;
            }
            r.Done(parsed);
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // ---------------- Troubleshooting ----------------
    private UIElement TroubleshootingPage()
    {
        var p = new StackPanel();
        p.Children.Add(Heading("Prices", first: true));
        p.Children.Add(_dataState);
        p.Children.Add(Ui.Row(Ui.Button("Refresh prices now", async (_, _) => await _h.Services.Catalog.RefreshNowAsync())));
        p.Children.Add(Heading("Tarkov's monitor (Graphics.ini)"));
        p.Children.Add(Ui.Text(TarkovDisplayText(), 11, Theme.Muted, wrap: true));
        p.Children.Add(Heading("Text reading (OCR)"));
        p.Children.Add(_ocrState);
        p.Children.Add(Heading("Card and lookups"));
        p.Children.Add(Ui.Row(
            Ui.Button("Test card (in 3 s)", (_, _) => TestCard(), "Shows the selected item's card beside your cursor 3 s after clicking, for 5 s, without hiding on mouse move. Switch to Tarkov during the countdown."),
            Ui.Button("Open saved image…", (_, _) => _h.OpenSavedImage(), "Read an item name from a screenshot you choose"),
            Ui.Button("Open log folder", (_, _) => OpenLogFolder())));
        p.Children.Add(_testStatus);
        p.Children.Add(Note("If an item shows no card, the log (overlay.log) has an \"auto lookup at …\" line saying what was found under the mouse."));
        p.Children.Add(Heading("Version"));
        p.Children.Add(Ui.Text($"Tarkov Price Overlay {typeof(SettingsWindow).Assembly.GetName().Version}", 11.5, Theme.Muted));
        return p;
    }

    private static string TarkovDisplayText()
    {
        try
        {
            var (d, note) = Desktop.TarkovDisplay();
            var mons = Desktop.Monitors();
            var lines = new List<string>();
            lines.Add(d != null ? d.Describe() : "No monitor information.");
            lines.Add(note);
            if (d?.Ini == null) lines.Add("Without Graphics.ini the search bar opens on the primary monitor; cards always follow the mouse.");
            lines.Add($"Monitors: " + string.Join(" · ", mons.Select(m => $"{m.Index + 1}: {m.Bounds.Width}×{m.Bounds.Height} @ {m.DpiScale * 100:0}%{(m.Primary ? " (primary)" : "")}")));
            lines.Add("Used for: where the in-game search bar opens. Card capture and placement use the monitor under the mouse and its DPI.");
            return string.Join("\n", lines);
        }
        catch (Exception ex) { return "Couldn't read the display setup: " + ex.Message; }
    }

    private async void TestCard()
    {
        var s = _h.Services;
        var ctx = s.Catalog.Context;
        if (ctx == null) { _testStatus.Text = "Catalog not loaded yet."; return; }
        var item = _h.SelectedItem() ?? ctx.Items.Search("Grizzly", 1).FirstOrDefault() ?? ctx.Snapshot.Items.Values.First();
        for (int i = 3; i > 0; i--)
        {
            _testStatus.Text = $"Test card for “{item.Name}” in {i} s — move the mouse over Tarkov (or anywhere) and hold still…";
            await Task.Delay(1000);
        }
        var origin = Desktop.Cursor();
        var model = s.Cards(ctx).ForMatch(new MatchResult(MatchKind.Verified, new[] { item }, item.Name));
        _h.Card.ShowCard(model, origin, s.Settings.EffectiveTooltipScale, dismissOnMove: false, timeout: TimeSpan.FromSeconds(5), reason: "test");
        _testStatus.Text = $"Test card shown at {origin.X},{origin.Y} for 5 s. If you could not see it over the game, switch Tarkov to Borderless. Log: {Log.FilePath}";
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + Log.Directory + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { _testStatus.Text = "Log folder: " + Log.Directory; }
    }

    // ---------------- status text ----------------
    private void UpdateHotkeyText()
    {
        var (text, warn) = MainWindow.HotkeyStatus(_h.Hotkey, _h.Services.Settings.HotkeyEnabled);
        _hotkeyText.Text = text;
        _hotkeyText.Foreground = warn ? Theme.Warn : Theme.Text;
        _hotkeyText.ToolTip = string.Join("\n", _h.Hotkey.Attempts);
    }

    private void UpdateDataState()
    {
        var st = _h.Services.Catalog.State;
        _dataState.Text = $"{GameMode.FromId(st.ModeId).DisplayName}: {st.Describe(DateTimeOffset.UtcNow)}";
        _dataState.Foreground = st.Kind is DataStateKind.Fresh ? Theme.Green : st.Kind is DataStateKind.None ? Theme.Warn : Theme.Muted;
    }

    private void UpdateOcrState()
    {
        var s = _h.Services;
        var w = s.WindowsOcr.IsAvailable ? $"Windows OCR: ready ({s.WindowsOcr.LanguageTag})" : "Windows OCR: unavailable — " + s.WindowsOcr.UnavailableReason;
        var t = s.Tesseract.IsAvailable ? "Tesseract: ready" : "Tesseract: unavailable — " + s.Tesseract.UnavailableReason;
        _ocrState.Text = w + "\n" + t + (!s.WindowsOcr.IsAvailable && !s.Tesseract.IsAvailable ? "\nHover lookup cannot read text; typed search still works." : "");
        _ocrState.Foreground = s.WindowsOcr.IsAvailable && s.Tesseract.IsAvailable ? Theme.Muted : Theme.Warn;
    }

    // ---------------- helpers ----------------
    private static TextBlock Heading(string text, bool first = false)
    {
        var t = Ui.Text(text, 13, Theme.Text, FontWeights.SemiBold);
        t.Margin = new Thickness(0, first ? 0 : 14, 0, 4);
        return t;
    }

    private static TextBlock Note(string text)
    {
        var t = Ui.Text(text, 11, Theme.Muted, wrap: true);
        t.Margin = new Thickness(0, 2, 0, 2);
        return t;
    }

    private static CheckBox Check(string label, bool on, string? tip = null) => new()
    {
        Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 600 }, IsChecked = on, Foreground = Theme.Text,
        Margin = new Thickness(0, 3, 0, 3), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center,
    };

    private static ComboBox Choice(int[] values, int current, Action<int> set)
    {
        var c = new ComboBox { Width = 70, Margin = new Thickness(2) };
        foreach (var v in values) c.Items.Add(v);
        if (!values.Contains(current)) c.Items.Add(current);
        c.SelectedItem = current;
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is int v) set(v); };
        return c;
    }
}

internal static class AppSettingsDefaults
{
    public const string Wiki = Core.Settings.AppSettings.DefaultWikiHotkey;
    public const string Dev = Core.Settings.AppSettings.DefaultDevHotkey;
    public const string Search = Core.Settings.AppSettings.DefaultSearchHotkey;
    public const string MapStyle = Core.Settings.AppSettings.DefaultMapStyleHotkey;
}
