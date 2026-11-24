using System.Globalization;
using System.Text;
using TarkovOverlay.Maps.Extracts;
using TarkovOverlay.Maps.Svg;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

public class SvgTests
{
    private const string Sample = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 200 100">
          <defs>
            <style>.a{fill:#ff0000;stroke:#00f;stroke-width:2px}.b{fill:none;stroke:url(#g);stroke-dasharray:4 2}.h{opacity:.5}.shadow{filter:drop-shadow(0 0 2px #000)}</style>
            <linearGradient id="g0" x1="0" y1="0" x2="10" y2="0" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#000"/><stop offset="1" stop-color="#fff" stop-opacity=".5"/></linearGradient>
            <linearGradient id="g" xlink:href="#g0" x1="5"/>
            <path id="p" d="M0 0h10v10H0z"/>
          </defs>
          <g id="Ground_Level">
            <path class="a" d="M10,10 L20 10 l0 10 z m30 0 c1 2 3 4 5 6 s 1 1 2 2 q1 1 2 2 t 3 3 a5 5 0 0 1 5 5"/>
            <g transform="translate(100 50)" class="h"><use href="#p" x="1" y="2" class="a"/></g>
            <circle cx="50" cy="50" r="5" style="fill:rgb(0,128,0);fill-opacity:0.25"/>
          </g>
          <g id="Underground_Level" class="shadow"><rect x="0" y="0" width="5" height="5" fill="#123456"/></g>
          <g id="Ground_Level_Labels" display="none"><path d="M0 0L1 1" class="b"/></g>
        </svg>
        """;

    [Fact]
    public void Parses_style_classes_use_transforms_and_gradients()
    {
        var doc = SvgParser.Parse(Sample);
        Assert.Equal(200, doc.ViewBoxWidth);
        Assert.Equal(new[] { "Ground_Level", "Underground_Level" }, doc.Layers.Where(l => l.Shapes.Count > 0).Select(l => l.Id));
        var g = doc.Layers.First(l => l.Id == "Ground_Level").Shapes;
        Assert.Equal(3, g.Count);
        Assert.Equal(0xFFFF0000u, g[0].Fill!.Argb);
        Assert.Equal(0xFF0000FFu, g[0].Stroke!.Argb);
        Assert.Equal(2, g[0].StrokeWidth);
        Assert.Equal(SegKind.Move, g[0].Segments[4].Kind);
        Assert.Equal(40, g[0].Segments[4].X, 9);               // relative m after close resolves from subpath start
        Assert.Contains(g[0].Segments, s => s.Kind == SegKind.Arc);
        // use + translate
        Assert.Equal(101, g[1].Segments[0].X, 9);
        Assert.Equal(52, g[1].Segments[0].Y, 9);
        Assert.Equal(0.5, g[1].Opacity, 9);
        Assert.Equal(0xFF008000u, g[2].Fill!.Argb);
        Assert.Equal(0.25, g[2].FillOpacity, 9);
        Assert.Null(doc.Layers.First(l => l.Id == "Ground_Level").DropShadow);
        Assert.Equal(new SvgDropShadow(2, 0xFF000000u), doc.Layers.First(l => l.Id == "Underground_Level").DropShadow);
    }

    [Theory]
    [InlineData("#fff", 0xFFFFFFFFu)] [InlineData("#12345678", 0x78123456u)] [InlineData("rgba(255,0,0,0.5)", 0x80FF0000u)]
    [InlineData("white", 0xFFFFFFFFu)] [InlineData("RGB(10%, 0%, 100%)", 0xFF1A00FFu)]
    public void Colours(string v, uint argb) => Assert.Equal(argb, SvgParser.Color(v));

    [Fact]
    public void Path_data_compact_numbers()
    {
        var s = PathData.Parse("M.5.5l1-1.5e1 2.5.5zM0,0a1,1,0,1,0,2,2");
        Assert.Equal(0.5, s[0].X); Assert.Equal(0.5, s[0].Y);
        Assert.Equal(1.5, s[1].X); Assert.Equal(-14.5, s[1].Y);
        Assert.Equal(4, s[2].X); Assert.Equal(-14, s[2].Y);
        var arc = s.Last();
        Assert.Equal(SegKind.Arc, arc.Kind);
        Assert.Equal(1, (int)arc.Y2); // large-arc=1, sweep=0
    }

    /// <summary>Writes each real tarkov.dev SVG back out from the parsed model so it can be rendered side by side with the
    /// original (tools/svgcheck). Runs only when MAP_SVG_DIR is set.</summary>
    [Fact]
    public void Reserialize_real_maps_when_requested()
    {
        var dir = Environment.GetEnvironmentVariable("MAP_SVG_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var outDir = Directory.CreateDirectory(Path.Combine(dir, "reserialized")).FullName;
        foreach (var f in Directory.GetFiles(dir, "*.svg"))
        {
            var doc = SvgParser.Parse(File.ReadAllText(f));
            Assert.True(doc.ShapeCount > 50, f);
            File.WriteAllText(Path.Combine(outDir, Path.GetFileName(f)), Write(doc, null));
        }
    }

    internal static string Write(SvgDocument d, string? baseLayer)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(double v) => v.ToString("0.###", inv);
        string C(uint a) => $"#{a & 0xFFFFFF:x6}";
        double A(uint a) => (a >> 24) / 255.0;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{N(d.ViewBoxX)} {N(d.ViewBoxY)} {N(d.ViewBoxWidth)} {N(d.ViewBoxHeight)}\"><defs>");
        int gid = 0;
        var paints = new StringBuilder();
        string P(SvgPaint? p, string kind, double op)
        {
            if (p == null) return $"{kind}=\"none\" ";
            if (p.Gradient is { } g)
            {
                var id = "g" + gid++;
                sb.Append($"<linearGradient id=\"{id}\" x1=\"{N(g.X1)}\" y1=\"{N(g.Y1)}\" x2=\"{N(g.X2)}\" y2=\"{N(g.Y2)}\" gradientUnits=\"{(g.BoundingBoxUnits ? "objectBoundingBox" : "userSpaceOnUse")}\">");
                foreach (var s in g.Stops) sb.Append($"<stop offset=\"{N(s.Offset)}\" stop-color=\"{C(s.Argb)}\" stop-opacity=\"{N(A(s.Argb))}\"/>");
                sb.Append("</linearGradient>");
                return $"{kind}=\"url(#{id})\" {kind}-opacity=\"{N(op)}\" ";
            }
            return $"{kind}=\"{C(p.Argb)}\" {kind}-opacity=\"{N(op * A(p.Argb))}\" ";
        }
        foreach (var l in d.Visible(baseLayer))
        {
            paints.Append(l.DropShadow is { } ds ? $"<g id=\"{l.Id}\" style=\"filter:drop-shadow(0 0 {N(ds.Blur)}px {C(ds.Argb)})\">" : $"<g id=\"{l.Id}\">");
            foreach (var s in l.Shapes)
            {
                var path = new StringBuilder();
                foreach (var g in s.Segments)
                    path.Append(g.Kind switch
                    {
                        SegKind.Move => $"M{N(g.X)} {N(g.Y)}",
                        SegKind.Line => $"L{N(g.X)} {N(g.Y)}",
                        SegKind.Cubic => $"C{N(g.X1)} {N(g.Y1)} {N(g.X2)} {N(g.Y2)} {N(g.X)} {N(g.Y)}",
                        SegKind.Quad => $"Q{N(g.X1)} {N(g.Y1)} {N(g.X)} {N(g.Y)}",
                        SegKind.Arc => $"A{N(g.X1)} {N(g.Y1)} {N(g.X2)} {((int)g.Y2 & 1)} {(((int)g.Y2 >> 1) & 1)} {N(g.X)} {N(g.Y)}",
                        _ => "Z",
                    });
                paints.Append($"<path d=\"{path}\" ").Append(P(s.Fill, "fill", s.FillOpacity)).Append(P(s.Stroke, "stroke", s.StrokeOpacity))
                    .Append($"stroke-width=\"{N(s.StrokeWidth)}\" opacity=\"{N(s.Opacity)}\" fill-rule=\"{(s.EvenOdd ? "evenodd" : "nonzero")}\" stroke-linecap=\"{s.LineCap}\" stroke-linejoin=\"{s.LineJoin}\" stroke-miterlimit=\"{N(s.MiterLimit)}\" ");
                if (s.Dash is { Length: > 0 }) paints.Append($"stroke-dasharray=\"{string.Join(' ', s.Dash.Select(N))}\" stroke-dashoffset=\"{N(s.DashOffset)}\" ");
                paints.Append("/>");
            }
            paints.Append("</g>");
        }
        sb.Append("</defs>").Append(paints).Append("</svg>");
        return sb.ToString();
    }
}
