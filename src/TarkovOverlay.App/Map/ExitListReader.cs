using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TarkovOverlay.App.Native;
using TarkovOverlay.App.Services;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Recognition;
using TarkovOverlay.Maps.Extracts;

namespace TarkovOverlay.App.Map;

/// <summary>
/// Experimental (off by default): when O is pressed in raid with Tarkov in front, captures the top-right of the game
/// window (where the exit list appears) ~0.7 s and ~1.5 s later, OCRs it and matches lines against this map's exits.
/// Polls the key state every 100 ms only while enabled, in raid, and a map is shown. Pixels stay in memory.
/// </summary>
internal sealed class ExitListReader : IDisposable
{
    private const int VK_O = 0x4F;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private readonly MapFeature _f;
    private readonly DispatcherTimer _timer;
    private bool _wasDown, _busy;
    private uint _gamePid;

    public ExitListReader(MapFeature f, Dispatcher ui)
    {
        _f = f;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() { _timer.Start(); Log.Write("map: exit-list reader on (press O in raid)"); }
    public void Dispose() { _timer.Stop(); }

    private void Poll()
    {
        bool down = (GetAsyncKeyState(VK_O) & 0x8000) != 0;
        bool pressed = down && !_wasDown;
        _wasDown = down;
        if (!pressed || _busy) return;
        if (GameWindow() is not { } rect) return;
        _ = ReadAsync(rect);
    }

    private PixelRect? GameWindow()
    {
        var h = NativeMethods.GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        NativeMethods.GetWindowThreadProcessId(h, out var pid);
        if (pid != _gamePid)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (!p.ProcessName.StartsWith("EscapeFromTarkov", StringComparison.OrdinalIgnoreCase)) return null;
                _gamePid = pid;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        }
        if (!NativeMethods.GetWindowRect(h, out var r) || r.Right - r.Left < 200 || r.Bottom - r.Top < 200) return null;
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private async Task ReadAsync(PixelRect game)
    {
        _busy = true;
        try
        {
            var exits = _f.Panel.CurrentExtracts.Where(e => e.Kind != ExtractKind.Transit).Select(e => (e.Id, e.Name)).ToList();
            if (exits.Count == 0) return;
            var region = HudTextIsolator.ExitListRegion(game);
            bool winOk = _f.App.WindowsOcr.IsAvailable;
            IOcrEngine primary = winOk ? _f.App.WindowsOcr : _f.App.Tesseract;
            var found = new Dictionary<string, ExitListMatcher.Match>();

            async Task RunPassAsync(IOcrEngine engine, RgbImage image, double scale, int delay, string tag)
            {
                var read = await engine.RecognizeAsync(image, scale, CancellationToken.None);
                var ms = ExitListMatcher.MatchLines(read.Lines, exits);
                foreach (var m in ms) found.TryAdd(m.ExtractId, m);
                Log.Write($"map: exit list OCR ({read.Engine} {tag}, +{delay} ms): {string.Join(" | ", read.Lines)} → {ms.Count} matched");
            }

            // 1.2.7+: both reads always run and their matches add up (the list fades in, and one read can miss a row).
            // 1.3.1+: upscale small HUD rows (2x at 1080p/1440p) and isolate bright/colored HUD text on translucent
            // bars so sky background or green active/timed V-Ex rows are not dropped.
            await Task.Delay(700);
            var img1 = Desktop.Capture(region);
            double scale1 = HudTextIsolator.ExitListScaleFor(img1.Height);
            if (winOk) await RunPassAsync(primary, img1, scale1, 700, $"{scale1:0.#}x");
            else await RunPassAsync(primary, HudTextIsolator.IsolateBrightHudText(img1.Scale(scale1)), 1.0, 700, $"hud@{scale1:0.#}x");

            await Task.Delay(800);
            var img2 = Desktop.Capture(region);
            double scale2 = HudTextIsolator.ExitListScaleFor(img2.Height);
            var isolated2 = HudTextIsolator.IsolateBrightHudText(img2.Scale(scale2));
            await RunPassAsync(primary, isolated2, 1.0, 800, $"hud@{scale2:0.#}x");
            if (winOk && _f.App.Tesseract.IsAvailable)
                await RunPassAsync(_f.App.Tesseract, isolated2, 1.0, 800, $"hud@{scale2:0.#}x");

            Log.Write($"map: exit list read: {(found.Count == 0 ? "nothing matched" : string.Join(", ", found.Values.Select(m => $"{m.ExtractName} ({m.Score:0.00})")))}");
            if (found.Count > 0) _f.SetListedExtracts(found.Keys.ToHashSet());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ExternalException)
        {
            Log.Write("map: exit list read failed: " + ex.Message);
        }
        finally { _busy = false; }
    }
}
