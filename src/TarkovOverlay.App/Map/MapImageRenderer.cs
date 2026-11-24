using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using TarkovOverlay.App.Services;
using TarkovOverlay.Maps;
using TarkovOverlay.Maps.Definitions;
using TarkovOverlay.Maps.Geometry;
using TarkovOverlay.Maps.Svg;

namespace TarkovOverlay.App.Map;

/// <summary>A rendered base map and where it sits in tarkov.dev zoom-0 pixel space.</summary>
public sealed record MapImage(BitmapSource Bitmap, MapRect Placement, string Source, MapStyle Style = MapStyle.Vector, int TileZoom = -1);

/// <summary>
/// Produces the base map bitmap: tarkov.dev SVG → own parser → WPF drawing → PNG cache, or tile maps stitched at
/// one zoom level. Everything is cached under %LOCALAPPDATA%\TarkovPriceOverlay\maps, so reopening is a PNG load.
/// </summary>
public sealed class MapImageRenderer
{
    public const int RenderVersion = 1;
    private const int MaxSide = 4096;
    private const long MaxPixels = 12_000_000;
    /// <summary>1.2.1: stitched satellite pictures are cached per tile set + zoom; bump to rebuild them.</summary>
    public const int TileRenderVersion = 2;
    private static readonly TimeSpan SourceMaxAge = TimeSpan.FromDays(7);

    private readonly MapPaths _paths;
    private readonly HttpClient _http;

    public MapImageRenderer(MapPaths paths, HttpClient http) { _paths = paths; _http = http; }

    /// <summary>Draws <paramref name="layer"/> in <paramref name="preferred"/> style when the map has it (else the other one).</summary>
    public async Task<MapImage> GetAsync(MapDefinition def, MapLayerChoice layer, MapStyle preferred, IProgress<string>? progress, CancellationToken ct)
    {
        var style = MapStyles.Effective(def, preferred, layer);
        var key = $"{def.Key}|{layer.Name}|{style}";
        lock (_recent) { var hit = _recent.FirstOrDefault(r => r.Key == key); if (hit.Image != null) { _recent.Remove(hit); _recent.Insert(0, hit); return hit.Image with { Source = "memory" }; } }
        MapImage img;
        if (style == MapStyle.Satellite && def.HasTiles) img = await TilesAsync(def, layer, progress, ct);
        else if (def.HasSvg) img = await SvgAsync(def, layer, progress, ct) with { Style = MapStyle.Vector };
        else throw new InvalidOperationException($"{def.Key} has no drawing");
        lock (_recent) { _recent.RemoveAll(r => r.Key == key); _recent.Insert(0, (key, img)); if (_recent.Count > 2) _recent.RemoveRange(2, _recent.Count - 2); }
        return img;
    }

    /// <summary>The last two pictures (typically the same map in both styles), so the toggle switches instantly.</summary>
    private readonly List<(string Key, MapImage Image)> _recent = new();

    /// <summary>Pre-1.2.1 behaviour (vector first), used by the self-test.</summary>
    public Task<MapImage> GetAsync(MapDefinition def, MapLayerChoice layer, IProgress<string>? progress, CancellationToken ct) =>
        GetAsync(def, layer, MapStyle.Vector, progress, ct);

    // ---------------- SVG ----------------
    private async Task<MapImage> SvgAsync(MapDefinition def, MapLayerChoice layer, IProgress<string>? progress, CancellationToken ct)
    {
        var url = def.SvgPath!;
        var srcFile = Path.Combine(_paths.SourceDir, MapPaths.Sanitize(Path.GetFileName(new Uri(url).AbsolutePath)));
        string style = $"svg{RenderVersion}-{Hash(url)}";
        var png = _paths.ImageFile(def.Key, layer.Name, style);
        var svgRect = def.SvgRect;

        if (File.Exists(png) && File.Exists(srcFile) && File.GetLastWriteTimeUtc(png) >= File.GetLastWriteTimeUtc(srcFile)
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(srcFile) < SourceMaxAge && ReadMeta(png) is { } meta)
        {
            var bmp = await Task.Run(() => LoadPng(png), ct);
            return new MapImage(bmp, MapTransform.FitMeet(svgRect, meta.W, meta.H), "cache");
        }

        progress?.Report("Downloading map drawing…");
        string xml = await DownloadTextCachedAsync(url, srcFile, ct);
        progress?.Report("Drawing map…");
        var (bitmap, vbW, vbH) = await RunSta(() =>
        {
            var doc = SvgParser.Parse(xml);
            var extra = layer.SvgLayer != null ? new[] { layer.SvgLayer } : null;
            var bmp = RenderSvg(doc, def.SvgLayer, extra);
            return (bmp, doc.ViewBoxWidth, doc.ViewBoxHeight);
        }, ct);
        SavePng(png, bitmap);
        WriteMeta(png, vbW, vbH);
        Log.Write($"map: rendered {def.Key}/{layer.Name} {bitmap.PixelWidth}×{bitmap.PixelHeight} from {Path.GetFileName(srcFile)}");
        return new MapImage(bitmap, MapTransform.FitMeet(svgRect, vbW, vbH), "rendered");
    }

    internal static BitmapSource RenderSvg(SvgDocument doc, string? baseLayer, IReadOnlyCollection<string>? extra)
    {
        double vw = Math.Max(1, doc.ViewBoxWidth), vh = Math.Max(1, doc.ViewBoxHeight);
        double scale = MaxSide / Math.Max(vw, vh);
        if (vw * vh * scale * scale > MaxPixels) scale = Math.Sqrt(MaxPixels / (vw * vh));
        int w = Math.Max(1, (int)Math.Round(vw * scale)), h = Math.Max(1, (int)Math.Round(vh * scale));

        var root = new ContainerVisual();
        var layers = doc.Visible(baseLayer).ToList();
        if (baseLayer != null && !layers.Any(l => l.Id == baseLayer)) layers = doc.Layers.Where(l => extra == null || !extra.Contains(l.Id)).ToList(); // unknown base id: draw everything
        // A selected floor is drawn on top of the dimmed ground level (like tarkov.dev's layer overlay).
        var upper = extra == null ? new List<SvgLayer>() : doc.Layers.Where(l => extra.Contains(l.Id) && !layers.Contains(l)).ToList();
        int dimAt = upper.Count > 0 ? layers.Count : -1;
        layers.AddRange(upper);
        for (int li = 0; li < layers.Count; li++)
        {
            var layer = layers[li];
            if (li == dimAt)
            {
                var dim = new DrawingVisual();
                using (var dc = dim.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, new Rect(0, 0, w, h));
                root.Children.Add(dim);
            }
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, -doc.ViewBoxX * scale, -doc.ViewBoxY * scale));
                foreach (var s in layer.Shapes) DrawShape(dc, s);
                dc.Pop();
            }
            if (layer.DropShadow is { } ds)
                dv.Effect = new DropShadowEffect { BlurRadius = ds.Blur * scale * 2, ShadowDepth = 0, Color = ToColor(ds.Argb), Opacity = ((ds.Argb >> 24) / 255.0), RenderingBias = RenderingBias.Performance };
            root.Children.Add(dv);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    private static void DrawShape(DrawingContext dc, SvgShape s)
    {
        var geo = ToGeometry(s);
        if (geo == null) return;
        Brush? fill = s.Fill != null ? ToBrush(s.Fill, s.FillOpacity, geo) : null;
        Pen? pen = null;
        if (s.Stroke != null && s.StrokeWidth > 0)
        {
            pen = new Pen(ToBrush(s.Stroke, s.StrokeOpacity, geo), s.StrokeWidth)
            {
                StartLineCap = Cap(s.LineCap), EndLineCap = Cap(s.LineCap), DashCap = Cap(s.LineCap),
                LineJoin = s.LineJoin switch { "round" => PenLineJoin.Round, "bevel" => PenLineJoin.Bevel, _ => PenLineJoin.Miter },
                MiterLimit = Math.Max(1, s.MiterLimit),
            };
            if (s.Dash is { Length: > 0 } dash && dash.Any(d => d > 0))
            {
                var arr = dash.Length % 2 == 1 ? dash.Concat(dash).ToArray() : dash;
                pen.DashStyle = new DashStyle(arr.Select(d => d / s.StrokeWidth), s.DashOffset / s.StrokeWidth);
            }
            pen.Freeze();
        }
        if (s.Opacity < 1) dc.PushOpacity(s.Opacity);
        dc.DrawGeometry(fill, pen, geo);
        if (s.Opacity < 1) dc.Pop();
    }

    private static PenLineCap Cap(string c) => c switch { "round" => PenLineCap.Round, "square" => PenLineCap.Square, _ => PenLineCap.Flat };

    private static StreamGeometry? ToGeometry(SvgShape s)
    {
        if (s.Segments.Count < 2) return null;
        var g = new StreamGeometry { FillRule = s.EvenOdd ? FillRule.EvenOdd : FillRule.Nonzero };
        bool fillable = s.Fill != null;
        var segs = s.Segments;
        using (var c = g.Open())
        {
            Point start = default, cur = default;
            int i = 0;
            while (i < segs.Count)
            {
                if (segs[i].Kind == SegKind.Close) { cur = start; i++; continue; }
                if (segs[i].Kind == SegKind.Move) { start = cur = new Point(segs[i].X, segs[i].Y); i++; }
                else start = cur; // drawing command without a move: continue from the current point
                // one figure = drawing commands up to the next Move/Close
                int end = i;
                while (end < segs.Count && segs[end].Kind is not (SegKind.Move or SegKind.Close)) end++;
                bool closed = end < segs.Count && segs[end].Kind == SegKind.Close;
                if (end == i) { i = end; continue; } // lone move
                c.BeginFigure(start, fillable, closed);
                for (; i < end; i++)
                {
                    var seg = segs[i];
                    var p = new Point(seg.X, seg.Y);
                    switch (seg.Kind)
                    {
                        case SegKind.Line: c.LineTo(p, true, true); break;
                        case SegKind.Cubic: c.BezierTo(new Point(seg.X1, seg.Y1), new Point(seg.X2, seg.Y2), p, true, true); break;
                        case SegKind.Quad: c.QuadraticBezierTo(new Point(seg.X1, seg.Y1), p, true, true); break;
                        case SegKind.Arc:
                            int flags = (int)seg.Y2;
                            if (seg.X1 == 0 || seg.Y1 == 0) c.LineTo(p, true, true);
                            else c.ArcTo(p, new Size(Math.Abs(seg.X1), Math.Abs(seg.Y1)), seg.X2, (flags & 1) != 0,
                                (flags & 2) != 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
                            break;
                    }
                    cur = p;
                }
                if (closed) { cur = start; i++; }
            }
        }
        g.Freeze();
        return g;
    }

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static Brush ToBrush(SvgPaint p, double opacity, Geometry geo)
    {
        Brush b;
        if (p.Gradient is { } gr)
        {
            var stops = new GradientStopCollection(gr.Stops.Select(st => new GradientStop(ToColor(st.Argb), st.Offset)));
            b = new LinearGradientBrush(stops, new Point(gr.X1, gr.Y1), new Point(gr.X2, gr.Y2))
            {
                MappingMode = gr.BoundingBoxUnits ? BrushMappingMode.RelativeToBoundingBox : BrushMappingMode.Absolute,
                Opacity = opacity,
            };
        }
        else b = new SolidColorBrush(ToColor(p.Argb)) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    // ---------------- tiles ----------------
    /// <summary>
    /// Satellite: the main tile set (plus, for a floor, the dimmed main set with the floor's transparent tiles on top,
    /// like tarkov.dev's level overlay) stitched at <see cref="TileGrid.Choose"/>'s zoom. Placement is the tile block's
    /// zoom-0 rectangle, so markers (projected with the same transform) land on the same spots as on the SVG.
    /// </summary>
    private async Task<MapImage> TilesAsync(MapDefinition def, MapLayerChoice layer, IProgress<string>? progress, CancellationToken ct)
    {
        var grid = TileGrid.Choose(def);
        var sets = new List<string> { def.TilePath! };
        if (layer.TilePath != null && layer.TilePath != def.TilePath) sets.Add(layer.TilePath);
        var style = $"tiles{TileRenderVersion}-z{grid.Z}-{Hash(string.Join("|", sets))}";
        var png = _paths.ImageFile(def.Key, layer.Name, style);
        if (File.Exists(png) && DateTime.UtcNow - File.GetLastWriteTimeUtc(png) < TimeSpan.FromDays(30))
        {
            try { return new MapImage(await Task.Run(() => LoadPng(png), ct), grid.Placement, "cache", MapStyle.Satellite, grid.Z); }
            catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException) { Log.Write($"map: cached {Path.GetFileName(png)} unreadable ({ex.Message}); rebuilding"); }
        }

        var jobs = new List<(int Set, int X, int Y, string File)>();
        for (int si = 0; si < sets.Count; si++)
        {
            var dir = _paths.TileDir(def.Key, si == 0 ? "Main" : layer.Name);
            Directory.CreateDirectory(dir);
            var prefix = $"{Hash(sets[si])}_{grid.Z}";
            for (int x = grid.X0; x <= grid.X1; x++)
                for (int y = grid.Y0; y <= grid.Y1; y++)
                    if (x >= 0 && y >= 0) jobs.Add((si, x, y, Path.Combine(dir, $"{prefix}_{x}_{y}.png")));
        }
        int done = 0, missing = 0;
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(jobs.Select(async j =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!File.Exists(j.File))
                {
                    var url = grid.Url(sets[j.Set], j.X, j.Y);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(30));
                    using var resp = await _http.GetAsync(url, cts.Token);
                    if (!resp.IsSuccessStatusCode) { Interlocked.Increment(ref missing); return; }
                    var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                    Core.AppPaths.AtomicWrite(j.File, s => s.Write(bytes));
                }
            }
            catch (HttpRequestException) { Interlocked.Increment(ref missing); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { Interlocked.Increment(ref missing); }
            finally
            {
                gate.Release();
                progress?.Report($"Downloading satellite map… {Interlocked.Increment(ref done)}/{jobs.Count}");
            }
        }));
        ct.ThrowIfCancellationRequested();
        if (missing == jobs.Count) throw new HttpRequestException("map tiles could not be downloaded");
        progress?.Report("Stitching satellite map…");
        var bmp = await RunSta(() => Stitch(grid, jobs.Select(j => (j.Set, j.X, j.Y, j.File)).ToList(), sets.Count > 1), ct);
        SavePng(png, bmp);
        Log.Write($"map: stitched {def.Key}/{layer.Name} satellite z{grid.Z} {jobs.Count} tiles ({missing} missing) → {bmp.PixelWidth}×{bmp.PixelHeight}");
        return new MapImage(bmp, grid.Placement, "rendered", MapStyle.Satellite, grid.Z);
    }

    /// <summary>Copies decoded tiles straight into one bitmap (no WPF render pass: fast and exact to the pixel).</summary>
    internal static BitmapSource Stitch(TileGrid grid, IReadOnlyList<(int Set, int X, int Y, string File)> tiles, bool dimBase)
    {
        int ts = grid.TileSize, w = grid.PixelWidth, h = grid.PixelHeight, stride = w * 4;
        var buf = new byte[(long)stride * h];
        var tileBuf = new byte[ts * ts * 4];
        foreach (var set in tiles.Select(t => t.Set).Distinct().OrderBy(x => x))
        {
            if (set == 1 && dimBase)
                for (int i = 0; i < buf.Length; i += 4) { buf[i] = (byte)(buf[i] * 0.45); buf[i + 1] = (byte)(buf[i + 1] * 0.45); buf[i + 2] = (byte)(buf[i + 2] * 0.45); }
            foreach (var t in tiles.Where(t => t.Set == set))
            {
                if (!File.Exists(t.File)) continue;
                BitmapSource src;
                try { src = LoadPng(t.File); }
                catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException or ArgumentException) { continue; }
                if (src.PixelWidth != ts || src.PixelHeight != ts) src = new TransformedBitmap(src, new ScaleTransform((double)ts / src.PixelWidth, (double)ts / src.PixelHeight));
                var conv = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
                conv.CopyPixels(tileBuf, ts * 4, 0);
                int ox = (t.X - grid.X0) * ts, oy = (t.Y - grid.Y0) * ts;
                for (int row = 0; row < ts; row++)
                {
                    long dst = (long)(oy + row) * stride + ox * 4;
                    int srcOff = row * ts * 4;
                    if (set == 0) { Buffer.BlockCopy(tileBuf, srcOff, buf, (int)dst, ts * 4); continue; }
                    for (int px = 0; px < ts * 4; px += 4) // premultiplied "over"
                    {
                        int a = tileBuf[srcOff + px + 3];
                        if (a == 0) continue;
                        int inv = 255 - a;
                        for (int c = 0; c < 4; c++) buf[dst + px + c] = (byte)(tileBuf[srcOff + px + c] + buf[dst + px + c] * inv / 255);
                    }
                }
            }
        }
        var wb = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
        wb.Freeze();
        return wb;
    }

    // ---------------- helpers ----------------
    private async Task<string> DownloadTextCachedAsync(string url, string file, CancellationToken ct)
    {
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < SourceMaxAge)
            return await File.ReadAllTextAsync(file, ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            var text = await _http.GetStringAsync(url, cts.Token);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Core.AppPaths.AtomicWrite(file, s => { using var w = new StreamWriter(s, new System.Text.UTF8Encoding(false), leaveOpen: true); w.Write(text); });
            return text;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested && File.Exists(file))
        {
            Log.Write($"map: {url} download failed ({ex.Message}); using the cached copy");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-6)); // retry again in about a day
            return await File.ReadAllTextAsync(file, ct);
        }
    }

    private static BitmapSource LoadPng(string file)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.UriSource = new Uri(file);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static void SavePng(string file, BitmapSource bmp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Core.AppPaths.AtomicWrite(file, s => enc.Save(s));
    }

    private static void WriteMeta(string png, double w, double h) =>
        File.WriteAllText(png + ".meta", FormattableString.Invariant($"{w} {h}"));

    private static (double W, double H)? ReadMeta(string png)
    {
        try
        {
            var p = File.ReadAllText(png + ".meta").Split(' ');
            return (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException or UnauthorizedAccessException) { return null; }
    }

    private static string Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>Runs WPF rendering on a short-lived STA thread so the UI stays responsive while big maps draw.</summary>
    internal static Task<T> RunSta<T>(Func<T> body, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.TrySetResult(body()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "map render" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }
}
