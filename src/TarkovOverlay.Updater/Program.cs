using System.Diagnostics;
using System.Text;
using TarkovOverlay.Core.Updates;

namespace TarkovOverlay.Updater;

/// <summary>
/// TarkovPriceOverlay.Updater.exe --plan &lt;update\plan.json&gt;
/// 1. waits for the overlay (plan.WaitForPid) to exit and its single-instance lock to go away,
/// 2. copies the staged files into the install folder (UpdateApplier: backup, rollback on any failure),
/// 3. writes update\last-update.json and relaunches the overlay (the new version, or the old one after a rollback).
/// Everything is appended to the overlay's own overlay.log ("updater:" lines). Never elevates.
/// </summary>
public static class Program
{
    private static string? _log;

    public static int Main(string[] args)
    {
        int i = Array.IndexOf(args, "--plan");
        if (i < 0 || i + 1 >= args.Length) return 2;
        var planFile = Path.GetFullPath(args[i + 1]);
        var plan = UpdateApplier.ReadPlan(planFile);
        if (plan == null) return 3;
        _log = string.IsNullOrWhiteSpace(plan.LogFile) ? null : plan.LogFile;
        Log($"updater: started (pid {Environment.ProcessId}) for {plan.FromVersion} → {plan.ToVersion}");

        UpdateOutcome outcome;
        // The plan must point at the staging folder next to it (both live in %LOCALAPPDATA%\TarkovPriceOverlay\update).
        var updateDir = Path.GetDirectoryName(planFile)!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(plan.StagingDir).StartsWith(updateDir, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(plan.BackupDir).StartsWith(updateDir, StringComparison.OrdinalIgnoreCase))
        {
            outcome = Fail(plan, "The update plan points outside the update folder; nothing was changed.");
        }
        else if (!WaitForOverlayExit(plan.WaitForPid, TimeSpan.FromSeconds(60)))
        {
            outcome = Fail(plan, "The overlay didn't close within 60 s, so nothing was changed. Close it and try Update now again.");
            Save(plan, outcome);
            return 1; // it is still running: don't start a second copy
        }
        else
        {
            outcome = UpdateApplier.Apply(plan, Log);
        }
        Save(plan, outcome);
        Relaunch(plan);
        return outcome.Ok ? 0 : 1;
    }

    private static UpdateOutcome Fail(UpdatePlan plan, string why)
    {
        Log("updater: FAILED: " + why);
        return new UpdateOutcome { Ok = false, FromVersion = plan.FromVersion, ToVersion = plan.ToVersion, Error = why, At = DateTimeOffset.UtcNow };
    }

    private static void Save(UpdatePlan plan, UpdateOutcome o)
    {
        try { UpdateApplier.WriteOutcome(plan.ResultFile, o); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("updater: couldn't write the result file: " + ex.Message); }
    }

    private static bool WaitForOverlayExit(int pid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        if (pid > 0)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                Log($"updater: waiting for the overlay (pid {pid}) to exit…");
                if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { Log("updater: the overlay is still running after 60 s"); return false; }
            }
            catch (ArgumentException) { /* already gone */ }
            catch (InvalidOperationException) { }
        }
        // The single-instance mutex goes away when the last overlay process ends.
        while (sw.Elapsed < timeout)
        {
            if (!Mutex.TryOpenExisting(@"Local\TarkovPriceOverlay.SingleInstance", out var m)) { Log($"updater: overlay closed after {sw.ElapsedMilliseconds} ms"); return true; }
            m.Dispose();
            Thread.Sleep(250);
        }
        Log("updater: another overlay window is still open after 60 s");
        return false;
    }

    private static void Relaunch(UpdatePlan plan)
    {
        var exe = Path.Combine(plan.InstallDir, UpdatePackage.ExeName);
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = plan.InstallDir };
            foreach (var a in plan.RelaunchArgs) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            Log($"updater: relaunched {exe} (pid {p?.Id})");
        }
        catch (Exception ex) { Log($"updater: couldn't relaunch {exe}: {ex.Message}. Start it by hand."); }
    }

    private static void Log(string m)
    {
        if (_log == null) return;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_log)!);
                File.AppendAllText(_log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {m}{Environment.NewLine}", Encoding.UTF8);
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { return; }
        }
    }
}
