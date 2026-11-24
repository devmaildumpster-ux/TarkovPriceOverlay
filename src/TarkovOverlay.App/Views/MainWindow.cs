using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TarkovOverlay.App.Native;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Services;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Economics;
using TarkovOverlay.Core.Indexes;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Core.Settings;

namespace TarkovOverlay.App.Views;

/// <summary>
/// Main window: a slim header (mode badge, shortcut, auto-hover, price status, Refresh prices, Settings…), the map,
/// typed search, results, tracked requirements, the scrollable detail card and the last-lookup diagnostics.
/// 1.1.4: the rarely changed options moved to Settings… (Views/SettingsWindow.cs); the map got their space.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly AppServices _s;
    private readonly HotkeyManager _hotkey;
    private readonly LookupController _lookup;
    private readonly TextBlock _modeBadge = Ui.Text("", 12, Theme.Window, FontWeights.Bold);
    private readonly Border _modeBadgeBox = new() { CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Background = Theme.Gold };
    private readonly TextBlock _hotkeyText = Ui.Text("", 12, Theme.Text, FontWeights.SemiBold);
    private readonly TextBlock _autoText = Ui.Text("", 12, Theme.Text);
    private readonly TextBlock _dataState = Ui.Text("", 12, Theme.Muted);
    private readonly TextBlock _warn = Ui.Text("", 11.5, Theme.Warn, wrap: true);
    // 1.2.6: "Update available" strip under the header (hidden unless a newer release exists)
    private readonly Border _updateBar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 3, 6, 3), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
    private readonly TextBlock _updateText = Ui.Text("", 12, Theme.Text, FontWeights.SemiBold);
    private bool _updateDismissed;
    private readonly TextBox _search = new() { Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(4), FontSize = 13, Background = Theme.Window, Foreground = Theme.Text, BorderBrush = Theme.CardEdge, CaretBrush = Theme.Text };
    private readonly TextBlock _resultsHeader = Ui.Text("Results", 11, Theme.Muted);
    private readonly ListBox _results = new() { MinHeight = 160 };
    private readonly ListBox _tracked = new() { MinHeight = 120 };
    private readonly StackPanel _detail = new();
    private readonly TextBlock _diagText = Ui.Text("No lookup yet. Press the shortcut while hovering an item in Tarkov.", 11, Theme.Muted, wrap: true);
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = CatalogService.RefreshInterval };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private CatalogItem? _selected;
    private CancellationTokenSource? _historyCts;
    private LookupReport? _lastReport;
    private FloatingCardWindow? _floating;

    private readonly CursorCardWindow _card;
    private readonly AutoReader _auto;

    private readonly ShellFeatures? _shell;
    private readonly DispatcherTimer _autoModeDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string? _pendingLoggedMode;

    public MainWindow(AppServices services, HotkeyManager hotkey, LookupController lookup, CursorCardWindow card, AutoReader auto, ShellFeatures? shell = null)
    {
        _s = services; _hotkey = hotkey; _lookup = lookup; _card = card; _auto = auto; _shell = shell;
        Title = "Tarkov Price Overlay";
        Width = 1040; Height = 760; MinWidth = 820; MinHeight = 560;
        Background = Theme.Window; Foreground = Theme.Text; FontFamily = Theme.Font;
        if (_s.Settings.MainLeft is { } l && _s.Settings.MainTop is { } t) { Left = l; Top = t; WindowStartupLocation = WindowStartupLocation.Manual; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _results.Style = Ui.DarkListStyle();
        _tracked.Style = Ui.DarkListStyle();
        Content = BuildLayout();

        ControlsWindow.MainHotkey = _hotkey;
        SettingsWindow.Host = new SettingsHost(_s, _hotkey, _auto, _card, () => _selected, SwitchModeAsync, OpenSavedImage, UpdateHeader,
            _shell, () => { if (_selected != null) Select(_selected, keepScroll: true); });
        _card.CardOpacity = _s.Settings.EffectiveCardOpacityPercent / 100.0;

        // 1.2.0: minimize to tray, tray menu
        if (_shell != null)
        {
            _shell.ShowMain = () => { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); };
            _shell.OpenSettings = () => OpenSettings();
            _shell.OpenControls = () => ControlsWindow.Open(_s, this);
            _shell.Quit = Close;
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized && _s.Settings.MinimizeToTray)
                {
                    _shell.ApplyTray();
                    if (_shell.TrayVisible) { Hide(); Services.Log.Write("tray: main window minimized to the tray"); }
                }
            };
        }
        // 1.2.0: progress changes (Quests… / Hideout…) refresh the open detail card
        _s.Hideout.Changed += _ => Dispatcher.BeginInvoke(() => { if (_selected != null) Select(_selected, keepScroll: true); });
        if (Map.MapFeature.Instance is { } mf)
        {
            mf.Progress.Changed += _ => Dispatcher.BeginInvoke(() => { if (_selected != null) Select(_selected, keepScroll: true); });
            // 1.2.0: follow PvP/PvE from Tarkov's log (debounced: catching up on an old log replays several lines)
            mf.SessionModeLogged += (mode, initial) => { _pendingLoggedMode = mode; _autoModeDebounce.Stop(); _autoModeDebounce.Start(); };
        }
        _autoModeDebounce.Tick += async (_, _) =>
        {
            _autoModeDebounce.Stop();
            var d = TarkovOverlay.Core.Settings.ModeAutoSwitch.Decide(_s.Settings.AutoDetectMode, _s.Settings.ModeId, _pendingLoggedMode);
            if (d.SwitchTo is not { } to) { if (_pendingLoggedMode != null) Services.Log.Write($"mode auto-detect: log says {_pendingLoggedMode}; {d.Reason}"); return; }
            Services.Log.Write($"mode auto-detect: switching {_s.Settings.ModeId} → {to} ({d.Reason})");
            await SwitchModeAsync(GameMode.FromId(to));
        };
        _search.TextChanged += (_, _) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RunSearch(); };
        _results.SelectionChanged += (_, _) => { if (_results.SelectedItem is ListBoxItem { Tag: CatalogItem it }) Select(it); };
        _refreshTimer.Tick += async (_, _) => await _s.Catalog.RefreshNowAsync(manual: false); // 1.2.5: sends nothing while tarkov.dev is down
        _refreshTimer.Start();
        _clock.Tick += (_, _) =>
        {
            UpdateDataState(_s.Catalog.State);
            // Tesseract is the fallback engine; free its native model after 3 idle minutes (reloads on demand).
            if (_s.Tesseract.ReleaseIfIdle(TimeSpan.FromMinutes(3))) Log.Write("memory: released idle Tesseract engine");
        };
        _clock.Start();

        _s.Catalog.StateChanged += st => Dispatcher.BeginInvoke(() => { UpdateDataState(st); UpdateModeBadge(); });
        // 1.2.5: outage banner follows the availability gate (down / next check / recovered), countdown every 15 s while down
        _s.Availability.Changed += a => Dispatcher.BeginInvoke(() =>
        {
            UpdateDataState(_s.Catalog.State);
            _clock.Interval = a.IsUp ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(15);
            if (_selected != null) Select(_selected, keepScroll: true); // card notice
        });
        _s.Catalog.ManualCheckDone += (ok, wait) => Dispatcher.BeginInvoke(() =>
        {
            UpdateDataState(_s.Catalog.State);
            if (ok == null) _dataState.Text += $" · checked under 30 s ago (try again {PriceStatus.In(wait)})";
            else if (ok == false) _dataState.Text += " · checked just now: still unavailable";
        });
        _s.Catalog.ContextChanged += _ => Dispatcher.BeginInvoke(() => { RunSearch(); RefreshTracked(); if (_selected != null) Select(_s.Catalog.Context?.Snapshot.Item(_selected.Id), keepScroll: true); });
        _lookup.Completed += r => Dispatcher.BeginInvoke(() => ShowReport(r));
        _lookup.CandidatesFound += m => Dispatcher.BeginInvoke(() => ShowCandidates(m));
        LocationChanged += (_, _) => { _s.Settings.MainLeft = Left; _s.Settings.MainTop = Top; _s.SaveSoon(); };
        Closed += (_, _) => { _floating?.Close(); };

        // 1.2.6: release check after the window is up (background, 5 s timeout, silent on failure, never activates a window)
        _s.UpdateResultChanged += r => Dispatcher.BeginInvoke(() => ShowUpdate(r));
        Loaded += (_, _) => { if (_s.Settings.CheckForUpdates) _ = _s.CheckForUpdatesAsync(); };

        UpdateHeader();
        RefreshTracked();
    }

    private void ShowUpdate(Core.Updates.UpdateCheckResult r)
    {
        if (!r.ShowBanner || _updateDismissed || r.Latest is not { } latest) { _updateBar.Visibility = Visibility.Collapsed; return; }
        _updateText.Text = $"Update available: {latest} (you have {r.Current})";
        _updateBar.ToolTip = $"A newer version is on GitHub. Download opens the release page in your browser; nothing is downloaded or installed by itself.\n{r.Url}";
        _updateBar.Tag = r;
        _updateBar.Visibility = Visibility.Visible;
    }

    private Border BuildUpdateBar()
    {
        _updateBar.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0xAF, 0x50));
        _updateBar.BorderBrush = Theme.Green;
        var row = new DockPanel { LastChildFill = true };
        var buttons = Ui.Row(
            Ui.Button("Download", (_, _) => { if (_updateBar.Tag is Core.Updates.UpdateCheckResult r) LinkLauncher.OpenRelease(r.Url); }, "Open the release page in your browser"),
            Ui.Button("Skip this version", (_, _) => { if (_updateBar.Tag is Core.Updates.UpdateCheckResult { Latest: { } v }) _s.SkipUpdate(v); }, "Don't show this version again (a later one will show)"),
            Ui.Button("×", (_, _) => { _updateDismissed = true; _updateBar.Visibility = Visibility.Collapsed; }, "Hide until next launch"));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        _updateText.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_updateText);
        _updateBar.Child = row;
        return _updateBar;
    }

    /// <summary>Settings… › Game mode: prices/cards switch, and outside a raid the map reloads for the new mode.</summary>
    private async Task SwitchModeAsync(GameMode gm)
    {
        if (gm.Id == _s.Catalog.Mode.Id && gm.Id == _s.Settings.ModeId) return;
        _s.Settings.ModeId = gm.Id; _s.SaveSoon();
        _historyCts?.Cancel();
        UpdateModeBadge();
        Map.MapFeature.Instance?.AppModeChanged();
        await _s.Catalog.SwitchModeAsync(gm);
        UpdateHeader();
    }

    public void OpenSettings(SettingsTab tab = SettingsTab.Overlay) => SettingsWindow.Open(tab, this);

    // ---------------- layout ----------------
    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(8) };

        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Left: search, results, tracked
        var left = new DockPanel { Width = 360 };
        var searchBox = new StackPanel();
        searchBox.Children.Add(Ui.Text("Search (name or shorthand)", 11, Theme.Muted));
        searchBox.Children.Add(_search);
        searchBox.Children.Add(_resultsHeader);
        DockPanel.SetDock(searchBox, Dock.Top);
        left.Children.Add(searchBox);
        var trackedBox = new StackPanel();
        trackedBox.Children.Add(Ui.Text("Tracked requirements (this mode)", 11, Theme.Muted));
        trackedBox.Children.Add(_tracked);
        _tracked.MaxHeight = 220;
        DockPanel.SetDock(trackedBox, Dock.Bottom);
        left.Children.Add(trackedBox);
        left.Children.Add(_results);
        DockPanel.SetDock(left, Dock.Left);
        var leftPanel = Ui.Panel(left);
        root.Children.Add(leftPanel);

        // Right: detail + diagnostics
        var right = new DockPanel();
        var diag = new Expander { Header = Ui.Text("Last lookup (diagnostics, in memory only)", 11, Theme.Muted), Foreground = Theme.Text, IsExpanded = false };
        var diagStack = new StackPanel();
        diagStack.Children.Add(_diagText);
        diagStack.Children.Add(Ui.Row(Ui.Button("Export last capture…", (_, _) => ExportLastCapture(), "Explicitly save the last title crop and report for troubleshooting")));
        diag.Content = new ScrollViewer { Content = diagStack, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(diag, Dock.Bottom);
        right.Children.Add(diag);
        _detail.Children.Add(Ui.Text("Search for an item, or press the shortcut over a Tarkov tooltip.", 12, Theme.Muted, wrap: true));
        right.Children.Add(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4) });
        var rightPanel = Ui.Panel(right);
        root.Children.Add(rightPanel);
        // 1.1: the map has the first column (see Map/MainMapLayout.cs); 1.1.4: the whole column, settings moved out
        if (Map.MapFeature.Instance is { } map)
        {
            root.Children.Clear();
            var outer = new DockPanel();
            header.Margin = new Thickness(8, 8, 8, 0);
            DockPanel.SetDock(header, Dock.Top);
            outer.Children.Add(header);
            outer.Children.Add(Map.MainMapLayout.Build(map, leftPanel, rightPanel));
            return outer;
        }
        return root;
    }

    /// <summary>One slim row: [PvP] Shortcut ` · Auto-hover on (350 ms) · prices …   [Refresh prices] [Settings…]</summary>
    private FrameworkElement BuildHeader()
    {
        var bar = new DockPanel { LastChildFill = true };
        var buttons = Ui.Row(
            Ui.Button("? Controls", (_, _) => ControlsWindow.Open(_s, this), "Every hotkey, mouse control and button, with your current keys"),
            Ui.Button("Refresh prices", async (_, _) => await _s.Catalog.RefreshNowAsync(), "Download fresh prices now"),
            Ui.Button("Settings…", (_, _) => OpenSettings(), "Shortcut, game mode, card size and dismiss rules, auto-read timing, map & screenshots, troubleshooting"));
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        _modeBadgeBox.Child = _modeBadge;
        _modeBadgeBox.Cursor = System.Windows.Input.Cursors.Hand;
        _modeBadgeBox.ToolTip = "Game mode for prices, cards and the map — change it in Settings…";
        _modeBadgeBox.MouseLeftButtonUp += (_, _) => OpenSettings();
        var info = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(_modeBadgeBox);
        info.Children.Add(Ui.Text("Shortcut ", 12, Theme.Muted));
        info.Children.Add(_hotkeyText);
        info.Children.Add(Ui.Text("   ·   ", 12, Theme.Muted));
        info.Children.Add(_autoText);
        info.Children.Add(Ui.Text("   ·   ", 12, Theme.Muted));
        info.Children.Add(_dataState);
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(info);
        col.Children.Add(_warn);
        col.Children.Add(BuildUpdateBar());
        bar.Children.Add(col);
        return Ui.Panel(bar, new Thickness(0, 0, 0, 6));
    }


    // ---------------- status ----------------
    /// <summary>Header + warning refresh (also called by Settings… after every change).</summary>
    public void UpdateHeader()
    {
        UpdateModeBadge();
        var (hk, hkWarn) = HotkeyStatus(_hotkey, _s.Settings.HotkeyEnabled);
        _hotkeyText.Text = hk;
        _hotkeyText.Foreground = hkWarn ? Theme.Warn : _s.Settings.HotkeyEnabled ? Theme.Text : Theme.Muted;
        _hotkeyText.ToolTip = string.Join("\n", _hotkey.Attempts);
        var st = _s.Settings;
        _autoText.Text = st.AutoReadEnabled ? $"Auto-hover on ({st.EffectiveAutoDwellMs} ms)" : "Auto-hover off";
        _autoText.Foreground = st.AutoReadEnabled ? Theme.Green : Theme.Muted;
        _autoText.ToolTip = st.AutoReadEnabled
            ? $"Cards show when an item's name tooltip appears. Rest {st.EffectiveAutoDwellMs} ms, wait for tooltip up to {st.EffectiveAutoMaxTooltipWaitMs} ms; card {(st.DismissOnMove ? $"hides after {st.EffectiveMoveTolerancePx} px of mouse movement" : "stays when the mouse moves")}. Change in Settings…"
            : "Press the shortcut over an item tooltip for a card. Turn auto-hover on in Settings…";
        UpdateDataState(_s.Catalog.State);
        UpdateOcrState();
    }

    private void UpdateModeBadge()
    {
        var gm = GameMode.FromId(_s.Settings.ModeId);
        _modeBadge.Text = gm.ShortCue;
        _modeBadgeBox.Background = gm.Id == GameMode.Regular.Id ? Theme.Gold : Theme.Blue;
        _modeBadgeBox.ToolTip = $"{gm.DisplayName} — prices, cards and the map use this mode. Change it in Settings…";
    }

    /// <summary>Text for the shortcut state, shared by the header and Settings….</summary>
    internal static (string Text, bool Warn) HotkeyStatus(HotkeyManager hotkey, bool enabled)
    {
        if (!enabled) return ("off", false);
        if (hotkey.Active is { } a) return (a.Label, false);
        return ("none available", true);
    }

    private void UpdateDataState(DataState st)
    {
        var now = DateTimeOffset.UtcNow;
        var ctx = _s.Catalog.Context;
        var a = _s.Availability.State;
        var (text, level) = PriceStatus.Bar(st, a, ctx?.Snapshot.FetchedAt, _s.NewestScan(ctx), now);
        _dataState.Text = text;
        _dataState.ToolTip = !a.IsUp
            ? $"{GameMode.FromId(st.ModeId).DisplayName} prices. {FeedAvailability.Describe(a.Kind)} since {(a.DownSince is { } ds ? PriceStatus.Clock(ds) : "—")} ({a.Reason}).\nThe app isn't sending requests; it checks by itself (1, 2, 5, 10, then every 15 min) and refreshes as soon as tarkov.dev is back.\nRefresh prices = one check now (at most every 30 s). Cards keep showing the last prices."
            : level == StatusLevel.Warn && PriceStatus.ScansPaused(_s.NewestScan(ctx), now)
                ? $"{GameMode.FromId(st.ModeId).DisplayName} prices — {st.Describe(now)}.\ntarkov.dev answers, but its newest flea-market scan of ANY item is from {PriceStatus.Clock(_s.NewestScan(ctx)!.Value)}, so its price scanner looks paused.\nCards show those last prices (marked stale). The app keeps its normal 20-min refresh and picks up new scans by itself."
                : $"{GameMode.FromId(st.ModeId).DisplayName} prices — {st.Describe(now)}. Refreshed automatically every 20 min; Refresh prices downloads now.";
        _dataState.Foreground = level switch { StatusLevel.Error => Theme.Warn, StatusLevel.Warn => Theme.Gold, _ => st.Kind is DataStateKind.Fresh ? Theme.Green : Theme.Muted };
    }

    /// <summary>Only problems reach the main window; the full OCR status lives in Settings… › Troubleshooting.</summary>
    public void UpdateOcrState()
    {
        var w = new List<string>();
        if (!_s.WindowsOcr.IsAvailable && !_s.Tesseract.IsAvailable) w.Add("Text reading unavailable (Windows OCR and Tesseract) — hover lookups can't read names; typed search still works. Details: Settings… › Troubleshooting.");
        if (_s.Settings.HotkeyEnabled && _hotkey.Active == null) w.Add("No global shortcut could be registered (" + string.Join("; ", _hotkey.Attempts) + ").");
        _warn.Text = string.Join("  ", w);
        _warn.Visibility = w.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- search & results ----------------
    private void RunSearch()
    {
        var ctx = _s.Catalog.Context;
        var q = _search.Text;
        if (ctx == null || string.IsNullOrWhiteSpace(q)) return;
        var items = ctx.Items.Search(q, 80);
        _resultsHeader.Text = items.Count == 0 ? "No matches" : $"{items.Count} result(s)";
        FillResults(items, ctx);
    }

    private void FillResults(IEnumerable<CatalogItem> items, CatalogContext ctx)
    {
        _results.Items.Clear();
        var list = items.ToList();
        var dupNames = list.GroupBy(i => i.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var it in list)
        {
            var s = ctx.Prices.Summarize(it);
            var sub = new List<string>();
            if (dupNames.Contains(it.Name)) sub.Add($"variant #{CardBuilder.VariantIndex(it) + 1}");
            if (it.ShortName != null) sub.Add(it.ShortName);
            if (s.Flea.Average is { } f && s.Flea.State != FleaState.Unavailable) sub.Add("flea " + Money.Rub(f) + (s.Flea.State == FleaState.Stale ? " (stale)" : ""));
            if (s.BestTrader is { } t) sub.Add("trader " + Money.Rub(t.PriceRub));
            var panel = new StackPanel { Margin = new Thickness(2) };
            panel.Children.Add(Ui.Text(it.Name, 12.5, Theme.Text, FontWeights.SemiBold, wrap: true));
            panel.Children.Add(Ui.Text(string.Join(" · ", sub), 11, Theme.Muted));
            _results.Items.Add(new ListBoxItem { Content = panel, Tag = it });
        }
    }

    /// <summary>Several/conflicting/possible hover results land here so the user can verify the intended item.</summary>
    private void ShowCandidates(MatchResult m)
    {
        var ctx = _s.Catalog.Context;
        if (ctx == null) return;
        _resultsHeader.Text = m.Kind switch
        {
            MatchKind.Verified => "Last lookup: verified",
            MatchKind.Conflict when m.Repair == "identical catalog names" => $"Last lookup: {m.Items.Count} catalog entries named “{m.Items[0].Name}”",
            MatchKind.Conflict => "Last lookup: choose the correct item (reads disagree)",
            MatchKind.Several => "Last lookup: several possible matches — pick one",
            _ => "Last lookup: possible match — verify",
        };
        FillResults(m.Items, ctx);
        if (m.Kind == MatchKind.Verified && m.Single != null) Select(m.Single);
    }

    // ---------------- detail ----------------
    private void Select(CatalogItem? item, bool keepScroll = false)
    {
        var ctx = _s.Catalog.Context;
        if (item == null || ctx == null) return;
        _selected = item;
        _detail.Children.Clear();
        var builder = _s.Cards(ctx);
        var model = builder.Full(item, compact: false);
        _detail.Children.Add(CardRenderer.Build(model, 1.25, interactive: true, width: 400));
        _detail.Children.Add(Ui.Row(
            Ui.Button("Pop out card", (_, _) => PopOut(), "A movable floating detail card"),
            Ui.Button("Load price history", (_, _) => LoadHistory(item), "7-day flea history for this mode"),
            Ui.Button("Wiki", (_, _) => LinkLauncher.OpenWiki(item), "Open this item's Tarkov Wiki page (" + TarkovOverlay.Core.Links.ItemLinks.Wiki(item).Host + ")"),
            Ui.Button("tarkov.dev", (_, _) => LinkLauncher.OpenTarkovDev(item), "Open this item on tarkov.dev")));
        _s.SetLastItem(item);

        // Tracking controls for every quest hand-in and hideout requirement (1.2.0: same progress / FIR filter as the card).
        var (quests, questsDone, questsNonFir) = builder.Options.ShowNeeds ? builder.VisibleQuestNeeds(item.Id) : (Array.Empty<TarkovOverlay.Core.Indexes.QuestNeed>(), 0, 0);
        if (questsDone + questsNonFir > 0)
            _detail.Children.Add(Ui.Text($"Quests hidden: {(questsDone > 0 ? $"{questsDone} already completed" : "")}{(questsDone > 0 && questsNonFir > 0 ? " · " : "")}{(questsNonFir > 0 ? $"{questsNonFir} non-FIR" : "")} (Settings… › Overlay & Card)", 10.5, Theme.Muted, wrap: true));
        if (quests.Count > 0)
        {
            _detail.Children.Add(Ui.Text($"Quests ({quests.Count})", 13, Theme.Text, FontWeights.SemiBold));
            foreach (var q in quests.OrderBy(q => q.Optional).ThenBy(q => q.MinPlayerLevel ?? 0))
                _detail.Children.Add(TrackRow(builder.QuestRow(q, compact: false), q.GoalKey, () => new TrackedGoal
                {
                    GoalKey = q.GoalKey, Mode = ctx.Mode.Id, Kind = "quest", Title = q.QuestName, ItemIds = q.AcceptedItemIds.ToList(),
                    ItemName = item.Name, Required = q.Count, Remaining = q.Count, FoundInRaid = q.FoundInRaid,
                }));
        }
        else if (!ctx.Quests.Available) _detail.Children.Add(Ui.Text("Quest data unavailable right now.", 11, Theme.Warn));

        var (hideout, hideoutBuilt, hideoutNonFir) = builder.Options.ShowNeeds ? builder.VisibleHideoutNeeds(item.Id) : (Array.Empty<TarkovOverlay.Core.Indexes.HideoutNeed>(), 0, 0);
        if (hideoutBuilt + hideoutNonFir > 0)
            _detail.Children.Add(Ui.Text($"Hideout hidden: {(hideoutBuilt > 0 ? $"{hideoutBuilt} for levels you've built" : "")}{(hideoutBuilt > 0 && hideoutNonFir > 0 ? " · " : "")}{(hideoutNonFir > 0 ? $"{hideoutNonFir} non-FIR" : "")} (Map › Hideout…)", 10.5, Theme.Muted, wrap: true));
        if (hideout.Count > 0)
        {
            _detail.Children.Add(Ui.Text($"Hideout ({hideout.Count})", 13, Theme.Text, FontWeights.SemiBold));
            foreach (var h in hideout)
                _detail.Children.Add(TrackRow(builder.HideoutRow(h, compact: false), h.GoalKey, () => new TrackedGoal
                {
                    GoalKey = h.GoalKey, Mode = ctx.Mode.Id, Kind = "hideout", Title = $"{h.StationName} L{h.Level}", ItemIds = new() { item.Id },
                    ItemName = item.Name, Required = h.Count, Remaining = h.Count, FoundInRaid = h.FoundInRaid,
                }));
        }
        else if (!ctx.Hideout.Available) _detail.Children.Add(Ui.Text("Hideout data unavailable right now.", 11, Theme.Warn));
        if (!ctx.Recipes.CraftsAvailable || !ctx.Recipes.BartersAvailable)
            _detail.Children.Add(Ui.Text("Some recipe data is unavailable right now.", 11, Theme.Warn));
        _detail.Children.Add(Ui.Text("Craft/barter cost: consumed ingredients at the cheaper of fresh flea average or unconditional LL1 cash price, per output unit. Excludes fuel, time, flea fees and tool wear.", 10.5, Theme.Muted, wrap: true));

        _detail.Children.Add(new Border { Height = 8 });
        _detail.Children.Add(_historyHost);
        _historyHost.Children.Clear();
        _historyCts?.Cancel();
        _floating?.Show(model);
    }

    private readonly StackPanel _historyHost = new();


    private UIElement TrackRow(string text, string goalKey, Func<TrackedGoal> make)
    {
        var g = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var tracked = _s.Tracker.Get(goalKey);
        var btn = Ui.Button(tracked == null ? "Track" : "Tracked ✓", (_, _) =>
        {
            if (_s.Tracker.Get(goalKey) == null) _s.Tracker.Track(make()); else _s.Tracker.Remove(goalKey);
            _s.SaveSoon();
            RefreshTracked();
            Select(_selected, keepScroll: true);
        });
        DockPanel.SetDock(btn, Dock.Right);
        g.Children.Add(btn);
        g.Children.Add(Ui.Text(text, 11.5, Theme.Text, wrap: true));
        return g;
    }

    private void RefreshTracked()
    {
        _tracked.Items.Clear();
        var mode = _s.Catalog.Mode.Id;
        foreach (var t in _s.Tracker.ForMode(mode).OrderByDescending(t => t.Remaining > 0).ThenBy(t => t.Title))
        {
            var row = new DockPanel();
            var buttons = Ui.Row(
                Ui.Button("−", (_, _) => { _s.Tracker.Adjust(t.GoalKey, -1); _s.SaveSoon(); RefreshTracked(); }, "One handed in"),
                Ui.Button("+", (_, _) => { _s.Tracker.Adjust(t.GoalKey, +1); _s.SaveSoon(); RefreshTracked(); }),
                Ui.Button("×", (_, _) => { _s.Tracker.Remove(t.GoalKey); _s.SaveSoon(); RefreshTracked(); }, "Stop tracking"));
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            var label = new StackPanel();
            label.Children.Add(Ui.Text($"{t.ItemName}{(t.ItemIds.Count > 1 ? $" (+{t.ItemIds.Count - 1} alt.)" : "")}", 12, Theme.Text, FontWeights.SemiBold, wrap: true));
            label.Children.Add(Ui.Text($"{t.Title} · {t.Remaining} left of {t.Required}{(t.FoundInRaid ? " · FIR" : "")}", 11, t.Remaining > 0 ? Theme.Gold : Theme.Muted, wrap: true));
            row.Children.Add(label);
            _tracked.Items.Add(new ListBoxItem { Content = row, Tag = t });
        }
        if (_tracked.Items.Count == 0) _tracked.Items.Add(new ListBoxItem { Content = Ui.Text("Nothing tracked yet. Use “Track” on a quest or hideout row.", 11, Theme.Muted, wrap: true), IsEnabled = false });
    }

    // ---------------- market history ----------------
    private async void LoadHistory(CatalogItem item)
    {
        _historyCts?.Cancel();
        var cts = _historyCts = new CancellationTokenSource();
        var mode = _s.Catalog.Mode.Id;
        var ctx = _s.Catalog.Context;
        _historyHost.Children.Clear();
        _historyHost.Children.Add(Ui.Text("Loading price history…", 11, Theme.Muted));
        List<FeedParser.PricePoint>? points;
        try { points = await _s.Catalog.GetPriceHistoryAsync(mode, item.Id, cts.Token); }
        catch (OperationCanceledException) { return; }
        // Ignore obsolete responses (selection or mode changed).
        if (cts.IsCancellationRequested || _selected?.Id != item.Id || _s.Catalog.Mode.Id != mode) return;
        _historyHost.Children.Clear();
        _historyHost.Children.Add(Ui.Text($"Market detail · {GameMode.FromId(mode).DisplayName}", 13, Theme.Text, FontWeights.SemiBold));
        var now = DateTimeOffset.UtcNow;
        var scanAge = item.LastScan is { } ls ? $"scanned {Money.Age(now - ls)}" : "scan time unknown";
        _historyHost.Children.Add(Ui.Text($"Observed low: {(item.LastLowPrice is { } low ? Money.Rub(low) : "—")} · listings: {(item.LastOfferCount?.ToString(CultureInfo.InvariantCulture) ?? "—")} · {scanAge}", 11.5, Theme.Text, wrap: true));
        _historyHost.Children.Add(Ui.Text($"24h range: {(item.Low24hPrice is { } a ? Money.Rub(a) : "—")} – {(item.High24hPrice is { } b ? Money.Rub(b) : "—")} · catalog fetched {(ctx != null ? Money.Age(now - ctx.Snapshot.FetchedAt) : "—")}", 11.5, Theme.Text, wrap: true));
        if (points == null) { _historyHost.Children.Add(Ui.Text(_s.Availability.IsUp ? "History unavailable (network or feed error)." : $"History unavailable: {FeedAvailability.Describe(_s.Availability.State.Kind)} (not requested; checks automatically).", 11, Theme.Warn)); return; }
        var week = points.Where(p => p.Time >= now.AddDays(-7)).ToList();
        if (week.Count < 2) { _historyHost.Children.Add(Ui.Text(points.Count == 0 ? "No history for this item." : $"Not enough data in the last 7 days (latest point {Money.Age(now - points[^1].Time)}).", 11, Theme.Muted)); return; }
        _historyHost.Children.Add(Chart(week));
    }

    private static UIElement Chart(List<FeedParser.PricePoint> pts)
    {
        double W = 400, H = 150, pad = 4;
        var canvas = new Canvas { Width = W, Height = H, Background = Theme.Card, Margin = new Thickness(0, 4, 0, 0) };
        var vals = pts.SelectMany(p => new[] { p.Average, p.Min }).Where(v => v is > 0).Select(v => (double)v!.Value).ToList();
        if (vals.Count == 0) return Ui.Text("No priced points.", 11, Theme.Muted);
        double min = vals.Min(), max = vals.Max(); if (max - min < 1) max = min + 1;
        double t0 = pts[0].Time.ToUnixTimeSeconds(), t1 = pts[^1].Time.ToUnixTimeSeconds(); if (t1 - t0 < 1) t1 = t0 + 1;
        Point P(DateTimeOffset t, double v) => new(pad + (t.ToUnixTimeSeconds() - t0) / (t1 - t0) * (W - 2 * pad), H - pad - (v - min) / (max - min) * (H - 2 * pad - 14));
        var avg = new Polyline { Stroke = Theme.Blue, StrokeThickness = 1.6 };
        foreach (var p in pts.Where(p => p.Average is > 0)) avg.Points.Add(P(p.Time, p.Average!.Value));
        var lo = new Polyline { Stroke = Theme.Muted, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } };
        foreach (var p in pts.Where(p => p.Min is > 0)) lo.Points.Add(P(p.Time, p.Min!.Value));
        canvas.Children.Add(lo); canvas.Children.Add(avg);
        var lbl = Ui.Text($"max {Money.Rub((long)max)} · min {Money.Rub((long)min)} · {pts[0].Time.ToLocalTime():MMM d} – {pts[^1].Time.ToLocalTime():MMM d HH:mm} (local) · blue avg, dashed low", 9.5, Theme.Muted);
        Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, 1);
        canvas.Children.Add(lbl);
        return canvas;
    }

    // ---------------- floating card / saved image / diagnostics ----------------
    private void PopOut()
    {
        if (_floating == null || !_floating.IsLoaded)
        {
            _floating = new FloatingCardWindow { Owner = null };
            _floating.Closed += (_, _) => _floating = null;
        }
        var ctx = _s.Catalog.Context;
        if (_selected != null && ctx != null) _floating.Show(_s.Cards(ctx).Full(_selected, compact: true));
    }

    private void OpenSavedImage()
    {
        var w = new SavedImageWindow(_s, m => ShowCandidates(m)) { Owner = this };
        w.Show();
    }

    private void ShowReport(LookupReport r)
    {
        _lastReport = r;
        _diagText.Text = DiagnosticsFormatter.Format(r);
        _diagText.Foreground = Theme.Muted;
    }

    private void ExportLastCapture()
    {
        if (_lastReport?.Panel == null) { MessageBox.Show(this, "No captured title panel in memory yet.", Title); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"tarkov-title-{DateTime.Now:yyyyMMdd-HHmmss}.png", Filter = "PNG image|*.png" };
        if (dlg.ShowDialog(this) != true) return;
        ImageIo.SavePng(_lastReport.Panel, dlg.FileName);
        File.WriteAllText(System.IO.Path.ChangeExtension(dlg.FileName, ".txt"), DiagnosticsFormatter.Format(_lastReport), Encoding.UTF8);
    }
}
