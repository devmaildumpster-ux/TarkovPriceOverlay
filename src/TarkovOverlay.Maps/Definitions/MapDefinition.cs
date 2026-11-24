using System.Text.Json;
using TarkovOverlay.Maps.Geometry;

namespace TarkovOverlay.Maps.Definitions;

public sealed record MapLabel(double X, double Z, string Text, double Size, double Rotation);

public sealed record MapLayerDefinition(string Name, string? SvgLayer, string? TilePath, IReadOnlyList<LayerExtent>? Extents = null, bool Show = false);

/// <summary>One "interactive" entry of tarkov-dev src/data/maps.json: how to draw the map and place game coordinates.</summary>
public sealed class MapDefinition
{
    public required string Key { get; init; }
    public required string GroupName { get; init; }
    public IReadOnlyList<string> AltMaps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<double>? Transform { get; init; }
    public double CoordinateRotation { get; init; }
    public required IReadOnlyList<IReadOnlyList<double>> Bounds { get; init; }
    public IReadOnlyList<IReadOnlyList<double>>? SvgBounds { get; init; }
    public string? SvgPath { get; init; }
    public string? SvgLayer { get; init; }
    public string? TilePath { get; init; }
    public int TileSize { get; init; } = 256;
    public int MinZoom { get; init; }
    public int MaxZoom { get; init; } = 6;
    public IReadOnlyList<double>? HeightRange { get; init; }
    public IReadOnlyList<MapLayerDefinition> Layers { get; init; } = Array.Empty<MapLayerDefinition>();
    public IReadOnlyList<MapLabel> Labels { get; init; } = Array.Empty<MapLabel>();
    /// <summary>maps.json "author" (Shebuka for most maps, TarkovBOT.eu for Icebreaker, Tarkov.dev for Labs).</summary>
    public string? Author { get; init; }
    public string? AuthorLink { get; init; }

    /// <summary>Credit line as tarkov.dev shows it.</summary>
    public string Attribution => string.IsNullOrWhiteSpace(Author) || Author.Equals("Tarkov.dev", StringComparison.OrdinalIgnoreCase)
        ? "Map © Tarkov.dev" : $"Map © Tarkov.dev / {Author}";

    private MapTransform? _t;
    public MapTransform Projection => _t ??= new MapTransform(Transform, CoordinateRotation);

    /// <summary>Pixel rectangle (zoom 0) of the whole map's bounds.</summary>
    public MapRect BoundsRect => Projection.RectFromBounds(Bounds);

    /// <summary>Pixel rectangle the SVG overlay is stretched into (svgBounds, else bounds).</summary>
    public MapRect SvgRect => Projection.RectFromBounds(SvgBounds ?? Bounds);

    public bool HasSvg => !string.IsNullOrWhiteSpace(SvgPath);
    public bool HasTiles => !string.IsNullOrWhiteSpace(TilePath);

    public bool Matches(string normalizedName) =>
        string.Equals(Key, normalizedName, StringComparison.OrdinalIgnoreCase) ||
        AltMaps.Any(a => string.Equals(a, normalizedName, StringComparison.OrdinalIgnoreCase));
}

public static class MapDefinitionParser
{
    /// <summary>Parses maps.json (array of groups, each with "maps"); keeps only projection == "interactive".</summary>
    public static List<MapDefinition> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var list = new List<MapDefinition>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("maps.json root is not an array");
        foreach (var g in doc.RootElement.EnumerateArray())
        {
            if (g.ValueKind != JsonValueKind.Object) continue;
            var group = Str(g, "normalizedName") ?? "";
            if (!g.TryGetProperty("maps", out var maps) || maps.ValueKind != JsonValueKind.Array) continue;
            foreach (var m in maps.EnumerateArray())
            {
                if (Str(m, "projection") != "interactive") continue;
                var key = Str(m, "key");
                var bounds = Corners(m, "bounds");
                if (key == null || bounds == null) continue;
                list.Add(new MapDefinition
                {
                    Key = key,
                    GroupName = group,
                    AltMaps = StrArr(m, "altMaps"),
                    Transform = Nums(m, "transform"),
                    CoordinateRotation = Num(m, "coordinateRotation") ?? 0,
                    Bounds = bounds,
                    SvgBounds = Corners(m, "svgBounds"),
                    SvgPath = Str(m, "svgPath"),
                    SvgLayer = Str(m, "svgLayer"),
                    TilePath = Str(m, "tilePath"),
                    TileSize = (int)(Num(m, "tileSize") ?? 256),
                    MinZoom = (int)(Num(m, "minZoom") ?? 0),
                    MaxZoom = (int)(Num(m, "maxZoom") ?? 6),
                    HeightRange = Nums(m, "heightRange"),
                    Layers = Layers(m),
                    Labels = Labels(m),
                    Author = Str(m, "author"),
                    AuthorLink = Str(m, "authorLink"),
                });
            }
        }
        return list;
    }

    /// <summary>Picks the drawing for an API map (normalizedName), including alternates like night-factory → factory.</summary>
    public static MapDefinition? Resolve(IReadOnlyList<MapDefinition> defs, string? normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName)) return null;
        var exact = defs.FirstOrDefault(d => d.Matches(normalizedName));
        if (exact != null) return exact;
        // e.g. "ground-zero-tutorial" → "ground-zero"; "night-factory" → "factory"
        return defs.Where(d => normalizedName.StartsWith(d.Key + "-", StringComparison.OrdinalIgnoreCase) ||
                               normalizedName.EndsWith("-" + d.Key, StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(d => d.Key.Length).FirstOrDefault();
    }

    private static List<MapLayerDefinition> Layers(JsonElement m)
    {
        var res = new List<MapLayerDefinition>();
        if (!m.TryGetProperty("layers", out var arr) || arr.ValueKind != JsonValueKind.Array) return res;
        foreach (var l in arr.EnumerateArray())
        {
            var name = Str(l, "name");
            if (name == null) continue;
            res.Add(new MapLayerDefinition(name, Str(l, "svgLayer"), Str(l, "tilePath"), AutoLayer.ParseExtents(l),
                l.TryGetProperty("show", out var sh) && sh.ValueKind == JsonValueKind.True));
        }
        return res;
    }

    private static List<MapLabel> Labels(JsonElement m)
    {
        var res = new List<MapLabel>();
        if (!m.TryGetProperty("labels", out var arr) || arr.ValueKind != JsonValueKind.Array) return res;
        foreach (var l in arr.EnumerateArray())
        {
            var p = Nums(l, "position");
            var text = Str(l, "text");
            if (p is not { Count: >= 2 } || string.IsNullOrWhiteSpace(text)) continue;
            res.Add(new MapLabel(p[0], p[1], text, Num(l, "size") ?? 100, Num(l, "rotation") ?? 0));
        }
        return res;
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static List<string> StrArr(JsonElement e, string name)
    {
        var res = new List<string>();
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) res.Add(x.GetString()!);
        return res;
    }

    private static List<double>? Nums(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var res = new List<double>();
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number || !x.TryGetDouble(out var d)) return null;
            res.Add(d);
        }
        return res;
    }

    private static List<IReadOnlyList<double>>? Corners(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var res = new List<IReadOnlyList<double>>();
        foreach (var c in v.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Array) return null;
            var pt = new List<double>();
            foreach (var x in c.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var d)) pt.Add(d);
            if (pt.Count < 2) return null;
            res.Add(pt);
        }
        return res.Count >= 2 ? res : null;
    }
}
