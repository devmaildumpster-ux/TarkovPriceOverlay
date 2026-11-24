using TarkovOverlay.Maps.Data;

namespace TarkovOverlay.Maps.Extracts;

public enum PlayerSide { Pmc, Scav }

public enum ExtractKind { Pmc, Scav, Shared, CoOp, Transit }

/// <summary>An extract ready to draw: who can use it, a label and requirement notes.</summary>
public sealed record ExtractMarker(string Id, string Name, ExtractKind Kind, Geometry.Vec3 Position, IReadOnlyList<Geometry.Vec3> Outline,
    IReadOnlyList<string> Notes, bool Secret)
{
    public string KindLabel => Kind switch
    {
        ExtractKind.Pmc => "PMC", ExtractKind.Scav => "Scav", ExtractKind.Shared => "PMC + Scav",
        ExtractKind.CoOp => "Co-op (PMC + Scav)", _ => "Transit",
    };
}

public static class ExtractFilter
{
    public const string RoublesId = "5449016a4bdc2d6f028b456f";
    public const string DollarsId = "5696686a4bdc2da3298b456a";
    public const string EurosId = "569668774bdc2da2298b4568";

    public static bool IsCoOp(ExtractInfo e) =>
        e.Name.Contains("co-op", StringComparison.OrdinalIgnoreCase) || e.RawName.Contains("coop", StringComparison.OrdinalIgnoreCase);

    public static ExtractKind KindOf(ExtractInfo e)
    {
        if (IsCoOp(e)) return ExtractKind.CoOp;
        return e.Faction switch
        {
            "pmc" => ExtractKind.Pmc,
            "scav" => ExtractKind.Scav,
            _ => ExtractKind.Shared, // "shared" or unknown (some maps list extracts without a faction)
        };
    }

    public static bool UsableBy(ExtractKind k, PlayerSide? side) => side == null || k switch
    {
        ExtractKind.Pmc => side == PlayerSide.Pmc,
        ExtractKind.Scav => side == PlayerSide.Scav,
        _ => true,
    };

    /// <summary>
    /// Extracts the given side can use (<paramref name="side"/> null = side unknown → every extract), plus transits.
    /// The feed lists a separate PMC and Scav copy of exits both can use (Customs: ZB-1011, Crossroads, Trailer Park…);
    /// such pairs (same name within 25 m) become one "PMC + Scav" marker. <paramref name="itemName"/> resolves item ids.
    /// </summary>
    public static List<ExtractMarker> For(MapInfo map, PlayerSide? side, Func<string, string?>? itemName = null, bool includeTransits = true)
    {
        var res = new List<ExtractMarker>();
        var sharedSwitches = SwitchSetSharedByAll(map);
        foreach (var e in map.Extracts)
        {
            var kind = KindOf(e);
            if (kind is ExtractKind.Pmc or ExtractKind.Scav)
            {
                var other = kind == ExtractKind.Pmc ? "scav" : "pmc";
                if (map.Extracts.Any(o => o != e && o.Faction == other && !IsCoOp(o) && o.Name == e.Name && Dist2(o.Position, e.Position) < 25 * 25))
                    kind = ExtractKind.Shared;
            }
            if (!UsableBy(kind, side)) continue;
            // Same name at (almost) the same spot already added → skip the duplicate.
            if (res.Any(r => r.Name == e.Name && Dist2(r.Position, e.Position) < 25 * 25)) continue;
            res.Add(new ExtractMarker(e.Id, e.Name, kind, e.Position, e.Outline, Notes(e, map, sharedSwitches, itemName), IsSecret(e)));
        }
        if (includeTransits)
            foreach (var t in map.Transits)
                res.Add(new ExtractMarker("transit:" + t.Id, t.Description, ExtractKind.Transit, t.Position, t.Outline,
                    new[] { "Transit: moves you (and your gear) to the next map; usually needs a fee in roubles." }, false));
        return res;
    }

    public static bool IsSecret(ExtractInfo e) => e.RawName.Contains("_secret_", StringComparison.OrdinalIgnoreCase);

    private static double Dist2(Geometry.Vec3 a, Geometry.Vec3 b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);

    /// <summary>
    /// The feed currently attaches the same switch list to every extract on some maps (Customs, Labs), which is
    /// not a real requirement. A switch set shared by every extract on a map is ignored.
    /// </summary>
    private static HashSet<string>? SwitchSetSharedByAll(MapInfo map)
    {
        if (map.Extracts.Count < 3) return null;
        var first = map.Extracts[0].SwitchIds;
        if (first.Count == 0) return null;
        var set = first.ToHashSet(StringComparer.Ordinal);
        return map.Extracts.All(x => x.SwitchIds.Count == set.Count && x.SwitchIds.All(set.Contains)) ? set : null;
    }

    public static List<string> Notes(ExtractInfo e, MapInfo map, HashSet<string>? ignoredSwitches, Func<string, string?>? itemName)
    {
        var notes = new List<string>();
        if (e.TransferItemId != null)
        {
            var c = e.TransferCount ?? 1;
            notes.Add(e.TransferItemId switch
            {
                RoublesId => $"Paid: ₽{c:#,0}",
                DollarsId => $"Paid: ${c:#,0}",
                EurosId => $"Paid: €{c:#,0}",
                _ => $"Requires item: {itemName?.Invoke(e.TransferItemId) ?? "special item"}{(c > 1 ? $" ×{c:0}" : "")}",
            });
        }
        if (IsCoOp(e)) notes.Add("Co-op: a PMC and a Scav must extract together (Scav without a PMC can't use it).");
        if (e.Name.Contains("(Flare)", StringComparison.OrdinalIgnoreCase)) notes.Add("Fire a green signal flare in the zone.");
        if (e.Name.Contains("V-Ex", StringComparison.OrdinalIgnoreCase) && e.TransferItemId == null) notes.Add("Paid vehicle extract (roubles), one group per raid.");
        if (IsSecret(e)) notes.Add("Secret exfil: needs the matching transfer item / must be discovered first.");
        foreach (var sid in e.SwitchIds)
        {
            if (ignoredSwitches != null && ignoredSwitches.Contains(sid)) continue;
            var sw = map.Switches.FirstOrDefault(s => s.Id == sid);
            notes.Add("Needs switch: " + (sw?.Name ?? "a power switch or lever"));
        }
        foreach (var (match, note) in Community)
            if (e.Name.Contains(match, StringComparison.OrdinalIgnoreCase) && !notes.Contains(note)) notes.Add(note);
        return notes;
    }

    /// <summary>Well-known requirements the API does not carry (community knowledge; may change with patches).</summary>
    internal static readonly (string Match, string Note)[] Community =
    {
        ("Cliff Descent", "Needs a Paracord and a Red Rebel ice pick; no armored vest/rig."),
        ("Sewer Manhole", "No backpack allowed."),
        ("D-2", "Power must be turned on (lever in the bunker)."),
        ("Bunker Hermetic Door", "Open with the lever in the bunker; a timer runs after it opens."),
        ("Railway Exfil", "Power must be turned on."),
    };
}
