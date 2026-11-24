using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Services;
using TarkovOverlay.App.Views;
using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Definitions;
using TarkovOverlay.Maps.Extracts;
using TarkovOverlay.Maps.Logs;
using TarkovOverlay.Maps.Quests;
using TarkovOverlay.Maps.Position;
using TarkovOverlay.Maps.Geometry;

namespace TarkovOverlay.App.Map;

public sealed record MapChoice(string NormalizedName, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// The 2D map: toolbar, legend and the pan/zoom view. Lives in the main window's left column; can be expanded over
/// the whole window or popped out into its own normal window (good maximized on a second monitor).
/// </summary>
public sealed class MapPanel : DockPanel
{
    private readonly MapFeature _f;
    private readonly MapImageRenderer _renderer;
    private readonly MapView _view = new();
    private readonly ComboBox _map = new() { MinWidth = 130, Margin = new Thickness(2) };
    private readonly ComboBox _floor = new() { MinWidth = 90, Margin = new Thickness(2), ToolTip = "Floor / level" };
    private readonly TextBlock _status = Ui.Text("", 11, Theme.Muted, wrap: true);
    private readonly TextBlock _posStatus = Ui.Text("", 11, Theme.Gold, wrap: true);
    private string? _loggedPosKey;
    /// <summary>What happened to the last position fix (drawn where / why not), for the status line and the test report.</summary>
    public string? LastPositionNote { get; private set; }
    private readonly TextBlock _overlay = Ui.Text("", 13, Theme.Muted, wrap: true);
    private readonly WrapPanel _questList = new();
    private readonly Expander _questsBox;
    private readonly Button _popBtn, _fullBtn, _styleBtn;
    private readonly Border _hintBox;
    private readonly TextBlock _hint = new() { Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(2.2) };
    /// <summary>Style of the picture on screen (null before the first map loads).</summary>
    public MapStyle? ShownStyle { get; private set; }
    private readonly CheckBox _questsCb;
    private readonly WrapPanel _questTools = new() { Margin = new Thickness(0, 0, 0, 2) };
    /// <summary>tarkov.dev map ids of the map shown (all variants, e.g. day/night Factory); null without a map.</summary>
    public IReadOnlyCollection<string>? CurrentMapIds { get; private set; }
    private readonly DispatcherTimer _tick;
    private CancellationTokenSource? _loadCts;
    private string? _mapName;
    private MapDefinition? _def;
    private bool _suppress;

    public bool HasMap { get; private set; }
    public IReadOnlyList<ExtractMarker> CurrentExtracts { get; private set; } = Array.Empty<ExtractMarker>();
    public string? CurrentMap => _mapName;

    /// <summary>Raised by the Fullscreen button (only shown in the pop-out window).</summary>
    public event Action? FullscreenToggleRequested;

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x7A, 0x7A), Color.FromRgb(0xFF, 0xB8, 0x4D), Color.FromRgb(0xE8, 0xE0, 0x5A), Color.FromRgb(0x9B, 0xE8, 0x6A),
        Color.FromRgb(0x5A, 0xE0, 0xC8), Color.FromRgb(0x6A, 0xB8, 0xFF), Color.FromRgb(0xA9, 0x8B, 0xFF), Color.FromRgb(0xF0, 0x7A, 0xD8),
        Color.FromRgb(0xFF, 0x9E, 0x9E), Color.FromRgb(0xC8, 0xFF, 0x9E), Color.FromRgb(0x9E, 0xE6, 0xFF), Color.FromRgb(0xFF, 0xD6, 0x9E),
    };

    public MapPanel(MapFeature f)
    {
        _f = f;
        _renderer = new MapImageRenderer(f.Paths, f.Http);
        LastChildFill = true;

        // ---- toolbar ----
        foreach (var c in Choices()) _map.Items.Add(c);
        _map.SelectionChanged += (_, _) => { if (!_suppress && _map.SelectedItem is MapChoice c) _ = LoadMapAsync(c.NormalizedName); };
        _floor.SelectionChanged += (_, _) => { if (!_suppress && _floor.SelectedItem is MapLayerChoice) _ = LoadImageAsync(keepView: true); };

        _popBtn = Ui.Button("Pop out", (_, _) => { if (f.MapPoppedOut) f.DockMap(); else f.PopOutMap(); }, "Open the map in its own window (F11 there for fullscreen on that monitor)");
        _fullBtn = Ui.Button("Fullscreen", (_, _) => FullscreenToggleRequested?.Invoke(), "Borderless fullscreen on this monitor (F11; Esc to leave)");
        _fullBtn.Visibility = Visibility.Collapsed;
        _styleBtn = Ui.Button("Satellite", (_, _) => ToggleStyle("button"), "Switch the map between Satellite and Vector");
        _styleBtn.MinWidth = 78;
        _styleBtn.ToolTipOpening += (_, _) => UpdateStyleButton(); // picks up a shortcut changed in Settings
        var follow = Toggle("Auto-follow", () => f.Settings.AutoSwitchMap, v => f.Settings.AutoSwitchMap = v, "Switch to the raid's map automatically when Tarkov loads one");
        var row1 = new WrapPanel();
        row1.Children.Add(_map);
        row1.Children.Add(_floor);
        row1.Children.Add(_styleBtn);
        row1.Children.Add(follow);
        row1.Children.Add(Toggle("Exits", () => f.Settings.ShowExtracts, v => f.Settings.ShowExtracts = v));
        _questsCb = Toggle("Quests", () => f.Settings.ShowQuests, v => f.Settings.ShowQuests = v, "Show the quests you picked (Quests on this map, below, or Quests…)");
        row1.Children.Add(_questsCb);
        row1.Children.Add(Toggle("Me", () => f.Settings.ShowPlayer, v => f.Settings.ShowPlayer = v, "Your position from the last in-raid screenshot (PrtScn)"));
        row1.Children.Add(_popBtn);
        row1.Children.Add(_fullBtn);

        // everything else lives in a small "More" menu to keep the toolbar to one line
        var more = Ui.Button("More ▾", (_, _) => { }, "Exit side, transits, quest options, quest tracker, settings");
        var menu = new ContextMenu();
        MenuItem Check(string header, Func<bool> get, Action<bool> set)
        {
            var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = get() };
            mi.Click += (_, _) => { set(mi.IsChecked); f.SaveSoon(); RebuildMarkers(); };
            return mi;
        }
        var sideItems = new[] { ("Exits for: auto (from the raid)", "auto"), ("Exits for: PMC", "pmc"), ("Exits for: Scav", "scav") }
            .Select(t => new MenuItem { Header = t.Item1, IsCheckable = true, IsChecked = f.Settings.Side == t.Item2, Tag = t.Item2 }).ToList();
        foreach (var si in sideItems)
        {
            si.Click += (_, _) =>
            {
                f.Settings.Side = (string)si.Tag; f.SaveSoon();
                foreach (var o in sideItems) o.IsChecked = o == si;
                RebuildMarkers();
            };
            menu.Items.Add(si);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Check("Show transits", () => f.Settings.ShowTransits, v => f.Settings.ShowTransits = v));
        menu.Items.Add(Check("Browse: also show every available quest (not just the ones I picked)", () => f.Settings.ShowAllAvailableQuests, v => f.Settings.ShowAllAvailableQuests = v));
        menu.Items.Add(Check("Area names", () => f.Settings.ShowLabels, v => { f.Settings.ShowLabels = v; _view.ShowLabels = v; }));
        menu.Items.Add(new Separator());
        var fit = new MenuItem { Header = "Fit whole map (or double-click the map)" };
        fit.Click += (_, _) => _view.Fit();
        menu.Items.Add(fit);
        var quests = new MenuItem { Header = "Quest tracker…" };
        quests.Click += (_, _) => f.OpenQuests();
        menu.Items.Add(quests);
        var hideout = new MenuItem { Header = "Hideout tracker…" };
        var ammoChart = new MenuItem { Header = "Ammo chart…" };
        ammoChart.Click += (_, _) =>
        {
            var snap = f.App.Catalog.Context?.Snapshot;
            var last = f.App.LastItem;
            var round = last != null && snap != null ? TarkovOverlay.Core.Ammo.AmmoChart.RoundOf(last, snap) : null;
            Views.AmmoChartWindow.Open(f.App, Window.GetWindow(this), round?.Ammo?.Caliber, round?.Id, fromHotkey: false);
        };
        hideout.Click += (_, _) => Views.HideoutWindow.Open(f.App, Window.GetWindow(this));
        menu.Items.Add(hideout);
        menu.Items.Add(ammoChart);
        var testPos = new MenuItem { Header = "Test position from latest screenshot" };
        testPos.Click += (_, _) => { _posStatus.Text = f.TestLatestScreenshot(); _posStatus.Visibility = Visibility.Visible; };
        menu.Items.Add(testPos);
        var settingsItem = new MenuItem { Header = "Map settings…" };
        settingsItem.Click += (_, _) => Views.SettingsWindow.Open(Views.SettingsTab.Map, Window.GetWindow(this)); // 1.1.4: Settings… › Map & Screenshots
        menu.Items.Add(settingsItem);
        var controlsItem = new MenuItem { Header = "Controls & hotkeys…" };
        controlsItem.Click += (_, _) => Views.ControlsWindow.Open(f.App, Window.GetWindow(this)); // 1.2.3
        menu.Items.Add(controlsItem);
        more.ContextMenu = menu;
        more.Click += (_, _) => { menu.PlacementTarget = more; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; };
        row1.Children.Add(more);
        row1.Children.Add(Ui.Button("Quests…", (_, _) => f.OpenQuests(), "Quest tracker: what you've done, what's active, quick setup"));
        row1.Children.Add(Ui.Button("Hideout…", (_, _) => Views.HideoutWindow.Open(f.App, Window.GetWindow(this)), "Hideout tracker: the station levels you've built (price cards skip those needs)"));
        _view.ShowLabels = f.Settings.ShowLabels;
        _questTools.Children.Add(Ui.Button("Deselect all", (_, _) => PinBulk(p => QuestPins.DeselectAll(p), "Removed {0} quest(s) from the map (all traders, all maps)."), "Take every quest off the map (all traders, all maps). Quest progress isn't changed."));
        _questTools.Children.Add(Ui.Button("Only my active quests", (_, _) => PinBulk(p =>
        {
            QuestPins.DeselectAll(p);
            return QuestPins.Select(p, p.States.Where(kv => kv.Value == QuestState.Active).Select(kv => kv.Key));
        }, "{0} active quest(s) on the map."), "Show just the quests you've started (all maps)"));
        _questTools.Children.Add(Ui.Button("Quests…", (_, _) => f.OpenQuests(), "Pick quests by trader, search or \"this map only\""));

        var legend = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        void Key(string label, Color c, bool diamond = false)
        {
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(diamond ? 0 : 5), Background = new SolidColorBrush(c), Margin = new Thickness(6, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            if (diamond) dot.RenderTransform = new RotateTransform(45, 5, 5);
            legend.Children.Add(dot);
            legend.Children.Add(Ui.Text(label, 10.5, Theme.Muted));
        }
        Key("PMC", MapView.PmcColor); Key("Scav", MapView.ScavColor); Key("Both", MapView.SharedColor); Key("Co-op", MapView.CoOpColor);
        Key("Transit", MapView.TransitColor, true); Key("You", MapView.PlayerColor);
        legend.Children.Add(Ui.Text("  · white dot = requirement (hover) · ◇ quest spot, hollow = possible spot", 10.5, Theme.Muted));

        var top = new StackPanel();
        top.Children.Add(row1);
        top.Children.Add(legend);
        top.Children.Add(_status);
        _posStatus.Visibility = Visibility.Collapsed;
        _posStatus.ToolTip = "Click to hide";
        _posStatus.MouseLeftButtonUp += (_, _) => _posStatus.Visibility = Visibility.Collapsed;
        top.Children.Add(_posStatus);
        SetDock(top, Dock.Top);
        Children.Add(top);

        _questsBox = new Expander
        {
            Header = Ui.Text("Quests on this map", 11, Theme.Muted), Foreground = Theme.Text, IsExpanded = false,
            Content = new StackPanel { Children = { _questTools, new ScrollViewer { Content = _questList, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } },
        };
        SetDock(_questsBox, Dock.Bottom);
        Children.Add(_questsBox);

        var surface = new Grid { MinHeight = 120 };
        surface.Children.Add(new Border { BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1), Child = _view });
        _overlay.HorizontalAlignment = HorizontalAlignment.Center;
        _overlay.VerticalAlignment = VerticalAlignment.Center;
        _overlay.TextAlignment = TextAlignment.Center;
        _overlay.Margin = new Thickness(16);
        _overlay.IsHitTestVisible = false;
        surface.Children.Add(_overlay);
        _hintBox = new Border
        {
            Child = _hint, Background = new SolidColorBrush(Color.FromArgb(200, 12, 14, 16)), CornerRadius = new CornerRadius(11),
            Padding = new Thickness(11, 4, 11, 5), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1),
        };
        surface.Children.Add(_hintBox);
        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); _hintBox.Visibility = Visibility.Collapsed; };
        Children.Add(surface);

        f.RaidChanged += OnRaidChanged;
        f.PositionChanged += OnPosition;
        f.ProgressChanged += _ => RebuildMarkers();
        f.FeedsChanged += RebuildMarkers;
        f.ExtractListChanged += RebuildMarkers;
        f.MapStyleChanged += () => { UpdateStyleButton(); if (_def != null) _ = LoadImageAsync(keepView: true); };
        UpdateStyleButton();
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _tick.Tick += (_, _) => { UpdateStatus(); if (HasMap) _view.InvalidateVisual(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) _tick.Start(); else _tick.Stop(); };

        _overlay.Text = "Pick a map above, or start a raid: the map switches automatically when Tarkov loads one.\n\nPress PrtScn in raid to put your position on the map.";
        if (f.Settings.LastMap is { } last && _map.Items.OfType<MapChoice>().FirstOrDefault(c => c.NormalizedName == last) is { } lc)
        { _suppress = true; _map.SelectedItem = lc; _suppress = false; }
        UpdateStatus();
        // If the game is already in a raid when the overlay starts, the log replay sets the map.
        if (f.Raid.Map != null && f.Raid.Phase != RaidPhase.Menu) _ = LoadMapAsync(f.Raid.Map);
        else if (f.Settings.LastMap is { } lastMap)
        {
            // show the last map shortly after start-up (after prices have loaded), from the on-disk cache when possible
            var t = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, _) => { t.Stop(); if (_mapName == null) _ = LoadMapAsync(lastMap); };
            t.Start();
        }
    }

    /// <summary>
    /// Satellite ↔ Vector for the map shown (button, global shortcut). Never activates or focuses a window, so the
    /// game keeps the keyboard. On a single-style map it only says so.
    /// </summary>
    public void ToggleStyle(string via)
    {
        if (_def != null && MapStyles.SingleModeReason(_def, DisplayName(_mapName ?? _def.Key)) is { } why)
        {
            ShowHint(why);
            Log.Write($"map: style toggle ({via}) ignored: {why}");
            return;
        }
        bool sat = !(_f.Settings.PreferSatelliteMap);
        _f.SetPreferSatellite(sat, via); // raises MapStyleChanged → reload with the view kept
        ShowHint("Map: " + (sat ? "Satellite" : "Vector"));
    }

    public void ShowHint(string text)
    {
        _hint.Text = text;
        _hintBox.Visibility = Visibility.Visible;
        _hintTimer.Stop(); _hintTimer.Start();
    }

    /// <summary>Last hint text (self-test).</summary>
    public string HintText => _hint.Text;
    public string StyleButtonText => _styleBtn.Content?.ToString() ?? "";
    public bool StyleButtonEnabled => _styleBtn.IsEnabled;

    private void UpdateStyleButton()
    {
        var key = _f.App.Settings.MapStyleHotkeyEnabled && _f.App.Settings.EffectiveMapStyleHotkey.Length > 0 ? _f.App.Settings.EffectiveMapStyleHotkey : null;
        var keyTip = key != null ? $" Shortcut: {key} (works in game, no alt-tab)." : " Set a shortcut in Settings › Map & Screenshots.";
        var shown = ShownStyle ?? _f.Settings.PreferredStyle;
        if (_def != null && MapStyles.SingleModeReason(_def, DisplayName(_mapName ?? _def.Key)) is { } why)
        {
            _styleBtn.Content = MapStyles.Label(_def.HasTiles ? MapStyle.Satellite : MapStyle.Vector);
            _styleBtn.IsEnabled = false;
            _styleBtn.ToolTip = why + ": tarkov.dev has only one map picture for it.";
            ToolTipService.SetShowOnDisabled(_styleBtn, true);
            return;
        }
        _styleBtn.IsEnabled = true;
        _styleBtn.Content = MapStyles.Label(shown);
        _styleBtn.ToolTip = $"Showing the {MapStyles.Label(shown).ToLowerInvariant()} map. Click for {MapStyles.Label(shown == MapStyle.Satellite ? MapStyle.Vector : MapStyle.Satellite)}.{keyTip}";
    }

    public void SetHostMode(bool poppedOut, bool fullscreen = false)
    {
        _popBtn.Content = poppedOut ? "Back to main window" : "Pop out";
        _fullBtn.Visibility = poppedOut ? Visibility.Visible : Visibility.Collapsed;
        _fullBtn.Content = fullscreen ? "Exit fullscreen" : "Fullscreen";
    }

    private CheckBox Toggle(string label, Func<bool> get, Action<bool> set, string? tip = null)
    {
        var cb = new CheckBox { Content = label, IsChecked = get(), Foreground = Theme.Text, Margin = new Thickness(0, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        cb.Checked += (_, _) => { set(true); _f.SaveSoon(); RebuildMarkers(); };
        cb.Unchecked += (_, _) => { set(false); _f.SaveSoon(); RebuildMarkers(); };
        return cb;
    }

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["customs"] = "Customs", ["factory"] = "Factory", ["night-factory"] = "Night Factory", ["infected-factory"] = "Factory (infected)",
        ["woods"] = "Woods", ["shoreline"] = "Shoreline", ["interchange"] = "Interchange", ["reserve"] = "Reserve", ["lighthouse"] = "Lighthouse",
        ["streets-of-tarkov"] = "Streets of Tarkov", ["the-lab"] = "The Lab", ["the-lab-dark"] = "The Lab (dark)", ["infected-lab"] = "The Lab (infected)",
        ["ground-zero"] = "Ground Zero", ["ground-zero-21"] = "Ground Zero 21+", ["terminal"] = "Terminal", ["the-labyrinth"] = "The Labyrinth",
        ["icebreaker"] = "Icebreaker",
    };
    public static string DisplayName(string n) => Names.TryGetValue(n, out var d) ? d : MapFeed.Pretty(n);

    private static IEnumerable<MapChoice> Choices()
    {
        var defs = MapDataService.LoadFallbackDefinitions();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs.OrderBy(d => DisplayName(d.Key)))
            foreach (var n in new[] { d.Key }.Concat(d.AltMaps))
                if (seen.Add(n) && !n.StartsWith("infected", StringComparison.OrdinalIgnoreCase)) yield return new MapChoice(n, DisplayName(n));
    }

    // ---------------- loading ----------------
    public async Task LoadMapAsync(string normalizedName)
    {
        _mapName = normalizedName;
        _f.Settings.LastMap = normalizedName;
        _f.SaveSoon();
        if (_map.Items.OfType<MapChoice>().FirstOrDefault(c => c.NormalizedName == normalizedName) is { } mc && !Equals(_map.SelectedItem, mc))
        { _suppress = true; _map.SelectedItem = mc; _suppress = false; }
        _overlay.Text = $"Loading {DisplayName(normalizedName)}…";
        await _f.EnsureFeedsAsync(quests: true);
        if (_mapName != normalizedName) return;
        var defs = _f.Definitions ?? MapDataService.LoadFallbackDefinitions();
        _def = MapDefinitionParser.Resolve(defs, normalizedName);
        if (_def == null) { _overlay.Text = $"tarkov.dev has no drawing for {DisplayName(normalizedName)} yet."; _view.SetMap(null, null); return; }
        ShownStyle = null;
        UpdateStyleButton();
        _suppress = true;
        _floor.Items.Clear();
        foreach (var l in MapLayerChoice.For(_def)) _floor.Items.Add(l);
        _floor.SelectedIndex = 0;
        _floor.Visibility = _floor.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _suppress = false;
        await LoadImageAsync(keepView: false);
    }

    private async Task LoadImageAsync(bool keepView)
    {
        if (_def == null) return;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var def = _def;
        var layer = _floor.SelectedItem as MapLayerChoice ?? MapLayerChoice.Main;
        var preferred = _f.Settings.PreferredStyle;
        var progress = new Progress<string>(s => { if (!cts.IsCancellationRequested) _overlay.Text = s; });
        try
        {
            if (!HasMap) _overlay.Text = "Preparing map…";
            var img = await _renderer.GetAsync(def, layer, preferred, progress, cts.Token);
            if (cts.IsCancellationRequested || _def != def) return;
            _view.Satellite = img.Style == MapStyle.Satellite;
            _view.SetMap(def, img, keepView);
            ShownStyle = img.Style;
            UpdateStyleButton();
            if (_layerHint != null) { ShowHint(_layerHint); _layerHint = null; }
            else if (img.Style != preferred && MapStyles.HasBoth(def))
                ShowHint($"{layer.Name} has no {MapStyles.Label(preferred).ToLowerInvariant()} image: showing {MapStyles.Label(img.Style)}");
            _overlay.Text = "";
            bool was = HasMap;
            HasMap = true;
            if (!was) _f.MapUseChanged();
            RebuildMarkers();
            OnPosition();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or System.Xml.XmlException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Write($"map: could not draw {def.Key}/{layer.Name}: {ex}");
            _view.SetMap(def, null);
            _overlay.Text = $"Couldn't load the {DisplayName(def.Key)} drawing ({ex.Message}). Exits and quests are still shown; try again with Fit or pick the map again.";
            RebuildMarkers();
        }
    }

    // ---------------- markers ----------------
    private string? _lastSideNote;
    private void RebuildMarkers()
    {
        if (_def == null) return;
        var mode = _f.ModeId;
        var side = _f.CurrentSide;
        var sideNote = $"{_f.Settings.Side}→{side?.ToString() ?? "all"} (raid {_f.Raid.Phase}, detected {_f.Raid.Side?.ToString() ?? "-"}, mode {_f.Raid.SessionMode ?? "?"})";
        if (sideNote != _lastSideNote) { _lastSideNote = sideNote; Log.Write("map: exit side " + sideNote); }

        var info = _f.MapFeed?.ByNormalizedName(_mapName) ?? _f.MapFeed?.ByNormalizedName(_def.Key);
        var extracts = new List<ExtractMarker>();
        if (info != null && (_f.Settings.ShowExtracts || _f.Settings.ShowTransits))
            extracts = ExtractFilter.For(info, side, _f.ItemName, _f.Settings.ShowTransits)
                .Where(x => _f.Settings.ShowExtracts || x.Kind == ExtractKind.Transit).ToList();
        CurrentExtracts = extracts;

        var markers = new List<QuestMarker>();
        _questList.Children.Clear();
        int pinnedHere = 0;
        if (_f.TaskFeed is { } tf && _f.MapFeed is { } mf)
        {
            var defs = _f.Definitions ?? MapDataService.LoadFallbackDefinitions();
            var mapIds = mf.Maps.Where(m => MapDefinitionParser.Resolve(defs, m.NormalizedName)?.Key == _def.Key).Select(m => m.Id).ToList();
            bool mapChanged = CurrentMapIds == null || !CurrentMapIds.SequenceEqual(mapIds);
            CurrentMapIds = mapIds;
            if (mapChanged) _f.RaiseMapShown();
            var progress = _f.Progress.For(mode);
            // Drawn: only the quests the user picked (+ every available one in opt-in browse mode). Nothing picked = no pins.
            var drawn = QuestLogic.ObjectivesOnMap(tf, progress, mapIds, _f.Settings.ShowAllAvailableQuests);
            // Checklist: picked + active + available quests on this map, picked first, unticked by default.
            var offer = QuestLogic.Candidates(tf, progress, mapIds, QuestLogic.CandidateKind.Selectable);
            var tasks = offer.GroupBy(o => o.Task.Id).Select(g => g.First()).OrderBy(o => o.Pinned ? 0 : 1)
                .ThenBy(o => o.Availability == QuestAvailability.Active ? 0 : 1).ThenBy(o => o.Task.Name).ToList();
            var colors = new Dictionary<string, Color>();
            for (int i = 0; i < tasks.Count; i++) colors[tasks[i].Task.Id] = Palette[i % Palette.Length];
            if (_f.Settings.ShowQuests)
                foreach (var o in drawn)
                    foreach (var spot in o.Spots)
                        markers.Add(new QuestMarker(o.Task.Id, o.Task.Name, o.Task.TraderName, o.Objective.Description, spot,
                            colors.TryGetValue(o.Task.Id, out var c) ? c : Palette[0], o.Objective.Optional, o.Availability == QuestAvailability.Active));
            pinnedHere = tasks.Count(t => t.Pinned);
            foreach (var row in tasks)
            {
                var t = row.Task;
                bool active = row.Availability == QuestAvailability.Active;
                var cb = new CheckBox
                {
                    IsChecked = row.Pinned, Margin = new Thickness(0, 1, 12, 1), Foreground = Theme.Text,
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            new Border { Width = 9, Height = 9, Background = new SolidColorBrush(colors[t.Id]), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center },
                            Ui.Text($"{t.Name}{(active ? "" : " (not started)")}", 11, row.Pinned ? Theme.Text : Theme.Muted),
                        },
                    },
                    ToolTip = $"{t.TraderName} · {string.Join(" / ", offer.Where(o => o.Task.Id == t.Id).Select(o => o.Objective.Description).Distinct())}",
                };
                var id = t.Id;
                cb.Checked += (_, _) => { if (progress.Pinned.Add(id)) _f.Progress.NotifyChanged(mode); };
                cb.Unchecked += (_, _) => { if (progress.Pinned.Remove(id)) _f.Progress.NotifyChanged(mode); };
                _questList.Children.Add(cb);
            }
            int shownQuests = drawn.Select(o => o.Task.Id).Distinct().Count();
            _questsBox.Header = Ui.Text($"Quests on this map: {pinnedHere} picked of {tasks.Count} ({tasks.Count(t => t.Availability == QuestAvailability.Active)} active) · {_f.ModeLabel}" +
                (_f.Settings.ShowAllAvailableQuests ? $" · browse mode: showing {shownQuests}" : " · tick to show"), 11, Theme.Muted);
            if (tasks.Count == 0) _questList.Children.Add(Ui.Text("No unlocked, unfinished quests with spots on this map. Set your progress in Quests…", 11, Theme.Muted, wrap: true));
            _questsCb.Content = _f.Settings.ShowAllAvailableQuests ? $"Quests ({shownQuests}, browse)" : $"Quests ({pinnedHere})";
        }
        else { _questsBox.Header = Ui.Text("Quests on this map (quest data not loaded yet)", 11, Theme.Muted); _questsCb.Content = "Quests"; }

        _view.SetMarkers(extracts, markers, _f.ListedExtracts);
        UpdateStatus();
    }

    private void PinBulk(Func<ModeProgress, int> op, string message)
    {
        var mode = _f.ModeId;
        var p = _f.Progress.For(mode);
        int n = op(p);
        _f.Progress.NotifyChanged(mode); // map + Quests window refresh at once, and saved
        Log.Write("map: " + string.Format(message, n));
    }

    // 1.2.2: auto layer switching. Key of the last screenshot applied per map, so a hand-picked floor stays until a newer one.
    private string? _autoLayerApplied;
    private string? _layerHint;

    /// <summary>Self-test: choose a floor the way a click on the Floor dropdown does.</summary>
    internal void PickFloorByHand(string name)
    {
        var c = _floor.Items.OfType<MapLayerChoice>().FirstOrDefault(x => x.Name == name);
        if (c != null) _floor.SelectedItem = c;
    }

    /// <summary>Self-test: centre the player's dot at a zoom (screen px per zoom-0 px).</summary>
    internal void CenterOnPlayer(double scale)
    {
        if (_def != null && _f.LastFix is { } fx) _view.CenterOn(_def.Projection.ToMap(fx.Data.Position), scale);
    }

    /// <summary>"Test position" re-applies the floor for the newest screenshot even if it was applied before.</summary>
    public void ResetAutoLayer() => _autoLayerApplied = null;

    /// <summary>Last auto layer decision, for logs/self-tests (e.g. "Bunkers", "Main").</summary>
    public string? LastAutoLayer { get; private set; }
    public string? FloorName => (_floor.SelectedItem as MapLayerChoice)?.Name;
    public IReadOnlyList<string> FloorNames => _floor.Items.OfType<MapLayerChoice>().Select(c => c.Name).ToList();

    /// <summary>Picks the floor for a new screenshot; true when the floor changed (the image reloads). Never activates a window.</summary>
    private bool ApplyAutoLayer(PositionFix fix)
    {
        if (_def == null) return false;
        var key = $"{_def.Key}|{fix.SeenAt.UtcTicks}|{fix.Data.Position.X:0.###},{fix.Data.Position.Y:0.###},{fix.Data.Position.Z:0.###}";
        var choices = _floor.Items.OfType<MapLayerChoice>().ToList();
        var mode = _f.Settings.AutoLayer;
        var pick = choices.Count > 1 ? AutoLayer.Decide(_def, choices, fix.Data.Position, mode, key, _autoLayerApplied) : null;
        if (mode != AutoLayerMode.Off) _autoLayerApplied = key;
        if (pick == null) return false;
        LastAutoLayer = pick.Name;
        var current = _floor.SelectedItem as MapLayerChoice ?? MapLayerChoice.Main;
        if (current.Name == pick.Name) return false;
        _suppress = true;
        try { _floor.SelectedItem = pick; }
        finally { _suppress = false; }
        var shown = MapStyles.Effective(_def, _f.Settings.PreferredStyle, pick);
        var hint = $"Layer: {(pick.IsMain ? "Main" : pick.Name)}";
        if (shown != _f.Settings.PreferredStyle) hint += $" ({MapStyles.Label(shown).ToLowerInvariant()} only)";
        Log.Write($"map: auto layer {current.Name} → {pick.Name} at y {fix.Data.Position.Y:0.##} ({AutoLayer.ModeId(mode)})");
        _layerHint = hint;
        LastPositionNote = $"You: drawn on {DisplayName(_def.Key)}; switched the floor to {(pick.IsMain ? "Main" : pick.Name)} (height {fix.Data.Position.Y:0.#}).";
        ShowHint(hint);
        _ = LoadImageAsync(keepView: true);
        return true;
    }

    private void OnPosition()
    {
        var fix = _f.LastFix;
        PlayerMarker? p = null;
        string? note = null;
        var defs = _f.Definitions ?? MapDataService.LoadFallbackDefinitions();
        if (fix == null) note = null;
        else if (!_f.Settings.ShowPlayer) note = "Position received, but \"Me\" is unticked.";
        else
        {
            var fixDef = _f.LastFixMap != null ? MapDefinitionParser.Resolve(defs, _f.LastFixMap) : null;
            if (_def == null || (fixDef != null && fixDef.Key != _def.Key))
            {
                // No map shown, or the raid is on another map: switch to the raid's map, or guess it from the coordinates.
                var target = fixDef?.Key ?? MapGuess.FromPosition(defs, fix.Data.Position);
                if (target != null && (_def == null || _f.Settings.AutoSwitchMap))
                {
                    note = $"Position is on {DisplayName(target)}: switching to it.";
                    LogPos(fix, note);
                    LastPositionNote = note;
                    _ = LoadMapAsync(target); // OnPosition runs again when the drawing is ready
                    UpdateStatus();
                    return;
                }
                note = target != null
                    ? $"Your position is on {DisplayName(target)} but {DisplayName(_def!.Key)} is shown (Auto-follow is off): pick {DisplayName(target)}."
                    : "Position received; pick the map you're on to see it.";
            }
            else
            {
                p = new PlayerMarker(fix.Data.Position, fix.Data.YawDegrees, fix.SeenAt);
                var px = _def.Projection.ToMap(fix.Data.Position);
                var r = _def.BoundsRect;
                bool inside = MapGuess.Inside(_def, fix.Data.Position, 0.15);
                if (inside && ApplyAutoLayer(fix)) { _view.SetPlayer(p); UpdateStatus(); return; } // OnPosition runs again when the floor is drawn
                note = inside
                    ? $"You: drawn on {DisplayName(_def.Key)} ({MapPanel.Ago(DateTimeOffset.Now - fix.SeenAt)})."
                    : $"Position x {fix.Data.Position.X:0}, z {fix.Data.Position.Z:0} is outside the {DisplayName(_def.Key)} map{(MapGuess.FromPosition(defs, fix.Data.Position) is { } g && g != _def.Key ? $" (looks like {DisplayName(g)})" : "")}; drawn at the edge of the view.";
                LogPos(fix, $"{note} pixel ({px.X:0},{px.Y:0}) bounds ({r.X:0},{r.Y:0} {r.Width:0}×{r.Height:0})");
            }
        }
        if (p == null && fix != null && note != null) LogPos(fix, note);
        LastPositionNote = note;
        _view.SetPlayer(p);
        if (p != null) _view.RevealPlayer();
        UpdateStatus();
    }

    private void LogPos(PositionFix fix, string what)
    {
        var key = fix.File + "|" + what;
        if (key == _loggedPosKey) return;
        _loggedPosKey = key;
        Log.Write($"map: marker {System.IO.Path.GetFileName(fix.File)}: {what}");
    }

    private void OnRaidChanged()
    {
        var r = _f.Raid;
        if (_f.Settings.AutoSwitchMap && r.Map != null && r.Phase != RaidPhase.Menu && !string.Equals(r.Map, _mapName, StringComparison.OrdinalIgnoreCase))
            _ = LoadMapAsync(r.Map);
        else RebuildMarkers();
    }

    public static string Ago(TimeSpan t) => t.TotalSeconds < 60 ? "just now" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min ago" : $"{(int)t.TotalHours} h {(int)t.Minutes} min ago";

    private void UpdateStatus()
    {
        var r = _f.Raid;
        var parts = new List<string>();
        string phase = r.Phase switch
        {
            RaidPhase.Loading => "Loading raid", RaidPhase.Matching => "Matching", RaidPhase.Countdown => "Raid countdown",
            RaidPhase.InRaid => "In raid", _ => "In menus",
        };
        parts.Add(r.Map != null && r.Phase != RaidPhase.Menu ? $"{phase}: {DisplayName(r.Map)}" : phase);
        parts.Add(_f.ModeLabel);
        parts.Add(_f.CurrentSide switch
        {
            PlayerSide.Pmc => _f.Settings.Side == "pmc" ? "PMC exits (set)" : "PMC exits",
            PlayerSide.Scav => _f.Settings.Side == "scav" ? "Scav exits (set)" : "Scav exits",
            _ => "all exits",
        });
        if (_f.LastFix is { } fx) parts.Add($"position {Ago(DateTimeOffset.Now - fx.SeenAt)}");
        string shotWarn = "";
        if (_f.Settings.WatchScreenshots && _f.Screenshots is { FolderExists: false } sw) shotWarn += $" · screenshots folder not found ({sw.Folder}): see More ▾ › Test position";
        if (_f.ScreenshotKeyStatus is "unbound" or "sysreq") shotWarn += " · " + ScreenshotKeyCheck.Describe(_f.ScreenshotKeyStatus, _f.ScreenshotKeys);
        if (_f.ListedExtracts != null && _f.ListedExtractsAt is { } la) parts.Add($"exit list read {Ago(DateTimeOffset.Now - la)}");
        string hint = _f.Settings.WatchScreenshots ? "Press PrtScn in raid to update your dot." : "Position dot is off (Settings…).";
        string logs = _f.Settings.WatchGameLogs ? "" : " · game log reading is off";
        _status.Text = string.Join(" · ", parts) + logs + shotWarn + ".  " + (LastPositionNote is { } pn && _f.LastFix != null ? pn + " " : "") + hint;
        var shots = _f.Screenshots is { } w ? $"Screenshots: {w.Folder} ({w.FolderSource}, {(w.FolderExists ? "found" : "not found")}); last file: {w.LastFile ?? "none yet"}{(w.LastResult != null ? " → " + w.LastResult : "")}" : "Screenshots: off";
        _status.ToolTip = $"Game logs: {_f.LogStatus}\n{shots}\n{ScreenshotKeyCheck.Describe(_f.ScreenshotKeyStatus, _f.ScreenshotKeys)}\n{_f.Data.LastProblem}".Trim();
    }
}
