using System.Text.Json;
using TarkovOverlay.Core;

namespace TarkovOverlay.Maps.Position;

public enum ScreenshotCleanupMode { KeepNewest, DeleteImmediately, MoveToRecycleBin, Off }

/// <summary>Deletes or recycles one file. The Windows app supplies a Recycle Bin implementation.</summary>
public interface IFileDisposer
{
    void Delete(string path);
    /// <summary>Moves to the Recycle Bin; throws when not possible (the file is then left alone).</summary>
    void Recycle(string path);
}

public sealed class PlainFileDisposer : IFileDisposer
{
    private readonly Action<string>? _recycle;
    public PlainFileDisposer(Action<string>? recycle = null) { _recycle = recycle; }
    public void Delete(string path) => File.Delete(path);
    public void Recycle(string path)
    {
        if (_recycle == null) throw new PlatformNotSupportedException("Recycle Bin not available");
        _recycle(path);
    }
}

/// <summary>
/// Cleans up Tarkov position screenshots so the folder doesn't grow forever.
/// Safety rules: only files directly in the watched folder whose NAME parses as an EFT coordinate screenshot
/// (<see cref="ScreenshotName.TryParse"/>) with a .png/.jpg/.jpeg extension are ever touched; never sub-folders,
/// menu/hideout screenshots, read-only files or anything else. A file is only removed after its name parsed.
/// I/O errors (Tarkov still writing, file in use, no permission) are logged and skipped, never thrown.
/// </summary>
public sealed class ScreenshotCleaner
{
    private static readonly string[] Exts = { ".png", ".jpg", ".jpeg" };
    private readonly IFileDisposer _disposer;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    public ScreenshotCleanupMode Mode { get; set; } = ScreenshotCleanupMode.KeepNewest;
    public int KeepCount { get; set; } = 5;
    /// <summary>Retries for a file that's still locked (DeleteImmediately right after Tarkov wrote it).</summary>
    public int LockedRetries { get; set; } = 5;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public ScreenshotCleaner(IFileDisposer disposer, Action<string>? log = null) { _disposer = disposer; _log = log ?? (_ => { }); }

    /// <summary>True if this path may be cleaned: directly inside <paramref name="folder"/>, coordinate name, png/jpg.</summary>
    public static bool IsCleanable(string folder, string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name) || !Exts.Contains(Path.GetExtension(name).ToLowerInvariant())) return false;
            if (ScreenshotName.TryParse(name) == null) return false;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir == null || !string.Equals(dir.TrimEnd('\\', '/'), Path.GetFullPath(folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
            var attr = File.GetAttributes(path);
            return (attr & (FileAttributes.Directory | FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>Coordinate screenshots in the folder, newest first.</summary>
    public static List<FileInfo> CoordinateShots(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => IsCleanable(folder, f.FullName))
                .OrderByDescending(f => f.LastWriteTimeUtc > f.CreationTimeUtc ? f.LastWriteTimeUtc : f.CreationTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new List<FileInfo>(); }
    }

    /// <summary>After a coordinate screenshot was read (its position is already saved). Returns files removed.</summary>
    public int AfterRead(string folder, string justRead)
    {
        if (Mode == ScreenshotCleanupMode.Off) return 0;
        if (Mode == ScreenshotCleanupMode.DeleteImmediately)
        {
            int n = RemoveWithRetry(folder, justRead) ? 1 : 0;
            return n + Trim(folder, "after reading"); // also anything older left behind
        }
        return Trim(folder, "after reading");
    }

    /// <summary>Applies the mode to the whole folder (start-up / after a new file). Returns files removed.</summary>
    public int Trim(string folder, string why = "start-up")
    {
        if (Mode == ScreenshotCleanupMode.Off || !Directory.Exists(folder)) return 0;
        lock (_gate)
        {
            var shots = CoordinateShots(folder);
            int keep = Mode == ScreenshotCleanupMode.DeleteImmediately ? 0 : Math.Max(0, KeepCount);
            int removed = 0, failed = 0;
            foreach (var f in shots.Skip(keep))
            {
                if (TryRemove(folder, f.FullName, out _)) removed++; else failed++;
            }
            if (removed > 0 || failed > 0)
                _log($"map: screenshot cleanup ({why}, {Describe(Mode, KeepCount)}): {(Mode == ScreenshotCleanupMode.MoveToRecycleBin ? "recycled" : "deleted")} {removed} old position screenshot(s)" +
                     (failed > 0 ? $", {failed} skipped (in use or no permission)" : "") + $"; {Math.Min(shots.Count, keep)} kept");
            return removed;
        }
    }

    private bool RemoveWithRetry(string folder, string path)
    {
        for (int i = 0; i <= LockedRetries; i++)
        {
            lock (_gate)
            {
                if (!File.Exists(path)) return false;
                if (TryRemove(folder, path, out bool retry)) { _log($"map: screenshot cleanup: {(Mode == ScreenshotCleanupMode.MoveToRecycleBin ? "recycled" : "deleted")} {Path.GetFileName(path)} after reading it"); return true; }
                if (!retry) return false;
            }
            if (i < LockedRetries) Thread.Sleep(RetryDelay);
        }
        _log($"map: screenshot cleanup: {Path.GetFileName(path)} still in use, left for the next cleanup");
        return false;
    }

    private bool TryRemove(string folder, string path, out bool retry)
    {
        retry = false;
        if (!IsCleanable(folder, path)) return false; // re-checked right before touching the file
        try
        {
            if (Mode == ScreenshotCleanupMode.MoveToRecycleBin) _disposer.Recycle(path);
            else _disposer.Delete(path);
            return true;
        }
        catch (IOException ex) { retry = true; _log($"map: screenshot cleanup: couldn't remove {Path.GetFileName(path)} ({ex.Message})"); }
        catch (UnauthorizedAccessException ex) { _log($"map: screenshot cleanup: no permission for {Path.GetFileName(path)} ({ex.Message})"); }
        catch (PlatformNotSupportedException ex) { _log($"map: screenshot cleanup: {ex.Message}; {Path.GetFileName(path)} left alone"); }
        return false;
    }

    public static string Describe(ScreenshotCleanupMode m, int keep) => m switch
    {
        ScreenshotCleanupMode.KeepNewest => $"keep newest {keep}",
        ScreenshotCleanupMode.DeleteImmediately => "delete right after reading",
        ScreenshotCleanupMode.MoveToRecycleBin => $"keep newest {keep}, older to the Recycle Bin",
        _ => "off",
    };
}

/// <summary>Last position read (last-position.json), so the dot survives a restart even when the file was cleaned up.</summary>
public sealed record SavedPosition(string FileName, double X, double Y, double Z, double Yaw, string? Map, DateTimeOffset SeenAt)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Save(string file, PositionFix fix, string? map)
    {
        var s = new SavedPosition(Path.GetFileName(fix.File), fix.Data.Position.X, fix.Data.Position.Y, fix.Data.Position.Z, fix.Data.YawDegrees, map, fix.SeenAt);
        AppPaths.AtomicWrite(file, st => JsonSerializer.Serialize(st, s, Json));
    }

    /// <summary>The saved fix if it exists and is younger than <paramref name="maxAge"/>; null otherwise (never throws).</summary>
    public static (PositionFix Fix, string? Map)? Load(string file, TimeSpan maxAge, string? folder = null)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var s = JsonSerializer.Deserialize<SavedPosition>(File.ReadAllText(file), Json);
            if (s == null || DateTimeOffset.Now - s.SeenAt > maxAge) return null;
            var parsed = ScreenshotName.TryParse(s.FileName);
            if (parsed == null) return null;
            return (new PositionFix(parsed, folder != null ? Path.Combine(folder, s.FileName) : s.FileName, s.SeenAt), s.Map);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
}
