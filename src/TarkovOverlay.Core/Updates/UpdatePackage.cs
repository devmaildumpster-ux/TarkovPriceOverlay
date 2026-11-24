using System.IO.Compression;
using System.Reflection;

namespace TarkovOverlay.Core.Updates;

/// <summary>A plain-words failure of Update now (shown in the banner / Settings and written to overlay.log).</summary>
public sealed class UpdateException : Exception
{
    /// <summary>The install folder can't be written without admin rights (e.g. under Program Files).</summary>
    public bool NeedsAdmin { get; }
    public UpdateException(string message, bool needsAdmin = false, Exception? inner = null) : base(message, inner) { NeedsAdmin = needsAdmin; }
}

/// <summary>Which of the two builds in the full zip this install is.</summary>
public enum InstallKind { SelfContained, FrameworkDependent }

/// <summary>Which release file Update now picked and why.</summary>
public sealed record AssetChoice(ReleaseAsset Asset, bool IsFullZip, InstallKind Kind);

/// <summary>1.2.8: picking, checking and unpacking the release file for Update now. Pure / file-system only (unit tested).</summary>
public static class UpdatePackage
{
    public const string ExeName = "TarkovPriceOverlay.exe", MainDll = "TarkovPriceOverlay.dll", UpdaterExe = "TarkovPriceOverlay.Updater.exe";
    public const string FullZipName = "TarkovPriceOverlay-win-x64.zip", SumsName = "SHA256SUMS.txt";
    public const string FullZipRoot = "TarkovPriceOverlay";
    public static string UpdateZipName(AppVersion v) => $"TarkovPriceOverlay-update-{v.Major}.{v.Minor}.{v.Patch}.zip";
    public static string KindFolder(InstallKind k) => k == InstallKind.SelfContained ? "self-contained" : "framework-dependent";

    /// <summary>Hard limits so a broken or hostile file can't fill the disk.</summary>
    public const long MaxDownloadBytes = 400L * 1024 * 1024, MaxUnpackedBytes = 1024L * 1024 * 1024, MaxSumsBytes = 64 * 1024;
    public const int MaxEntries = 5000;

    /// <summary>The self-contained build ships its own .NET runtime (hostfxr.dll / coreclr.dll next to the exe).</summary>
    public static InstallKind DetectKind(string installDir) =>
        File.Exists(Path.Combine(installDir, "hostfxr.dll")) || File.Exists(Path.Combine(installDir, "coreclr.dll"))
            ? InstallKind.SelfContained : InstallKind.FrameworkDependent;

    /// <summary>
    /// Self-contained installs take the small update zip for exactly this release's version (it holds every file that
    /// changed since 1.0.0, built from the self-contained folder); otherwise, or for framework-dependent installs
    /// (their runtime files differ), the full win-x64 zip. Null when the release has neither.
    /// </summary>
    public static AssetChoice? SelectAsset(ReleaseInfo release, InstallKind kind)
    {
        if (release.Version is not { } v) return null;
        ReleaseAsset? Find(string name) => release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (kind == InstallKind.SelfContained && Find(UpdateZipName(v)) is { } upd) return new(upd, false, kind);
        return Find(FullZipName) is { } full ? new(full, true, kind) : null;
    }

    private static readonly string[] GitHubHosts = { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    /// <summary>
    /// Downloads are only accepted over HTTPS from github.com (this repo's release downloads) and GitHub's two asset
    /// CDN hosts it redirects to. <paramref name="testOrigin"/> is set only when TPO_UPDATE_URL overrides the API for a
    /// local test; then that exact origin is accepted too (plain http only on loopback).
    /// </summary>
    public static bool IsAllowedDownload(Uri u, Uri? testOrigin = null, bool firstHop = true)
    {
        if (!u.IsAbsoluteUri) return false;
        if (testOrigin != null && SameOrigin(u, testOrigin) && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
            return true;
        if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || !string.IsNullOrEmpty(u.UserInfo)) return false;
        var host = u.IdnHost.ToLowerInvariant();
        if (!GitHubHosts.Contains(host)) return false;
        // The asset link itself must be this repo's release download; the CDN hops after it carry signed tokens.
        if (firstHop || host == "github.com")
            return host == "github.com" && u.AbsolutePath.StartsWith($"/{UpdateChecker.Owner}/{UpdateChecker.Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    /// <summary>
    /// A zip entry name is safe when it is a plain relative path: no "..", no root or drive, no ':' (alternate data
    /// streams), no control characters, and not a reserved device name. Returns the normalised '/'-separated path.
    /// </summary>
    public static string? SafeRelative(string entryName)
    {
        if (string.IsNullOrEmpty(entryName)) return null;
        var n = entryName.Replace('\\', '/');
        if (n.StartsWith('/') || n.Contains(':') || n.Any(c => c < 0x20 || c == '"' || c == '<' || c == '>' || c == '|' || c == '?' || c == '*')) return null;
        var parts = n.Split('/');
        var keep = new List<string>();
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0) { if (i == parts.Length - 1) break; return null; } // trailing '/' = directory entry; '//' = junk
            if (p == "." || p == "..") return null;
            if (p.EndsWith('.') || p.EndsWith(' ')) return null;              // Windows silently strips these
            var stem = p.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                return null;
            keep.Add(p);
        }
        return keep.Count == 0 ? null : string.Join('/', keep);
    }

    /// <summary>
    /// Checks the zip's layout and unpacks the files for this install into <paramref name="stagingDir"/> (emptied first).
    /// Update zip: files at the top level (plus licenses/ etc.), must hold TarkovPriceOverlay.exe and .dll.
    /// Full zip: everything under TarkovPriceOverlay/, and TarkovPriceOverlay/&lt;kind&gt;/TarkovPriceOverlay.exe must exist;
    /// only that folder is unpacked. Then the staged TarkovPriceOverlay.dll must carry the release's version.
    /// Returns the staged relative paths.
    /// </summary>
    public static IReadOnlyList<string> ExtractAndValidate(string zipPath, string stagingDir, AssetChoice choice, AppVersion expected,
        Func<string, AppVersion?>? versionOf = null)
    {
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
        Directory.CreateDirectory(stagingDir);
        var root = Path.GetFullPath(stagingDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var prefix = choice.IsFullZip ? $"{FullZipRoot}/{KindFolder(choice.Kind)}/" : "";
        var staged = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count == 0) throw new UpdateException("The downloaded zip is empty.");
            if (zip.Entries.Count > MaxEntries) throw new UpdateException($"The downloaded zip has too many files ({zip.Entries.Count}).");
            long total = 0;
            var files = new List<(ZipArchiveEntry Entry, string Rel)>();
            foreach (var e in zip.Entries)
            {
                var rel = SafeRelative(e.FullName) ?? throw new UpdateException($"The downloaded zip has an unsafe file name ({e.FullName}); not installing it.");
                bool isDir = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
                if (choice.IsFullZip)
                {
                    if (!rel.StartsWith(FullZipRoot + "/", StringComparison.Ordinal) && rel != FullZipRoot)
                        throw new UpdateException($"The full zip isn't laid out as expected ({e.FullName} is outside {FullZipRoot}/).");
                }
                else if (rel.StartsWith(FullZipRoot + "/", StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("The update zip is laid out like the full zip; not installing it.");
                if (isDir) continue;
                if (choice.IsFullZip && !rel.StartsWith(prefix, StringComparison.Ordinal)) continue; // the other build / top-level docs
                var inner = rel[prefix.Length..];
                total += e.Length;
                if (e.Length < 0 || total > MaxUnpackedBytes) throw new UpdateException("The downloaded zip unpacks to more than 1 GB; not installing it.");
                files.Add((e, inner));
            }
            bool Has(string name) => files.Any(f => string.Equals(f.Rel, name, StringComparison.OrdinalIgnoreCase));
            if (!Has(ExeName) || !Has(MainDll))
                throw new UpdateException(choice.IsFullZip
                    ? $"The full zip has no {prefix}{ExeName}; not installing it."
                    : $"The update zip has no {ExeName} / {MainDll} at its top level; not installing it.");
            foreach (var (e, rel) in files)
            {
                var dest = Path.GetFullPath(Path.Combine(stagingDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UpdateException($"The downloaded zip tries to write outside its folder ({e.FullName}).");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using (var src = e.Open())
                using (var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
                {
                    var buf = new byte[81920]; long written = 0; int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        written += n;
                        if (written > e.Length) throw new UpdateException($"{e.FullName} is bigger than the zip says; not installing it.");
                        dst.Write(buf, 0, n);
                    }
                }
                staged.Add(rel);
            }
        }
        catch (InvalidDataException ex) { throw new UpdateException("The downloaded file isn't a valid zip (" + ex.Message + ").", inner: ex); }

        var got = (versionOf ?? AssemblyVersionOf)(Path.Combine(stagingDir, MainDll));
        if (got is not { } g || g.Major != expected.Major || g.Minor != expected.Minor || g.Patch != expected.Patch)
            throw new UpdateException($"The downloaded files are version {got?.ToString() ?? "?"}, not {expected}; not installing them.");
        return staged;
    }

    /// <summary>The assembly version of a managed dll without loading it (null if it isn't one).</summary>
    public static AppVersion? AssemblyVersionOf(string path)
    {
        try { return AssemblyName.GetAssemblyName(path).Version is { } v ? AppVersion.From(v) : null; }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or ArgumentException) { return null; }
    }

    /// <summary>Lower-case hex SHA-256 of a file.</summary>
    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Integrity: the file must match GitHub's digest for the asset when GitHub gives one, and the SHA256SUMS.txt entry
    /// when the release has that file. At least one of the two must exist, or the file is refused.
    /// </summary>
    public static void VerifyHash(string file, string assetName, string? apiDigest, IReadOnlyDictionary<string, string>? sums, Action<string>? log = null)
    {
        string? fromSums = null;
        if (sums != null && !sums.TryGetValue(assetName, out fromSums))
            throw new UpdateException($"The release's {SumsName} has no line for {assetName}; not installing it.");
        if (apiDigest == null && fromSums == null)
            throw new UpdateException($"GitHub gave no checksum for {assetName}, so it can't be verified; not installing it. Use Open releases page instead.");
        var actual = Sha256File(file);
        if (apiDigest != null && !string.Equals(apiDigest, actual, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException($"{assetName} doesn't match GitHub's checksum (got {actual[..12]}…, expected {apiDigest[..12]}…); deleted it.");
        if (fromSums != null && !string.Equals(fromSums, actual, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException($"{assetName} doesn't match {SumsName} (got {actual[..12]}…, expected {fromSums[..12]}…); deleted it.");
        log?.Invoke($"update: {assetName} sha256 {actual} ok ({(apiDigest != null ? "GitHub digest" : "")}{(apiDigest != null && fromSums != null ? " + " : "")}{(fromSums != null ? SumsName : "")})");
    }

    /// <summary>Can this process create files in <paramref name="dir"/>? (false under Program Files without admin).</summary>
    public static bool CanWrite(string dir)
    {
        var probe = Path.Combine(dir, ".tpo-write-test-" + Guid.NewGuid().ToString("N"));
        try { using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { } return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
        finally { try { if (File.Exists(probe)) File.Delete(probe); } catch { /* best effort */ } }
    }
}
