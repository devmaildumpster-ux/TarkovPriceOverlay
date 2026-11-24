using System.Runtime.InteropServices;
using System.Windows.Interop;
using TarkovOverlay.Core.Input;
using static TarkovOverlay.App.Native.NativeMethods;

namespace TarkovOverlay.App.Native;

/// <summary>
/// 1.2.0: the extra global shortcuts (open Wiki, open tarkov.dev, in-game search). Each has its own id on a separate
/// message-only window, so a failure here never touches the main backtick shortcut. Status per action is kept for
/// Settings… ("registered", "in use by another app", "off").
/// </summary>
public sealed class ExtraHotkeys : IDisposable
{
    private sealed class Slot
    {
        public int Id;
        public KeyChord? Chord;
        public Action? Action;
        public bool Registered;
        public string Status = "off";
    }

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private HwndSource? _source;
    private int _nextId = 0x5460;

    /// <summary>(Re)binds <paramref name="name"/>. Null chord or disabled → just unregisters. Returns the status text.</summary>
    public string Set(string name, KeyChord? chord, bool enabled, Action action)
    {
        EnsureSource();
        if (!_slots.TryGetValue(name, out var s)) _slots[name] = s = new Slot { Id = _nextId++ };
        Release(s);
        s.Action = action;
        s.Chord = chord;
        if (!enabled) { s.Status = "off"; return s.Status; }
        if (chord == null) { s.Status = "no key set"; return s.Status; }
        if (!chord.IsSafe) { s.Status = $"{chord}: not allowed (needs Ctrl, Alt or Win, or an F-key)"; return s.Status; }
        if (RegisterHotKey(_source!.Handle, s.Id, (uint)chord.Modifiers | MOD_NOREPEAT, chord.Vk))
        {
            s.Registered = true;
            s.Status = $"{chord}: registered";
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            s.Status = $"{chord}: " + (err == ERROR_HOTKEY_ALREADY_REGISTERED ? "already in use by another app (pick another key)" : $"failed (error {err})");
        }
        Services.Log.Write($"hotkey {name}: {s.Status}");
        return s.Status;
    }

    public string Status(string name) => _slots.TryGetValue(name, out var s) ? s.Status : "off";

    private void EnsureSource()
    {
        if (_source != null) return;
        var p = new HwndSourceParameters("TarkovPriceOverlayExtraHotkeys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    private void Release(Slot s)
    {
        if (s.Registered && _source != null) UnregisterHotKey(_source.Handle, s.Id);
        s.Registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        int id = wParam.ToInt32();
        foreach (var s in _slots.Values)
            if (s.Registered && s.Id == id)
            {
                handled = true;
                try { s.Action?.Invoke(); }
                catch (Exception ex) { Services.Log.Write($"hotkey action failed: {ex.Message}"); }
            }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var s in _slots.Values) Release(s);
        _slots.Clear();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
