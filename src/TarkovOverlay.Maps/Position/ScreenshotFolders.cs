namespace TarkovOverlay.Maps.Position;

/// <summary>
/// Where EFT saves screenshots: "&lt;Documents&gt;\Escape from Tarkov\Screenshots". Documents is often redirected
/// (OneDrive backup, moved to another drive), so several places are checked in order.
/// </summary>
public static class ScreenshotFolders
{
    public sealed record Candidate(string Path, string Source, bool Exists);

    public const string Sub = @"Escape from Tarkov\Screenshots";

    /// <summary>Candidate folders, best first. <paramref name="env"/> / <paramref name="myDocuments"/> are injectable for tests.</summary>
    public static IReadOnlyList<Candidate> Candidates(string? overrideDir = null, Func<string, string?>? env = null, string? myDocuments = null,
        Func<string, bool>? exists = null, Func<IEnumerable<string>>? fixedDrives = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        exists ??= Directory.Exists;
        myDocuments ??= SafeMyDocuments();
        var list = new List<(string path, string source)>();
        if (!string.IsNullOrWhiteSpace(overrideDir)) list.Add((overrideDir.Trim().Trim('"'), "Map settings"));
        // GetFolderPath(MyDocuments) uses SHGetKnownFolderPath(FOLDERID_Documents): follows OneDrive / moved Documents.
        if (!string.IsNullOrEmpty(myDocuments)) list.Add((Combine(myDocuments, Sub), "Documents (Windows known folder)"));
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (env(v) is { Length: > 0 } od)
            {
                list.Add((Combine(od, "Documents", Sub), $"%{v}%\\Documents"));
            }
        if (env("USERPROFILE") is { Length: > 0 } up)
        {
            list.Add((Combine(up, "OneDrive", "Documents", Sub), @"%USERPROFILE%\OneDrive\Documents"));
            list.Add((Combine(up, "Documents", Sub), @"%USERPROFILE%\Documents"));
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Candidate>();
        foreach (var (path, source) in list)
        {
            var norm = path.TrimEnd('\\', '/');
            if (!seen.Add(norm)) continue;
            bool ok; try { ok = exists(norm); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
            result.Add(new Candidate(norm, source, ok));
        }
        // Fallback: Documents folders at the root of other fixed drives (e.g. D:\Documents) when nothing above exists.
        if (string.IsNullOrWhiteSpace(overrideDir) && !result.Any(c => c.Exists))
        {
            var user = env("USERNAME");
            foreach (var root in SafeDrives(fixedDrives ?? FixedDriveRoots))
            {
                var paths = new List<(string, string)> { (Combine(root, "Documents", Sub), $"{root.TrimEnd('\\', '/')}\\Documents (drive scan)") };
                if (!string.IsNullOrEmpty(user)) paths.Add((Combine(root, "Users", user, "Documents", Sub), $"{root.TrimEnd('\\', '/')}\\Users\\{user}\\Documents (drive scan)"));
                foreach (var (path, source) in paths)
                {
                    var norm = path.TrimEnd('\\', '/');
                    if (!seen.Add(norm)) continue;
                    bool ok; try { ok = exists(norm); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
                    result.Add(new Candidate(norm, source, ok));
                }
            }
        }
        return result;
    }

    private static IEnumerable<string> SafeDrives(Func<IEnumerable<string>> drives)
    {
        try { return drives().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Roots of ready fixed drives ("C:\\", "E:\\"…).</summary>
    public static IEnumerable<string> FixedDriveRoots()
    {
        var res = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.DriveType == DriveType.Fixed && d.IsReady) res.Add(d.RootDirectory.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // on Linux (tests) "/" etc. are fixed too; harmless.
        return res;
    }

    /// <summary>The folder to watch: the override if set (even if missing), else the first existing candidate, else the known-folder default.</summary>
    public static Candidate Resolve(IReadOnlyList<Candidate> candidates)
    {
        if (candidates.Count == 0) return new Candidate(Combine(".", Sub), "none", false);
        if (candidates[0].Source == "Map settings") return candidates[0];
        return candidates.FirstOrDefault(c => c.Exists) ?? candidates[0];
    }

    // Windows separators even when the tests run on Linux (paths are only compared / shown).
    private static string Combine(params string[] parts) =>
        string.Join(System.IO.Path.DirectorySeparatorChar == '\\' ? "\\" : "/",
            parts.Select((p, i) => i == 0 ? p.TrimEnd('\\', '/') : p.Trim('\\', '/')).Select(p => System.IO.Path.DirectorySeparatorChar == '\\' ? p.Replace('/', '\\') : p.Replace('\\', '/')));

    private static string? SafeMyDocuments()
    {
        try { var d = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify); return string.IsNullOrEmpty(d) ? null : d; }
        catch (PlatformNotSupportedException) { return null; }
    }
}
