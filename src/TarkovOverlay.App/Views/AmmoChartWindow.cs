using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Services;
using TarkovOverlay.Core.Ammo;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Economics;
using TarkovOverlay.Core.Presentation;

namespace TarkovOverlay.App.Views;

/// <summary>
/// 1.3.0 ammo chart: every round of a caliber side by side (pen, damage, armor damage, frag, speed, tracer,
/// this mode's flea / trader prices) with color-coded armor classes 1–6. Opened from the Ammo… button, or by the
/// ammo-chart shortcut (default Ctrl+Alt+A) for the caliber you last hovered — that path doesn't steal focus from the game.
/// </summary>
public sealed class AmmoChartWindow : Window
{
    private static AmmoChartWindow? _current;
    private readonly AppServices _s;
    private readonly ComboBox _caliber = new() { MinWidth = 200, Margin = new Thickness(4, 0, 8, 0) };
    private readonly TextBox _search = new()
    {
        Width = 180, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(4, 2, 4, 2),
        FontSize = 13, Background = Theme.Window, Foreground = Theme.Text, BorderBrush = Theme.CardEdge, CaretBrush = Theme.Text,
    };
    private readonly CheckBox _tracers = new() { Content = "Tracers only", Foreground = Theme.Text, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _traders = new() { Content = "Sold by traders", Foreground = Theme.Text, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _sort = new() { MinWidth = 140, Margin = new Thickness(4, 0, 4, 0) };
    private readonly CheckBox _desc = new() { Content = "High to low", IsChecked = true, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Ui.Text("", 11, Theme.Muted, wrap: true);
    private readonly StackPanel _header = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private string? _highlightId;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _wired;

    private static readonly (string Label, double Width, AmmoSort? Sort)[] Cols =
    {
        ("Round", 168, AmmoSort.Name), ("Dmg", 56, AmmoSort.Damage), ("Pen", 44, AmmoSort.Penetration),
        ("Armor", 56, AmmoSort.ArmorDamage), ("Frag", 48, AmmoSort.FragChance), ("Speed", 78, AmmoSort.Speed),
        ("Tracer", 88, null), ("Flea", 84, AmmoSort.FleaPrice), ("Trader", 168, AmmoSort.TraderPrice),
    };

    public static AmmoChartWindow Open(AppServices s, Window? owner = null, string? caliber = null, string? highlightId = null, bool fromHotkey = false)
    {
        if (_current is { IsLoaded: true } w)
        {
            w.Select(caliber, highlightId);
            if (!fromHotkey)
            {
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
            }
            else { w.Topmost = true; w.ShowActivated = false; }
            return w;
        }
        w = new AmmoChartWindow(s, caliber, highlightId)
        {
            Owner = fromHotkey ? null : owner,
            ShowActivated = !fromHotkey,
            WindowStartupLocation = fromHotkey ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        if (fromHotkey) { w.Topmost = true; w.ShowInTaskbar = false; }
        w.Closed += (_, _) => { if (ReferenceEquals(_current, w)) _current = null; };
        _current = w;
        w.Show();
        return w;
    }

    private AmmoChartWindow(AppServices s, string? caliber, string? highlightId)
    {
        _s = s; _highlightId = highlightId;
        Title = "Ammo chart";
        Width = 1120; Height = 640; MinWidth = 860; MinHeight = 420;
        Background = Theme.Window; Foreground = Theme.Text; FontFamily = Theme.Font;
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Theme.Window };
        Content = Build();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Loaded += (_, _) =>
        {
            if (_wired) return;
            _wired = true;
            s.Catalog.ContextChanged += OnCatalog;
            s.Catalog.StateChanged += OnState;
        };
        Closed += (_, _) =>
        {
            s.Catalog.ContextChanged -= OnCatalog;
            s.Catalog.StateChanged -= OnState;
        };
        FillCalibers(caliber);
        Rebuild();
    }

    private void OnCatalog(CatalogContext _) => Dispatcher.BeginInvoke(Rebuild);
    private void OnState(DataState _) => Dispatcher.BeginInvoke(UpdateStatus);

    public void Select(string? caliber, string? highlightId)
    {
        _highlightId = highlightId;
        if (caliber != null) PickCaliber(caliber);
        Rebuild();
    }

    private UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(10) };
        var top = new StackPanel();
        var row1 = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        row1.Children.Add(Ui.Text("Caliber", 12, Theme.Muted));
        row1.Children.Add(_caliber);
        row1.Children.Add(Ui.Text("Find", 12, Theme.Muted));
        row1.Children.Add(_search);
        row1.Children.Add(_tracers);
        row1.Children.Add(_traders);
        row1.Children.Add(Ui.Text("Sort", 12, Theme.Muted));
        foreach (var (label, sort) in new (string, AmmoSort)[]
        {
            ("Penetration", AmmoSort.Penetration), ("Damage", AmmoSort.Damage), ("Armor dmg", AmmoSort.ArmorDamage),
            ("Frag", AmmoSort.FragChance), ("Speed", AmmoSort.Speed), ("Flea price", AmmoSort.FleaPrice),
            ("Trader price", AmmoSort.TraderPrice), ("Name", AmmoSort.Name),
        }) _sort.Items.Add(new ComboBoxItem { Content = label, Tag = sort, IsSelected = sort == AmmoSort.Penetration });
        row1.Children.Add(_sort);
        row1.Children.Add(_desc);
        top.Children.Add(row1);
        top.Children.Add(_status);
        top.Children.Add(Legend());
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var table = new StackPanel();
        table.Children.Add(_header);
        table.Children.Add(_rows);
        _scroll.Content = table;
        root.Children.Add(_scroll);

        _caliber.SelectionChanged += (_, _) => Rebuild();
        _sort.SelectionChanged += (_, _) => Rebuild();
        _desc.Checked += (_, _) => Rebuild();
        _desc.Unchecked += (_, _) => Rebuild();
        _tracers.Checked += (_, _) => Rebuild();
        _tracers.Unchecked += (_, _) => Rebuild();
        _traders.Checked += (_, _) => Rebuild();
        _traders.Unchecked += (_, _) => Rebuild();
        _search.TextChanged += (_, _) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); Rebuild(); };
        return root;
    }

    private static UIElement Legend()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        row.Children.Add(Ui.Text("vs armor class  ", 11, Theme.Muted));
        foreach (var (lv, word) in new (int, string)[] { (4, "usually"), (3, "often"), (2, "50/50"), (1, "rarely"), (0, "almost never") })
        {
            row.Children.Add(new Border
            {
                Width = 12, Height = 12, Background = Theme.ForArmor(lv), CornerRadius = new CornerRadius(2),
                Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(Ui.Text(word, 11, Theme.Muted));
        }
        row.Children.Add(Ui.Text("   ·   compare penetration to 10 × class (a glance scale, not an in-raid roll)", 11, Theme.Muted));
        return row;
    }

    private void FillCalibers(string? prefer)
    {
        var snap = _s.Catalog.Context?.Snapshot;
        _caliber.Items.Clear();
        if (snap == null)
        {
            _caliber.Items.Add(new ComboBoxItem { Content = "(prices not loaded yet)", Tag = null, IsSelected = true });
            return;
        }
        var list = AmmoChart.Calibers(snap);
        if (list.Count == 0)
        {
            _caliber.Items.Add(new ComboBoxItem { Content = "(ammo stats appear after the next price refresh)", Tag = null, IsSelected = true });
            return;
        }
        foreach (var (id, display, count) in list)
        {
            _caliber.Items.Add(new ComboBoxItem
            {
                Content = $"{display}  ({count})",
                Tag = id,
                IsSelected = prefer != null && string.Equals(id, prefer, StringComparison.Ordinal),
            });
        }
        if (_caliber.SelectedIndex < 0) _caliber.SelectedIndex = 0;
    }

    private void PickCaliber(string id)
    {
        if (_caliber.Items.Count == 0) FillCalibers(id);
        foreach (ComboBoxItem it in _caliber.Items)
            if (it.Tag is string t && t == id) { _caliber.SelectedItem = it; return; }
    }

    private string? SelectedCaliber => (_caliber.SelectedItem as ComboBoxItem)?.Tag as string;
    private AmmoSort SelectedSort => (_sort.SelectedItem as ComboBoxItem)?.Tag is AmmoSort s ? s : AmmoSort.Penetration;

    private void Rebuild()
    {
        var ctx = _s.Catalog.Context;
        _header.Children.Clear();
        _rows.Children.Clear();
        if (ctx == null)
        {
            _rows.Children.Add(Ui.Text("No price catalog yet. It loads when the app starts, or press Refresh prices.", 12, Theme.Muted, wrap: true));
            UpdateStatus();
            return;
        }
        if (_caliber.Items.Count == 0 || SelectedCaliber == null && AmmoChart.Calibers(ctx.Snapshot).Count > 0)
            FillCalibers(SelectedCaliber);
        var rows = AmmoChart.Rows(ctx.Snapshot, SelectedCaliber, _search.Text, _tracers.IsChecked == true, _traders.IsChecked == true, SelectedSort, _desc.IsChecked != false);
        _header.Children.Add(HeaderRow());
        foreach (var r in rows) _rows.Children.Add(BodyRow(r, r.Item.Id == _highlightId));
        if (rows.Count == 0)
            _rows.Children.Add(Ui.Text(SelectedCaliber == null ? "No ammo stats in the current catalog. They load with the next price refresh (tarkov.dev)." : "No rounds match the filters.", 12, Theme.Muted, wrap: true));
        UpdateStatus();
        if (_highlightId != null)
        {
            var hit = _rows.Children.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag as string == _highlightId);
            if (hit != null) Dispatcher.BeginInvoke(() => hit.BringIntoView(), DispatcherPriority.Loaded);
        }
    }

    private void UpdateStatus()
    {
        var ctx = _s.Catalog.Context;
        var st = _s.Catalog.State;
        var mode = _s.Catalog.Mode.DisplayName;
        var n = _rows.Children.OfType<FrameworkElement>().Count(e => e.Tag is string);
        var notice = PriceStatus.CardNote(_s.Availability.State, ctx?.Snapshot.FetchedAt, _s.NewestScan(ctx), DateTimeOffset.UtcNow);
        var bits = new List<string> { $"{n} round{(n == 1 ? "" : "s")}", mode + " prices", st.Describe(DateTimeOffset.UtcNow) };
        if (notice != null) bits.Add(notice);
        _status.Text = string.Join(" · ", bits);
        _status.Foreground = notice != null || st.Kind is DataStateKind.None ? Theme.Warn : Theme.Muted;
    }

    private UIElement HeaderRow()
    {
        var g = NewGrid();
        int col = 0;
        foreach (var (label, width, sort) in Cols)
        {
            var t = Ui.Text(label, 11, Theme.Muted, FontWeights.SemiBold);
            t.TextAlignment = col == 0 ? TextAlignment.Left : TextAlignment.Right;
            if (sort is { } so)
            {
                t.Cursor = Cursors.Hand;
                t.ToolTip = "Sort by " + label.ToLowerInvariant();
                var captured = so;
                t.MouseLeftButtonUp += (_, _) =>
                {
                    if (SelectedSort == captured) _desc.IsChecked = _desc.IsChecked != true;
                    else
                    {
                        foreach (ComboBoxItem it in _sort.Items)
                            if (it.Tag is AmmoSort x && x == captured) { _sort.SelectedItem = it; break; }
                        _desc.IsChecked = captured != AmmoSort.Name;
                    }
                };
            }
            SetCell(g, t, col++);
        }
        for (int i = 1; i <= 6; i++)
        {
            var t = Ui.Text(i.ToString(), 11, Theme.Muted, FontWeights.SemiBold);
            t.TextAlignment = TextAlignment.Center;
            t.ToolTip = "Armor class " + i;
            SetCell(g, t, col++);
        }
        return new Border { Child = g, BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(4, 4, 4, 6), Background = Theme.Panel };
    }

    private UIElement BodyRow(AmmoRow r, bool highlight)
    {
        var g = NewGrid();
        int col = 0;
        var name = Ui.Text(r.DisplayName, 12, highlight ? Theme.Gold : Theme.Text, highlight ? FontWeights.SemiBold : FontWeights.Normal);
        name.ToolTip = r.Item.Name;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        SetCell(g, name, col++);
        SetCell(g, Num(r.DamageText), col++);
        SetCell(g, Num(r.PenText, Theme.Text, FontWeights.SemiBold), col++);
        SetCell(g, Num(r.ArmorDmgText), col++);
        SetCell(g, Num(r.FragText), col++);
        SetCell(g, Num(r.SpeedText), col++);
        SetCell(g, Num(r.TracerText, r.Stats.IsTracer ? Theme.Gold : Theme.Muted, FontWeights.Normal, left: false), col++);
        SetCell(g, Num(AmmoChart.FormatFlea(r)), col++);
        var trader = Num(AmmoChart.FormatTrader(r), r.Trader != null ? Theme.Text : Theme.Muted, FontWeights.Normal, left: false);
        trader.TextAlignment = TextAlignment.Left;
        trader.TextTrimming = TextTrimming.CharacterEllipsis;
        SetCell(g, trader, col++);
        for (int i = 0; i < 6; i++)
        {
            int lv = i < r.Armor.Count ? r.Armor[i] : 0;
            var cell = new Border
            {
                Background = Theme.ForArmor(lv), CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 2, 2, 2), Height = 22,
                ToolTip = $"Class {i + 1}: {ArmorEffect.Word(lv)}",
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(), FontSize = 10, FontWeight = FontWeights.Bold, FontFamily = Theme.Font,
                    Foreground = lv == 0 ? Theme.Muted : Theme.Window, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            SetCell(g, cell, col++);
        }
        var border = new Border
        {
            Child = g, Padding = new Thickness(4, 1, 4, 1), Tag = r.Item.Id,
            Background = highlight ? new SolidColorBrush(Color.FromArgb(0x33, 0xE8, 0xC7, 0x84)) : Brushes.Transparent,
            BorderBrush = highlight ? Theme.Gold : Theme.Separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        border.MouseLeftButtonUp += (_, _) => { _highlightId = r.Item.Id; _s.SetLastItem(r.Item); };
        return border;
    }

    private static Grid NewGrid()
    {
        var g = new Grid();
        foreach (var (_, width, _) in Cols)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        for (int i = 0; i < 6; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        return g;
    }

    private static TextBlock Num(string text, Brush? fg = null, FontWeight? w = null, bool left = false) => new()
    {
        Text = text, FontSize = 12, Foreground = fg ?? Theme.Text, FontWeight = w ?? FontWeights.Normal,
        FontFamily = Theme.Numbers, TextAlignment = left ? TextAlignment.Left : TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 2, 4, 2),
    };

    private static void SetCell(Grid g, UIElement el, int col) { Grid.SetColumn(el, col); g.Children.Add(el); }
}
