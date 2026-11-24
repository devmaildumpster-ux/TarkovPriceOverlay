using System.Text;
using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Quests;

namespace TarkovOverlay.Maps.Logs;

public sealed record QuestLogEntry(string ModeId, string TaskId, string Status, DateTimeOffset? Time);

/// <summary>
/// Quest progress from the game's own logs: notifications.log records a system message when a quest is started
/// (type 10), failed (11) or finished (12). Applying them keeps the built-in tracker current without TarkovTracker.
/// </summary>
public static class QuestLog
{
    /// <summary>
    /// Applies one quest event. started → Active (unless already Complete) and its "must be complete" prerequisites
    /// → Complete; finished → Complete; failed → Failed. Returns true when something changed.
    /// </summary>
    public static bool Apply(QuestLogEntry e, ModeProgress p, TaskFeed? feed)
    {
        bool changed = false;
        var cur = p.Get(e.TaskId);
        switch (e.Status)
        {
            case "started":
                if (cur is QuestState.None or QuestState.Failed) { p.Set(e.TaskId, QuestState.Active); changed = true; }
                if (feed != null) changed |= QuestLogic.MarkEverythingBefore(e.TaskId, feed, p, alsoMarkTargetActive: false) > 0;
                break;
            case "finished":
                if (cur != QuestState.Complete) { p.Set(e.TaskId, QuestState.Complete); changed = true; }
                if (feed != null) changed |= QuestLogic.MarkEverythingBefore(e.TaskId, feed, p, alsoMarkTargetActive: false) > 0;
                break;
            case "failed":
                if (cur != QuestState.Failed) { p.Set(e.TaskId, QuestState.Failed); changed = true; }
                break;
        }
        if (e.Time is { } t && (p.LastLogEvent == null || t > p.LastLogEvent)) p.LastLogEvent = t;
        return changed;
    }

    /// <summary>
    /// Reads every session folder under the Logs folder (oldest first) and returns the quest events with the
    /// session mode in effect at the time. Folders without a "Session mode" line use <paramref name="defaultMode"/>.
    /// </summary>
    public static List<QuestLogEntry> ReadHistory(string logsDir, string defaultMode, Action<string>? progress = null, CancellationToken ct = default)
    {
        var res = new List<QuestLogEntry>();
        List<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(logsDir, "log_*")
                .OrderBy(d => EftInstallLocator.FolderStamp(d) ?? Directory.GetLastWriteTimeUtc(d)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return res; }
        int i = 0;
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke($"Reading {++i}/{folders.Count}: {Path.GetFileName(folder)}");
            List<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.log").Where(f => { var n = Path.GetFileName(f); return n.Contains("application", StringComparison.OrdinalIgnoreCase) || n.Contains("notifications", StringComparison.OrdinalIgnoreCase); }).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var events = new List<LogEvent>();
            foreach (var f in files)
            {
                try
                {
                    var p = new EftLogParser();
                    if (EftInstallLocator.FolderStamp(folder) is { } stamp) p.AssumeDate(DateOnly.FromDateTime(stamp.ToLocalTime()));
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var r = new StreamReader(fs, Encoding.UTF8);
                    var buf = new char[1 << 16];
                    int n;
                    while ((n = r.Read(buf, 0, buf.Length)) > 0)
                        foreach (var e in p.Feed(new string(buf, 0, n)))
                            if (e.Kind is LogEventKind.SessionMode or LogEventKind.TaskStatus) events.Add(e);
                    events.AddRange(p.Flush().Where(e => e.Kind is LogEventKind.SessionMode or LogEventKind.TaskStatus));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            string mode = defaultMode;
            bool sawMode = false;
            foreach (var e in events.OrderBy(e => e.Time ?? DateTimeOffset.MinValue))
            {
                if (e.Kind == LogEventKind.SessionMode) { mode = e.Value ?? mode; sawMode = true; continue; }
                res.Add(new QuestLogEntry(sawMode ? mode : defaultMode, e.Value!, e.Extra!, e.Time));
            }
        }
        return res;
    }
}
