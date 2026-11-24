using System.Text.Json;
using TarkovOverlay.Maps.Geometry;

namespace TarkovOverlay.Maps.Definitions;

/// <summary>
/// One tarkov.dev layer "extent": a height band [HeightMin, HeightMax) and optional 2D boxes in game (x, z).
/// Matching is tarkov.dev's markerIsOnLayer for a point (top = bottom = y):
///   y ≥ height[0] &amp;&amp; y &lt; height[1], and — when the extent has bounds — the point is inside any box
///   (Leaflet LatLngBounds.contains on [z, x]: corners in any order, edges inclusive). No bounds = the whole map.
/// </summary>
public sealed record LayerExtent(double HeightMin, double HeightMax, IReadOnlyList<ExtentBox> Boxes)
{
    public bool Contains(double x, double y, double z)
    {
        if (!(y >= HeightMin && y < HeightMax)) return false;
        if (Boxes.Count == 0) return true;
        foreach (var b in Boxes) if (b.Contains(x, z)) return true;
        return false;
    }
}

/// <summary>A 2D box from two [x, z] corners (maps.json order; a third array element is just a note like "dorms").</summary>
public sealed record ExtentBox(double X1, double Z1, double X2, double Z2, string? Note = null)
{
    public bool Contains(double x, double z) =>
        x >= Math.Min(X1, X2) && x <= Math.Max(X1, X2) && z >= Math.Min(Z1, Z2) && z <= Math.Max(Z1, Z2);
}

public enum AutoLayerMode { All, Underground, Off }

/// <summary>
/// 1.2.2: picks the floor for a position the way tarkov.dev's activateMarkerLayer does — the first layer, in
/// maps.json order, that has extents containing the point; none = the base map ("Main").
/// </summary>
public static class AutoLayer
{
    public const string Main = "Main";

    public static AutoLayerMode ParseMode(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "underground" => AutoLayerMode.Underground,
        "off" => AutoLayerMode.Off,
        _ => AutoLayerMode.All,
    };

    public static string ModeId(AutoLayerMode m) => m switch { AutoLayerMode.Underground => "underground", AutoLayerMode.Off => "off", _ => "all" };

    /// <summary>First layer (maps.json order) whose extents contain the point; null = surface / base map.</summary>
    public static MapLayerDefinition? Match(MapDefinition d, Vec3 p)
    {
        foreach (var l in d.Layers)
        {
            if (l.Extents is not { Count: > 0 } ex) continue; // tarkov.dev: layers without extents are never auto-activated
            foreach (var e in ex) if (e.Contains(p.X, p.Y, p.Z)) return l;
        }
        return null;
    }

    private static readonly string[] UndergroundWords = { "underground", "bunker", "tunnel", "garage", "basement", "technical", "sewer", "cellar" };

    /// <summary>
    /// Below ground: the name says so (Underground, Bunkers, Tunnels, Garage, Basement, Technical…), or every extent
    /// of the layer tops out at (±0.1 m) or below the bottom of the map's base heightRange (e.g. Icebreaker's lower decks).
    /// </summary>
    public static bool IsUnderground(MapDefinition d, MapLayerDefinition l)
    {
        var n = l.Name.ToLowerInvariant();
        if (UndergroundWords.Any(n.Contains)) return true;
        if (d.HeightRange is { Count: >= 2 } hr && l.Extents is { Count: > 0 } ex && ex.All(e => e.HeightMax <= hr[0] + 0.1)) return true;
        return false;
    }

    /// <summary>
    /// The layer to show for a new position: a layer name, "Main", or null = leave the user's choice alone (Off).
    /// Under Underground only, upper floors count as Main.
    /// </summary>
    public static string? Target(MapDefinition d, Vec3 p, AutoLayerMode mode)
    {
        if (mode == AutoLayerMode.Off) return null;
        var m = Match(d, p);
        if (m == null) return Main;
        if (mode == AutoLayerMode.Underground && !IsUnderground(d, m)) return Main;
        return m.Name;
    }

    /// <summary>
    /// Floor choice to select for a new position, or null to change nothing. Only a screenshot not applied before on
    /// this map (<paramref name="fixKey"/> ≠ <paramref name="lastAppliedKey"/>) switches, so a floor picked by hand
    /// stays until the next screenshot. A matched layer that isn't a separate choice (Icebreaker's Infirmary = the
    /// base tiles) is Main.
    /// </summary>
    public static MapLayerChoice? Decide(MapDefinition d, IReadOnlyList<MapLayerChoice> choices, Vec3 p, AutoLayerMode mode,
        string fixKey, string? lastAppliedKey)
    {
        if (mode == AutoLayerMode.Off || fixKey == lastAppliedKey) return null;
        var name = Target(d, p, mode);
        if (name == null) return null;
        return choices.FirstOrDefault(c => c.Name == name) ?? choices.FirstOrDefault(c => c.IsMain);
    }

    internal static List<LayerExtent>? ParseExtents(JsonElement layer)
    {
        if (!layer.TryGetProperty("extents", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var res = new List<LayerExtent>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("height", out var h) || h.ValueKind != JsonValueKind.Array) continue;
            var hv = h.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToList();
            if (hv.Count < 2) continue;
            var boxes = new List<ExtentBox>();
            if (e.TryGetProperty("bounds", out var bs) && bs.ValueKind == JsonValueKind.Array)
                foreach (var b in bs.EnumerateArray())
                {
                    if (b.ValueKind != JsonValueKind.Array) continue;
                    var items = b.EnumerateArray().ToList();
                    var pts = items.Where(i => i.ValueKind == JsonValueKind.Array)
                        .Select(i => i.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToList())
                        .Where(v => v.Count >= 2).ToList();
                    if (pts.Count < 2) continue;
                    var note = items.FirstOrDefault(i => i.ValueKind == JsonValueKind.String) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
                    boxes.Add(new ExtentBox(pts[0][0], pts[0][1], pts[1][0], pts[1][1], note));
                }
            res.Add(new LayerExtent(hv[0], hv[1], boxes));
        }
        return res;
    }
}
