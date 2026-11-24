using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TarkovOverlay.App.Native;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Services;
using TarkovOverlay.Core.Help;

namespace TarkovOverlay.App.Views;

/// <summary>
/// 1.2.3: "Controls &amp; hotkeys" — every key, mouse control and button (ControlsHelp, the same source as CONTROLS.txt),
/// with the user's CURRENT shortcuts. Normal window; opened from "? Controls", More ▾, the tray menu and Settings…,
/// and once on first launch (without activation, so it never takes the keyboard from the game).
/// </summary>
public sealed class ControlsWindow : Window
{
    private static ControlsWindow? _current;
    /// <summary>Set by MainWindow so the help can show which price shortcut Windows gave us.</summary>
    internal static HotkeyManager? MainHotkey { get; set; }

    private readonly AppServices _s;
    private readonly StackPanel _body = new();

    public static ControlsWindow Open(AppServices s, Window? owner, bool activate = true)
    {
        if (_current is { IsLoaded: true } w)
        {
            w.Refresh();
            if (activate) { if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal; w.Activate(); }
            return w;
        }
        w = new ControlsWindow(s) { ShowActivated = activate };
        if (owner is { IsLoaded: true }) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        _current = w;
        w.Closed += (_, _) => { if (_current == w) _current = null; };
        w.Show();
        Log.Write($"help: controls window opened{(activate ? "" : " (first launch, not activated)")}");
        return w;
    }

    public ControlsWindow(AppServices s)
    {
        _s = s;
        Title = ControlsHelp.Title + " — Tarkov Price Overlay";
        Width = 780; Height = 820; MinWidth = 560; MinHeight = 400;
        Background = Theme.Window; Foreground = Theme.Text; FontFamily = Theme.Font;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel();
        var top = new StackPanel { Margin = new Thickness(16, 18, 16, 8) };
        top.Children.Add(Ui.Text(ControlsHelp.Title, 18, Theme.Text, FontWeights.SemiBold));
        top.Children.Add(Ui.Text("Your current keys are shown in gold. " + ControlsHelp.ChangeNote +
            " Run Tarkov in Borderless or Windowed mode so cards can draw over the game.", 11.5, Theme.Muted, wrap: true));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var bottom = Ui.Row(
            Ui.Button("Open Settings…", (_, _) => SettingsWindow.Open(SettingsTab.Overlay, Owner ?? this), "Change or clear shortcuts"),
            Ui.Button("Close", (_, _) => Close()));
        bottom.HorizontalAlignment = HorizontalAlignment.Right;
        bottom.Margin = new Thickness(12, 6, 12, 10);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new Border
        {
            BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1), Background = Theme.Panel, Margin = new Thickness(12, 4, 12, 0),
            Child = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(14, 6, 14, 10) },
        });
        Content = root;
        Activated += (_, _) => Refresh(); // picks up keys changed in Settings…
        Refresh();
    }

    /// <summary>What the app knows right now (registered price key, map options, Tarkov's screenshot key).</summary>
    internal ControlsLive Live()
    {
        var mf = Map.MapFeature.Instance;
        string? shot = null;
        if (mf?.ScreenshotKeyStatus == "ok" && mf.ScreenshotKeys is { Length: > 0 } k)
            shot = string.Join(" / ", k.Split(" / ").Select(v => string.Join("+", v.Split('+').Select(x => x == "Print" ? "PrtScn" : x))));
        return new ControlsLive(MainHotkey?.Active?.Label, mf?.Settings.MapAutoLayerMode, mf?.Settings.ReadExitListWithOcr, shot);
    }

    internal IReadOnlyList<ControlSection> Sections { get; private set; } = Array.Empty<ControlSection>();

    public void Refresh()
    {
        Sections = ControlsHelp.Build(_s.Settings, Live());
        _body.Children.Clear();
        foreach (var sec in Sections)
        {
            _body.Children.Add(new TextBlock
            {
                Text = sec.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.Blue,
                Margin = new Thickness(0, 12, 0, 4),
            });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int row = 0;
            foreach (var r in sec.Rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                bool hot = r.HotkeyId != null;
                bool unset = r.Keys is ControlsHelp.NotSet or ControlsHelp.Off || r.Keys.StartsWith("(none", StringComparison.Ordinal);
                var key = new Border
                {
                    Background = hot ? new SolidColorBrush(Color.FromRgb(0x2A, 0x24, 0x14)) : Theme.Card,
                    BorderBrush = hot ? Theme.Gold : Theme.CardEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 3, 10, 3),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock
                    {
                        Text = r.Keys, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        FontWeight = hot ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = unset ? Theme.Warn : hot ? Theme.Gold : Theme.Text,
                    },
                };
                var act = new TextBlock { Text = r.Action, FontSize = 12, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 3) };
                Grid.SetRow(key, row); Grid.SetRow(act, row); Grid.SetColumn(act, 1);
                grid.Children.Add(key); grid.Children.Add(act);
                row++;
            }
            _body.Children.Add(grid);
        }
    }

    /// <summary>First launch only: shows the help once, without activating it, and remembers that it did.</summary>
    internal static void ShowOnFirstLaunch(AppServices s, Window owner)
    {
        if (s.Settings.ShownControlsHelp) return;
        s.Settings.ShownControlsHelp = true;
        s.SaveSoon();
        Open(s, owner, activate: false);
    }
}
