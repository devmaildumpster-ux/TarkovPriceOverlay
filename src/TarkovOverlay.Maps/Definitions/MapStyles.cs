using TarkovOverlay.Maps.Geometry;

namespace TarkovOverlay.Maps.Definitions;

/// <summary>How the base map is drawn: tarkov.dev's rendered top-down tiles ("Satellite") or Shebuka's SVG drawing ("Vector").</summary>
public enum MapStyle { Satellite, Vector }

/// <summary>A floor/level of a map: the base drawing (Main), or the base plus one extra SVG group and/or tile set.</summary>
public sealed record MapLayerChoice(string Name, string? SvgLayer, string? TilePath)
{
    public static readonly MapLayerChoice Main = new("Main", null, null);
    public bool IsMain => SvgLayer == null && TilePath == null;
    public override string ToString() => Name;

    /// <summary>
    /// Main plus every floor that can be drawn in at least one style (an SVG group when the map has an SVG, or a tile
    /// set when it has tiles). The list doesn't depend on the style, so switching Satellite ↔ Vector keeps the floor.
    /// </summary>
    public static List<MapLayerChoice> For(MapDefinition d)
    {
        var list = new List<MapLayerChoice> { Main };
        foreach (var l in d.Layers)
        {
            var svg = d.HasSvg && !string.IsNullOrEmpty(l.SvgLayer) ? l.SvgLayer : null;
            var tile = d.HasTiles && !string.IsNullOrEmpty(l.TilePath) ? l.TilePath : null;
            if (svg == null && tile == null) continue;
            // Icebreaker / Labs list the base tile set again as their first "floor": that's just Main.
            if (svg == null && tile == d.TilePath) continue;
            list.Add(new MapLayerChoice(l.Name, svg, tile));
        }
        return list;
    }
}

public static class MapStyles
{
    public static bool HasBoth(MapDefinition d) => d.HasSvg && d.HasTiles;

    /// <summary>The style actually drawn for <paramref name="layer"/> when the user prefers <paramref name="preferred"/>.</summary>
    public static MapStyle Effective(MapDefinition d, MapStyle preferred, MapLayerChoice? layer = null)
    {
        layer ??= MapLayerChoice.Main;
        bool canTile = d.HasTiles && (layer.IsMain || layer.TilePath != null);
        bool canSvg = d.HasSvg && (layer.IsMain || layer.SvgLayer != null);
        if (preferred == MapStyle.Satellite) return canTile || !canSvg ? (d.HasTiles ? MapStyle.Satellite : MapStyle.Vector) : MapStyle.Vector;
        return canSvg || !canTile ? (d.HasSvg ? MapStyle.Vector : MapStyle.Satellite) : MapStyle.Satellite;
    }

    public static MapStyle FromPreference(bool preferSatellite) => preferSatellite ? MapStyle.Satellite : MapStyle.Vector;

    /// <summary>Why the toggle can't switch on this map (null = it can), for the button tooltip and the hint.</summary>
    public static string? SingleModeReason(MapDefinition d, string displayName) =>
        HasBoth(d) ? null : d.HasTiles ? $"{displayName} is satellite-only" : d.HasSvg ? $"{displayName} is vector-only" : $"{displayName} has no drawing";

    public static string Label(MapStyle s) => s == MapStyle.Satellite ? "Satellite" : "Vector";
}

/// <summary>
/// The block of Leaflet tiles covering a map's bounds at one zoom level, and where the stitched picture sits in
/// tarkov.dev zoom-0 pixel space. Leaflet's tile (x, y) at zoom z covers CRS pixels [x·ts, (x+1)·ts) at that zoom,
/// i.e. [x·ts/2^z, (x+1)·ts/2^z) at zoom 0 — the same space <see cref="MapTransform.ToMap(double,double)"/> returns.
/// </summary>
public sealed record TileGrid(int Z, int TileSize, int X0, int X1, int Y0, int Y1)
{
    public double Scale => 1 << Z;
    public int Columns => X1 - X0 + 1;
    public int Rows => Y1 - Y0 + 1;
    public int PixelWidth => Columns * TileSize;
    public int PixelHeight => Rows * TileSize;
    public long Pixels => (long)PixelWidth * PixelHeight;
    public int TileCount => Columns * Rows;

    /// <summary>Zoom-0 rectangle the stitched bitmap is drawn into.</summary>
    public MapRect Placement => new(X0 * TileSize / Scale, Y0 * TileSize / Scale, Columns * TileSize / Scale, Rows * TileSize / Scale);

    public static TileGrid At(MapRect bounds, int tileSize, int z)
    {
        double sc = 1 << z;
        return new TileGrid(z, tileSize,
            (int)Math.Floor(bounds.X * sc / tileSize), (int)Math.Floor((bounds.Right * sc - 1e-9) / tileSize),
            (int)Math.Floor(bounds.Y * sc / tileSize), (int)Math.Floor((bounds.Bottom * sc - 1e-9) / tileSize));
    }

    /// <summary>Highest zoom (between the map's min and max) whose stitched picture stays within the limits.</summary>
    public static TileGrid Choose(MapDefinition d, long maxPixels = DefaultMaxPixels, int maxSide = DefaultMaxSide)
    {
        int ts = d.TileSize > 0 ? d.TileSize : 256;
        int zMin = Math.Max(0, d.MinZoom), zMax = Math.Max(zMin, d.MaxZoom);
        var best = At(d.BoundsRect, ts, zMin);
        for (int z = zMin + 1; z <= zMax; z++)
        {
            var g = At(d.BoundsRect, ts, z);
            if (g.Pixels > maxPixels || Math.Max(g.PixelWidth, g.PixelHeight) > maxSide) break;
            best = g;
        }
        return best;
    }

    /// <summary>1.2.1: about 4000 px across for the big maps (zoom 4) — sharp when zoomed in, ~50–80 MB in memory.</summary>
    public const long DefaultMaxPixels = 20_000_000;
    public const int DefaultMaxSide = 8192;

    /// <summary>Pixel in the stitched bitmap for a zoom-0 point.</summary>
    public (double X, double Y) BitmapPixel(MapPoint p) => (p.X * Scale - X0 * TileSize, p.Y * Scale - Y0 * TileSize);

    /// <summary>Leaflet tile index holding a zoom-0 point at this zoom.</summary>
    public (int X, int Y) TileOf(MapPoint p) => ((int)Math.Floor(p.X * Scale / TileSize), (int)Math.Floor(p.Y * Scale / TileSize));

    public string Url(string template, int x, int y) => template
        .Replace("{z}", Z.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
