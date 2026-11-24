using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TarkovOverlay.Core.Updates;

/// <summary>What the updater is asked to do (update\plan.json, written by the app just before it exits).</summary>
public sealed class UpdatePlan
{
    public string InstallDir { get; set; } = "";
    public string StagingDir { get; set; } = "";
    public string BackupDir { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    /// <summary>The app's process id; the updater waits for it to exit before touching files.</summary>
    public int WaitForPid { get; set; }
    public string LogFile { get; set; } = "";
    public string ResultFile { get; set; } = "";
    /// <summary>Extra arguments for the relaunch (tests only, e.g. --auto-close-ms).</summary>
    public string[] RelaunchArgs { get; set; } = Array.Empty<string>();
}

/// <summary>update\last-update.json: written by the updater, read (and deleted) by the relaunched app.</summary>
public sealed class UpdateOutcome
{
    public bool Ok { get; set; }
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public int FilesReplaced { get; set; }
    public int FilesAdded { get; set; }
    public bool RolledBack { get; set; }
    public bool NeedsAdmin { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>
/// 1.2.8: copies the staged files into the install folder. Every file it replaces is first copied to the backup
/// folder; if anything fails, the files already written are put back (replaced ones from the backup, new ones
/// deleted), so the install is either fully the new version or unchanged. User data (settings, caches, logs) lives
/// in %LOCALAPPDATA% and is never touched; the same names are also skipped here if a zip ever contained them.
/// Runs inside TarkovPriceOverlay.Updater.exe (and in unit tests against temp folders).
/// </summary>
public static class UpdateApplier
{
    /// <summary>Files from the zip that are never copied: user data names and the update zip's own notes.</summary>
    public static bool IsSkipped(string rel)
    {
        var n = rel.Replace('\\', '/');
        var first = n.Split('/')[0];
        if (first.Equals("cache", StringComparison.OrdinalIgnoreCase) || first.Equals("logs", StringComparison.OrdinalIgnoreCase)
            || first.Equals("diagnostics", StringComparison.OrdinalIgnoreCase) || first.Equals("update", StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(n);
        return name.Equals("settings.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("update-check.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("hideout-progress.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            || (!n.Contains('/') && (name.Equals("UPDATE-README.txt", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("CHANGELOG-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))));
    }

    /// <param name="beforeWrite">Test hook: called with each relative path just before it is written (throw to simulate a failure).</param>
    public static UpdateOutcome Apply(UpdatePlan plan, Action<string> log, Action<string>? beforeWrite = null, int retries = 10, int retryDelayMs = 300)
    {
        var o = new UpdateOutcome { FromVersion = plan.FromVersion, ToVersion = plan.ToVersion, At = DateTimeOffset.UtcNow };
        var install = Path.GetFullPath(plan.InstallDir);
        var staging = Path.GetFullPath(plan.StagingDir);
        var installRoot = install.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var journal = new List<(string Rel, string Target, bool HadOriginal)>();
        try
        {
            if (!File.Exists(Path.Combine(install, UpdatePackage.ExeName)))
                throw new UpdateException($"{install} doesn't look like the overlay's folder (no {UpdatePackage.ExeName}).");
            if (!Directory.Exists(staging)) throw new UpdateException("The unpacked update is missing.");
            if (!UpdatePackage.CanWrite(install))
                throw new UpdateException($"Windows won't let the overlay change its own folder ({install}); it needs admin rights there (for example under Program Files). " +
                    "Move the TarkovPriceOverlay folder somewhere you own, such as another drive, or install the update by hand from the releases page.", needsAdmin: true);

            var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(staging, f).Replace('\\', '/'))
                .OrderBy(r => r.Equals(UpdatePackage.ExeName, StringComparison.OrdinalIgnoreCase) ? 1 : 0) // the exe last
                .ThenBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
            if (Directory.Exists(plan.BackupDir)) Directory.Delete(plan.BackupDir, recursive: true);
            Directory.CreateDirectory(plan.BackupDir);
            log($"updater: applying {plan.FromVersion} → {plan.ToVersion}: {files.Count} file(s) into {install}; backup in {plan.BackupDir}");

            foreach (var rel in files)
            {
                if (UpdatePackage.SafeRelative(rel) is null) throw new UpdateException($"Unsafe file name in the update ({rel}).");
                if (IsSkipped(rel)) { log($"updater: skipped {rel} (user data / notes, never copied)"); continue; }
                var src = Path.Combine(staging, rel);
                var target = Path.GetFullPath(Path.Combine(install, rel));
                if (!target.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase)) throw new UpdateException($"{rel} would land outside the install folder.");
                bool had = File.Exists(target);
                if (had)
                {
                    var bak = Path.Combine(plan.BackupDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                    Retry(() => File.Copy(target, bak, overwrite: true), retries, retryDelayMs);
                }
                journal.Add((rel, target, had));
                beforeWrite?.Invoke(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var tmp = target + ".tpo-new";
                File.Copy(src, tmp, overwrite: true);
                Retry(() => File.Move(tmp, target, overwrite: true), retries, retryDelayMs);
                if (new FileInfo(target).Length != new FileInfo(src).Length) throw new IOException($"{rel} didn't copy completely.");
                if (had) o.FilesReplaced++; else o.FilesAdded++;
            }
            o.Ok = true;
            log($"updater: done · {o.FilesReplaced} replaced, {o.FilesAdded} added");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UpdateException or ArgumentException or NotSupportedException)
        {
            o.Ok = false;
            o.NeedsAdmin = ex is UpdateException { NeedsAdmin: true } || ex is UnauthorizedAccessException;
            o.Error = ex is UnauthorizedAccessException
                ? $"Windows refused to write a file in {install} ({ex.Message}). The folder may need admin rights, or the file is in use."
                : ex.Message;
            log($"updater: FAILED: {ex.GetType().Name}: {ex.Message}");
            if (journal.Count > 0) o.RolledBack = Rollback(plan, journal, log, retries, retryDelayMs);
        }
        return o;
    }

    private static bool Rollback(UpdatePlan plan, List<(string Rel, string Target, bool HadOriginal)> journal, Action<string> log, int retries, int delay)
    {
        bool ok = true;
        for (int i = journal.Count - 1; i >= 0; i--)
        {
            var (rel, target, had) = journal[i];
            try
            {
                if (File.Exists(target + ".tpo-new")) File.Delete(target + ".tpo-new");
                if (had) Retry(() => File.Copy(Path.Combine(plan.BackupDir, rel), target, overwrite: true), retries, delay);
                else if (File.Exists(target)) Retry(() => File.Delete(target), retries, delay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
                log($"updater: rollback of {rel} FAILED: {ex.Message} (the original is in {plan.BackupDir})");
            }
        }
        log(ok ? $"updater: rolled back {journal.Count} file(s); the install is unchanged ({plan.FromVersion})"
               : $"updater: rollback incomplete; copy the files from {plan.BackupDir} back by hand");
        return ok;
    }

    /// <summary>Antivirus scanners and the just-exited app can hold a file for a moment: retry sharing errors.</summary>
    private static void Retry(Action a, int tries, int delayMs)
    {
        for (int i = 1; ; i++)
        {
            try { a(); return; }
            catch (IOException) when (i < tries) { Thread.Sleep(delayMs); }
        }
    }

    public static void WritePlan(string path, UpdatePlan p) => Write(path, p, UpdateJson.Default.UpdatePlan);
    public static UpdatePlan? ReadPlan(string path) => Read(path, UpdateJson.Default.UpdatePlan);
    public static void WriteOutcome(string path, UpdateOutcome o) => Write(path, o, UpdateJson.Default.UpdateOutcome);
    public static UpdateOutcome? ReadOutcome(string path) => Read(path, UpdateJson.Default.UpdateOutcome);

    private static void Write<T>(string path, T value, JsonTypeInfo<T> info) => AppPaths.AtomicWrite(path, s => JsonSerializer.Serialize(s, value, info));

    private static T? Read<T>(string path, JsonTypeInfo<T> info) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), info) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
}

/// <summary>Source-generated JSON (the updater is trimmed, so reflection-based JSON is off there).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UpdatePlan))]
[JsonSerializable(typeof(UpdateOutcome))]
internal sealed partial class UpdateJson : JsonSerializerContext { }
