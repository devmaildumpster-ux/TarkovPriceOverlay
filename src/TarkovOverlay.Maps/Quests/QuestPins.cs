using TarkovOverlay.Maps.Data;

namespace TarkovOverlay.Maps.Quests;

/// <summary>Filter of the Quests window list (status, trader, search text, "this map only").</summary>
public sealed record QuestListFilter(string Status = "Not done", string? Trader = null, string? Search = null, IReadOnlyCollection<string>? MapIds = null)
{
    public static readonly string[] Statuses = { "Not done", "Available now", "Active", "On map", "Locked", "Done", "Failed", "All" };
}

/// <summary>Bulk "on the map" selection: Deselect all, Select / Deselect shown. Pure functions over progress.</summary>
public static class QuestPins
{
    /// <summary>The quests the list shows for a filter (faction-filtered like the window).</summary>
    public static List<TaskInfo> Filter(TaskFeed feed, ModeProgress p, QuestListFilter f)
    {
        var q = f.Search?.Trim() ?? "";
        var res = new List<TaskInfo>();
        foreach (var t in feed.Tasks)
        {
            if (!QuestLogic.FactionOk(t, p.Faction)) continue;
            var a = QuestLogic.Availability(t, feed, p);
            bool pass = f.Status switch
            {
                "Available now" => a is QuestAvailability.Available or QuestAvailability.Active,
                "Active" => a == QuestAvailability.Active,
                "On map" => p.Pinned.Contains(t.Id),
                "Locked" => a == QuestAvailability.Locked,
                "Done" => a == QuestAvailability.Complete,
                "Failed" => a == QuestAvailability.Failed,
                "All" => true,
                _ => a is not (QuestAvailability.Complete or QuestAvailability.Failed),
            };
            if (!pass) continue;
            if (!string.IsNullOrEmpty(f.Trader) && f.Trader != "All traders" && t.TraderName != f.Trader) continue;
            if (q.Length > 0 && !MatchesSearch(t, q)) continue;
            if (f.MapIds != null && !QuestLogic.TouchesMap(t, f.MapIds)) continue;
            res.Add(t);
        }
        return res;
    }

    /// <summary>Takes every quest (all traders, all maps) off the map. Returns how many were removed.</summary>
    public static int DeselectAll(ModeProgress p) { int n = p.Pinned.Count; p.Pinned.Clear(); return n; }

    public static int Select(ModeProgress p, IEnumerable<string> ids) => ids.Count(id => p.Pinned.Add(id));
    public static int Deselect(ModeProgress p, IEnumerable<string> ids) => ids.Count(id => p.Pinned.Remove(id));

    /// <summary>Puts the shown (filtered) quests on the map; done/failed ones are skipped (they'd draw nothing).</summary>
    public static int SelectShown(TaskFeed feed, ModeProgress p, QuestListFilter f) =>
        Select(p, Filter(feed, p, f).Where(t => QuestLogic.Availability(t, feed, p) is not (QuestAvailability.Complete or QuestAvailability.Failed)).Select(t => t.Id));

    public static int DeselectShown(TaskFeed feed, ModeProgress p, QuestListFilter f) => Deselect(p, Filter(feed, p, f).Select(t => t.Id));

    public static bool Toggle(ModeProgress p, string id) { if (p.Pinned.Remove(id)) return false; p.Pinned.Add(id); return true; }

    /// <summary>Pinned quests that are still open (not done/failed), overall and touching the map ids.</summary>
    public static (int Total, int OnThisMap) Counts(TaskFeed feed, ModeProgress p, IReadOnlyCollection<string>? mapIds)
    {
        int total = 0, here = 0;
        foreach (var id in p.Pinned)
        {
            var t = feed.Get(id);
            if (t == null) continue;
            if (QuestLogic.Availability(t, feed, p) is QuestAvailability.Complete or QuestAvailability.Failed) continue;
            total++;
            if (mapIds != null && QuestLogic.TouchesMap(t, mapIds)) here++;
        }
        return (total, here);
    }


    /// <summary>
    /// Case-insensitive match on the quest or trader name, the normalized name, or a punctuation-stripped
    /// form so "postman pat", "postman-pat" and "Postman Pat – Part 1" (en-dash) all hit the same quest.
    /// </summary>
    internal static bool MatchesSearch(TaskInfo t, string q)
    {
        if (t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (t.TraderName.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        var dashed = t.NormalizedName.Replace('-', ' ');
        if (dashed.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        var nq = NormalizeSearch(q);
        if (nq.Length == 0) return false;
        return NormalizeSearch(t.Name).Contains(nq, StringComparison.Ordinal)
            || NormalizeSearch(t.TraderName).Contains(nq, StringComparison.Ordinal)
            || NormalizeSearch(dashed).Contains(nq, StringComparison.Ordinal);
    }

    /// <summary>Lowercase, turn dashes and other punctuation into spaces, collapse whitespace.</summary>
    internal static string NormalizeSearch(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var buf = new char[s.Length];
        int n = 0;
        bool space = false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                buf[n++] = char.ToLowerInvariant(c);
                space = false;
            }
            else if (!space)
            {
                buf[n++] = ' ';
                space = true;
            }
        }
        return new string(buf, 0, n).Trim();
    }

    /// <summary>
    /// One-time upgrade from 1.1.0/1.1.1 (settings schema 1): the map selection starts as the Active quests (not the
    /// ~50 merely-available ones the old default drew), minus any the user had hidden. Returns true when it ran.
    /// </summary>
    public static bool MigrateFrom11(Settings.MapSettings s, QuestProgressFile f)
    {
        if (s.Schema >= 2) return false;
        foreach (var m in f.Modes.Values)
        {
            var pins = m.Pinned; // created from Active states if the file had none
            foreach (var h in s.HiddenQuests) pins.Remove(h);
        }
        s.HiddenQuests.Clear();
        s.ShowAllAvailableQuests = false;
        s.Schema = 2;
        return true;
    }
}
