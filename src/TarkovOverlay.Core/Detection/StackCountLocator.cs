namespace TarkovOverlay.Core.Detection;

public enum StackLocateStatus { Found, NoGrid, HiddenByTooltip, NoDigits }

/// <summary>Where the hovered cell is and the cleaned-up image of its bottom-right stack count.</summary>
public sealed record StackLocateResult(StackLocateStatus Status, PixelRect? Cell, int Pitch, PixelRect? DigitBounds, RgbImage? Digits, string Note);

/// <summary>
/// Finds the inventory cell under the cursor and isolates the small white stack-count digits in its
/// bottom-right corner. Pure pixel code (testable on any OS). It never reads the item name and is only used
/// after the item is already identified.
///
/// Cell: Tarkov's grid is periodic, so the pitch and phase are found by a comb search over edge energy
/// (vertical edges for X, horizontal edges for Y) in a band around the cursor, with the tooltip masked out.
/// Digits: near-white, low-saturation pixels in the bottom-right of the cell, grouped into a right-aligned
/// run of glyph-sized components, re-drawn as clean white-on-black for OCR.
/// </summary>
public static class StackCountLocator
{
    /// <summary>Tarkov's 1×1 cell is about 63-64 px at 1080p and scales with the game resolution height.</summary>
    public static int NominalPitch(int screenHeight) => Math.Max(24, (int)Math.Round(63.5 * screenHeight / 1080.0));

    public static StackLocateResult Locate(RgbImage img, int cursorX, int cursorY, PixelRect? tooltip, int nominalPitch)
    {
        var mask = tooltip?.Inflate(Math.Max(3, nominalPitch / 16));
        int lo = Math.Max(16, (int)Math.Floor(nominalPitch * 0.8)), hi = (int)Math.Ceiling(nominalPitch * 1.25);

        var xs = Comb(img, cursorX, cursorY, mask, lo, hi, vertical: true);
        var ys = Comb(img, cursorX, cursorY, mask, lo, hi, vertical: false);
        if (xs is null || ys is null)
            return new(StackLocateStatus.NoGrid, null, 0, null, null, "inventory grid not found");
        int p = (int)Math.Round((xs.Value.Pitch + ys.Value.Pitch) / 2.0);
        if (Math.Abs(xs.Value.Pitch - ys.Value.Pitch) > Math.Max(2, p / 20))
            return new(StackLocateStatus.NoGrid, null, 0, null, null, "grid pitch inconsistent");

        int cx0 = CellStart(cursorX, xs.Value.Phase, xs.Value.Pitch), cy0 = CellStart(cursorY, ys.Value.Phase, ys.Value.Pitch);
        var cell = new PixelRect(cx0, cy0, xs.Value.Pitch, ys.Value.Pitch);

        // Bottom-right search area (inset to skip the cell frame).
        int inset = Math.Max(2, p / 32);
        var area = new PixelRect(cell.X + (int)(p * 0.30), cell.Y + (int)(p * 0.58), (int)(p * 0.70) - inset, (int)(p * 0.42) - inset)
            .Intersect(new PixelRect(0, 0, img.Width, img.Height));
        if (area.IsEmpty) return new(StackLocateStatus.NoDigits, cell, p, null, null, "cell outside capture");
        // Where a count would sit; used when nothing readable is found next to the tooltip.
        var corner = new PixelRect(cell.X + (int)(p * 0.55), cell.Y + (int)(p * 0.70), (int)(p * 0.45), (int)(p * 0.30));
        bool cornerCovered = mask is { } m0 && !m0.Intersect(corner).IsEmpty;

        // Bright, neutral pixels (never inside the tooltip, which has its own white lettering).
        var bright = new bool[area.Width * area.Height];
        for (int y = 0; y < area.Height; y++)
            for (int x = 0; x < area.Width; x++)
            {
                if (mask is { } mk && mk.Contains(area.X + x, area.Y + y)) continue;
                var (r, g, b) = img.Get(area.X + x, area.Y + y);
                int mn = Math.Min(r, Math.Min(g, b)), mx = Math.Max(r, Math.Max(g, b));
                bright[y * area.Width + x] = mn >= 165 && mx - mn <= 45;
            }
        var comps = Components(bright, area.Width, area.Height);
        double minH = p * 0.09, maxH = p * 0.30, maxW = p * 0.30;
        var glyphs = comps.Where(c => c.H >= minH && c.H <= maxH && c.W <= maxW && c.Count >= 4).ToList();
        StackLocateResult Hidden() => new(StackLocateStatus.HiddenByTooltip, cell, p, null, null, "count hidden by the name tooltip");
        if (glyphs.Count == 0) return cornerCovered ? Hidden() : new(StackLocateStatus.NoDigits, cell, p, null, null, "no count digits in the cell corner");

        // Right-aligned run: start from the rightmost glyph, extend left along the same baseline.
        var right = glyphs.OrderByDescending(c => c.X + c.W).First();
        if (area.X + right.X + right.W < cell.Right - p * 0.30)
            return cornerCovered ? Hidden() : new(StackLocateStatus.NoDigits, cell, p, null, null, "no right-aligned count");
        var run = new List<Comp> { right };
        int left = right.X;
        foreach (var c in glyphs.Where(c => c != right).OrderByDescending(c => c.X + c.W))
        {
            int overlap = Math.Min(c.Y + c.H, right.Y + right.H) - Math.Max(c.Y, right.Y);
            bool sameLine = overlap >= 0.6 * Math.Min(c.H, right.H) && Math.Abs(c.H - right.H) <= Math.Max(2, right.H * 0.35);
            if (sameLine && left - (c.X + c.W) <= Math.Max(3, p * 0.12)) { run.Add(c); left = Math.Min(left, c.X); }
        }
        int bx = run.Min(c => c.X), by = run.Min(c => c.Y), bx2 = run.Max(c => c.X + c.W), by2 = run.Max(c => c.Y + c.H);
        var bounds = new PixelRect(area.X + bx, area.Y + by, bx2 - bx, by2 - by);
        // The tooltip may hide part of the number (a leading digit to the left, or the rest of it to the right,
        // e.g. the trader screen's tooltip opening at the pointer tip). Any overlap with the count's line, from
        // one glyph-gap left of it to the cell's right edge, means the visible digits may be incomplete.
        if (mask is { } mm)
        {
            int gap = (int)Math.Ceiling(Math.Max(3, p * 0.12));
            var line = new PixelRect(bounds.X - gap, bounds.Y - 2, cell.Right - (bounds.X - gap), bounds.Height + 4);
            if (!mm.Intersect(line).IsEmpty) return Hidden();
        }

        // Clean rendering: white digits on near-black with padding (both OCR engines expect light-on-dark).
        int pad = Math.Max(6, bounds.Height / 2);
        var outImg = new RgbImage(bounds.Width + 2 * pad, bounds.Height + 2 * pad);
        outImg.Fill(new PixelRect(0, 0, outImg.Width, outImg.Height), 10, 10, 10);
        var keep = new HashSet<int>(run.SelectMany(c => c.Pixels));
        foreach (int idx in keep)
        {
            int x = idx % area.Width, y = idx / area.Width;
            outImg.Set(x - bx + pad, y - by + pad, 240, 240, 240);
        }
        return new(StackLocateStatus.Found, cell, p, bounds, outImg, $"{run.Count} glyph(s)");
    }

    private static int CellStart(int c, int phase, int pitch)
    {
        int k = (int)Math.Floor((c - phase) / (double)pitch);
        return phase + k * pitch;
    }

    /// <summary>Comb search for the grid period/phase along one axis. Returns null when no clear periodic edges exist.</summary>
    private static (int Pitch, int Phase, double Ratio)? Comb(RgbImage img, int cx, int cy, PixelRect? mask, int lo, int hi, bool vertical)
    {
        int span = hi * 3;
        int a0 = Math.Max(2, (vertical ? cx : cy) - span), a1 = Math.Min((vertical ? img.Width : img.Height) - 2, (vertical ? cx : cy) + span);
        int b0 = Math.Max(0, (vertical ? cy : cx) - hi * 2), b1 = Math.Min(vertical ? img.Height : img.Width, (vertical ? cy : cx) + hi * 2);
        int n = a1 - a0;
        if (n < hi * 2 || b1 - b0 < hi) return null;
        var e = new double[n];
        for (int i = 0; i < n; i++)
        {
            int a = a0 + i;
            double sum = 0; int cnt = 0;
            for (int b = b0; b < b1; b++)
            {
                // Thin-line response: the grid line differs from the pixels two steps away on BOTH sides. Step
                // edges of blocks (trader price tags, icons, check marks) respond on one side only, so they score ~0
                // and can't pull the grid phase off (1-2 px lines at any scale).
                int x = vertical ? a : b, y = vertical ? b : a;
                int xm = vertical ? x - 2 : x, ym = vertical ? y : y - 2, xp = vertical ? x + 2 : x, yp = vertical ? y : y + 2;
                if (mask is { } m && (m.Contains(x, y) || m.Contains(xm, ym) || m.Contains(xp, yp))) continue;
                var (r1, g1, bb1) = img.Get(x, y);
                var (r0, g0, bb0) = img.Get(xm, ym);
                var (r2, g2, bb2) = img.Get(xp, yp);
                int before = Math.Abs(r1 - r0) + Math.Abs(g1 - g0) + Math.Abs(bb1 - bb0);
                int after = Math.Abs(r1 - r2) + Math.Abs(g1 - g2) + Math.Abs(bb1 - bb2);
                // Count rows where the line is visible rather than summing strength: grid lines run through the
                // whole band, while thin text strokes (short names, counts, tags) cover only a few rows.
                if (Math.Min(before, after) >= 24) sum += 1;
                cnt++;
            }
            e[i] = cnt > 0 ? sum / cnt : 0;
        }
        double mean = e.Average();
        if (mean <= 0.01) return null;
        (int Pitch, int Phase, double Score) best = (0, 0, 0);
        for (int p = lo; p <= hi; p++)
            for (int ph = 0; ph < p; ph++)
            {
                double s = 0, raw = 0; int k = 0;
                for (int i = ph; i < n; i += p)
                {
                    double v = e[i];
                    if (i > 0) v = Math.Max(v, e[i - 1]);
                    if (i + 1 < n) v = Math.Max(v, e[i + 1]);
                    s += v; raw += e[i]; k++;
                }
                if (k < 2) continue;
                s = (s + raw * 0.01) / k; // ±1 px tolerance, ties go to the phase that sits on the line itself
                if (s > best.Score + 1e-9) best = (p, ph, s);
            }
        if (best.Pitch == 0) return null;
        // The search range [0.8, 1.25] × nominal never contains a multiple of the true pitch.
        double ratio = best.Score / mean;
        if (ratio < 1.8) return null;
        return (best.Pitch, (best.Phase + a0) % best.Pitch, ratio);
    }

    private sealed record Comp(int X, int Y, int W, int H, int Count, List<int> Pixels);

    private static List<Comp> Components(bool[] on, int w, int h)
    {
        var seen = new bool[on.Length];
        var list = new List<Comp>();
        var stack = new Stack<int>();
        for (int i = 0; i < on.Length; i++)
        {
            if (!on[i] || seen[i]) continue;
            var px = new List<int>();
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int j = stack.Pop(); px.Add(j);
                int x = j % w, y = j / w;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int k = ny * w + nx;
                        if (on[k] && !seen[k]) { seen[k] = true; stack.Push(k); }
                    }
            }
            list.Add(new Comp(x0, y0, x1 - x0 + 1, y1 - y0 + 1, px.Count, px));
        }
        return list;
    }
}
