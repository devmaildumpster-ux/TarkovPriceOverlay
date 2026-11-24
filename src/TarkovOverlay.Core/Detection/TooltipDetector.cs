namespace TarkovOverlay.Core.Detection;

[Flags]
public enum Sides { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

public enum DetectionStatus { Found, NotFound, ClippedNeedsWider }

/// <summary>Tunable detection parameters, defined at 100% scale and scaled by DPI.</summary>
public sealed record DetectionParams
{
    public int DarkMax { get; init; } = 24;            // interior RGB channels ≤ this
    /// <summary>Second pass when nothing is found: the trader screen's tooltip is a little lighter (~#1a1a1a).</summary>
    public int DarkMaxRetry { get; init; } = 34;
    public int ColorTextBrightMin { get; init; } = 120; // saturated lettering (red "cannot buy", coloured values)
    public int CursorZoneW { get; init; } = 16;        // area right/below the pointer tip that a drawn cursor may cover
    public int CursorZoneH { get; init; } = 24;
    /// <summary>
    /// Smallest tooltip interior. Tarkov fits the box to the text, so a short name ("Awl", "Bolts", "Chainlet")
    /// gives a box only ~35-65 px wide (1.0.6 had 64 and missed Chainlet). Price tags stay out via MinHeight.
    /// </summary>
    public int MinWidth { get; init; } = 28;
    public int MinHeight { get; init; } = 20;          // spec suggests ~25; slightly lower to tolerate compact fonts
    public int MaxHeight { get; init; } = 150;
    public double MinCoverage { get; init; } = 0.72;   // dark pixels / bounding rectangle
    public int BorderTolerance { get; init; } = 3;     // px band searched for the gray frame
    public double MinBorderFraction { get; init; } = 0.80;
    public int BorderGrayMin { get; init; } = 40;
    public int BorderGrayMax { get; init; } = 200;
    public int BorderMaxSpread { get; init; } = 28;    // neutral gray: max-min channel spread
    public int TextBrightMin { get; init; } = 140;     // bright neutral lettering
    public int TextMaxSpread { get; init; } = 48;
    public int MinTextPixels { get; init; } = 25;
    public double MinTextWidthFraction { get; init; } = 0.45; // a tooltip is fitted to its title
    public int MinVerticalPad { get; init; } = 2;      // text must not touch top/bottom edges
    public int MinClipPadding { get; init; } = 4;      // padding before a clipped side
    public int LineGap { get; init; } = 2;

    public DetectionParams Scaled(double s)
    {
        if (s <= 0 || Math.Abs(s - 1) < 0.001) return this;
        int S(int v) => Math.Max(1, (int)Math.Round(v * s, MidpointRounding.AwayFromZero));
        return this with
        {
            MinWidth = S(MinWidth), MinHeight = S(MinHeight), MaxHeight = S(MaxHeight),
            BorderTolerance = Math.Max(BorderTolerance, (int)Math.Ceiling(BorderTolerance * s)),
            MinTextPixels = Math.Max(10, (int)(MinTextPixels * s * s)),
            MinVerticalPad = S(MinVerticalPad), MinClipPadding = S(MinClipPadding), LineGap = S(LineGap),
            CursorZoneW = S(CursorZoneW), CursorZoneH = S(CursorZoneH),
        };
    }
}

/// <param name="TitlePanel">
/// The part of the interior to OCR: from the top through the last leading line of neutral (white/grey) lettering.
/// Secondary lines in colour (the trader screen's red "The trader cannot buy this item.") are cut off so only the
/// item name is read. Equals <see cref="Interior"/> for plain tooltips.
/// </param>
public sealed record TooltipCandidate(
    PixelRect Interior, PixelRect TextBounds, int Lines, Sides ClippedSides, bool Accepted, string Reason, double Score)
{
    public PixelRect TitlePanel { get; init; } = Interior;
    public int TitleLines { get; init; } = Lines;
    /// <summary>The coloured secondary lines below the name (trader offer in gold, "cannot buy" in red), if any.</summary>
    public PixelRect? SecondaryPanel { get; init; }
}

public sealed record DetectionResult(DetectionStatus Status, TooltipCandidate? Best, IReadOnlyList<TooltipCandidate> Considered, string Title, string Hint)
{
    public static string NotVisibleTitle => "Item tooltip not visible";
    public static string NotVisibleHint => "Wait for Tarkov’s item name and retry.";
}

/// <summary>
/// Finds Tarkov's black, gray-framed item-name tooltip in a small capture around the cursor:
/// dark mask → connected components → rectangle, frame, and lettering validation.
/// </summary>
public static class TooltipDetector
{
    private const int MarginBlackMax = 12;
    public static DetectionResult Detect(RgbImage img, int cursorX, int cursorY, double dpiScale, bool wideRetry, DetectionParams? baseParams = null)
    {
        var p = (baseParams ?? new DetectionParams()).Scaled(dpiScale);
        var first = DetectPass(img, cursorX, cursorY, wideRetry, p);
        if (first.Status == DetectionStatus.Found || p.DarkMaxRetry <= p.DarkMax) return first;
        var second = DetectPass(img, cursorX, cursorY, wideRetry, p with { DarkMax = p.DarkMaxRetry });
        if (second.Status == DetectionStatus.Found) return second;
        var both = first.Considered.Concat(second.Considered).ToList();
        var pick = first.Status == DetectionStatus.ClippedNeedsWider ? first : second.Status == DetectionStatus.ClippedNeedsWider ? second : first;
        return pick with { Considered = both };
    }

    private static DetectionResult DetectPass(RgbImage img, int cursorX, int cursorY, bool wideRetry, DetectionParams p)
    {
        int w = img.Width, h = img.Height;
        var px = img.Bgra;
        var dark = new bool[w * h];
        for (int i = 0, o = 0; i < dark.Length; i++, o += 4)
            dark[i] = px[o] <= p.DarkMax && px[o + 1] <= p.DarkMax && px[o + 2] <= p.DarkMax;

        // Uniform outer black columns/rows (monitor margin / letterbox) must not merge into a candidate.
        // Judged on true black (independent of the dark threshold) so dim stash backgrounds are never stripped.
        var black = new bool[w * h];
        for (int i = 0, o = 0; i < black.Length; i++, o += 4)
            black[i] = px[o] <= MarginBlackMax && px[o + 1] <= MarginBlackMax && px[o + 2] <= MarginBlackMax;
        int mL = 0, mR = 0, mT = 0, mB = 0;
        while (mL < w / 3 && ColumnDark(black, w, h, mL) >= 0.98) mL++;
        while (mR < w / 3 && ColumnDark(black, w, h, w - 1 - mR) >= 0.98) mR++;
        while (mT < h / 3 && RowDark(black, w, mT) >= 0.98) mT++;
        while (mB < h / 3 && RowDark(black, w, h - 1 - mB) >= 0.98) mB++;
        var inner = new PixelRect(mL, mT, w - mL - mR, h - mT - mB);
        if (inner.IsEmpty) return NotFound(Array.Empty<TooltipCandidate>());
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (!inner.Contains(x, y)) dark[y * w + x] = false;

        // A drawn mouse pointer (white with a black outline) at the tip can bridge the tooltip's frame and join
        // its interior to dark item art outside; the pointer's area never decides the tooltip's shape.
        var pointer = new PixelRect(cursorX - 2, cursorY - 2, p.CursorZoneW + 2, p.CursorZoneH + 2).Intersect(new PixelRect(0, 0, w, h));
        for (int y = pointer.Y; y < pointer.Bottom; y++)
            for (int x = pointer.X; x < pointer.Right; x++) dark[y * w + x] = false;

        var considered = new List<TooltipCandidate>();
        foreach (var (rect, count) in Components(dark, w, h, p))
        {
            var c = Evaluate(img, rect, count, inner, cursorX, cursorY, wideRetry, p);
            if (c != null) considered.Add(c);
        }

        var best = considered.Where(c => c.Accepted).OrderByDescending(c => c.Score).FirstOrDefault();
        if (best != null)
            return new DetectionResult(DetectionStatus.Found, best, considered, "", "");
        if (!wideRetry && considered.Any(c => c.ClippedSides != Sides.None && c.Reason.StartsWith("edge", StringComparison.Ordinal)))
            return new DetectionResult(DetectionStatus.ClippedNeedsWider, null, considered, DetectionResult.NotVisibleTitle, "Tooltip touches the capture edge; retrying wider.");
        return NotFound(considered);
    }

    /// <summary>
    /// Crops a panel for OCR. A mouse pointer captured at the tip (pure white body, pure black outline) inside
    /// the panel is painted over with the tooltip background so it can't be read as a letter.
    /// </summary>
    public static RgbImage CropPanel(RgbImage img, PixelRect panel, int? cursorX = null, int? cursorY = null, double dpiScale = 1.0)
    {
        var crop = img.Crop(panel);
        if (cursorX is not { } cx || cursorY is not { } cy) return crop;
        var p = new DetectionParams().Scaled(dpiScale);
        var zone = new PixelRect(cx - 2 - panel.X, cy - 2 - panel.Y, p.CursorZoneW + 2, p.CursorZoneH + 2).Intersect(new PixelRect(0, 0, crop.Width, crop.Height));
        if (zone.IsEmpty) return crop;
        var bg = crop.Get(Math.Min(crop.Width - 1, crop.Width - 2), Math.Min(crop.Height - 1, 1));
        for (int y = zone.Y; y < zone.Bottom; y++)
            for (int x = zone.X; x < zone.Right; x++)
            {
                var (R, G, B) = crop.Get(x, y);
                int mn = Math.Min(R, Math.Min(G, B)), mx = Math.Max(R, Math.Max(G, B));
                if (mx <= 8 || mn >= 240) crop.Set(x, y, bg.R, bg.G, bg.B);
            }
        return crop;
    }

    private static DetectionResult NotFound(IReadOnlyList<TooltipCandidate> c) =>
        new(DetectionStatus.NotFound, null, c, DetectionResult.NotVisibleTitle, DetectionResult.NotVisibleHint);

    private static double ColumnDark(bool[] d, int w, int h, int x)
    {
        int n = 0; for (int y = 0; y < h; y++) if (d[y * w + x]) n++;
        return (double)n / h;
    }

    private static double RowDark(bool[] d, int w, int y)
    {
        int n = 0; for (int x = 0; x < w; x++) if (d[y * w + x]) n++;
        return (double)n / w;
    }

    /// <summary>4-connected components whose bounding box could be a tooltip interior.</summary>
    private static IEnumerable<(PixelRect Rect, int Count)> Components(bool[] dark, int w, int h, DetectionParams p)
    {
        var seen = new bool[dark.Length];
        var stack = new Stack<int>();
        int minCount = (int)(p.MinWidth * p.MinHeight * p.MinCoverage * 0.9);
        for (int start = 0; start < dark.Length; start++)
        {
            if (!dark[start] || seen[start]) continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            seen[start] = true; stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % w, y = i / w;
                count++;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (x > 0 && dark[i - 1] && !seen[i - 1]) { seen[i - 1] = true; stack.Push(i - 1); }
                if (x < w - 1 && dark[i + 1] && !seen[i + 1]) { seen[i + 1] = true; stack.Push(i + 1); }
                if (y > 0 && dark[i - w] && !seen[i - w]) { seen[i - w] = true; stack.Push(i - w); }
                if (y < h - 1 && dark[i + w] && !seen[i + w]) { seen[i + w] = true; stack.Push(i + w); }
            }
            if (count < minCount) continue;
            yield return (new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1), count);
        }
    }

    private static TooltipCandidate? Evaluate(RgbImage img, PixelRect r, int darkCount, PixelRect inner, int cx, int cy, bool wideRetry, DetectionParams p)
    {
        if (r.Width < p.MinWidth || r.Height < p.MinHeight) return null; // too small to be worth reporting
        TooltipCandidate Reject(string why, Sides clip = Sides.None, PixelRect tb = default, int lines = 0) => new(r, tb, lines, clip, false, why, 0);
        if (r.Height > p.MaxHeight) return Reject("too tall");
        // The pointer zone was cleared from the dark mask; don't count it against a small tooltip it overlaps.
        var ptr = new PixelRect(cx - 2, cy - 2, p.CursorZoneW + 2, p.CursorZoneH + 2).Intersect(r);
        long area = (long)r.Width * r.Height - (ptr.IsEmpty ? 0 : (long)ptr.Width * ptr.Height);
        double coverage = area <= 0 ? 0 : (double)darkCount / area;
        if (coverage < p.MinCoverage) return Reject($"dark coverage {coverage:P0} below threshold");

        Sides clipped = Sides.None;
        if (r.X <= inner.X) clipped |= Sides.Left;
        if (r.Right >= inner.Right) clipped |= Sides.Right;
        if (r.Y <= inner.Y) clipped |= Sides.Top;
        if (r.Bottom >= inner.Bottom) clipped |= Sides.Bottom;

        // Lettering analysis. Neutral bright pixels are the item name; saturated bright pixels are secondary
        // lines (red "cannot buy", coloured values). Both count as ink for padding/centring/fit; only neutral
        // ink proves a name is there. A drawn mouse pointer just below-right of the tip is ignored.
        var cursorZone = new PixelRect(cx - 2, cy - 2, p.CursorZoneW + 2, p.CursorZoneH + 2);
        int tMinX = int.MaxValue, tMinY = int.MaxValue, tMaxX = -1, tMaxY = -1, tCount = 0;
        var rowNeutral = new int[r.Height];
        var rowColor = new int[r.Height];
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
            {
                if (cursorZone.Contains(x, y)) continue;
                var (R, G, B) = img.Get(x, y);
                int mn = Math.Min(R, Math.Min(G, B)), mx = Math.Max(R, Math.Max(G, B));
                bool neutral = mn >= p.TextBrightMin && mx - mn <= p.TextMaxSpread;
                bool color = !neutral && mx >= p.ColorTextBrightMin && mx - mn > p.TextMaxSpread;
                if (!neutral && !color) continue;
                if (neutral) { tCount++; rowNeutral[y - r.Y]++; } else rowColor[y - r.Y]++;
                if (x < tMinX) tMinX = x; if (x > tMaxX) tMaxX = x;
                if (y < tMinY) tMinY = y; if (y > tMaxY) tMaxY = y;
            }
        if (tCount < p.MinTextPixels) return Reject("empty rectangle (no bright lettering)", clipped);
        var tb = new PixelRect(tMinX, tMinY, tMaxX - tMinX + 1, tMaxY - tMinY + 1);
        var bands = Bands(rowNeutral, rowColor, p.LineGap);
        int lines = bands.Count;
        // Title = leading bands that are mostly neutral; stop at the first coloured band.
        int titleBands = 0;
        while (titleBands < bands.Count && bands[titleBands].Neutral >= bands[titleBands].Color) titleBands++;
        if (titleBands == 0) return Reject("first line is not neutral lettering", clipped, tb, lines);
        var titlePanel = r;
        PixelRect? secondary = null;
        if (titleBands < bands.Count)
        {
            int endRow = bands[titleBands - 1].End;                // exclusive, relative to r.Y
            int nextRow = bands[titleBands].Start;
            int cut = Math.Min(r.Height, endRow + Math.Max(p.MinVerticalPad, (nextRow - endRow) / 2));
            titlePanel = new PixelRect(r.X, r.Y, r.Width, Math.Max(1, cut));
            if (r.Height - cut > 2) secondary = new PixelRect(r.X, r.Y + cut, r.Width, r.Height - cut);
        }

        int padTop = tb.Y - r.Y, padBottom = r.Bottom - tb.Bottom, padLeft = tb.X - r.X, padRight = r.Right - tb.Right;
        if ((clipped & (Sides.Top | Sides.Bottom)) != 0 && (padTop < p.MinClipPadding || padBottom < p.MinClipPadding))
            return Reject("edge: lettering cut at top/bottom of capture", clipped, tb, lines);
        if (padTop < p.MinVerticalPad || padBottom < p.MinVerticalPad)
            return Reject("text touches top or bottom edge", clipped, tb, lines);
        double vr = (double)Math.Max(padTop, padBottom) / Math.Max(1, Math.Min(padTop, padBottom));
        if (vr > 4.0) return Reject("text not vertically centred", clipped, tb, lines);
        if ((clipped & Sides.Left) != 0 && padLeft < p.MinClipPadding) return Reject("edge: lettering cut at left of capture", clipped, tb, lines);
        if ((clipped & Sides.Right) != 0 && padRight < p.MinClipPadding) return Reject("edge: lettering cut at right of capture", clipped, tb, lines);
        if ((clipped & Sides.Left) == 0 && padLeft < 1) return Reject("text touches left edge", clipped, tb, lines);
        if ((clipped & Sides.Right) == 0 && padRight < 1) return Reject("text touches right edge", clipped, tb, lines);
        if ((double)tb.Width / r.Width < p.MinTextWidthFraction)
            return Reject("scattered text, not a fitted title", clipped, tb, lines);

        // Thin gray frame on each non-clipped side.
        int framed = 0;
        foreach (var side in new[] { Sides.Left, Sides.Top, Sides.Right, Sides.Bottom })
        {
            if ((clipped & side) != 0) continue;
            double f = BorderFraction(img, r, side, p, cursorZone);
            if (f < p.MinBorderFraction) return Reject($"no continuous gray border on {side} ({f:P0})", clipped, tb, lines);
            framed++;
        }

        if (clipped != Sides.None)
        {
            if (!wideRetry) return Reject("edge: touches capture edge, needs wider capture", clipped, tb, lines);
            if (framed < 2) return Reject("edge: too few convincing edges", clipped, tb, lines);
        }

        // Prefer a complete framed tooltip near the pointer over one at a crop edge.
        double dx = cx < r.X ? r.X - cx : cx > r.Right ? cx - r.Right : 0;
        double dy = cy < r.Y ? r.Y - cy : cy > r.Bottom ? cy - r.Bottom : 0;
        double score = (clipped == Sides.None ? 1000 : 400) + framed * 10 - Math.Sqrt(dx * dx + dy * dy) + Math.Min(50, tCount / 50.0);
        return new TooltipCandidate(r, tb, lines, clipped, true, clipped == Sides.None ? "framed" : "edge-aligned", score)
        {
            TitlePanel = titlePanel, TitleLines = titleBands, SecondaryPanel = secondary,
        };
    }

    private readonly record struct Band(int Start, int End, int Neutral, int Color);

    /// <summary>Text lines (row runs separated by more than <paramref name="gap"/> empty rows) with their ink kinds.</summary>
    private static List<Band> Bands(int[] neutral, int[] color, int gap)
    {
        var list = new List<Band>();
        int start = -1, last = -1, n = 0, c = 0;
        for (int y = 0; y < neutral.Length; y++)
        {
            if (neutral[y] + color[y] == 0) continue;
            if (start >= 0 && y - last - 1 > gap) { list.Add(new Band(start, last + 1, n, c)); start = -1; }
            if (start < 0) { start = y; n = 0; c = 0; }
            n += neutral[y]; c += color[y]; last = y;
        }
        if (start >= 0) list.Add(new Band(start, last + 1, n, c));
        return list;
    }

    private static bool IsBorderGray(RgbImage img, int x, int y, DetectionParams p)
    {
        if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) return false;
        var (R, G, B) = img.Get(x, y);
        int mn = Math.Min(R, Math.Min(G, B)), mx = Math.Max(R, Math.Max(G, B));
        return mx >= p.BorderGrayMin && mx <= p.BorderGrayMax && mx - mn <= p.BorderMaxSpread;
    }

    /// <summary>Fraction of positions along a side with a gray pixel within the tolerance band just outside (or on) the edge.</summary>
    private static double BorderFraction(RgbImage img, PixelRect r, Sides side, DetectionParams p, PixelRect skip)
    {
        int hits = 0, total = 0, t = p.BorderTolerance;
        if (side is Sides.Top or Sides.Bottom)
        {
            int edge = side == Sides.Top ? r.Y - 1 : r.Bottom;
            int dir = side == Sides.Top ? -1 : 1;
            for (int x = r.X; x < r.Right; x++)
            {
                if (skip.Contains(x, edge)) continue;
                total++;
                for (int k = 0; k < t; k++) if (IsBorderGray(img, x, edge + dir * k, p)) { hits++; break; }
            }
        }
        else
        {
            int edge = side == Sides.Left ? r.X - 1 : r.Right;
            int dir = side == Sides.Left ? -1 : 1;
            for (int y = r.Y; y < r.Bottom; y++)
            {
                if (skip.Contains(edge, y)) continue;
                total++;
                for (int k = 0; k < t; k++) if (IsBorderGray(img, edge + dir * k, y, p)) { hits++; break; }
            }
        }
        return total == 0 ? 0 : (double)hits / total;
    }
}
