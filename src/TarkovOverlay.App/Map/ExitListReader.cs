using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TarkovOverlay.App.Native;
using TarkovOverlay.App.Services;
using TarkovOverlay.Core.Detection;
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
            // exit list: top-right corner, roughly the right 38 % × top 45 % of the game window
            var region = new PixelRect(game.X + (int)(game.Width * 0.62), game.Y, (int)(game.Width * 0.38), (int)(game.Height * 0.45));
            var engine = _f.App.WindowsOcr.IsAvailable ? (Core.Recognition.IOcrEngine)_f.App.WindowsOcr : _f.App.Tesseract;
            var found = new Dictionary<string, ExitListMatcher.Match>();
            foreach (var delay in new[] { 700, 800 })
            {
                await Task.Delay(delay);
                var img = Desktop.Capture(region);
                var read = await engine.RecognizeAsync(img, 1.0, CancellationToken.None);
                foreach (var m in ExitListMatcher.MatchLines(read.Lines, exits)) found.TryAdd(m.ExtractId, m);
                if (found.Count > 0) break;
            }
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
