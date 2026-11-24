using System.Text;
using System.Text.Json;
using TarkovOverlay.Core.Detection;

namespace TarkovOverlay.Core.Display;

/// <summary>What Tarkov's own Graphics.ini says about the screen it runs on (1.2.0).</summary>
public sealed record GraphicsIniInfo(int DisplayIndex, int? Width, int? Height, int? FullScreenMode, string Source);

/// <summary>
/// Reads %AppData%\Battlestate Games\Escape From Tarkov\Settings\Graphics.ini (a JSON document despite the name):
/// DisplaySettings.Display = the active display index, Stored[index].WindowResolution = {Width, Height}
/// (the same fields RatScanner uses). Shared read (the game may have it open); anything missing or malformed
/// gives null / a reason instead of an exception.
/// </summary>
public static class GraphicsIni
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Battlestate Games", "Escape From Tarkov", "Settings", "Graphics.ini");

    public static (GraphicsIniInfo? Info, string Note) TryRead(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return (null, $"Graphics.ini not found ({path}) — start Tarkov once, or the game isn't installed for this Windows user");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var info = Parse(r.ReadToEnd(), path);
            return info == null ? (null, "Graphics.ini has no DisplaySettings.Display (format changed?)") : (info, "read " + path);
        }
        catch (IOException ex) { return (null, "Graphics.ini unreadable: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { return (null, "Graphics.ini unreadable: " + ex.Message); }
    }

    /// <summary>Parses the JSON text. Null when it isn't JSON or has no display index.</summary>
    public static GraphicsIniInfo? Parse(string text, string source = "Graphics.ini")
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("DisplaySettings", out var ds) || ds.ValueKind != JsonValueKind.Object) return null;
            if (!TryInt(ds, "Display", out var display) || display < 0 || display > 16) return null;
            int? fsm = TryInt(ds, "FullScreenMode", out var m) ? m : null;
            int? w = null, h = null;
            if (root.TryGetProperty("Stored", out var stored))
            {
                JsonElement? entry = stored.ValueKind switch
                {
                    JsonValueKind.Object when stored.TryGetProperty(display.ToString(System.Globalization.CultureInfo.InvariantCulture), out var e) => e,
                    JsonValueKind.Array when display < stored.GetArrayLength() => stored[display],
                    _ => null,
                };
                if (entry is { ValueKind: JsonValueKind.Object } en && en.TryGetProperty("WindowResolution", out var wr) && wr.ValueKind == JsonValueKind.Object
                    && TryInt(wr, "Width", out var ww) && TryInt(wr, "Height", out var hh) && ww is > 0 and < 20000 && hh is > 0 and < 20000)
                { w = ww; h = hh; }
            }
            // older files: DisplaySettings.Resolution {Width, Height}
            if (w == null && ds.TryGetProperty("Resolution", out var res) && res.ValueKind == JsonValueKind.Object
                && TryInt(res, "Width", out var rw) && TryInt(res, "Height", out var rh) && rw is > 0 and < 20000 && rh is > 0 and < 20000)
            { w = rw; h = rh; }
            return new GraphicsIniInfo(display, w, h, fsm, source);
        }
        catch (JsonException) { return null; }
    }

    private static bool TryInt(JsonElement o, string name, out int v)
    {
        v = 0;
        if (!o.TryGetProperty(name, out var e)) return false;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v)) return true;
        return e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out v);
    }
}

/// <summary>A Windows monitor in physical pixels with its effective DPI scale.</summary>
public sealed record MonitorInfo(int Index, PixelRect Bounds, PixelRect WorkArea, double DpiScale, bool Primary);

public sealed record TarkovDisplay(MonitorInfo Monitor, GraphicsIniInfo? Ini, string How)
{
    public string Describe() =>
        $"Monitor {Monitor.Index + 1} of the desktop ({Monitor.Bounds.Width}×{Monitor.Bounds.Height} at {Monitor.Bounds.X},{Monitor.Bounds.Y}, {Monitor.DpiScale * 100:0}% scale)" +
        (Ini is { Width: { } w, Height: { } h } ? $" · Tarkov resolution {w}×{h}" : "") +
        (Ini is { FullScreenMode: { } f } ? $" · FullScreenMode {f}" : "") + $" · {How}";
}

public static class TarkovDisplayResolver
{
    /// <summary>
    /// Picks Tarkov's monitor: the Graphics.ini display index in Windows' monitor order; if that monitor's size
    /// doesn't match Tarkov's resolution but exactly one other monitor does, that one; with no Graphics.ini, the primary.
    /// </summary>
    public static TarkovDisplay? Resolve(IReadOnlyList<MonitorInfo> monitors, GraphicsIniInfo? ini)
    {
        if (monitors.Count == 0) return null;
        var primary = monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
        if (ini == null) return new TarkovDisplay(primary, null, "no Graphics.ini → primary monitor");
        bool Fits(MonitorInfo m) => ini.Width is { } w && ini.Height is { } h && m.Bounds.Width == w && m.Bounds.Height == h;
        var byIndex = ini.DisplayIndex < monitors.Count ? monitors[ini.DisplayIndex] : null;
        if (byIndex != null && (ini.Width == null || Fits(byIndex))) return new TarkovDisplay(byIndex, ini, $"Graphics.ini display {ini.DisplayIndex}");
        var fitting = monitors.Where(Fits).ToList();
        if (fitting.Count == 1) return new TarkovDisplay(fitting[0], ini, $"only monitor matching Tarkov's {ini.Width}×{ini.Height}");
        if (byIndex != null) return new TarkovDisplay(byIndex, ini, $"Graphics.ini display {ini.DisplayIndex} (resolution differs: windowed or scaled)");
        return new TarkovDisplay(primary, ini, $"display {ini.DisplayIndex} not present → primary monitor");
    }
}
