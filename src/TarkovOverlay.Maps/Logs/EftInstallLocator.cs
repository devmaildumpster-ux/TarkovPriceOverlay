using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TarkovOverlay.Maps.Logs;

/// <summary>
/// Finds Escape from Tarkov's Logs folder: configured path → running game process → uninstall registry keys →
/// BSG launcher settings ("gameRootDir") → Steam library (app 3932890) → common default folders.
/// </summary>
public static class EftInstallLocator
{
    public const string SteamAppId = "3932890";
    private static readonly string[] UninstallKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + SteamAppId,
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + SteamAppId,
    };

    public sealed record Found(string InstallDir, string LogsDir, string Source);

    /// <summary>Logs folder inside an install folder ("Logs" or "build\Logs"), or the folder itself if it is a Logs folder.</summary>
    public static string? LogsDirFor(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            dir = dir.Trim().Trim('"');
            if (!Directory.Exists(dir)) return null;
            if (Path.GetFileName(dir.TrimEnd('\\', '/')).Equals("Logs", StringComparison.OrdinalIgnoreCase) &&
                Directory.EnumerateDirectories(dir, "log_*").Any()) return Path.GetFullPath(dir);
            foreach (var c in new[] { Path.Combine(dir, "Logs"), Path.Combine(dir, "build", "Logs") })
                if (Directory.Exists(c)) return Path.GetFullPath(c);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        return null;
    }

    public static Found? Locate(string? configured, Action<string>? log = null)
    {
        foreach (var (dir, source) in Candidates(configured))
        {
            var logs = LogsDirFor(dir);
            if (logs != null)
            {
                log?.Invoke($"map: EFT logs folder {logs} (from {source})");
                return new Found(dir, logs, source);
            }
        }
        log?.Invoke("map: EFT logs folder not found (set the game folder in More ▾ › Map settings)");
        return null;
    }

    public static IEnumerable<(string Dir, string Source)> Candidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return (configured, "setting");
        foreach (var d in FromProcess()) yield return (d, "running game");
        if (OperatingSystem.IsWindows())
            foreach (var d in FromRegistry()) yield return (d, "registry");
        foreach (var d in FromLauncherSettings()) yield return (d, "BSG launcher settings");
        if (OperatingSystem.IsWindows())
            foreach (var d in FromSteam()) yield return (d, "Steam");
        foreach (var d in Defaults()) yield return (d, "default folder");
    }

    private static IEnumerable<string> FromProcess()
    {
        var res = new List<string>();
        try
        {
            foreach (var p in Process.GetProcessesByName("EscapeFromTarkov"))
            {
                using (p)
                {
                    try { if (p.MainModule?.FileName is { } f && Path.GetDirectoryName(f) is { } d) res.Add(d); }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                }
            }
        }
        catch (InvalidOperationException) { }
        return res;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> FromRegistry()
    {
        var res = new List<string>();
        foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using var b = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    foreach (var k in UninstallKeys)
                    {
                        using var key = b.OpenSubKey(k);
                        if (key?.GetValue("InstallLocation") is string s && s.Length > 0 && !res.Contains(s, StringComparer.OrdinalIgnoreCase)) res.Add(s);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        return res;
    }

    /// <summary>%APPDATA%\Battlestate Games\BsgLauncher\settings is JSON with "gameRootDir".</summary>
    internal static IEnumerable<string> FromLauncherSettings(string? settingsFile = null)
    {
        var res = new List<string>();
        try
        {
            settingsFile ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Battlestate Games", "BsgLauncher", "settings");
            if (!File.Exists(settingsFile)) return res;
            var text = File.ReadAllText(settingsFile);
            try
            {
                using var d = JsonDocument.Parse(text);
                if (d.RootElement.TryGetProperty("gameRootDir", out var g) && g.ValueKind == JsonValueKind.String && g.GetString() is { Length: > 0 } s) res.Add(s);
            }
            catch (JsonException)
            {
                var m = Regex.Match(text, "\"gameRootDir\"\\s*:\\s*\"(?<p>(?:[^\"\\\\]|\\\\.)*)\"");
                if (m.Success) res.Add(Regex.Unescape(m.Groups["p"].Value));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return res;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> FromSteam()
    {
        var res = new List<string>();
        var roots = new List<string>();
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string sp) roots.Add(sp.Replace('/', '\\'));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        roots.Add(@"C:\Program Files (x86)\Steam");
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var libs = new List<string> { root };
            try
            {
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(?<p>[^\"]+)\""))
                        libs.Add(m.Groups["p"].Value.Replace(@"\\", @"\"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (var lib in libs)
            {
                var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{SteamAppId}.acf");
                try
                {
                    if (File.Exists(manifest))
                    {
                        var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"(?<d>[^\"]+)\"");
                        if (m.Success) res.Add(Path.Combine(lib, "steamapps", "common", m.Groups["d"].Value));
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                res.Add(Path.Combine(lib, "steamapps", "common", "Escape from Tarkov"));
            }
        }
        return res;
    }

    private static IEnumerable<string> Defaults()
    {
        var res = new List<string>();
        IEnumerable<string> drives;
        try { drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName).ToList(); }
        catch (IOException) { drives = new[] { @"C:\" }; }
        catch (UnauthorizedAccessException) { drives = new[] { @"C:\" }; }
        foreach (var d in drives)
            foreach (var sub in new[] { @"Battlestate Games\EFT", @"Battlestate Games\Escape from Tarkov", @"Games\Battlestate Games\EFT", @"Games\EFT", @"Escape from Tarkov", "EFT" })
                res.Add(Path.Combine(d, sub));
        return res;
    }

    /// <summary>Newest "log_*" session folder (by the timestamp in its name, falling back to write time).</summary>
    public static string? NewestSessionFolder(string logsDir)
    {
        try
        {
            return Directory.EnumerateDirectories(logsDir, "log_*")
                .Select(d => (d, t: Directory.GetLastWriteTimeUtc(d)))
                .OrderByDescending(x => FolderStamp(x.d) ?? x.t).ThenByDescending(x => x.t)
                .Select(x => x.d).FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>"log_2025.07.29_1-26-18_0.16.8.1.38114" → 2025-07-29 01:26:18 (local).</summary>
    public static DateTime? FolderStamp(string folder)
    {
        var m = Regex.Match(Path.GetFileName(folder), @"log_(?<y>\d{4})\.(?<mo>\d{1,2})\.(?<d>\d{1,2})_(?<h>\d{1,2})-(?<mi>\d{1,2})-(?<s>\d{1,2})");
        if (!m.Success) return null;
        try
        {
            return new DateTime(int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["mo"].Value), int.Parse(m.Groups["d"].Value),
                int.Parse(m.Groups["h"].Value), int.Parse(m.Groups["mi"].Value), int.Parse(m.Groups["s"].Value), DateTimeKind.Local).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>The application / notifications log files of one session folder (newest file per kind).</summary>
    public static (string? Application, string? Notifications) SessionFiles(string folder)
    {
        try
        {
            var files = Directory.EnumerateFiles(folder, "*.log").ToList();
            string? Pick(string kind) => files.Where(f => Path.GetFileName(f).Contains(kind, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            return (Pick("application"), Pick("notifications"));
        }
        catch (IOException) { return (null, null); }
        catch (UnauthorizedAccessException) { return (null, null); }
    }
}
