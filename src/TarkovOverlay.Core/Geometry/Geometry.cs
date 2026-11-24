using TarkovOverlay.Core.Detection;

namespace TarkovOverlay.Core.Geometry;

/// <summary>Monitor info in physical screen pixels (may have negative coordinates) plus its DPI scale (1.0 = 96 DPI).</summary>
public readonly record struct MonitorSnapshot(PixelRect Bounds, PixelRect WorkArea, double DpiScale);

/// <summary>Capture rectangles defined in DIPs at 100% and converted to physical pixels for the cursor's monitor.</summary>
public static class CaptureGeometry
{
    public const double FirstWidthDip = 670, FirstHeightDip = 360, FirstLeftDip = 245, FirstAboveDip = 125;
    public const double WideWidthDip = 1060, WideHeightDip = 360;

    public static PixelRect First(int cursorX, int cursorY, MonitorSnapshot m) =>
        Build(cursorX - Px(FirstLeftDip, m), cursorY - Px(FirstAboveDip, m), Px(FirstWidthDip, m), Px(FirstHeightDip, m), m);

    /// <summary>Wider retry crop, centred horizontally on the cursor (long names / tooltips opening left).</summary>
    public static PixelRect Wide(int cursorX, int cursorY, MonitorSnapshot m)
    {
        int w = Px(WideWidthDip, m);
        return Build(cursorX - w / 2, cursorY - Px(FirstAboveDip, m), w, Px(WideHeightDip, m), m);
    }

    public static int Px(double dip, MonitorSnapshot m) => (int)Math.Round(dip * m.DpiScale);

    private static PixelRect Build(int x, int y, int w, int h, MonitorSnapshot m) => new PixelRect(x, y, w, h).Intersect(m.Bounds);
}

public readonly record struct PlacementResult(PixelRect Bounds, double FitScale, bool RightSide, bool Below);

/// <summary>
/// Places the cursor card beside the original cursor with a small gap, preferring an available side and
/// below/above as space permits, then clamps to the monitor work area. All math is in physical pixels.
/// </summary>
public static class CardPlacement
{
    public const double GapDip = 18;
    public const double MarginDip = 4;

    /// <param name="cardWidthDip">Card size in DIPs (already including the user's 1×/2×/3× scale).</param>
    public static PlacementResult Place(int cursorX, int cursorY, double cardWidthDip, double cardHeightDip, MonitorSnapshot m)
    {
        var work = m.WorkArea.IsEmpty ? m.Bounds : m.WorkArea;
        int margin = (int)Math.Round(MarginDip * m.DpiScale);
        int gap = (int)Math.Round(GapDip * m.DpiScale);
        double availW = Math.Max(1, work.Width - 2 * margin), availH = Math.Max(1, work.Height - 2 * margin);
        double wPx = cardWidthDip * m.DpiScale, hPx = cardHeightDip * m.DpiScale;

        // Fit oversized cards to the work area (scale content down uniformly).
        double fit = Math.Min(1.0, Math.Min(availW / wPx, availH / hPx));
        int w = (int)Math.Floor(wPx * fit), h = (int)Math.Floor(hPx * fit);

        int spaceRight = work.Right - margin - (cursorX + gap);
        int spaceLeft = (cursorX - gap) - (work.X + margin);
        bool right = spaceRight >= w || (spaceLeft < w && spaceRight >= spaceLeft);
        int x = right ? cursorX + gap : cursorX - gap - w;

        // Vertical: start a little above the pointer and extend downward; flip above when it does not fit.
        int spaceBelow = work.Bottom - margin - (cursorY - gap);
        int spaceAbove = (cursorY + gap) - (work.Y + margin);
        bool below = spaceBelow >= h || spaceBelow >= spaceAbove;
        int y = below ? cursorY - gap : cursorY + gap - h;

        x = Math.Clamp(x, work.X + margin, Math.Max(work.X + margin, work.Right - margin - w));
        y = Math.Clamp(y, work.Y + margin, Math.Max(work.Y + margin, work.Bottom - margin - h));
        return new PlacementResult(new PixelRect(x, y, w, h), fit, right, below);
    }

    /// <summary>Selects the monitor containing a physical point; falls back to the nearest.</summary>
    public static MonitorSnapshot MonitorFor(int x, int y, IReadOnlyList<MonitorSnapshot> monitors)
    {
        foreach (var m in monitors) if (m.Bounds.Contains(x, y)) return m;
        return monitors.OrderBy(m => Dist(m.Bounds, x, y)).First();
    }

    private static double Dist(PixelRect r, int x, int y)
    {
        double dx = x < r.X ? r.X - x : x >= r.Right ? x - r.Right + 1 : 0;
        double dy = y < r.Y ? r.Y - y : y >= r.Bottom ? y - r.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }

    /// <summary>Cursor movement test used by the 30 ms dismissal timer.</summary>
    public static bool HasMoved(int x0, int y0, int x1, int y1, int tolerancePx = 2) =>
        Math.Abs(x1 - x0) > tolerancePx || Math.Abs(y1 - y0) > tolerancePx;
}

public enum DismissReason { None, Moved, Timeout }

/// <summary>
/// Pure dismissal rule for the cursor card. Movement is measured in physical pixels from where the cursor was
/// when the card appeared, with a DPI-scaled tolerance and a short grace period so hand jitter or the first
/// render frame cannot hide the card instantly.
/// </summary>
public static class DismissPolicy
{
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(7);

    public static int TolerancePx(int tolerancePxAt100, double dpiScale) =>
        Math.Max(0, (int)Math.Round(tolerancePxAt100 * Math.Max(1.0, dpiScale)));

    /// <summary>
    /// How far the pointer may drift while a hover lookup is still capturing/reading before its result is thrown
    /// away. 1.1.3 used a fixed 2 px here (the card itself allows the user's tolerance, default 6 px), so ordinary
    /// hand jitter during a slow, multi-pass read (e.g. a misread name that falls through to Tesseract) silently
    /// discarded the card. Now it is the same tolerance the card uses, never below 2 px.
    /// </summary>
    public static int LookupTolerancePx(int tolerancePxAt100, double dpiScale) => Math.Max(2, TolerancePx(tolerancePxAt100, dpiScale));

    public static DismissReason Evaluate((int X, int Y) shownAt, (int X, int Y) now, TimeSpan elapsed, bool dismissOnMove,
        int tolerancePx, TimeSpan timeout)
    {
        if (elapsed >= timeout) return DismissReason.Timeout;
        if (!dismissOnMove || elapsed < Grace) return DismissReason.None;
        return CardPlacement.HasMoved(shownAt.X, shownAt.Y, now.X, now.Y, tolerancePx) ? DismissReason.Moved : DismissReason.None;
    }
}
