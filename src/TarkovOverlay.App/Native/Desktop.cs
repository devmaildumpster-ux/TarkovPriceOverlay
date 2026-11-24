using System.Runtime.InteropServices;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Geometry;
using static TarkovOverlay.App.Native.NativeMethods;

namespace TarkovOverlay.App.Native;

/// <summary>Cursor, monitor and on-demand screen capture, all in physical pixels (per-monitor DPI aware).</summary>
public static class Desktop
{
    public static (int X, int Y) Cursor()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    public static MonitorSnapshot MonitorAt(int x, int y)
    {
        var h = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(h, ref mi);
        double scale = 1.0;
        try
        {
            if (GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0) scale = dx / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return new MonitorSnapshot(ToRect(mi.rcMonitor), ToRect(mi.rcWork), scale);
    }

    /// <summary>1.2.0: every monitor in Windows' enumeration order (the order Graphics.ini's display index follows).</summary>
    public static IReadOnlyList<TarkovOverlay.Core.Display.MonitorInfo> Monitors()
    {
        var list = new List<TarkovOverlay.Core.Display.MonitorInfo>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(h, ref mi)) return true;
                double scale = 1.0;
                try { if (GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0) scale = dx / 96.0; }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                list.Add(new TarkovOverlay.Core.Display.MonitorInfo(list.Count, ToRect(mi.rcMonitor), ToRect(mi.rcWork), scale, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return list;
    }

    /// <summary>Tarkov's monitor from Graphics.ini (falls back to the primary monitor).</summary>
    public static (TarkovOverlay.Core.Display.TarkovDisplay? Display, string Note) TarkovDisplay()
    {
        var (ini, note) = TarkovOverlay.Core.Display.GraphicsIni.TryRead();
        return (TarkovOverlay.Core.Display.TarkovDisplayResolver.Resolve(Monitors(), ini), note);
    }

    private static PixelRect ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>
    /// Copies a small screen region with BitBlt into a top-down 32-bit DIB. Every DC and bitmap is released
    /// on every path. Pixels stay in memory only.
    /// </summary>
    public static RgbImage Capture(PixelRect r)
    {
        if (r.IsEmpty) throw new ArgumentException("empty capture rectangle");
        IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new InvalidOperationException("GetDC failed");
            mem = CreateCompatibleDC(screen);
            if (mem == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed");
            var bi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = r.Width, biHeight = -r.Height, // top-down
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            bmp = CreateDIBSection(screen, ref bi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, SRCCOPY))
                throw new InvalidOperationException($"BitBlt failed ({Marshal.GetLastWin32Error()})");
            var img = new RgbImage(r.Width, r.Height);
            Marshal.Copy(bits, img.Bgra, 0, img.Bgra.Length);
            for (int i = 3; i < img.Bgra.Length; i += 4) img.Bgra[i] = 255;
            return img;
        }
        finally
        {
            if (old != IntPtr.Zero && mem != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Checks whether the Visual C++ runtime that Tesseract needs can be loaded.</summary>
    public static string? MissingVcRuntime()
    {
        var missing = new List<string>();
        foreach (var dll in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" })
        {
            var h = LoadLibraryW(dll);
            if (h == IntPtr.Zero) missing.Add(dll); else FreeLibrary(h);
        }
        return missing.Count == 0 ? null : string.Join(", ", missing);
    }
}
