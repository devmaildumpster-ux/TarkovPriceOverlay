using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TarkovOverlay.App.Native;
using TarkovOverlay.App.Services;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Geometry;
using TarkovOverlay.Core.Presentation;
using static TarkovOverlay.App.Native.NativeMethods;

namespace TarkovOverlay.App.Overlay;

/// <summary>
/// Small independent topmost card: transparent outer corners, never activated, click-through
/// (WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) and not hit-testable.
/// Dismissed when the cursor moves past a small tolerance (30 ms check, after a short grace period) or after
/// a timeout. Every show/hide is logged with physical + DIP bounds. Note: no normal window can draw over a
/// game in exclusive fullscreen; Tarkov must be Borderless or Windowed.
/// </summary>
public sealed class CursorCardWindow : Window
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(30);

    private readonly DispatcherTimer _poll;
    private readonly Stopwatch _shownFor = new();
    private (int X, int Y) _shownAt;
    private PixelRect _physical;
    private IntPtr _hwnd;
    private bool _dismissOnMove = true;
    private int _tolerancePx = 6;
    private TimeSpan _timeout = DismissPolicy.DefaultTimeout;
    private int _topmostTicks;

    /// <summary>Set by the last show: true if the foreground window covered the whole monitor (fullscreen game).</summary>
    public bool LastForegroundWasFullscreen { get; private set; }

    public CursorCardWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        Opacity = 1;
        Title = "Tarkov price card";
        _poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = PollInterval };
        _poll.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) => ApplyNativeStyles();
        DpiChanged += (_, _) => ReapplyBounds(); // moved to a monitor with a different DPI
    }

    public bool IsCardVisible => IsVisible;

    /// <summary>1.2.0: card opacity 0.4–1.0 (Settings… › Card opacity); applies at once, also to a visible card.</summary>
    public double CardOpacity
    {
        get => Opacity;
        set => Opacity = Math.Clamp(value, 0.4, 1.0);
    }

    private object? _key;

    /// <summary>True while this card (identified by <paramref name="key"/>) is visible and the cursor is still where it appeared.</summary>
    public bool IsShowingFor(object key)
    {
        if (!IsVisible || !ReferenceEquals(_key, key)) return false;
        var c = Desktop.Cursor();
        return !CardPlacement.HasMoved(_shownAt.X, _shownAt.Y, c.X, c.Y, _tolerancePx);
    }

    private void ApplyNativeStyles()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Shows the card beside <paramref name="origin"/> (physical pixels).</summary>
    /// <param name="dismissOnMove">False for the main window's test card.</param>
    public void ShowCard(CardModel model, (int X, int Y) origin, int userScale, bool dismissOnMove = true, int tolerancePxAt100 = 6, TimeSpan? timeout = null, string reason = "lookup", object? key = null)
    {
        _key = key;
        var monitor = Desktop.MonitorAt(origin.X, origin.Y);
        var content = CardRenderer.Build(model, 1.0);
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var dip = content.DesiredSize;
        var place = CardPlacement.Place(origin.X, origin.Y, dip.Width * userScale, dip.Height * userScale, monitor);
        double s = userScale * place.FitScale;
        content.LayoutTransform = new ScaleTransform(s, s);
        Content = content;
        _physical = place.Bounds;

        // Approximate DIP position for WPF, then the exact physical bounds via SetWindowPos.
        Left = place.Bounds.X / monitor.DpiScale;
        Top = place.Bounds.Y / monitor.DpiScale;
        Width = Math.Max(1, place.Bounds.Width / monitor.DpiScale);
        Height = Math.Max(1, place.Bounds.Height / monitor.DpiScale);
        if (!IsVisible) Show();
        ReapplyBounds();

        _dismissOnMove = dismissOnMove;
        _tolerancePx = DismissPolicy.TolerancePx(tolerancePxAt100, monitor.DpiScale);
        _timeout = timeout ?? DismissPolicy.DefaultTimeout;
        _shownAt = Desktop.Cursor(); // measure movement from where the cursor is when the card appears
        _shownFor.Restart();
        _topmostTicks = 0;
        _poll.Stop(); _poll.Start();

        var fg = ForegroundInfo(monitor);
        LastForegroundWasFullscreen = fg.Fullscreen;
        GetWindowRect(_hwnd, out var actual);
        Log.Write($"card show ({reason}) '{model.Title}' cursor {origin.X},{origin.Y} now {_shownAt.X},{_shownAt.Y} · monitor {R(monitor.Bounds)} work {R(monitor.WorkArea)} dpi {monitor.DpiScale:0.##} · " +
                  $"content {dip.Width:0}x{dip.Height:0} DIP × {userScale} fit {place.FitScale:0.##} · physical {R(place.Bounds)} DIP [{Left:0.#},{Top:0.#} {Width:0.#}x{Height:0.#}] · " +
                  $"actual [{actual.Left},{actual.Top} {actual.Right - actual.Left}x{actual.Bottom - actual.Top}] visible {IsWindowVisible(_hwnd)} · " +
                  $"dismissOnMove {dismissOnMove} tol {_tolerancePx}px timeout {_timeout.TotalSeconds:0}s · foreground {fg.Text}");
    }

    private void Tick()
    {
        var c = Desktop.Cursor();
        var r = DismissPolicy.Evaluate(_shownAt, c, _shownFor.Elapsed, _dismissOnMove, _tolerancePx, _timeout);
        if (r != DismissReason.None)
        {
            HideCard(r == DismissReason.Moved ? $"moved to {c.X},{c.Y} (from {_shownAt.X},{_shownAt.Y}, tol {_tolerancePx}px)" : "timeout");
            return;
        }
        // Re-assert topmost about every 300 ms so a game that raises itself cannot bury the card.
        if (++_topmostTicks % 10 == 0) ReapplyBounds();
    }

    private void ReapplyBounds()
    {
        if (_hwnd == IntPtr.Zero || _physical.IsEmpty) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, _physical.X, _physical.Y, _physical.Width, _physical.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
    }

    public void HideCard(string reason = "hidden")
    {
        _poll.Stop();
        if (IsVisible)
        {
            Hide();
            Log.Write($"card hide: {reason} after {_shownFor.ElapsedMilliseconds} ms");
        }
    }

    private static (bool Fullscreen, string Text) ForegroundInfo(MonitorSnapshot m)
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return (false, "none");
        var sb = new StringBuilder(128);
        GetClassNameW(h, sb, sb.Capacity);
        GetWindowRect(h, out var r);
        var style = GetWindowLongPtr(h, -16).ToInt64(); // GWL_STYLE
        bool coversMonitor = r.Left <= m.Bounds.X && r.Top <= m.Bounds.Y && r.Right >= m.Bounds.Right && r.Bottom >= m.Bounds.Bottom;
        bool hasCaption = (style & 0x00C00000L) == 0x00C00000L; // WS_CAPTION
        bool full = coversMonitor && !hasCaption && !sb.ToString().StartsWith("HwndWrapper", StringComparison.Ordinal);
        return (full, $"'{sb}' [{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}] style 0x{style:X}{(full ? " (covers monitor)" : "")}");
    }

    private static string R(PixelRect r) => $"[{r.X},{r.Y} {r.Width}x{r.Height}]";
}
