using TarkovOverlay.Maps.Definitions;
using TarkovOverlay.Maps.Geometry;

namespace TarkovOverlay.Maps.Position;

public static class MapGuess
{
    /// <summary>True when the world point projects inside the map's bounds (grown by <paramref name="margin"/> of its size).</summary>
    public static bool Inside(MapDefinition d, Vec3 pos, double margin = 0)
    {
        var px = d.Projection.ToMap(pos); var r = d.BoundsRect;
        double mx = Math.Abs(r.Width) * margin, my = Math.Abs(r.Height) * margin;
        return px.X >= Math.Min(r.X, r.X + r.Width) - mx && px.X <= Math.Max(r.X, r.X + r.Width) + mx
            && px.Y >= Math.Min(r.Y, r.Y + r.Height) - my && px.Y <= Math.Max(r.Y, r.Y + r.Height) + my;
    }

    /// <summary>The one map whose bounds contain the point (null when none or several: map coordinates overlap a lot).</summary>
    public static string? FromPosition(IEnumerable<MapDefinition> defs, Vec3 pos)
    {
        var hits = defs.Where(d => Inside(d, pos)).Select(d => d.Key).Distinct().ToList();
        return hits.Count == 1 ? hits[0] : null;
    }
}
