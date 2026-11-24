namespace TarkovOverlay.Maps.Geometry;

/// <summary>A game-world position (Unity: X east/west, Y up, Z north/south).</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public override string ToString() => FormattableString.Invariant($"({X:0.##}, {Y:0.##}, {Z:0.##})");
}

/// <summary>A point in the map's "zoom 0" pixel space (Leaflet CRS.Simple at zoom 0). Y grows downward.</summary>
public readonly record struct MapPoint(double X, double Y)
{
    public override string ToString() => FormattableString.Invariant($"[{X:0.###}, {Y:0.###}]");
}

public readonly record struct MapRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(MapPoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public static MapRect FromCorners(MapPoint a, MapPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    public MapRect Inflate(double fraction) =>
        new(X - Width * fraction, Y - Height * fraction, Width * (1 + 2 * fraction), Height * (1 + 2 * fraction));
}

/// <summary>
/// The tarkov.dev interactive-map projection (src/pages/map/index.jsx getCRS + applyRotation + pos()):
///   lat = z, lng = x; rotate (x, z) by coordinateRotation degrees: rx = x·cosθ − z·sinθ, ry = x·sinθ + z·cosθ;
///   Leaflet Transformation(a = t[0], b = t[1], c = −t[2], d = t[3]) at zoom 0: px = a·rx + b, py = c·ry + d.
/// Each zoom level doubles the pixel coordinates.
/// </summary>
public sealed class MapTransform
{
    private readonly double _a, _b, _c, _d, _cos, _sin;
    public double RotationDegrees { get; }

    public MapTransform(IReadOnlyList<double>? transform, double rotationDegrees)
    {
        if (transform is { Count: >= 4 }) { _a = transform[0]; _b = transform[1]; _c = -transform[2]; _d = transform[3]; }
        else { _a = 1; _b = 0; _c = -1; _d = 0; }
        if (_a == 0) _a = 1;
        if (_c == 0) _c = -1;
        RotationDegrees = rotationDegrees;
        var r = rotationDegrees * Math.PI / 180.0;
        // Snap the common right angles so 180° does not leave 1e-16 noise.
        _cos = Snap(Math.Cos(r)); _sin = Snap(Math.Sin(r));
    }

    private static double Snap(double v) => Math.Abs(v) < 1e-12 ? 0 : Math.Abs(Math.Abs(v) - 1) < 1e-12 ? Math.Sign(v) : v;

    public MapPoint ToMap(double x, double z)
    {
        double rx = x * _cos - z * _sin;
        double ry = x * _sin + z * _cos;
        return new MapPoint(_a * rx + _b, _c * ry + _d);
    }

    public MapPoint ToMap(Vec3 p) => ToMap(p.X, p.Z);

    /// <summary>Inverse of <see cref="ToMap(double,double)"/>: map pixel → game (x, z).</summary>
    public (double X, double Z) ToGame(MapPoint p)
    {
        double rx = (p.X - _b) / _a;
        double ry = (p.Y - _d) / _c;
        // rotate back by −θ
        double x = rx * _cos + ry * _sin;
        double z = -rx * _sin + ry * _cos;
        return (x, z);
    }

    /// <summary>
    /// Screen heading (degrees clockwise from "up") of a player at (x, z) facing <paramref name="yawDegrees"/>
    /// (Unity yaw: 0 = +Z, 90 = +X). Computed by projecting a unit step, so it is correct for every rotation.
    /// </summary>
    public double ScreenHeading(double x, double z, double yawDegrees)
    {
        var y = yawDegrees * Math.PI / 180.0;
        var p0 = ToMap(x, z);
        var p1 = ToMap(x + Math.Sin(y), z + Math.Cos(y));
        var deg = Math.Atan2(p1.X - p0.X, -(p1.Y - p0.Y)) * 180.0 / Math.PI;
        return Normalize(deg);
    }

    /// <summary>Axis-aligned pixel rectangle covered by game-space bounds [[x1, z1], [x2, z2]].</summary>
    public MapRect RectFromBounds(IReadOnlyList<IReadOnlyList<double>> bounds)
    {
        if (bounds.Count < 2 || bounds[0].Count < 2 || bounds[1].Count < 2) throw new ArgumentException("bounds need two [x, z] corners");
        return MapRect.FromCorners(ToMap(bounds[0][0], bounds[0][1]), ToMap(bounds[1][0], bounds[1][1]));
    }

    public static double Normalize(double deg)
    {
        deg %= 360.0;
        if (deg < 0) deg += 360.0;
        if (Math.Abs(deg - 360.0) < 1e-9) deg = 0;
        return deg;
    }

    /// <summary>
    /// Fits a viewBox of the given aspect into a rectangle the way a browser does for an &lt;svg&gt; with the default
    /// preserveAspectRatio="xMidYMid meet" (what Leaflet's svgOverlay ends up using).
    /// </summary>
    public static MapRect FitMeet(MapRect target, double contentWidth, double contentHeight)
    {
        if (contentWidth <= 0 || contentHeight <= 0) return target;
        double s = Math.Min(target.Width / contentWidth, target.Height / contentHeight);
        double w = contentWidth * s, h = contentHeight * s;
        return new MapRect(target.X + (target.Width - w) / 2, target.Y + (target.Height - h) / 2, w, h);
    }
}

public static class Rotation
{
    /// <summary>
    /// Unity yaw (rotation about +Y, degrees, 0 = facing +Z, 90 = facing +X) from a quaternion (x, y, z, w).
    /// Uses the Y-axis component of the Unity ZXY Euler decomposition, so pitch/roll noise has little effect.
    /// </summary>
    public static double YawFromQuaternion(double x, double y, double z, double w)
    {
        double n = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (n < 1e-9) return 0;
        x /= n; y /= n; z /= n; w /= n;
        double siny = 2.0 * (w * y + x * z);
        double cosy = 1.0 - 2.0 * (x * x + y * y);
        return MapTransform.Normalize(Math.Atan2(siny, cosy) * 180.0 / Math.PI);
    }
}
