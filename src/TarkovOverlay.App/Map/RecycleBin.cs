using System.Runtime.InteropServices;

namespace TarkovOverlay.App.Map;

/// <summary>Sends one file to the Windows Recycle Bin silently (SHFileOperationW, FO_DELETE + FOF_ALLOWUNDO).</summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400, FOF_NOCONFIRMMKDIR = 0x0200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);

    public static void Send(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recycle Bin is only available on Windows");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return;
        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = full + "\0\0", // double-null-terminated list with one file
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR),
        };
        int rc = SHFileOperationW(ref op);
        if (rc != 0 || op.fAnyOperationsAborted) throw new IOException($"Recycle Bin move failed (code 0x{rc:X})");
    }
}
