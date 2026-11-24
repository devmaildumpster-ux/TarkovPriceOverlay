using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TarkovOverlay.App.Native;

/// <summary>
/// 1.2.0: notification-area icon with Shell_NotifyIcon (no WinForms). Left click / double click → Open; right
/// click → Open · Settings… · Quit. Re-adds itself when Explorer restarts. Dispose removes the icon (NIM_DELETE),
/// destroys its HICON and window; it is a shutdown step, so it can never keep the process alive.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public enum Command { Open = 1, Settings = 2, Quit = 3, Controls = 4 }

    private const int WM_APP_TRAY = 0x8000 + 0x51;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const int WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203, WM_RBUTTONUP = 0x205, WM_NULL = 0;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint n);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr LoadIconW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessageW(string name);

    private readonly Action<Command> _onCommand;
    private HwndSource? _source;
    private IntPtr _icon;
    private bool _ownsIcon, _added;
    private readonly int _taskbarCreated;
    private string _tip = "Tarkov Price Overlay";

    public TrayIcon(Action<Command> onCommand)
    {
        _onCommand = onCommand;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    public bool IsVisible => _added;

    /// <summary>Adds the icon (idempotent). Returns false if the shell refused (no taskbar, etc.).</summary>
    public bool Show(string? tip = null)
    {
        if (tip != null) _tip = tip.Length > 120 ? tip[..120] : tip;
        if (_added) { Modify(); return true; }
        if (_source == null)
        {
            // hidden popup window (not message-only: the context menu needs a window that can be foreground)
            var p = new HwndSourceParameters("TarkovPriceOverlayTray") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }
        if (_icon == IntPtr.Zero) LoadIcon();
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIconW(NIM_ADD, ref d);
        Services.Log.Write($"tray: icon {(_added ? "added" : "could not be added")}");
        return _added;
    }

    public void Hide()
    {
        if (!_added) return;
        var d = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref d);
        _added = false;
    }

    private void Modify()
    {
        var d = Data(NIF_TIP | NIF_ICON);
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = _source?.Handle ?? IntPtr.Zero, uID = 1, uFlags = flags,
        uCallbackMessage = WM_APP_TRAY, hIcon = _icon, szTip = _tip, szInfo = "", szInfoTitle = "",
    };

    private void LoadIcon()
    {
        try
        {
            var small = new IntPtr[1];
            if (Environment.ProcessPath is { } exe && ExtractIconExW(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
            { _icon = small[0]; _ownsIcon = true; return; }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        _icon = LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION (shared, not destroyed)
        _ownsIcon = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = lParam.ToInt32() & 0xFFFF;
            if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK) { handled = true; _onCommand(Command.Open); }
            else if (ev == WM_RBUTTONUP) { handled = true; ShowMenu(hwnd); }
        }
        else if (_taskbarCreated != 0 && msg == _taskbarCreated && _added)
        {
            _added = false; // Explorer restarted: add again
            Show();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)Command.Open, "Open");
            AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)Command.Settings, "Settings…");
            AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)Command.Controls, "Controls & hotkeys");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)Command.Quit, "Quit");
            var (x, y) = Desktop.Cursor();
            NativeMethods.SetForegroundWindow(hwnd);
            int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, hwnd, IntPtr.Zero);
            PostMessageW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd is >= 1 and <= 4) _onCommand((Command)cmd);
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        Hide();
        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
