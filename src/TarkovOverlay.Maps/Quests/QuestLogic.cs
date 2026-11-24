using TarkovOverlay.Maps.Data;

namespace TarkovOverlay.Maps.Quests;

public enum QuestAvailability { Locked, Available, Active, Complete, Failed }

/// <summary>Prerequisite and map-filter rules for the built-in quest tracker. Pure functions over the feed + progress.</summary>
public static class QuestLogic
{
    public static QuestAvailability Availability(TaskInfo t, TaskFeed feed, ModeProgress p)
    {
        switch (p.Get(t.Id))
        {
            case QuestState.Complete: return QuestAvailability.Complete;
            case QuestState.Failed: return QuestAvailability.Failed;
            case QuestState.Active: return QuestAvailability.Active;
        }
        if (!FactionOk(t, p.Faction)) return QuestAvailability.Locked;
        if (p.PlayerLevel is { } lvl && t.MinPlayerLevel > lvl) return QuestAvailability.Locked;
        return PrerequisitesMet(t, feed, p) ? QuestAvailability.Available : QuestAvailability.Locked;
    }

    public static bool FactionOk(TaskInfo t, string? faction) =>
        string.IsNullOrEmpty(faction) || faction.Equals("Any", StringComparison.OrdinalIgnoreCase) ||
        t.Faction.Equals("Any", StringComparison.OrdinalIgnoreCase) || t.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every taskRequirement is met: "complete" ← prerequisite complete; "active" ← prerequisite started (or already
    /// complete); "failed" ← prerequisite failed. Prerequisites missing from the feed count as met.
    /// </summary>
    public static bool PrerequisitesMet(TaskInfo t, TaskFeed feed, ModeProgress p)
    {
        foreach (var r in t.Requires)
        {
            if (feed.Get(r.TaskId) == null) continue;
            var s = p.Get(r.TaskId);
            bool ok = r.Status.Any(st => st switch
            {
                "complete" => s == QuestState.Complete,
                "active" => s is QuestState.Active or QuestState.Complete,
                "failed" => s == QuestState.Failed,
                _ => s == QuestState.Complete,
            });
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>All quests that must be done (or started) before <paramref name="taskId"/>, transitively.</summary>
    public static List<TaskInfo> PrerequisiteClosure(string taskId, TaskFeed feed)
    {
        var res = new List<TaskInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { taskId };
        var stack = new Stack<string>();
        stack.Push(taskId);
        while (stack.Count > 0)
        {
            var t = feed.Get(stack.Pop());
            if (t == null) continue;
            foreach (var r in t.Requires)
            {
                if (!seen.Add(r.TaskId)) continue;
                var pre = feed.Get(r.TaskId);
                if (pre == null) continue;
                res.Add(pre);
                stack.Push(pre.Id);
            }
        }
        return res;
    }

    /// <summary>
    /// "I'm on this quest": every prerequisite whose requirement accepts "complete" is marked complete (a
    /// prerequisite that must be failed is marked failed), and the quest itself becomes active.
    /// Returns how many quests changed.
    /// </summary>
    public static int MarkEverythingBefore(string taskId, TaskFeed feed, ModeProgress p, bool alsoMarkTargetActive = true)
    {
        int changed = 0;
        var target = feed.Get(taskId);
        if (target == null) return 0;
        // Walk requirement edges so we know which status each prerequisite needs.
        var seen = new HashSet<string>(StringComparer.Ordinal) { taskId };
        var queue = new Queue<TaskInfo>();
        queue.Enqueue(target);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            foreach (var r in t.Requires)
            {
                var pre = feed.Get(r.TaskId);
                if (pre == null || !seen.Add(pre.Id)) continue;
                var want = r.Status.Contains("complete") || r.Status.Contains("active") ? QuestState.Complete
                         : r.Status.Contains("failed") ? QuestState.Failed : QuestState.Complete;
                if (p.Get(pre.Id) != want) { p.Set(pre.Id, want); changed++; }
                queue.Enqueue(pre);
            }
        }
        if (alsoMarkTargetActive && p.Get(taskId) is QuestState.None) { p.Set(taskId, QuestState.Active); changed++; }
        return changed;
    }

    /// <summary>
    /// Quests whose objectives the map shows: the ones the user put on the map (pinned) that aren't done or failed.
    /// Every available quest only when <paramref name="showAllAvailable"/> (explicit opt-in, off by default).
    /// An empty selection shows nothing: there is no "show all" fallback.
    /// </summary>
    public static bool ShownOnMap(QuestAvailability a, bool pinned, bool showAllAvailable) =>
        a is not (QuestAvailability.Complete or QuestAvailability.Failed) &&
        (pinned || (showAllAvailable && (a is QuestAvailability.Available or QuestAvailability.Active)));

    public sealed record MapObjective(TaskInfo Task, ObjectiveInfo Objective, IReadOnlyList<ObjectiveSpot> Spots, QuestAvailability Availability, bool Pinned = true);

    /// <summary>Objectives of shown quests on one map (spots filtered to that map). Objectives on the map without spots are kept (no marker, listed only).</summary>
    public static List<MapObjective> ObjectivesOnMap(TaskFeed feed, ModeProgress p, IReadOnlyCollection<string> mapIds, bool showAllAvailable = false)
        => Candidates(feed, p, mapIds, showAllAvailable ? CandidateKind.Browse : CandidateKind.Pinned);

    public enum CandidateKind { Pinned, Browse, Selectable }

    /// <summary>
    /// Objectives on the map for quests of the given kind: Pinned = drawn by default; Browse = pinned + every
    /// available/active; Selectable = everything the map checklist offers (pinned, active, available).
    /// </summary>
    public static List<MapObjective> Candidates(TaskFeed feed, ModeProgress p, IReadOnlyCollection<string> mapIds, CandidateKind kind)
    {
        var res = new List<MapObjective>();
        var pins = p.Pinned;
        foreach (var t in feed.Tasks)
        {
            var a = Availability(t, feed, p);
            bool pinned = pins.Contains(t.Id);
            if (!ShownOnMap(a, pinned, kind != CandidateKind.Pinned)) continue;
            foreach (var o in t.Objectives)
            {
                if (p.DoneObjectives.Contains(o.Id)) continue;
                var spots = o.Spots.Where(s => mapIds.Contains(s.MapId)).ToList();
                if (!ObjectiveOnMap(t, o, spots, mapIds)) continue;
                res.Add(new MapObjective(t, o, spots, a, pinned));
            }
        }
        return res;
    }

    private static bool ObjectiveOnMap(TaskInfo t, ObjectiveInfo o, List<ObjectiveSpot> spots, IReadOnlyCollection<string> mapIds) =>
        spots.Count > 0 || o.MapIds.Any(mapIds.Contains) || (o.MapIds.Count == 0 && o.Spots.Count == 0 && t.MapId != null && mapIds.Contains(t.MapId) && IsInRaidType(o.Type));

    /// <summary>True when any of the quest's in-raid objectives is on one of the map ids.</summary>
    public static bool TouchesMap(TaskInfo t, IReadOnlyCollection<string> mapIds) =>
        t.Objectives.Any(o => ObjectiveOnMap(t, o, o.Spots.Where(s => mapIds.Contains(s.MapId)).ToList(), mapIds));

    /// <summary>Objective types that happen in raid (hand-ins and trader-level goals don't belong on a map).</summary>
    public static bool IsInRaidType(string type) => type is not ("giveItem" or "giveQuestItem" or "traderLevel" or "traderStanding" or "skill" or "experience" or "taskStatus" or "buildWeapon" or "sellItem");
}
