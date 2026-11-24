using System.Windows;
using TarkovOverlay.App.Native;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.Core.Input;

namespace TarkovOverlay.App.Services;

/// <summary>
/// 1.2.0 extras owned by the GUI: the Wiki / tarkov.dev / search shortcuts, the in-game search bar and the tray
/// icon. Program registers <see cref="Dispose"/> as a shutdown step (removes the tray icon, unregisters the keys,
/// closes the search bar), so none of them can keep the process alive.
/// </summary>
public sealed class ShellFeatures : IDisposable
{
    public const string Wiki = "wiki", Dev = "tarkov.dev", Search = "search", MapStyle = "map style";
    private readonly AppServices _s;
    private readonly ExtraHotkeys _keys = new();
    private SearchOverlayWindow? _search;
    private TrayIcon? _tray;
    private bool _disposed;

    /// <summary>1.2.3: tray › Controls &amp; hotkeys.</summary>
    public Action? OpenControls { get; set; }
    /// <summary>Main window hooks for the tray menu.</summary>
    public Action? ShowMain { get; set; }
    public Action? OpenSettings { get; set; }
    public Action? Quit { get; set; }

    public ShellFeatures(AppServices s) => _s = s;

    public SearchOverlayWindow SearchWindow => _search ??= new SearchOverlayWindow(_s);

    /// <summary>(Re)registers the three extra shortcuts from the settings. Never touches the main shortcut.</summary>
    public void ApplyHotkeys()
    {
        if (_disposed) return;
        var st = _s.Settings;
        _keys.Set(Wiki, Chord(st.EffectiveWikiHotkey), st.WikiHotkeyEnabled, () => { if (!LinkLauncher.OpenWiki(_s.LastItem)) Log.Write("wiki shortcut: no item looked up yet"); });
        _keys.Set(Dev, Chord(st.EffectiveDevHotkey), st.DevHotkeyEnabled, () => { if (!LinkLauncher.OpenTarkovDev(_s.LastItem)) Log.Write("tarkov.dev shortcut: no item looked up yet"); });
        _keys.Set(Search, Chord(st.EffectiveSearchHotkey), st.SearchHotkeyEnabled, () => SearchWindow.Toggle());
        // 1.2.1: Satellite ↔ Vector without leaving the game. WM_HOTKEY arrives on the UI thread; nothing is activated or focused.
        _keys.Set(MapStyle, Chord(st.EffectiveMapStyleHotkey), st.MapStyleHotkeyEnabled, () =>
        {
            if (Map.MapFeature.Instance is { } f) f.ToggleMapStyle("shortcut");
            else Log.Write("map style shortcut: the map isn't running");
        });
        foreach (var n in new[] { Wiki, Dev, Search, MapStyle })
            if (st.HotkeyClashes(n) is { Count: > 0 } c) Log.Write($"hotkey {n}: same key as {string.Join(", ", c)} (only one of them can work)");
    }

    public string HotkeyStatus(string name) => _keys.Status(name);

    /// <summary>Releases the extra shortcuts while Settings… records a new key (so the old chord can be pressed).</summary>
    public void SuspendHotkeys()
    {
        foreach (var n in new[] { Wiki, Dev, Search, MapStyle }) _keys.Set(n, null, false, () => { });
    }

    private static KeyChord? Chord(string text) => KeyChord.TryParse(text, out var c) ? c : null;

    // ---------------- tray ----------------
    /// <summary>Shows the tray icon while "Minimize to tray" is on; removes it when off.</summary>
    public void ApplyTray()
    {
        if (_disposed) return;
        if (_s.Settings.MinimizeToTray)
        {
            _tray ??= new TrayIcon(OnTray);
            _tray.Show("Tarkov Price Overlay — click to open");
        }
        else _tray?.Hide();
    }

    public bool TrayVisible => _tray?.IsVisible == true;

    private void OnTray(TrayIcon.Command c)
    {
        switch (c)
        {
            case TrayIcon.Command.Open: ShowMain?.Invoke(); break;
            case TrayIcon.Command.Settings: ShowMain?.Invoke(); OpenSettings?.Invoke(); break;
            case TrayIcon.Command.Quit: Quit?.Invoke(); break;
            case TrayIcon.Command.Controls: ShowMain?.Invoke(); OpenControls?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tray?.Dispose();
        _tray = null;
        _keys.Dispose();
        _search?.CloseForGood();
        _search = null;
    }
}
