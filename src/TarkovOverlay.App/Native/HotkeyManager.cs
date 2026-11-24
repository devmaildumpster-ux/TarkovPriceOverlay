using System.Runtime.InteropServices;
using System.Windows.Interop;
using static TarkovOverlay.App.Native.NativeMethods;

namespace TarkovOverlay.App.Native;

/// <summary>
/// Global hotkey via RegisterHotKey + WM_HOTKEY on a message-only HwndSource. Tries plain backtick
/// (VK_OEM_3), then Ctrl+Backtick, then Ctrl+Shift+F8, always with MOD_NOREPEAT. Unregisters on dispose.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public sealed record Candidate(uint Modifiers, uint Key, string Label);

    public static readonly IReadOnlyList<Candidate> Candidates = new[]
    {
        new Candidate(0, VK_OEM_3, Core.Help.ControlsHelp.MainKey1),
        new Candidate(MOD_CONTROL, VK_OEM_3, Core.Help.ControlsHelp.MainKey2),
        new Candidate(MOD_CONTROL | MOD_SHIFT, VK_F8, Core.Help.ControlsHelp.MainKey3),
    };

    private const int HotkeyId = 0x5450; // "TP"
    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;
    public Candidate? Active { get; private set; }
    public List<string> Attempts { get; } = new();

    /// <summary>Registers the first free candidate. Returns false if all are taken.</summary>
    public bool Register()
    {
        if (_source == null)
        {
            var p = new HwndSourceParameters("TarkovPriceOverlayHotkey") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, WindowStyle = 0 };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }
        Unregister();
        Attempts.Clear();
        foreach (var c in Candidates)
        {
            if (RegisterHotKey(_source.Handle, HotkeyId, c.Modifiers | MOD_NOREPEAT, c.Key))
            {
                _registered = true;
                Active = c;
                Attempts.Add($"{c.Label}: registered");
                return true;
            }
            int err = Marshal.GetLastWin32Error();
            Attempts.Add($"{c.Label}: {(err == ERROR_HOTKEY_ALREADY_REGISTERED ? "already in use by another app" : $"failed (error {err})")}");
        }
        Active = null;
        return false;
    }

    public void Unregister()
    {
        if (_registered && _source != null) UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        Active = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
