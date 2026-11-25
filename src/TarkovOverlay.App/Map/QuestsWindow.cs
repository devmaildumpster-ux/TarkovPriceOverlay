using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Views;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Quests;

namespace TarkovOverlay.App.Map;

public sealed record QuestRow(string Id, string Name, string Trader, int Level, string Maps, string Status, string Kappa, bool OnMap, bool CanPin);

/// <summary>
/// Built-in quest tracker (per game mode). Updates itself from the game's quest messages; this window is for the
/// first setup ("I'm on this quest" marks everything before it done), corrections, and optional TarkovTracker sync.
/// </summary>
public sealed class QuestsWindow : Window
{
    private readonly MapFeature _f;
    private readonly ComboBox _mode = new() { Width = 130, Margin = new Thickness(2) };
    private readonly TextBox _level = new() { Width = 44, Margin = new Thickness(2), Background = Theme.Panel, Foreground = Theme.Text, ToolTip = "Your PMC level (locks quests that need a higher level). Empty = ignore levels." };
    private readonly ComboBox _faction = new() { Width = 80, Margin = new Thickness(2) };
    private readonly TextBox _search = new() { Width = 200, Margin = new Thickness(2), Background = Theme.Panel, Foreground = Theme.Text };
    private readonly ComboBox _trader = new() { Width = 130, Margin = new Thickness(2) };
    private readonly ComboBox _filter = new() { Width = 150, Margin = new Thickness(2) };
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended, Background = Theme.Window, Foreground = Theme.Text, BorderBrush = Theme.CardEdge };
    private readonly TextBlock _status = Ui.Text("", 11, Theme.Muted, wrap: true);
    private readonly TextBlock _summary = Ui.Text("", 11.5, Theme.Text, wrap: true);
    private readonly TextBlock _counts = Ui.Text("", 12.5, Theme.Gold, FontWeights.SemiBold, wrap: true);
    private readonly CheckBox _thisMap = new() { Content = "This map only", Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private TaskFeed? _feed;
    private bool _loading;

    private static readonly string[] Filters = QuestListFilter.Statuses;

    internal TextBox SearchBox => _search;
    internal ComboBox ModeBox => _mode;
    internal ComboBox FilterBox => _filter;
    internal ListView List => _list;
    internal string SummaryText => _summary.Text;
    internal string StatusText => _status.Text;
    internal IReadOnlyList<QuestRow> Rows => _list.ItemsSource as IReadOnlyList<QuestRow> ?? Array.Empty<QuestRow>();

    public QuestsWindow(MapFeature f)
    {
        _f = f;
        Title = "Quests · Tarkov Price Overlay";
        Background = Theme.Window; Foreground = Theme.Text; FontFamily = Theme.Font;
        Width = 980; Height = 720; MinWidth = 720; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        foreach (var m in GameMode.All) _mode.Items.Add(m);
        _mode.SelectedItem = GameMode.FromId(f.ModeId);
        _faction.Items.Add("Any"); _faction.Items.Add("BEAR"); _faction.Items.Add("USEC");
        foreach (var x in Filters) _filter.Items.Add(x);
        _filter.SelectedIndex = 0;

        var root = new DockPanel { Margin = new Thickness(8) };
        var head = new StackPanel();
        head.Children.Add(Ui.Text("Your quests update automatically while the overlay runs: Tarkov writes each quest you accept, finish or fail to its log, and the app reads it. Use this window once to set where you are (select your current quest → \"I'm on this quest\"), and to fix anything by hand.", 11.5, Theme.Muted, wrap: true));
        var r1 = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        r1.Children.Add(Ui.Text("Mode ", 11, Theme.Muted)); r1.Children.Add(_mode);
        r1.Children.Add(Ui.Text("  Level ", 11, Theme.Muted)); r1.Children.Add(_level);
        r1.Children.Add(Ui.Text("  Faction ", 11, Theme.Muted)); r1.Children.Add(_faction);
        r1.Children.Add(Ui.Text("  Search ", 11, Theme.Muted)); r1.Children.Add(_search);
        r1.Children.Add(Ui.Text("  Trader ", 11, Theme.Muted)); r1.Children.Add(_trader);
        r1.Children.Add(Ui.Text("  Show ", 11, Theme.Muted)); r1.Children.Add(_filter);
        r1.Children.Add(_thisMap);
        head.Children.Add(r1);
        // map selection: what's drawn on the map is exactly the ticked quests (nothing ticked = no quest pins)
        var pins = new WrapPanel { Margin = new Thickness(0, 6, 0, 2) };
        pins.Children.Add(Ui.Text("On the map: ", 11.5, Theme.Muted));
        pins.Children.Add(Ui.Button("Deselect all", (_, _) => Bulk(p => QuestPins.DeselectAll(p), "Removed {0} quest(s) from the map (all traders, all maps)."), "Take every quest off the map, all traders and maps. Quest progress isn't changed."));
        pins.Children.Add(Ui.Button("Deselect shown", (_, _) => Bulk(p => QuestPins.DeselectShown(_feed!, p, CurrentFilter()), "Removed {0} shown quest(s) from the map."), "Only the quests in the list below (current trader, search, map filter)"));
        pins.Children.Add(Ui.Button("Select shown", (_, _) => SelectShown(), "Put the quests in the list below on the map (done/failed ones are skipped)"));
        pins.Children.Add(new Border { Width = 12 });
        pins.Children.Add(_counts);
        head.Children.Add(pins);
        head.Children.Add(_summary);
        var headPanel = Ui.Panel(head);
        DockPanel.SetDock(headPanel, Dock.Top);
        root.Children.Add(headPanel);

        // bottom: actions, import, TarkovTracker
        var bottom = new StackPanel();
        var actions = new WrapPanel();
        actions.Children.Add(Ui.Button("I'm on this quest", (_, _) => MarkBefore(), "Marks every quest needed before the selected one as done, and the selected one as active. Fastest way to set up."));
        actions.Children.Add(Ui.Button("Mark active", (_, _) => SetSelected(QuestState.Active)));
        actions.Children.Add(Ui.Button("Mark done", (_, _) => SetSelected(QuestState.Complete)));
        actions.Children.Add(Ui.Button("Mark failed", (_, _) => SetSelected(QuestState.Failed)));
        actions.Children.Add(Ui.Button("Reset", (_, _) => SetSelected(QuestState.None), "Back to not started"));
        actions.Children.Add(Ui.Button("Show / hide on map", (_, _) => TogglePinned(), "Tick or untick the selected rows' map checkbox"));
        actions.Children.Add(new Border { Width = 16 });
        actions.Children.Add(Ui.Button("Import from game logs", async (_, _) => await ImportAsync(), "Reads quest messages from the game's older log folders (Tarkov keeps the recent ones)"));
        bottom.Children.Add(actions);
        bottom.Children.Add(_status);
        bottom.Children.Add(BuildTrackerBox());
        var bottomPanel = Ui.Panel(bottom);
        DockPanel.SetDock(bottomPanel, Dock.Bottom);
        root.Children.Add(bottomPanel);

        var grid = new GridView();
        var pinCell = new FrameworkElementFactory(typeof(CheckBox));
        pinCell.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(QuestRow.OnMap)) { Mode = BindingMode.OneWay });
        pinCell.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(QuestRow.CanPin)) { Mode = BindingMode.OneWay });
        pinCell.SetValue(FrameworkElement.ToolTipProperty, "Show this quest's spots on the map");
        pinCell.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((sender, _) => { if (sender is CheckBox { DataContext: QuestRow r }) Pin(new[] { r }, null); }));
        grid.Columns.Add(new GridViewColumn { Header = "Map", Width = 44, CellTemplate = new DataTemplate { VisualTree = pinCell } });
        grid.Columns.Add(Col("Quest", nameof(QuestRow.Name), 300));
        grid.Columns.Add(Col("Trader", nameof(QuestRow.Trader), 100));
        grid.Columns.Add(Col("Lvl", nameof(QuestRow.Level), 40));
        grid.Columns.Add(Col("Map", nameof(QuestRow.Maps), 170));
        grid.Columns.Add(Col("Status", nameof(QuestRow.Status), 110));
        grid.Columns.Add(Col("Kappa", nameof(QuestRow.Kappa), 50));
        _list.View = grid;
        _list.MouseDoubleClick += (_, e) => { if (_list.SelectedItem is QuestRow q) Toggle(q); };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Space && _list.SelectedItem is QuestRow q) { Toggle(q); e.Handled = true; } };
        root.Children.Add(Ui.Panel(_list));
        Content = root;

        _mode.SelectionChanged += async (_, _) => await LoadAsync();
        _search.TextChanged += (_, _) => Refresh();
        _trader.SelectionChanged += (_, _) => Refresh();
        _filter.SelectionChanged += (_, _) => Refresh();
        _thisMap.Checked += (_, _) => Refresh();
        _thisMap.Unchecked += (_, _) => Refresh();
        f.MapShown += OnMapShown;
        Closed += (_, _) => f.MapShown -= OnMapShown;
        _faction.SelectionChanged += (_, _) => { if (_loading) return; P.Faction = _faction.SelectedItem as string ?? "Any"; _f.Progress.NotifyChanged(ModeId); };
        _level.LostFocus += (_, _) => SaveLevel();
        _level.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveLevel(); };
        f.ProgressChanged += OnProgressChanged;
        Closed += (_, _) => f.ProgressChanged -= OnProgressChanged;
        Loaded += async (_, _) => await LoadAsync();
    }

    private static GridViewColumn Col(string header, string path, double w) => new() { Header = header, Width = w, DisplayMemberBinding = new Binding(path) };

    private string ModeId => (_mode.SelectedItem as GameMode)?.Id ?? _f.ModeId;
    private ModeProgress P => _f.Progress.For(ModeId);

    private void OnProgressChanged(string mode) { if (string.Equals(mode, ModeId, StringComparison.OrdinalIgnoreCase)) Refresh(); }

    private async Task LoadAsync()
    {
        _status.Text = "Loading quests from tarkov.dev…";
        var mode = ModeId;
        _feed = string.Equals(_f.TaskFeed?.ModeId, mode, StringComparison.OrdinalIgnoreCase) ? _f.TaskFeed : null;
        if (_feed == null)
        {
            if (string.Equals(mode, _f.ModeId, StringComparison.OrdinalIgnoreCase)) { await _f.EnsureFeedsAsync(quests: true); _feed = _f.TaskFeed; }
            else
            {
                try { _feed = await _f.Data.GetTaskFeedAsync(mode, CancellationToken.None); }
                catch (OperationCanceledException) { }
            }
        }
        if (mode != ModeId) return;
        if (_feed == null) { _status.Text = "Couldn't load the quest list: " + (_f.Data.LastProblem ?? "no connection") + ". Try again later."; return; }
        _loading = true;
        var p = P;
        _level.Text = p.PlayerLevel?.ToString() ?? "";
        _faction.SelectedItem = p.Faction is "BEAR" or "USEC" ? p.Faction : "Any";
        var current = _trader.SelectedItem as string;
        _trader.Items.Clear();
        _trader.Items.Add("All traders");
        foreach (var t in _feed.Tasks.Select(t => t.TraderName).Distinct().OrderBy(n => n)) _trader.Items.Add(t);
        _trader.SelectedItem = current != null && _trader.Items.Contains(current) ? current : "All traders";
        _loading = false;
        _status.Text = $"{_feed.Tasks.Count} quests ({GameMode.FromId(mode).DisplayName}, tarkov.dev data from {_feed.FetchedAt.ToLocalTime():MMM d HH:mm}). Double-click or Space toggles done.";
        Refresh();
    }

    private void SaveLevel()
    {
        var p = P;
        int? lvl = int.TryParse(_level.Text.Trim(), out var v) && v is > 0 and < 100 ? v : null;
        if (lvl == p.PlayerLevel) return;
        p.PlayerLevel = lvl;
        _f.Progress.NotifyChanged(ModeId);
    }

    private void OnMapShown() => Dispatcher.BeginInvoke(() => { UpdateThisMapLabel(); if (_thisMap.IsChecked == true) Refresh(); else UpdateCounts(); });

    private void UpdateThisMapLabel()
    {
        var name = _f.CurrentMapName;
        _thisMap.Content = name != null ? $"{name} only" : "This map only (no map shown)";
        _thisMap.IsEnabled = name != null;
        if (name == null) _thisMap.IsChecked = false;
    }

    private QuestListFilter CurrentFilter() => new(
        _filter.SelectedItem as string ?? "Not done", _trader.SelectedItem as string, _search.Text,
        _thisMap.IsChecked == true ? _f.CurrentMapIds : null);

    private void UpdateCounts()
    {
        if (_feed == null) return;
        var p = P;
        var (total, here) = QuestPins.Counts(_feed, p, _f.CurrentMapIds);
        int active = p.States.Count(kv => kv.Value == QuestState.Active && _feed.Get(kv.Key) != null);
        _counts.Text = $"On map: {total}" + (_f.CurrentMapName is { } m ? $" ({here} on {m})" : "") + $" · Active: {active}" +
            (_f.Settings.ShowAllAvailableQuests ? " · browse mode is on (map › More ▾)" : "");
    }

    private void Refresh()
    {
        if (_feed == null || _loading) return;
        var p = P;
        UpdateThisMapLabel();
        var maps = _f.MapFeed;
        var keep = _list.SelectedItems.OfType<QuestRow>().Select(r => r.Id).ToHashSet();
        var rows = new List<QuestRow>();
        int done = 0, active = 0, avail = 0;
        foreach (var t in _feed.Tasks)
        {
            var a = QuestLogic.Availability(t, _feed, p);
            if (a == QuestAvailability.Complete) done++;
            else if (a == QuestAvailability.Active) active++;
            else if (a == QuestAvailability.Available) avail++;
        }
        foreach (var t in QuestPins.Filter(_feed, p, CurrentFilter()))
        {
            var a = QuestLogic.Availability(t, _feed, p);
            var mapNames = t.MapIds.Select(id => maps?.ById(id)?.Name).Where(n => n != null).Distinct().ToList();
            string status = a switch
            {
                QuestAvailability.Complete => "Done", QuestAvailability.Active => "Active", QuestAvailability.Failed => "Failed",
                QuestAvailability.Available => "Available", _ => t.MinPlayerLevel > (p.PlayerLevel ?? 0) && p.PlayerLevel != null && QuestLogic.PrerequisitesMet(t, _feed, p) ? $"Locked (lvl {t.MinPlayerLevel})" : "Locked",
            };
            bool open = a is not (QuestAvailability.Complete or QuestAvailability.Failed);
            rows.Add(new QuestRow(t.Id, t.Name, t.TraderName, t.MinPlayerLevel, mapNames.Count == 0 ? (t.MapIds.Any() ? "" : "any") : string.Join(", ", mapNames!),
                status, t.Kappa ? "yes" : "", open && p.Pinned.Contains(t.Id), open));
        }
        rows = rows.OrderBy(r => r.Trader).ThenBy(r => r.Level).ThenBy(r => r.Name).ToList();
        _list.ItemsSource = rows;
        foreach (var r in rows.Where(r => keep.Contains(r.Id))) _list.SelectedItems.Add(r);
        if (_list.SelectedItem is QuestRow sel) _list.ScrollIntoView(sel);
        else if (rows.Count > 0) _list.ScrollIntoView(rows[0]);
        var sync = p.LastTrackerSync is { } ls ? $" · TarkovTracker sync {ls.ToLocalTime():MMM d HH:mm}" : "";
        var log = p.LastLogEvent is { } le ? $" · last update from game log {le.ToLocalTime():MMM d HH:mm}" : " · no quest messages read from the game yet";
        _summary.Text = $"{GameMode.FromId(ModeId).DisplayName}: {done} done, {active} active, {avail} available to accept{log}{sync}. Showing {rows.Count}. Tick \"Map\" for the quests you're running; only those are drawn.";
        UpdateCounts();
    }

    private void Bulk(Func<ModeProgress, int> op, string message)
    {
        if (_feed == null) return;
        int n = op(P);
        _f.Progress.NotifyChanged(ModeId); // saves, redraws the map, refreshes this list
        _status.Text = string.Format(message, n);
    }

    private void SelectShown()
    {
        if (_feed == null) return;
        var count = QuestPins.Filter(_feed, P, CurrentFilter()).Count;
        if (count > 15 && MessageBox.Show(this, $"Put all {count} quests in the list on the map?\n\nTip: tick \"{(_f.CurrentMapName ?? "This map")} only\" or pick a trader first.",
                "Select shown", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Bulk(p => QuestPins.SelectShown(_feed!, p, CurrentFilter()), "Put {0} quest(s) on the map.");
    }

    private void Pin(IEnumerable<QuestRow> rows, bool? on)
    {
        var p = P;
        int n = 0;
        foreach (var r in rows.Where(r => r.CanPin))
        {
            bool want = on ?? !p.Pinned.Contains(r.Id);
            if (want ? p.Pinned.Add(r.Id) : p.Pinned.Remove(r.Id)) n++;
        }
        if (n > 0) _f.Progress.NotifyChanged(ModeId);
    }

    private IEnumerable<QuestRow> Selected => _list.SelectedItems.OfType<QuestRow>().ToList();

    private void SetSelected(QuestState s)
    {
        var sel = Selected.ToList();
        if (sel.Count == 0) { _status.Text = "Select one or more quests first."; return; }
        var p = P;
        foreach (var r in sel) p.Set(r.Id, s);
        _f.Progress.NotifyChanged(ModeId);
        _status.Text = $"{sel.Count} quest(s) set to {(s == QuestState.None ? "not started" : s.ToString().ToLowerInvariant())}.";
    }

    private void Toggle(QuestRow r)
    {
        var p = P;
        p.Set(r.Id, p.Get(r.Id) == QuestState.Complete ? QuestState.None : QuestState.Complete);
        _f.Progress.NotifyChanged(ModeId);
    }

    private void MarkBefore()
    {
        if (_feed == null || _list.SelectedItem is not QuestRow r) { _status.Text = "Select the quest you're currently on first."; return; }
        var before = QuestLogic.PrerequisiteClosure(r.Id, _feed);
        if (MessageBox.Show(this, $"Mark {before.Count} quest(s) needed before \"{r.Name}\" as done, and \"{r.Name}\" as active?\n\nOther quest lines aren't changed; do this once for each trader line you're on.",
                "I'm on this quest", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int n = QuestLogic.MarkEverythingBefore(r.Id, _feed, P);
        _f.Progress.NotifyChanged(ModeId);
        _status.Text = $"Updated {n} quest(s) for the \"{r.Name}\" line.";
    }

    private void TogglePinned()
    {
        var sel = Selected.ToList();
        if (sel.Count == 0) { _status.Text = "Select one or more quests first."; return; }
        bool anyOff = sel.Any(r => r.CanPin && !r.OnMap);
        Pin(sel, anyOff);
    }

    private async Task ImportAsync()
    {
        _status.Text = "Reading the game's log folders…";
        _status.Text = await _f.ImportQuestHistoryAsync(s => _status.Text = s);
    }

    private UIElement BuildTrackerBox()
    {
        var s = _f.Settings;
        var box = new StackPanel();
        box.Children.Add(Ui.Text("Not needed: the built-in tracker above works on its own. Link TarkovTracker only if you already use it (e.g. on your phone or with friends). Read-only: the app never changes your TarkovTracker data. The token is stored only on this PC (%LOCALAPPDATA%\\TarkovPriceOverlay\\maps\\map-settings.json).", 11, Theme.Muted, wrap: true));
        var domain = new ComboBox { Width = 150, Margin = new Thickness(2) };
        foreach (var d in TarkovOverlay.Maps.Quests.TarkovTracker.Domains) domain.Items.Add(d);
        domain.SelectedItem = s.TrackerDomain;
        var token = new PasswordBox { Width = 260, Margin = new Thickness(2), Password = s.TrackerToken ?? "", Background = Theme.Panel, Foreground = Theme.Text, ToolTip = "API token with the \"Get progression\" permission (Settings → API tokens on the site)" };
        var auto = new CheckBox { Content = "Sync when the quest data loads", IsChecked = s.TrackerAutoSync, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var result = Ui.Text(_f.TrackerStatus ?? "", 11, Theme.Muted, wrap: true);
        void SaveTracker()
        {
            s.TrackerDomain = domain.SelectedItem as string ?? "tarkovtracker.org";
            s.TrackerToken = string.IsNullOrWhiteSpace(token.Password) ? null : token.Password.Trim();
            s.TrackerAutoSync = auto.IsChecked == true;
            _f.SaveNow();
        }
        var row = new WrapPanel();
        row.Children.Add(Ui.Text("Site ", 11, Theme.Muted)); row.Children.Add(domain);
        row.Children.Add(Ui.Text("  Token ", 11, Theme.Muted)); row.Children.Add(token);
        row.Children.Add(auto);
        row.Children.Add(Ui.Button("Save & sync now", async (_, _) => { SaveTracker(); result.Text = "Syncing…"; result.Text = await _f.SyncTrackerAsync(); }));
        row.Children.Add(Ui.Button("Remove token", (_, _) => { token.Password = ""; SaveTracker(); result.Text = "Token removed from this PC."; }));
        box.Children.Add(row);
        box.Children.Add(result);
        return new Expander { Header = Ui.Text("Optional: TarkovTracker sync", 11, Theme.Muted), Foreground = Theme.Text, Content = box, Margin = new Thickness(0, 6, 0, 0) };
    }
}
