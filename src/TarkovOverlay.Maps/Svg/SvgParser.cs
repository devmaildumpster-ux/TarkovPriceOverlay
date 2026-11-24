using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TarkovOverlay.Maps.Svg;

/// <summary>
/// Small SVG reader for the tarkov.dev vector maps (Shebuka's tarkov-dev-svg-maps). Supports what those files use:
/// &lt;g&gt;/&lt;path&gt;/&lt;circle&gt;/&lt;ellipse&gt;/&lt;rect&gt;/&lt;polygon&gt;/&lt;polyline&gt;/&lt;line&gt;/&lt;use&gt;, translate/scale/matrix/rotate transforms,
/// class rules from &lt;style&gt; (".a{…}" incl. comma lists), presentation attributes, inline style, linear gradients.
/// Everything is flattened to absolute segments so the WPF side only builds geometry.
/// </summary>
public static class SvgParser
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    private sealed class Style
    {
        public string? Fill = "#000000", Stroke = "none";
        public double StrokeWidth = 1, FillOpacity = 1, StrokeOpacity = 1, MiterLimit = 4, DashOffset;
        public double[]? Dash;
        public bool EvenOdd, Hidden;
        public string LineCap = "butt", LineJoin = "miter";
        public double Opacity = 1; // accumulated group opacity
        public Style Clone() => (Style)MemberwiseClone();
    }

    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);
        // [a c e; b d f]
        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
        public Affine Then(Affine o) => new( // this applied first, then o  (o ∘ this)
            o.A * A + o.C * B, o.B * A + o.D * B,
            o.A * C + o.C * D, o.B * C + o.D * D,
            o.A * E + o.C * F + o.E, o.B * E + o.D * F + o.F);
        public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));
        public bool IsTranslateScale => B == 0 && C == 0;
    }

    public static SvgDocument Parse(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersFromEntities = 0 };
        using var sr = new StringReader(xml);
        using var xr = XmlReader.Create(sr, settings);
        var doc = XDocument.Load(xr);
        var root = doc.Root ?? throw new FormatException("empty SVG");
        var vb = ParseNums(Attr(root, "viewBox") ?? "");
        double vw = vb.Count == 4 ? vb[2] : Len(Attr(root, "width")) ?? 0, vh = vb.Count == 4 ? vb[3] : Len(Attr(root, "height")) ?? 0;
        var result = new SvgDocument { ViewBoxX = vb.Count == 4 ? vb[0] : 0, ViewBoxY = vb.Count == 4 ? vb[1] : 0, ViewBoxWidth = vw, ViewBoxHeight = vh };

        var rules = new List<(string Cls, Dictionary<string, string> Decls)>();
        foreach (var st in root.Descendants().Where(e => e.Name.LocalName == "style")) ParseCss(st.Value, rules);
        var ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.DescendantsAndSelf()) if (Attr(e, "id") is { } id) ids.TryAdd(id, e);
        var ctx = new Ctx(rules, ids);

        SvgLayer? loose = null;
        foreach (var child in root.Elements())
        {
            var name = child.Name.LocalName;
            if (name is "style" or "defs" or "metadata" or "title" or "desc" or "linearGradient" or "radialGradient" or "namedview") continue;
            if (name == "g" && Attr(child, "id") is { } gid)
            {
                var layer = new SvgLayer { Id = gid, KeepWithGroup = child.Attribute("data-keep-with-group")?.Value, DropShadow = DropShadowOf(child, ctx) };
                Walk(child, new Style(), Affine.Identity, layer.Shapes, ctx, 0);
                result.Layers.Add(layer);
            }
            else
            {
                if (loose == null) { loose = new SvgLayer { Id = "" }; result.Layers.Add(loose); }
                Walk(child, new Style(), Affine.Identity, loose.Shapes, ctx, 0);
            }
        }
        return result;
    }

    /// <summary>tarkov.dev draws upper/lower floors with <c>.shadow { filter: drop-shadow(0 0 2px #000) }</c> on the layer group.
    /// Only that simple form is supported: blur radius and colour (offsets ignored).</summary>
    private static SvgDropShadow? DropShadowOf(XElement g, Ctx ctx)
    {
        string? filter = null;
        if (Attr(g, "class") is { } cls)
        {
            var classes = cls.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            foreach (var (c, d) in ctx.Rules)
                if (classes.Contains(c) && d.TryGetValue("filter", out var f)) filter = f;
        }
        if (Attr(g, "style") is { } inline && Decls(inline).TryGetValue("filter", out var fi)) filter = fi;
        if (filter == null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(filter, @"drop-shadow\(\s*(?<a>[^\s)]+)\s+(?<b>[^\s)]+)\s+(?<r>[^\s)]+)\s*(?<c>[^)]*\)?)?\s*\)");
        if (!m.Success) return null;
        var blur = Len(m.Groups["r"].Value) ?? 0;
        var col = m.Groups["c"].Success && m.Groups["c"].Value.Trim() is { Length: > 0 } cv ? Color(cv) ?? 0xFF000000u : 0xFF000000u;
        return blur > 0 ? new SvgDropShadow(blur, col) : null;
    }

    private sealed record Ctx(List<(string Cls, Dictionary<string, string> Decls)> Rules, Dictionary<string, XElement> Ids);

    private static void Walk(XElement e, Style inherited, Affine parent, List<SvgShape> output, Ctx ctx, int depth)
    {
        if (depth > 64) return;
        var name = e.Name.LocalName;
        if (name is "style" or "defs" or "metadata" or "title" or "desc" or "linearGradient" or "radialGradient" or "clipPath" or "mask" or "symbol" or "filter" or "pattern" or "text") return;
        var style = Resolve(e, inherited, ctx);
        if (style.Hidden) return;
        var tf = Transform(Attr(e, "transform")).Then(parent);
        switch (name)
        {
            case "svg":
            case "g":
            case "a":
            case "switch":
                // group opacity multiplies into children (approximation of an offscreen group)
                foreach (var c in e.Elements()) Walk(c, style, tf, output, ctx, depth + 1);
                break;
            case "use":
            {
                var href = (e.Attribute(XLink + "href") ?? e.Attribute("href"))?.Value;
                if (href is null || !href.StartsWith('#') || !ctx.Ids.TryGetValue(href[1..], out var target) || target == e) break;
                double x = Len(Attr(e, "x")) ?? 0, y = Len(Attr(e, "y")) ?? 0;
                var useTf = new Affine(1, 0, 0, 1, x, y).Then(tf);
                Walk(target, style, useTf, output, ctx, depth + 1);
                break;
            }
            default:
                var segs = Geometry(e, name);
                if (segs == null || segs.Count == 0) break;
                Emit(segs, style, tf, output, ctx);
                break;
        }
    }

    private static void Emit(List<Seg> segs, Style s, Affine tf, List<SvgShape> output, Ctx ctx)
    {
        var fill = Paint(s.Fill, ctx, tf);
        var stroke = Paint(s.Stroke, ctx, tf);
        if (fill == null && stroke == null) return;
        var abs = new List<Seg>(segs.Count);
        foreach (var g in segs)
        {
            var (x, y) = tf.Apply(g.X, g.Y);
            switch (g.Kind)
            {
                case SegKind.Cubic:
                    var (x1, y1) = tf.Apply(g.X1, g.Y1); var (x2, y2) = tf.Apply(g.X2, g.Y2);
                    abs.Add(new Seg(SegKind.Cubic, x, y, x1, y1, x2, y2)); break;
                case SegKind.Quad:
                    var (qx, qy) = tf.Apply(g.X1, g.Y1);
                    abs.Add(new Seg(SegKind.Quad, x, y, qx, qy)); break;
                case SegKind.Arc:
                    abs.Add(new Seg(SegKind.Arc, x, y, g.X1 * tf.Scale, g.Y1 * tf.Scale, g.X2, g.Y2)); break;
                default:
                    abs.Add(new Seg(g.Kind, x, y)); break;
            }
        }
        double sc = tf.Scale;
        output.Add(new SvgShape
        {
            Segments = abs, Fill = fill, Stroke = stroke, StrokeWidth = s.StrokeWidth * sc,
            Dash = s.Dash?.Select(d => d * sc).ToArray(), DashOffset = s.DashOffset * sc, Opacity = s.Opacity,
            FillOpacity = s.FillOpacity, StrokeOpacity = s.StrokeOpacity, EvenOdd = s.EvenOdd, LineCap = s.LineCap, LineJoin = s.LineJoin, MiterLimit = s.MiterLimit,
        });
    }

    // ---------------- style ----------------
    private static Style Resolve(XElement e, Style inherited, Ctx ctx)
    {
        var s = inherited.Clone();
        s.Opacity = inherited.Opacity; // opacity (not inherited) multiplies below
        var decls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // presentation attributes (lowest), then class rules in stylesheet order, then inline style (highest)
        foreach (var a in e.Attributes())
            if (a.Name.Namespace == XNamespace.None && IsStyleProp(a.Name.LocalName)) decls[a.Name.LocalName] = a.Value;
        if (Attr(e, "class") is { } cls)
        {
            var classes = cls.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            foreach (var (c, d) in ctx.Rules)
                if (classes.Contains(c)) foreach (var kv in d) decls[kv.Key] = kv.Value;
        }
        if (Attr(e, "style") is { } inline) foreach (var kv in Decls(inline)) decls[kv.Key] = kv.Value;

        foreach (var (k, raw) in decls)
        {
            var v = raw.Replace("!important", "").Trim();
            switch (k.ToLowerInvariant())
            {
                case "fill": s.Fill = v; break;
                case "stroke": s.Stroke = v; break;
                case "stroke-width": s.StrokeWidth = Len(v) ?? s.StrokeWidth; break;
                case "fill-opacity": s.FillOpacity = Clamp01(Num(v) ?? 1); break;
                case "stroke-opacity": s.StrokeOpacity = Clamp01(Num(v) ?? 1); break;
                case "opacity": s.Opacity *= Clamp01(Num(v) ?? 1); break;
                case "stroke-dasharray":
                    if (v == "none") s.Dash = null;
                    else { var d = ParseNums(v); s.Dash = d.Count > 0 && d.Any(x => x > 0) ? (d.Count % 2 == 1 ? d.Concat(d).ToArray() : d.ToArray()) : null; }
                    break;
                case "stroke-dashoffset": s.DashOffset = Len(v) ?? 0; break;
                case "stroke-miterlimit": s.MiterLimit = Num(v) ?? 4; break;
                case "stroke-linecap": s.LineCap = v; break;
                case "stroke-linejoin": s.LineJoin = v; break;
                case "fill-rule": s.EvenOdd = v == "evenodd"; break;
                case "display": if (v == "none") s.Hidden = true; break;
                case "visibility": s.Hidden = v is "hidden" or "collapse"; break;
            }
        }
        return s;
    }

    private static bool IsStyleProp(string n) => n is "fill" or "stroke" or "stroke-width" or "fill-opacity" or "stroke-opacity" or "opacity"
        or "stroke-dasharray" or "stroke-dashoffset" or "stroke-miterlimit" or "stroke-linecap" or "stroke-linejoin" or "fill-rule" or "display" or "visibility";

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    internal static void ParseCss(string css, List<(string, Dictionary<string, string>)> rules)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(css, @"([^{}]+)\{([^}]*)\}"))
        {
            var decls = Decls(m.Groups[2].Value);
            foreach (var sel in m.Groups[1].Value.Split(','))
            {
                var t = sel.Trim();
                // only simple class selectors are used by the map files: ".name"
                if (Regex.IsMatch(t, @"^\.[A-Za-z0-9_-]+$")) rules.Add((t[1..], decls));
            }
        }
    }

    private static Dictionary<string, string> Decls(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in s.Split(';'))
        {
            int i = part.IndexOf(':');
            if (i <= 0) continue;
            d[part[..i].Trim()] = part[(i + 1)..].Trim();
        }
        return d;
    }

    private static SvgPaint? Paint(string? v, Ctx ctx, Affine tf)
    {
        if (string.IsNullOrWhiteSpace(v) || v == "none" || v == "transparent") return null;
        var m = Regex.Match(v, @"url\(\s*['""]?#([^)'""]+)['""]?\s*\)");
        if (m.Success)
        {
            if (ctx.Ids.TryGetValue(m.Groups[1].Value, out var g) && g.Name.LocalName == "linearGradient")
            {
                var grad = Gradient(g, ctx, tf);
                if (grad != null) return grad.Stops.Count == 1 ? new SvgPaint(grad.Stops[0].Argb) : new SvgPaint(grad.Stops[0].Argb, grad);
            }
            // fallback colour after the url, e.g. "url(#x) #fff"
            var rest = v[(m.Index + m.Length)..].Trim();
            return rest.Length > 0 ? Paint(rest, ctx, tf) : null;
        }
        return Color(v) is { } c ? new SvgPaint(c) : null;
    }

    private static SvgLinearGradient? Gradient(XElement g, Ctx ctx, Affine tf)
    {
        // stops and attributes may be inherited through href chains
        var chain = new List<XElement>();
        for (var cur = g; cur != null && chain.Count < 8; )
        {
            chain.Add(cur);
            var href = (cur.Attribute(XLink + "href") ?? cur.Attribute("href"))?.Value;
            cur = href != null && href.StartsWith('#') && ctx.Ids.TryGetValue(href[1..], out var nx) && !chain.Contains(nx) ? nx : null;
        }
        string? A(string n) => chain.Select(x => Attr(x, n)).FirstOrDefault(x => x != null);
        var stopsEl = chain.Select(x => x.Elements().Where(s => s.Name.LocalName == "stop").ToList()).FirstOrDefault(l => l.Count > 0);
        if (stopsEl == null) return null;
        var stops = new List<SvgStop>();
        foreach (var s in stopsEl)
        {
            var d = Decls(Attr(s, "style") ?? "");
            var color = d.GetValueOrDefault("stop-color") ?? Attr(s, "stop-color") ?? "#000";
            var op = Num(d.GetValueOrDefault("stop-opacity") ?? Attr(s, "stop-opacity") ?? "1") ?? 1;
            var off = Attr(s, "offset") ?? "0";
            double o = off.EndsWith('%') ? (Num(off.TrimEnd('%')) ?? 0) / 100 : Num(off) ?? 0;
            var c = Color(color) ?? 0xFF000000;
            uint a = (uint)Math.Round(((c >> 24) & 0xFF) * Clamp01(op));
            stops.Add(new SvgStop(Clamp01(o), (a << 24) | (c & 0xFFFFFF)));
        }
        bool bbox = A("gradientUnits") != "userSpaceOnUse";
        double P(string n, double def) { var s = A(n); if (s == null) return def; return s.EndsWith('%') ? (Num(s.TrimEnd('%')) ?? 0) / 100 : Len(s) ?? def; }
        double x1 = P("x1", 0), y1 = P("y1", 0), x2 = P("x2", 1), y2 = P("y2", 0);
        if (!bbox)
        {
            var gt = Transform(A("gradientTransform")).Then(tf);
            (x1, y1) = gt.Apply(x1, y1); (x2, y2) = gt.Apply(x2, y2);
        }
        return new SvgLinearGradient(x1, y1, x2, y2, bbox, stops);
    }

    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 0xFF000000, ["white"] = 0xFFFFFFFF, ["red"] = 0xFFFF0000, ["green"] = 0xFF008000, ["blue"] = 0xFF0000FF,
        ["yellow"] = 0xFFFFFF00, ["gray"] = 0xFF808080, ["grey"] = 0xFF808080, ["orange"] = 0xFFFFA500, ["silver"] = 0xFFC0C0C0,
        ["lime"] = 0xFF00FF00, ["navy"] = 0xFF000080, ["purple"] = 0xFF800080, ["teal"] = 0xFF008080, ["maroon"] = 0xFF800000,
        ["olive"] = 0xFF808000, ["aqua"] = 0xFF00FFFF, ["cyan"] = 0xFF00FFFF, ["fuchsia"] = 0xFFFF00FF, ["magenta"] = 0xFFFF00FF,
        ["brown"] = 0xFFA52A2A, ["darkgray"] = 0xFFA9A9A9, ["lightgray"] = 0xFFD3D3D3, ["darkgreen"] = 0xFF006400,
    };

    /// <summary>"#rgb", "#rrggbb", "#rrggbbaa", "rgb(r,g,b)", "rgba(r,g,b,a)", basic names → ARGB.</summary>
    public static uint? Color(string v)
    {
        v = v.Trim();
        if (v.StartsWith('#'))
        {
            var h = v[1..];
            if (h.Length == 3) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
            if (h.Length == 6 && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return 0xFF000000 | rgb;
            if (h.Length == 8 && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba)) return (rgba << 24) | (rgba >> 8);
            return null;
        }
        var m = Regex.Match(v, @"^rgba?\(\s*([\d.]+%?)\s*[, ]\s*([\d.]+%?)\s*[, ]\s*([\d.]+%?)\s*(?:[,/]\s*([\d.]+%?))?\s*\)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            byte Ch(string s) => (byte)Math.Clamp(Math.Round(s.EndsWith('%') ? (Num(s.TrimEnd('%')) ?? 0) * 2.55 : Num(s) ?? 0), 0, 255);
            uint a = 255;
            if (m.Groups[4].Success) { var s = m.Groups[4].Value; a = (uint)Math.Clamp(Math.Round((s.EndsWith('%') ? (Num(s.TrimEnd('%')) ?? 100) / 100 : Num(s) ?? 1) * 255), 0, 255); }
            return (a << 24) | ((uint)Ch(m.Groups[1].Value) << 16) | ((uint)Ch(m.Groups[2].Value) << 8) | Ch(m.Groups[3].Value);
        }
        return Named.TryGetValue(v, out var n) ? n : null;
    }

    // ---------------- geometry ----------------
    private static List<Seg>? Geometry(XElement e, string name)
    {
        switch (name)
        {
            case "path": return PathData.Parse(Attr(e, "d") ?? "");
            case "circle":
            {
                double cx = Len(Attr(e, "cx")) ?? 0, cy = Len(Attr(e, "cy")) ?? 0, r = Len(Attr(e, "r")) ?? 0;
                return r > 0 ? Ellipse(cx, cy, r, r) : null;
            }
            case "ellipse":
            {
                double cx = Len(Attr(e, "cx")) ?? 0, cy = Len(Attr(e, "cy")) ?? 0, rx = Len(Attr(e, "rx")) ?? 0, ry = Len(Attr(e, "ry")) ?? 0;
                return rx > 0 && ry > 0 ? Ellipse(cx, cy, rx, ry) : null;
            }
            case "rect":
            {
                double x = Len(Attr(e, "x")) ?? 0, y = Len(Attr(e, "y")) ?? 0, w = Len(Attr(e, "width")) ?? 0, h = Len(Attr(e, "height")) ?? 0;
                if (w <= 0 || h <= 0) return null;
                return new List<Seg> { new(SegKind.Move, x, y), new(SegKind.Line, x + w, y), new(SegKind.Line, x + w, y + h), new(SegKind.Line, x, y + h), new(SegKind.Close, x, y) };
            }
            case "line":
            {
                return new List<Seg> { new(SegKind.Move, Len(Attr(e, "x1")) ?? 0, Len(Attr(e, "y1")) ?? 0), new(SegKind.Line, Len(Attr(e, "x2")) ?? 0, Len(Attr(e, "y2")) ?? 0) };
            }
            case "polygon":
            case "polyline":
            {
                var n = ParseNums(Attr(e, "points") ?? "");
                if (n.Count < 4) return null;
                var res = new List<Seg> { new(SegKind.Move, n[0], n[1]) };
                for (int i = 2; i + 1 < n.Count; i += 2) res.Add(new Seg(SegKind.Line, n[i], n[i + 1]));
                if (name == "polygon") res.Add(new Seg(SegKind.Close, n[0], n[1]));
                return res;
            }
        }
        return null;
    }

    private static List<Seg> Ellipse(double cx, double cy, double rx, double ry)
    {
        const double k = 0.5522847498307936;
        double ox = rx * k, oy = ry * k;
        return new List<Seg>
        {
            new(SegKind.Move, cx + rx, cy),
            new(SegKind.Cubic, cx, cy + ry, cx + rx, cy + oy, cx + ox, cy + ry),
            new(SegKind.Cubic, cx - rx, cy, cx - ox, cy + ry, cx - rx, cy + oy),
            new(SegKind.Cubic, cx, cy - ry, cx - rx, cy - oy, cx - ox, cy - ry),
            new(SegKind.Cubic, cx + rx, cy, cx + ox, cy - ry, cx + rx, cy - oy),
            new(SegKind.Close, cx + rx, cy),
        };
    }

    private static Affine Transform(string? s)
    {
        var t = Affine.Identity;
        if (string.IsNullOrWhiteSpace(s)) return t;
        foreach (Match m in Regex.Matches(s, @"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)"))
        {
            var a = ParseNums(m.Groups[2].Value);
            Affine op = m.Groups[1].Value switch
            {
                "matrix" when a.Count == 6 => new Affine(a[0], a[1], a[2], a[3], a[4], a[5]),
                "translate" when a.Count >= 1 => new Affine(1, 0, 0, 1, a[0], a.Count > 1 ? a[1] : 0),
                "scale" when a.Count >= 1 => new Affine(a[0], 0, 0, a.Count > 1 ? a[1] : a[0], 0, 0),
                "rotate" when a.Count >= 1 => Rotate(a[0], a.Count >= 3 ? a[1] : 0, a.Count >= 3 ? a[2] : 0),
                "skewX" when a.Count == 1 => new Affine(1, 0, Math.Tan(a[0] * Math.PI / 180), 1, 0, 0),
                "skewY" when a.Count == 1 => new Affine(1, Math.Tan(a[0] * Math.PI / 180), 0, 1, 0, 0),
                _ => Affine.Identity,
            };
            // transform list: rightmost applies first → t = t · op
            t = op.Then(t);
        }
        return t;
    }

    private static Affine Rotate(double deg, double cx, double cy)
    {
        double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        return new Affine(1, 0, 0, 1, -cx, -cy).Then(new Affine(c, s, -s, c, 0, 0)).Then(new Affine(1, 0, 0, 1, cx, cy));
    }

    // ---------------- helpers ----------------
    private static string? Attr(XElement e, string n) => e.Attribute(n)?.Value;

    internal static double? Num(string? s) =>
        s != null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    internal static double? Len(string? s)
    {
        if (s == null) return null;
        s = s.Trim();
        foreach (var u in new[] { "px", "pt", "mm", "cm", "in" }) if (s.EndsWith(u, StringComparison.OrdinalIgnoreCase)) { s = s[..^u.Length]; break; }
        return Num(s);
    }

    internal static List<double> ParseNums(string s)
    {
        var res = new List<double>();
        foreach (Match m in Regex.Matches(s, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?"))
            if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) res.Add(d);
        return res;
    }
}
