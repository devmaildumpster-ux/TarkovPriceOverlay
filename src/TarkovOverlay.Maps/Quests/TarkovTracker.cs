using System.Net.Http.Headers;
using System.Text.Json;

namespace TarkovOverlay.Maps.Quests;

/// <summary>
/// Read-only TarkovTracker progress sync (GET /progress with the user's API token). Nothing is ever written to
/// TarkovTracker. The token is kept only in map-settings.json on this PC and sent only to the chosen domain.
/// </summary>
public static class TarkovTracker
{
    public static readonly string[] Domains = { "tarkovtracker.org", "tarkovtracker.io" };

    public static Uri ProgressUri(string domain) => domain.Equals("tarkovtracker.org", StringComparison.OrdinalIgnoreCase)
        ? new Uri("https://api.tarkovtracker.org/progress")
        : new Uri($"https://{domain}/api/v2/progress");

    /// <summary>tarkovtracker.org tokens start with PVP_, PVE_ or SZN_; returns the app mode id or null.</summary>
    public static string? ModeFromToken(string token)
    {
        var t = token.Trim();
        if (t.Length < 4 || t[3] != '_') return null;
        return t[..3].ToUpperInvariant() switch { "PVE" => "pve", "PVP" => "regular", "SZN" => "pvp-season", _ => null };
    }

    public sealed record TaskProgress(string Id, bool Complete, bool Failed, bool Invalid);
    public sealed record Progress(IReadOnlyList<TaskProgress> Tasks, IReadOnlyList<string> CompletedObjectives, int? PlayerLevel, string? Faction, string? DisplayName, string? GameMode);

    public static Progress ParseProgress(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var d) ? d : root;
        var tasks = new List<TaskProgress>();
        if (data.TryGetProperty("tasksProgress", out var tp) && tp.ValueKind == JsonValueKind.Array)
            foreach (var t in tp.EnumerateArray())
            {
                var id = t.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                if (id == null) continue;
                tasks.Add(new TaskProgress(id, B(t, "complete"), B(t, "failed"), B(t, "invalid")));
            }
        var objs = new List<string>();
        if (data.TryGetProperty("taskObjectivesProgress", out var op) && op.ValueKind == JsonValueKind.Array)
            foreach (var o in op.EnumerateArray())
                if (B(o, "complete") && o.TryGetProperty("id", out var oi) && oi.ValueKind == JsonValueKind.String) objs.Add(oi.GetString()!);
        int? level = data.TryGetProperty("playerLevel", out var pl) && pl.ValueKind == JsonValueKind.Number && pl.TryGetInt32(out var lv) ? lv : null;
        string? S(string n) => data.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new Progress(tasks, objs, level, S("pmcFaction"), S("displayName"), S("gameMode"));
    }

    private static bool B(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// TarkovTracker is the source of truth for every quest it reports: complete → Complete, failed → Failed,
    /// otherwise a local Complete/Failed is cleared (a local "Active" mark is kept). Returns the number of changes.
    /// </summary>
    public static int Apply(Progress pr, ModeProgress p)
    {
        int changed = 0;
        foreach (var t in pr.Tasks)
        {
            var want = t.Complete ? QuestState.Complete : t.Failed ? QuestState.Failed : (QuestState?)null;
            var cur = p.Get(t.Id);
            if (want is { } w) { if (cur != w) { p.Set(t.Id, w); changed++; } }
            else if (cur is QuestState.Complete or QuestState.Failed) { p.Set(t.Id, QuestState.None); changed++; }
        }
        foreach (var o in pr.CompletedObjectives) if (p.DoneObjectives.Add(o)) changed++;
        if (pr.PlayerLevel is > 0) p.PlayerLevel = pr.PlayerLevel;
        if (pr.Faction is "USEC" or "BEAR") p.Faction = pr.Faction;
        p.LastTrackerSync = DateTimeOffset.UtcNow;
        return changed;
    }

    public static async Task<Progress> FetchAsync(HttpClient http, string domain, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ProgressUri(domain));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        req.Headers.Accept.ParseAdd("application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
        if ((int)resp.StatusCode is 401 or 403) throw new UnauthorizedAccessException("TarkovTracker rejected the token (check it has the \"Get progression\" permission).");
        resp.EnsureSuccessStatusCode();
        return ParseProgress(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
    }
}
