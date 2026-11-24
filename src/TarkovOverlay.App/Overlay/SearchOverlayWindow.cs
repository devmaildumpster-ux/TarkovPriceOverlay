using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TarkovOverlay.App.Native;
using TarkovOverlay.App.Services;
using TarkovOverlay.App.Views;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Search;
using static TarkovOverlay.App.Native.NativeMethods;

namespace TarkovOverlay.App.Overlay;

/// <summary>
/// 1.2.0 in-game search (default Alt+`): a small topmost dark bar near the top of Tarkov's monitor. Type a name,
/// Up/Down + Enter for the full price card (with Wiki / tarkov.dev), Esc to close and hand focus back to the game.
/// Clicking anywhere else closes it too. Needs Tarkov in Borderless/Windowed, like the cursor card.
/// </summary>
public sealed class SearchOverlayWindow : Window
{
    private readonly AppServices _s;
    private readonly TextBox _box = new()
    {
        FontSize = 17, Padding = new Thickness(8, 5, 8, 5), Background = Theme.Window, Foreground = Theme.Text, CaretBrush = Theme.Text,
        BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1),
    };
    private readonly ListBox _list = new() { MaxHeight = 380, Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly ContentControl _detail = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _hint = Ui.Text("Type an item name · ↑↓ Enter for the card · Esc to close", 11, Theme.Muted);
    private readonly System.Windows.Threading.DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private IntPtr _previous;
    private bool _closingForGood;

    public SearchOverlayWindow(AppServices s)
    {
        _s = s;
        Title = "Tarkov Price Overlay search";
        // opaque borderless window (no layered/transparent window: simpler focus + IME handling, works under every compositor)
        WindowStyle = WindowStyle.None; AllowsTransparency = false; Background = Theme.Card; BorderBrush = Theme.Gold; BorderThickness = new Thickness(1);
        Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        Width = 600; SizeToContent = SizeToContent.Height; FontFamily = Theme.Font;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _list.Style = Ui.DarkListStyle();
        _list.ItemContainerStyle = RowStyle();

        var p = new StackPanel();
        p.Children.Add(_box);
        p.Children.Add(_hint);
        p.Children.Add(_list);
        p.Children.Add(_detail);
        Content = new Border
        {
            Background = Theme.Card, Padding = new Thickness(10), Child = p,
        };

        _box.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        _box.PreviewKeyDown += OnKey;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Dismiss(restoreFocus: true); } };
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        _s.Catalog.ContextChanged += _ => Dispatcher.BeginInvoke(() => { if (IsVisible && _detail.Content == null) Refresh(); });
        Deactivated += (_, _) => { if (IsVisible) Dismiss(restoreFocus: false); };
        Closing += (_, e) => { if (!_closingForGood) { e.Cancel = true; Dismiss(restoreFocus: false); } };
    }

    /// <summary>Text for tests / the UI preview.</summary>
    public string Query { get => _box.Text; set { _box.Text = value; Refresh(); } }

    public void Toggle()
    {
        if (IsVisible) { Dismiss(restoreFocus: true); return; }
        Open();
    }

    public void Open()
    {
        _previous = GetForegroundWindow();
        _detail.Content = null;
        _list.Visibility = Visibility.Visible;
        Show();
        PlaceOnTarkovMonitor();
        Activate();
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) SetForegroundWindow(h);
        _box.Focus();
        Keyboard.Focus(_box);
        _box.SelectAll();
        Log.Write("search overlay: opened");
    }

    private void PlaceOnTarkovMonitor()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        var (disp, _) = Desktop.TarkovDisplay();
        var b = disp?.Monitor.Bounds ?? Desktop.MonitorAt(Desktop.Cursor().X, Desktop.Cursor().Y).Bounds;
        double scale = disp?.Monitor.DpiScale ?? 1.0;
        int w = (int)Math.Round(Width * scale);
        int x = b.X + (b.Width - w) / 2;
        int y = b.Y + (int)Math.Round(b.Height * 0.08);
        SetWindowPos(h, HWND_TOPMOST, x, y, 0, 0, 0x0001 /* SWP_NOSIZE */ | SWP_SHOWWINDOW);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true; break;
            case Key.Up:
                if (_list.Items.Count > 0) _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true; break;
            case Key.Enter:
                OpenSelected(); e.Handled = true; break;
        }
    }

    private void Refresh()
    {
        _detail.Content = null;
        _list.Visibility = Visibility.Visible;
        _list.Items.Clear();
        var ctx = _s.Catalog.Context;
        var notice = _s.PriceNotice(ctx); // 1.2.5: "tarkov.dev down · prices from 1 h ago"
        if (ctx == null) { _hint.Text = notice != null ? $"{notice} · no saved prices yet" : "Prices are still loading…"; _hint.Foreground = notice != null ? Theme.Warn : Theme.Muted; return; }
        _hint.Foreground = notice != null ? Theme.Gold : Theme.Muted;
        var rows = QuickSearch.Run(ctx, _box.Text, 12);
        _hint.Text = string.IsNullOrWhiteSpace(_box.Text) ? "Type an item name · ↑↓ Enter for the card · Esc to close"
            : rows.Count == 0 ? "No matches" : $"{rows.Count} match{(rows.Count == 1 ? "" : "es")} · {ctx.Mode.DisplayName} · ↑↓ Enter for the card · Esc to close";
        if (notice != null) _hint.Text = "⚠ " + notice + " · " + _hint.Text;
        foreach (var r in rows)
        {
            var g = new Grid { Margin = new Thickness(2, 1, 2, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            left.Children.Add(Ui.Text(r.Name, 13, Theme.Text, FontWeights.SemiBold));
            left.Children.Add(Ui.Text(string.Join(" · ", new[] { r.ShortName, r.Flea, r.Trader }.Where(x => !string.IsNullOrEmpty(x))), 11, Theme.Muted));
            g.Children.Add(left);
            if (r.PerSlot != null)
            {
                var ps = Ui.Text(r.PerSlot, 12.5, r.Tier is { } t ? Theme.ForTier(t) : Theme.Text, FontWeights.Bold);
                ps.VerticalAlignment = VerticalAlignment.Center;
                ps.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(ps, 1);
                g.Children.Add(ps);
            }
            _list.Items.Add(new ListBoxItem { Content = g, Tag = r.Item });
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    /// <summary>Visible result rows as text (previews / tests).</summary>
    public IEnumerable<string> ResultsText => _list.Items.OfType<ListBoxItem>().Select(i => (i.Tag as CatalogItem)?.Name ?? "?");

    /// <summary>Dark rows with a gold-edged selection (the system highlight is white-on-white with our light text).</summary>
    private static Style RowStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2, 0, 0, 0));
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(4, 2, 4, 2));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var t = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Window, "Bd"));
        sel.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Gold, "Bd"));
        t.Triggers.Add(sel);
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Window, "Bd"));
        t.Triggers.Add(hover);
        var st = new Style(typeof(ListBoxItem));
        st.Setters.Add(new Setter(Control.TemplateProperty, t));
        st.Setters.Add(new Setter(Control.ForegroundProperty, Theme.Text));
        return st;
    }

    public void OpenSelected()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not CatalogItem item) return;
        ShowItem(item);
    }

    public void ShowItem(CatalogItem item)
    {
        var ctx = _s.Catalog.Context;
        if (ctx == null) return;
        _s.SetLastItem(item);
        var model = _s.Cards(ctx).Full(item, compact: true, expanded: true);
        var p = new StackPanel();
        p.Children.Add(CardRenderer.Build(model, 1.45, interactive: true, width: 390));
        var back = Ui.Button("← Results", (_, _) => { _detail.Content = null; _list.Visibility = Visibility.Visible; _box.Focus(); });
        p.Children.Add(Ui.Row(back,
            Ui.Button("Wiki", (_, _) => { LinkLauncher.OpenWiki(item); Dismiss(restoreFocus: false); }, "Open the Tarkov Wiki page"),
            Ui.Button("tarkov.dev", (_, _) => { LinkLauncher.OpenTarkovDev(item); Dismiss(restoreFocus: false); }, "Open the tarkov.dev page")));
        _list.Visibility = Visibility.Collapsed;
        _detail.Content = p;
        _hint.Text = "Esc to close · ← Results to search again";
    }

    public void Dismiss(bool restoreFocus)
    {
        if (!IsVisible) return;
        Hide();
        if (restoreFocus && _previous != IntPtr.Zero && IsWindow(_previous)) SetForegroundWindow(_previous);
        _previous = IntPtr.Zero;
    }

    /// <summary>Real close at shutdown.</summary>
    public void CloseForGood()
    {
        _closingForGood = true;
        _debounce.Stop();
        Close();
    }
}
