using System.Text.Json;
using TarkovOverlay.Maps.Geometry;

namespace TarkovOverlay.Maps.Data;

public sealed record ExtractInfo(
    string Id, string Name, string RawName, string? Faction, Vec3 Position, IReadOnlyList<Vec3> Outline,
    string? TransferItemId, double? TransferCount, IReadOnlyList<string> SwitchIds);

public sealed record TransitInfo(string Id, string Description, string? TargetMapId, Vec3 Position, IReadOnlyList<Vec3> Outline);

public sealed record SwitchInfo(string Id, string Name, Vec3? Position);

public sealed record MapInfo(
    string Id, string Name, string NormalizedName, string? NameId, string? ScenePath,
    IReadOnlyList<ExtractInfo> Extracts, IReadOnlyList<TransitInfo> Transits, IReadOnlyList<SwitchInfo> Switches);

/// <summary>Trimmed copy of json.tarkov.dev/{mode}/maps (+ maps_en) with only what the map window draws.</summary>
public sealed class MapFeed
{
    public const int CurrentSchema = 1;
    public int Schema { get; set; } = CurrentSchema;
    public string ModeId { get; set; } = "";
    public DateTimeOffset FetchedAt { get; set; }
    public List<MapInfo> Maps { get; set; } = new();

    public MapInfo? ById(string? id) => id == null ? null : Maps.FirstOrDefault(m => m.Id == id);
    public MapInfo? ByNormalizedName(string? n) => n == null ? null : Maps.FirstOrDefault(m => string.Equals(m.NormalizedName, n, StringComparison.OrdinalIgnoreCase));
    public MapInfo? ByNameId(string? n) => n == null ? null : Maps.FirstOrDefault(m => string.Equals(m.NameId, n, StringComparison.OrdinalIgnoreCase));
    public MapInfo? ByScenePath(string? p) => p == null ? null : Maps.FirstOrDefault(m => string.Equals(m.ScenePath, p, StringComparison.OrdinalIgnoreCase));

    public static MapFeed Parse(JsonDocument maps, JsonDocument? mapsEn, string modeId, DateTimeOffset now)
    {
        var tr = J.Translations(mapsEn);
        var data = J.Data(maps);
        if (J.Prop(data, "maps") is not { } rows) throw new FormatException("maps feed has no data.maps member");
        var feed = new MapFeed { ModeId = modeId, FetchedAt = now };
        foreach (var m in J.Rows(rows))
        {
            var id = J.Str(m, "id");
            var nn = J.Str(m, "normalizedName");
            if (id == null || nn == null) continue;
            var extracts = new List<ExtractInfo>();
            foreach (var e in J.Arr(m, "extracts"))
            {
                var pos = J.Vec(e, "position");
                var eid = J.Str(e, "id");
                if (pos == null || eid == null) continue;
                var raw = J.Str(e, "name") ?? eid;
                string? tItem = null; double? tCount = null;
                if (J.Prop(e, "transferItem") is { ValueKind: JsonValueKind.Object } ti)
                {
                    tItem = J.Str(ti, "item") ?? (J.Prop(ti, "item") is { ValueKind: JsonValueKind.Object } io ? J.Str(io, "id") : null);
                    tCount = J.Num(ti, "count");
                }
                var sw = J.Strs(e, "switches").ToList();
                if (sw.Count == 0 && J.Str(e, "switch") is { } one) sw.Add(one);
                extracts.Add(new ExtractInfo(eid, J.Tr(raw, tr, raw), raw, J.Str(e, "faction")?.ToLowerInvariant(), pos.Value, J.Outline(e), tItem, tCount, sw));
            }
            var transits = new List<TransitInfo>();
            foreach (var t in J.Arr(m, "transits"))
            {
                var pos = J.Vec(t, "position");
                if (pos == null) continue;
                var tid = J.Str(t, "id") ?? Guid.NewGuid().ToString("N");
                var desc = J.Str(t, "description");
                var target = J.Str(t, "map") ?? (J.Prop(t, "map") is { ValueKind: JsonValueKind.Object } mo ? J.Str(mo, "id") : null);
                transits.Add(new TransitInfo(tid, J.Tr(desc, tr, "Transit"), target, pos.Value, J.Outline(t)));
            }
            var switches = new List<SwitchInfo>();
            foreach (var s in J.Arr(m, "switches"))
            {
                var sid = J.Str(s, "id");
                if (sid == null) continue;
                var sname = J.Str(s, "name");
                switches.Add(new SwitchInfo(sid, J.Tr(sname, tr, sname ?? "Switch"), J.Vec(s, "position")));
            }
            var nameKey = J.Str(m, "name");
            feed.Maps.Add(new MapInfo(id, J.Tr(nameKey, tr, Pretty(nn)), nn, J.Str(m, "nameId"), J.Str(m, "scenePath"), extracts, transits, switches));
        }
        if (feed.Maps.Count == 0) throw new FormatException("maps feed contained no maps");
        return feed;
    }

    public static string Pretty(string normalized) =>
        string.Join(' ', normalized.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
