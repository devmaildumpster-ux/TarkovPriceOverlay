namespace TarkovOverlay.Core;

/// <summary>App-specific folders. Defaults to %LOCALAPPDATA%\TarkovPriceOverlay; tests pass a temp root.</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "TarkovPriceOverlay");
    }
    public string CacheDir(string modeId) => Path.Combine(Root, "cache", Sanitize(modeId));
    public string CatalogFile(string modeId) => Path.Combine(CacheDir(modeId), "catalog.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string DiagnosticsDir => Path.Combine(Root, "diagnostics");
    /// <summary>1.2.6: last GitHub release check (6 h cache).</summary>
    public string UpdateCheckFile => Path.Combine(Root, "update-check.json");
    /// <summary>1.2.8: Update now's download, staging, backup, plan and result (update\).</summary>
    public string UpdateDir => Path.Combine(Root, "update");

    private static string Sanitize(string s) => string.Concat(s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));

    /// <summary>Writes to a temp file in the same folder and then atomically replaces the destination.</summary>
    public static void AtomicWrite(string path, Action<Stream> write)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                write(fs);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
