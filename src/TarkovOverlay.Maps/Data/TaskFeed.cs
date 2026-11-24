using System.Text.Json;
using TarkovOverlay.Maps.Geometry;

namespace TarkovOverlay.Maps.Data;

/// <summary>A place on a map for an objective: a zone (with outline) or one possible quest-item spawn.</summary>
public sealed record ObjectiveSpot(string MapId, Vec3 Position, IReadOnlyList<Vec3> Outline, bool PossibleSpawn);

public sealed record ObjectiveInfo(
    string Id, string Type, string Description, bool Optional, IReadOnlyList<string> MapIds, IReadOnlyList<ObjectiveSpot> Spots);

public sealed record TaskRequirement(string TaskId, IReadOnlyList<string> Status);

public sealed record TaskInfo(
    string Id, string Name, string NormalizedName, string? TraderId, string TraderName, int MinPlayerLevel, string Faction,
    IReadOnlyList<TaskRequirement> Requires, IReadOnlyList<ObjectiveInfo> Objectives, string? MapId,
    bool Kappa, bool Lightkeeper, string? WikiLink)
{
    public IEnumerable<string> MapIds => Objectives.SelectMany(o => o.MapIds.Concat(o.Spots.Select(s => s.MapId)))
        .Concat(MapId != null ? new[] { MapId } : Array.Empty<string>()).Distinct();
}

/// <summary>Trimmed copy of json.tarkov.dev/{mode}/tasks (+ tasks_en, traders_en).</summary>
public sealed class TaskFeed
{
    public const int CurrentSchema = 1;
    public int Schema { get; set; } = CurrentSchema;
    public string ModeId { get; set; } = "";
    public DateTimeOffset FetchedAt { get; set; }
    public List<TaskInfo> Tasks { get; set; } = new();

    private Dictionary<string, TaskInfo>? _byId;
    public TaskInfo? Get(string id) => (_byId ??= Tasks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First())).GetValueOrDefault(id);

    public static TaskFeed Parse(JsonDocument tasks, JsonDocument? tasksEn, JsonDocument? tradersEn, string modeId, DateTimeOffset now)
    {
        var tr = J.Translations(tasksEn);
        var trTraders = J.Translations(tradersEn);
        var data = J.Data(tasks);
        if (J.Prop(data, "tasks") is not { } rows) throw new FormatException("tasks feed has no data.tasks member");
        var questItemNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (J.Prop(data, "questItems") is { } qi)
            foreach (var q in J.Rows(qi))
                if (J.Str(q, "id") is { } qid) questItemNames[qid] = J.Tr(J.Str(q, "name"), tr, "quest item");

        var feed = new TaskFeed { ModeId = modeId, FetchedAt = now };
        foreach (var t in J.Rows(rows))
        {
            var id = J.Str(t, "id");
            if (id == null) continue;
            var nn = J.Str(t, "normalizedName") ?? id;
            var name = J.Tr(J.Str(t, "name"), tr, MapFeed.Pretty(nn));
            var trader = J.Str(t, "trader") ?? (J.Prop(t, "trader") is { ValueKind: JsonValueKind.Object } to ? J.Str(to, "id") : null);
            var traderName = trader != null ? J.Tr(trader + " Nickname", trTraders, "Unknown trader") : "Unknown trader";
            var reqs = new List<TaskRequirement>();
            foreach (var r in J.Arr(t, "taskRequirements"))
            {
                var rt = J.Str(r, "task") ?? (J.Prop(r, "task") is { ValueKind: JsonValueKind.Object } ro ? J.Str(ro, "id") : null);
                if (rt == null) continue;
                var st = J.Strs(r, "status").Select(s => s.ToLowerInvariant()).ToList();
                if (st.Count == 0) st.Add("complete");
                reqs.Add(new TaskRequirement(rt, st));
            }
            var objectives = new List<ObjectiveInfo>();
            foreach (var o in J.Arr(t, "objectives"))
            {
                var oid = J.Str(o, "id");
                if (oid == null) continue;
                var type = J.Str(o, "type") ?? "";
                var desc = J.Tr(J.Str(o, "description"), tr, "");
                if (desc.Length == 0 && J.Str(o, "questItem") is { } qid && questItemNames.TryGetValue(qid, out var qn)) desc = $"Find {qn}";
                if (desc.Length == 0) desc = type;
                var spots = new List<ObjectiveSpot>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var z in J.Arr(o, "zones"))
                {
                    var map = J.Str(z, "map") ?? (J.Prop(z, "map") is { ValueKind: JsonValueKind.Object } zm ? J.Str(zm, "id") : null);
                    var pos = J.Vec(z, "position");
                    if (map == null || pos == null) continue;
                    if (!seen.Add(FormattableString.Invariant($"{map}|{pos.Value.X:0.0}|{pos.Value.Z:0.0}"))) continue;
                    spots.Add(new ObjectiveSpot(map, pos.Value, J.Outline(z), false));
                }
                foreach (var pl in J.Arr(o, "possibleLocations"))
                {
                    var map = J.Str(pl, "map") ?? (J.Prop(pl, "map") is { ValueKind: JsonValueKind.Object } pm ? J.Str(pm, "id") : null);
                    if (map == null) continue;
                    foreach (var p in J.Arr(pl, "positions"))
                        if (J.Vec(p) is { } v && seen.Add(FormattableString.Invariant($"{map}|{v.X:0.0}|{v.Z:0.0}")))
                            spots.Add(new ObjectiveSpot(map, v, Array.Empty<Vec3>(), true));
                }
                var maps = J.Arr(o, "maps").Select(m => m.ValueKind == JsonValueKind.String ? m.GetString() : J.Str(m, "id"))
                    .Where(m => m != null).Select(m => m!).Distinct().ToList();
                objectives.Add(new ObjectiveInfo(oid, type, desc, J.Bool(o, "optional"), maps, spots));
            }
            var mapId = J.Str(t, "map") ?? (J.Prop(t, "map") is { ValueKind: JsonValueKind.Object } tm ? J.Str(tm, "id") : null);
            feed.Tasks.Add(new TaskInfo(id, name, nn, trader, traderName, (int)(J.Num(t, "minPlayerLevel") ?? 0),
                J.Str(t, "factionName") ?? "Any", reqs, objectives, mapId, J.Bool(t, "kappaRequired"), J.Bool(t, "lightkeeperRequired"), J.Str(t, "wikiLink")));
        }
        if (feed.Tasks.Count == 0) throw new FormatException("tasks feed contained no tasks");
        return feed;
    }
}
