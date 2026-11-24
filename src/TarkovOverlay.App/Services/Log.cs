using System.Text;

namespace TarkovOverlay.App.Services;

/// <summary>
/// Small rotating text log in %LOCALAPPDATA%\TarkovPriceOverlay\logs\overlay.log (1 MB, 3 old files kept).
/// Records card show/hide events, bounds, monitor and lookup summaries. No screen pixels are written.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private const int Keep = 3;
    private static readonly object Gate = new();
    private static string? _dir;

    public static string Directory => _dir ??= Path.Combine(new Core.AppPaths().Root, "logs");
    public static string FilePath => Path.Combine(Directory, "overlay.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var f = new FileInfo(FilePath);
                if (f.Exists && f.Length > MaxBytes) Rotate();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Rotate()
    {
        for (int i = Keep; i >= 1; i--)
        {
            var src = i == 1 ? FilePath : $"{FilePath}.{i - 1}";
            var dst = $"{FilePath}.{i}";
            if (!File.Exists(src)) continue;
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }
    }
}
