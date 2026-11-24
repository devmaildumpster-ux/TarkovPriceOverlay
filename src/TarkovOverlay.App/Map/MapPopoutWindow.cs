using System.Windows;
using System.Windows.Input;
using TarkovOverlay.App.Overlay;

namespace TarkovOverlay.App.Map;

/// <summary>
/// Optional normal window for the map (not topmost, nothing drawn over the game). Remembers its monitor, size and
/// maximized state; F11 or the Fullscreen button switches to borderless fullscreen on the monitor it's on, Esc leaves.
/// </summary>
public sealed class MapPopoutWindow : Window
{
    private readonly MapFeature _f;
    private readonly MapPanel _panel;
    private bool _fullscreen;
    private WindowState _beforeFullscreen = WindowState.Normal;

    public MapPopoutWindow(MapFeature f, MapPanel panel)
    {
        _f = f; _panel = panel;
        Title = "Tarkov Price Overlay · Map";
        Background = Theme.Window; Foreground = Theme.Text; FontFamily = Theme.Font;
        var s = f.Settings;
        Width = s.PopWidth is >= 400 and <= 20000 ? s.PopWidth.Value : 1100;
        Height = s.PopHeight is >= 300 and <= 20000 ? s.PopHeight.Value : 800;
        MinWidth = 420; MinHeight = 320;
        if (s.PopLeft is { } l && s.PopTop is { } t && OnScreen(l, t, Width, Height)) { Left = l; Top = t; WindowStartupLocation = WindowStartupLocation.Manual; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        panel.Margin = new Thickness(8);
        Content = panel;
        panel.SetHostMode(poppedOut: true, fullscreen: false);
        panel.FullscreenToggleRequested += ToggleFullscreen;

        Loaded += (_, _) =>
        {
            // restore on the monitor it was on (Left/Top above), then maximize / fullscreen there
            if (s.PopFullscreen) { if (s.PopMaximized) _beforeFullscreen = WindowState.Maximized; SetFullscreen(true); }
            else if (s.PopMaximized) WindowState = WindowState.Maximized;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
            else if (e.Key == Key.Escape && _fullscreen) { SetFullscreen(false); e.Handled = true; }
        };
        Closing += (_, _) =>
        {
            s.PopFullscreen = _fullscreen;
            s.PopMaximized = _fullscreen ? _beforeFullscreen == WindowState.Maximized : WindowState == WindowState.Maximized;
            var b = RestoreBounds;
            if (!b.IsEmpty && b.Width > 0) { s.PopLeft = b.Left; s.PopTop = b.Top; s.PopWidth = b.Width; s.PopHeight = b.Height; }
            f.SaveSoon();
            panel.FullscreenToggleRequested -= ToggleFullscreen;
            Content = null; // hand the panel back to the main window
        };
    }

    private void ToggleFullscreen() => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        if (on)
        {
            _beforeFullscreen = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            // WPF only covers the taskbar if the style changes while not maximized; maximizing keeps the current monitor
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _beforeFullscreen;
        }
        _fullscreen = on;
        _panel.SetHostMode(poppedOut: true, fullscreen: on);
    }

    private static bool OnScreen(double l, double t, double w, double h) =>
        l + w > SystemParameters.VirtualScreenLeft + 80 && t >= SystemParameters.VirtualScreenTop - 20 &&
        l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
        t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;
}
