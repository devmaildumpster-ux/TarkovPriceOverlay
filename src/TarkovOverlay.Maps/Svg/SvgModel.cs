namespace TarkovOverlay.Maps.Svg;

public enum SegKind { Move, Line, Cubic, Quad, Arc, Close }

/// <summary>One absolute path segment (after transforms). Cubic: (X1,Y1) (X2,Y2) → (X,Y). Quad: (X1,Y1) → (X,Y).
/// Arc: radii (X1,Y1), rotation X2, flags in Y2 (bit0 large arc, bit1 sweep) → (X,Y).</summary>
public readonly record struct Seg(SegKind Kind, double X, double Y, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0);

public sealed record SvgStop(double Offset, uint Argb);

/// <summary>Linear gradient; coordinates are absolute user space unless <see cref="BoundingBoxUnits"/>.</summary>
public sealed record SvgLinearGradient(double X1, double Y1, double X2, double Y2, bool BoundingBoxUnits, IReadOnlyList<SvgStop> Stops);

/// <summary>A paint: a solid ARGB colour or a gradient.</summary>
public sealed record SvgPaint(uint Argb, SvgLinearGradient? Gradient = null);

public sealed class SvgShape
{
    public required List<Seg> Segments { get; init; }
    public SvgPaint? Fill { get; init; }
    public SvgPaint? Stroke { get; init; }
    public double StrokeWidth { get; init; } = 1;
    public double[]? Dash { get; init; }
    public double DashOffset { get; init; }
    public double Opacity { get; init; } = 1;
    public double FillOpacity { get; init; } = 1;
    public double StrokeOpacity { get; init; } = 1;
    public bool EvenOdd { get; init; }
    public string LineCap { get; init; } = "butt";
    public string LineJoin { get; init; } = "miter";
    public double MiterLimit { get; init; } = 4;
}

/// <summary>CSS drop-shadow on a layer group (blur radius in user units).</summary>
public sealed record SvgDropShadow(double Blur, uint Argb);

public sealed class SvgLayer
{
    public required string Id { get; init; }
    public string? KeepWithGroup { get; init; }
    public SvgDropShadow? DropShadow { get; init; }
    public List<SvgShape> Shapes { get; } = new();
}

public sealed class SvgDocument
{
    public double ViewBoxX { get; init; }
    public double ViewBoxY { get; init; }
    public double ViewBoxWidth { get; init; }
    public double ViewBoxHeight { get; init; }
    /// <summary>Top-level &lt;g id=…&gt; groups in document order (anything else lands in a layer with Id "").</summary>
    public List<SvgLayer> Layers { get; } = new();

    public int ShapeCount => Layers.Sum(l => l.Shapes.Count);

    /// <summary>Layers drawn for a base layer id, plus optional extra layer ids, in document order.</summary>
    public IEnumerable<SvgLayer> Visible(string? baseLayer, IReadOnlyCollection<string>? extra = null) =>
        baseLayer == null ? Layers
        : Layers.Where(l => l.Id == "" || l.Id == baseLayer || l.KeepWithGroup == baseLayer || (extra != null && extra.Contains(l.Id)));
}
