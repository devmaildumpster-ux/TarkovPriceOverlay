using System.Windows;
using System.Windows.Controls;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Services;
using TarkovOverlay.Core.Catalog;

namespace TarkovOverlay.App.Views;

/// <summary>
/// 1.2.0 hideout tracker: the built level of every station, per game mode (hideout-progress.json). Price cards
/// hide hideout needs for levels you've already built (Settings… › "Filter needs by my quest &amp; hideout progress").
/// </summary>
public sealed class HideoutWindow : Window
{
    private static HideoutWindow? _current;
    private readonly AppServices _s;
    private readonly ComboBox _mode = new() { Width = 150, Margin = new Thickness(0, 0, 8, 0) };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _summary = Ui.Text("", 11.5, Theme.Muted, wrap: true);

    public static HideoutWindow Open(AppServices s, Window? owner)
    {
        if (_current is { IsLoaded: true } w) { if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal; w.Activate(); return w; }
        w = new HideoutWindow(s);
        if (owner is { IsLoaded: true }) w.Owner = owner;
        _current = w;
        w.Closed += (_, _) => { if (_current == w) _current = null; };
        w.Show();
        return w;
    }

    public HideoutWindow(AppServices s)
    {
        _s = s;
        Title = "Hideout — Tarkov Price Overlay";
        Background = Theme.Window; Foreground = Theme.Text; FontFamily = Theme.Font;
        Width = 560; Height = 720; MinWidth = 420; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var m in GameMode.All) _mode.Items.Add(m);
        _mode.SelectedItem = GameMode.FromId(s.Settings.ModeId);
        _mode.SelectionChanged += (_, _) => Refresh();

        var top = new StackPanel { Margin = new Thickness(10, 10, 10, 4) };
        top.Children.Add(Ui.Text("Your hideout: set each station to the level you've built.", 13, Theme.Text, FontWeights.SemiBold));
        top.Children.Add(Ui.Text("Price cards then skip hideout needs you've already built (and quests marked done in Quests…). Saved per game mode.", 11, Theme.Muted, wrap: true));
        top.Children.Add(Ui.Row(Ui.Text("Mode ", 12, Theme.Muted), _mode,
            Ui.Button("Reset this mode", (_, _) =>
            {
                if (MessageBox.Show(this, $"Set every station to \"not built\" for {Mode.DisplayName}?", Title, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                { _s.Hideout.Reset(Mode.Id); Refresh(); }
            })));
        top.Children.Add(_summary);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new Border
        {
            BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1), Background = Theme.Panel, Margin = new Thickness(10, 4, 10, 10),
            Child = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) },
        });
        Content = root;
        Refresh();
    }

    private GameMode Mode => _mode.SelectedItem as GameMode ?? GameMode.FromId(_s.Settings.ModeId);

    private void Refresh()
    {
        _rows.Children.Clear();
        var ctx = _s.Catalog.Context;
        var stations = ctx?.Snapshot.Hideout;
        if (stations == null || stations.Count == 0)
        {
            _rows.Children.Add(Ui.Text("Hideout data isn't loaded yet (prices still downloading).", 12, Theme.Warn, wrap: true));
            return;
        }
        var mode = Mode.Id;
        int built = 0, total = 0;
        foreach (var st in stations.Where(x => x.Levels.Count > 0).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            int max = st.Levels.Max(l => l.Level);
            int cur = Math.Min(_s.Hideout.Level(mode, st.Id), max);
            built += cur; total += max;
            var combo = new ComboBox { Width = 120, Margin = new Thickness(0, 2, 8, 2) };
            combo.Items.Add("Not built");
            for (int l = 1; l <= max; l++) combo.Items.Add($"Level {l}{(l == max ? " (max)" : "")}");
            combo.SelectedIndex = cur;
            var next = st.Levels.FirstOrDefault(l => l.Level == cur + 1);
            var nextText = Ui.Text(NextText(ctx!, next), 10.5, Theme.Muted, wrap: true);
            var stationId = st.Id;
            combo.SelectionChanged += (_, _) =>
            {
                _s.Hideout.SetLevel(mode, stationId, combo.SelectedIndex, max);
                Log.Write($"hideout: {st.Name} = {combo.SelectedIndex} ({mode})");
                nextText.Text = NextText(ctx!, st.Levels.FirstOrDefault(l => l.Level == combo.SelectedIndex + 1));
                UpdateSummary();
            };
            var g = new Grid { Margin = new Thickness(0, 2, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel();
            name.Children.Add(Ui.Text(st.Name, 12.5, Theme.Text, FontWeights.SemiBold));
            name.Children.Add(nextText);
            g.Children.Add(name);
            Grid.SetColumn(combo, 1);
            g.Children.Add(combo);
            _rows.Children.Add(g);
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var stations = _s.Catalog.Context?.Snapshot.Hideout;
        if (stations == null) return;
        int built = stations.Sum(st => Math.Min(_s.Hideout.Level(Mode.Id, st.Id), st.Levels.Count == 0 ? 0 : st.Levels.Max(l => l.Level)));
        int total = stations.Sum(st => st.Levels.Count == 0 ? 0 : st.Levels.Max(l => l.Level));
        _summary.Text = $"{Mode.DisplayName}: {built} of {total} station levels built.";
    }

    private static string NextText(CatalogContext ctx, HideoutLevel? next)
    {
        if (next == null) return "Fully built";
        var items = next.Items.Select(i => $"{i.Count}× {ctx.Snapshot.Item(i.ItemId)?.ShortName ?? ctx.Snapshot.Item(i.ItemId)?.Name ?? "?"}{(i.FoundInRaid ? " (FIR)" : "")}").ToList();
        return $"Next: level {next.Level}" + (items.Count > 0 ? " · " + string.Join(", ", items.Take(6)) + (items.Count > 6 ? $" +{items.Count - 6}" : "") : "");
    }
}
