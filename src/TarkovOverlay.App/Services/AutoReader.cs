using System.Diagnostics;
using System.Windows.Threading;
using TarkovOverlay.App.Native;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Geometry;
using TarkovOverlay.Core.Settings;
using static TarkovOverlay.App.Native.NativeMethods;

namespace TarkovOverlay.App.Services;

/// <summary>
/// Auto-read on hover. Polls only the cursor position (every 40 ms, cheap) while Tarkov is the foreground
/// window. After the cursor rests for the dwell time it probes the normal capture area every 50 ms, up to the
/// max wait, for Tarkov's dark tooltip (pixel-level detector, no OCR). Only when a tooltip is seen does it run
/// the full lookup. The same spot never re-triggers until the cursor moves beyond the tolerance.
/// </summary>
/// <summary>A capture taken while the mouse rested, before Tarkov's tooltip appeared (for stack counts).</summary>
public sealed record ProbeFrame(RgbImage Image, PixelRect Rect, (int X, int Y) Cursor, TimeSpan At);

public sealed record HoverSnapshot(RgbImage Image, PixelRect Rect, (int X, int Y) Cursor, PixelRect? Tooltip, TimeSpan At);

public sealed class AutoReader
{
    /// <summary>Rest time before the clean pre-tooltip frame is grabbed (Tarkov's tooltip needs longer to appear).</summary>
    public static readonly TimeSpan SnapshotAfter = TimeSpan.FromMilliseconds(90);
    private HoverSnapshot? _snapshot;
    private (int X, int Y)? _snapshotAnchor;
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(50);

    private readonly Func<AppSettings> _settings;
    private readonly LookupController _lookup;
    private readonly CursorCardWindow _card;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HoverDwellTracker _tracker;
    private bool _probing;
    private string? _lastSkip;
    private IntPtr _fgHwnd;
    private bool _fgIsGame;
    private uint _fgPid;

    public AutoReader(Func<AppSettings> settings, LookupController lookup, CursorCardWindow card)
    {
        _settings = settings; _lookup = lookup; _card = card;
        _tracker = new HoverDwellTracker(TimeSpan.FromMilliseconds(settings().EffectiveAutoDwellMs), settings().EffectiveMoveTolerancePx);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Starts or stops polling to match the current setting.</summary>
    public void Apply()
    {
        var s = _settings();
        _tracker.Dwell = TimeSpan.FromMilliseconds(s.EffectiveAutoDwellMs);
        _tracker.TolerancePx = s.EffectiveMoveTolerancePx;
        _tracker.Reset();
        if (s.AutoReadEnabled && !_timer.IsEnabled) { _timer.Start(); Log.Write($"auto-read on (dwell {s.EffectiveAutoDwellMs} ms, tooltip wait {s.EffectiveAutoMaxTooltipWaitMs} ms)"); }
        else if (!s.AutoReadEnabled && _timer.IsEnabled) { _timer.Stop(); Log.Write("auto-read off"); }
    }

    public void Stop() => _timer.Stop();

    private bool GameForeground()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        if (h == _fgHwnd) return _fgIsGame;
        _fgHwnd = h;
        GetWindowThreadProcessId(h, out var pid);
        if (pid == _fgPid) return _fgIsGame;
        _fgPid = pid;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            _fgIsGame = p.ProcessName.StartsWith("EscapeFromTarkov", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { _fgIsGame = false; }
        return _fgIsGame;
    }

    private void Skip(string? reason)
    {
        if (reason == _lastSkip) return;
        _lastSkip = reason;
        if (reason != null) Log.Write("auto-read: " + reason);
    }

    private void Tick()
    {
        if (_probing) return;
        var c = Desktop.Cursor();
        bool fg = GameForeground();
        if (!fg) Skip("waiting: Tarkov is not the foreground window");
        var action = _tracker.Tick(_clock.Elapsed, c, fg, _lookup.IsBusy);
        if (fg && _tracker.LastSkip != null) Skip(_tracker.LastSkip);
        if (fg && action == DwellAction.None && _tracker.Anchor is { } anchor && anchor != _snapshotAnchor
            && _tracker.StillFor(_clock.Elapsed) >= SnapshotAfter && !_card.IsCardVisible)
        {
            _snapshotAnchor = anchor;
            TakeSnapshot(c);
        }
        if (action != DwellAction.StartProbe) return;
        if (_card.IsCardVisible) { Skip("card already visible"); return; }
        Skip(null);
        _ = ProbeAsync(c);
    }

    private void TakeSnapshot((int X, int Y) at)
    {
        try
        {
            var monitor = Desktop.MonitorAt(at.X, at.Y);
            var rect = CaptureGeometry.First(at.X, at.Y, monitor);
            var img = Desktop.Capture(rect);
            var det = TooltipDetector.Detect(img, at.X - rect.X, at.Y - rect.Y, monitor.DpiScale, wideRetry: false);
            _snapshot = new HoverSnapshot(img, rect, at, det.Status == DetectionStatus.Found ? det.Best!.Interior.Offset(rect.X, rect.Y) : null, _clock.Elapsed);
        }
        catch (Exception ex) { _snapshot = null; Log.Write("auto-read snapshot failed: " + ex.Message); }
    }

    private ProbeFrame? _probeFrame;

    /// <summary>
    /// The probe frame in which the tooltip was seen, if it is for this cursor position and under 2 s old. The lookup
    /// falls back to it when its own capture misses the tooltip (1.1.4: a tooltip seen by the probe must give a card).
    /// </summary>
    public ProbeFrame? ProbeFrameNear((int X, int Y) at)
    {
        var f = _probeFrame;
        if (f == null || _clock.Elapsed - f.At > TimeSpan.FromSeconds(2)) return null;
        var monitor = Desktop.MonitorAt(at.X, at.Y);
        int tol = DismissPolicy.TolerancePx(_settings().EffectiveMoveTolerancePx, monitor.DpiScale);
        return CardPlacement.HasMoved(f.Cursor.X, f.Cursor.Y, at.X, at.Y, tol) ? null : f;
    }

    /// <summary>The resting frame for this cursor position, if it was taken in the last few seconds.</summary>
    public HoverSnapshot? SnapshotNear((int X, int Y) at)
    {
        var s = _snapshot;
        if (s == null || _clock.Elapsed - s.At > TimeSpan.FromSeconds(5)) return null;
        var monitor = Desktop.MonitorAt(at.X, at.Y);
        int tol = DismissPolicy.TolerancePx(_settings().EffectiveMoveTolerancePx, monitor.DpiScale);
        return CardPlacement.HasMoved(s.Cursor.X, s.Cursor.Y, at.X, at.Y, tol) ? null : s;
    }

    private async Task ProbeAsync((int X, int Y) at)
    {
        _probing = true;
        try
        {
            var s = _settings();
            var monitor = Desktop.MonitorAt(at.X, at.Y);
            int tol = DismissPolicy.TolerancePx(s.EffectiveMoveTolerancePx, monitor.DpiScale);
            DetectionStatus last = DetectionStatus.NotFound;
            var r = await TooltipWait.RunAsync(
                probe: () =>
                {
                    var rect = CaptureGeometry.First(at.X, at.Y, monitor);
                    var img = Desktop.Capture(rect);
                    last = TooltipDetector.Detect(img, at.X - rect.X, at.Y - rect.Y, monitor.DpiScale, wideRetry: false).Status;
                    if (last == DetectionStatus.Found) _probeFrame = new ProbeFrame(img, rect, at, _clock.Elapsed);
                    return last != DetectionStatus.NotFound; // Found, or clipped at the crop edge (the lookup retries wider)
                },
                cursorMoved: () => { var c = Desktop.Cursor(); return CardPlacement.HasMoved(at.X, at.Y, c.X, c.Y, tol); },
                interval: ProbeInterval,
                maxWait: TimeSpan.FromMilliseconds(s.EffectiveAutoMaxTooltipWaitMs),
                clock: () => _clock.Elapsed,
                delay: d => Task.Delay(d));
            Log.Write($"auto-read probe at {at.X},{at.Y}: {r.Result} after {r.Probes} probe(s), {r.Waited.TotalMilliseconds:0} ms ({last})");
            if (r.Result == TooltipWait.Outcome.Found && !_lookup.IsBusy) _lookup.TriggerAuto();
        }
        catch (Exception ex) { Log.Write("auto-read probe failed: " + ex.Message); }
        finally { _probing = false; }
    }
}
