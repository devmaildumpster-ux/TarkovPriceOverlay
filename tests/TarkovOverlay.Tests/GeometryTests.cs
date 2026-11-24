using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Geometry;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>Capture and card placement geometry (pure math; FIXTURE KIND: synthetic monitor layouts).</summary>
public class GeometryTests
{
    static MonitorSnapshot Mon(int x, int y, int w, int h, double dpi, int taskbar = 40) =>
        new(new PixelRect(x, y, w, h), new PixelRect(x, y, w, h - (int)(taskbar * dpi)), dpi);

    [Fact]
    public void FirstCapture_At100Percent_Is670x360_From245Left125Above()
    {
        var m = Mon(0, 0, 1920, 1080, 1.0);
        Assert.Equal(new PixelRect(755, 375, 670, 360), CaptureGeometry.First(1000, 500, m));
    }

    [Theory]
    [InlineData(1.25, 838, 450)]
    [InlineData(1.5, 1005, 540)]
    public void FirstCapture_ScalesWithDpi(double dpi, int w, int h)
    {
        var m = Mon(0, 0, 3840, 2160, dpi);
        var r = CaptureGeometry.First(2000, 1000, m);
        Assert.Equal(w, r.Width);
        Assert.Equal(h, r.Height);
        Assert.Equal(2000 - (int)Math.Round(245 * dpi), r.X);
        Assert.Equal(1000 - (int)Math.Round(125 * dpi), r.Y);
    }

    [Fact]
    public void WideCapture_IsCenteredOnCursor_AndClampedToMonitor()
    {
        var m = Mon(0, 0, 1920, 1080, 1.0);
        Assert.Equal(new PixelRect(470, 375, 1060, 360), CaptureGeometry.Wide(1000, 500, m));
        var nearRight = CaptureGeometry.Wide(1900, 500, m);
        Assert.Equal(1920, nearRight.Right);
        Assert.Equal(1370, nearRight.X);
    }

    [Fact]
    public void Capture_OnNegativeCoordinateMonitor_StaysOnThatMonitor()
    {
        var left = Mon(-1920, -200, 1920, 1080, 1.0);
        var r = CaptureGeometry.First(-100, -150, left);
        Assert.Equal(-345, r.X);
        Assert.Equal(-200, r.Y);              // clamped to the monitor top
        Assert.Equal(0, r.Right);             // clamped to the monitor's right edge (x = 0)
        Assert.True(r.Width < 670);
    }

    [Fact]
    public void MonitorFor_PicksMonitorContainingPointer_MixedDpi()
    {
        var mons = new[] { Mon(0, 0, 2560, 1440, 1.0), Mon(2560, 0, 3840, 2160, 1.5), Mon(-1920, 0, 1920, 1080, 1.25) };
        Assert.Equal(1.5, CardPlacement.MonitorFor(3000, 100, mons).DpiScale);
        Assert.Equal(1.25, CardPlacement.MonitorFor(-5, 500, mons).DpiScale);
        Assert.Equal(1.0, CardPlacement.MonitorFor(10, 10, mons).DpiScale);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Card_PrefersRightOfCursor_WithGap(double dpi)
    {
        var m = Mon(0, 0, (int)(1920 * dpi), (int)(1080 * dpi), dpi);
        var p = CardPlacement.Place(500, 300, 200, 150, m);
        Assert.True(p.RightSide);
        Assert.Equal(500 + (int)Math.Round(CardPlacement.GapDip * dpi), p.Bounds.X);
        Assert.Equal((int)Math.Floor(200 * dpi), p.Bounds.Width);
        Assert.Equal(1.0, p.FitScale);
    }

    [Fact]
    public void Card_NearRightEdge_FlipsLeft_NearBottom_FlipsAbove()
    {
        var m = Mon(0, 0, 1920, 1080, 1.0);
        var p = CardPlacement.Place(1900, 1000, 300, 400, m);
        Assert.False(p.RightSide);
        Assert.True(p.Bounds.Right <= 1900);
        Assert.False(p.Below);
        Assert.True(p.Bounds.Bottom <= m.WorkArea.Bottom);
        Assert.False(p.Bounds.Contains(1900, 1000));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Card_AllCorners_AllSizes_StaysInsideWorkArea(int size)
    {
        foreach (var m in new[] { Mon(0, 0, 1920, 1080, 1.0), Mon(-2560, -400, 2560, 1440, 1.25), Mon(1920, 0, 3840, 2160, 1.5) })
        {
            var b = m.Bounds;
            var corners = new[] { (b.X + 2, b.Y + 2), (b.Right - 3, b.Y + 2), (b.X + 2, b.Bottom - 3), (b.Right - 3, b.Bottom - 3), (b.X + b.Width / 2, b.Y + b.Height / 2) };
            foreach (var (x, y) in corners)
            {
                var p = CardPlacement.Place(x, y, 180 * size, 260 * size, m);
                Assert.True(p.Bounds.X >= m.WorkArea.X && p.Bounds.Y >= m.WorkArea.Y, $"{m} {x},{y}");
                Assert.True(p.Bounds.Right <= m.WorkArea.Right && p.Bounds.Bottom <= m.WorkArea.Bottom, $"{m} {x},{y} {p.Bounds}");
            }
        }
    }

    [Fact]
    public void OversizedCard_At3x_IsFittedToWorkArea()
    {
        var m = Mon(0, 0, 1280, 720, 1.0);
        var p = CardPlacement.Place(600, 300, 180 * 3, 330 * 3, m); // 990 DIP tall on a 680 px work area
        Assert.True(p.FitScale < 1.0);
        Assert.True(p.Bounds.Height <= m.WorkArea.Height);
        Assert.InRange(p.Bounds.Y, m.WorkArea.Y, m.WorkArea.Bottom);
    }

    [Fact]
    public void Card_OnNegativeMonitor_UsesPhysicalCoordinates()
    {
        var m = Mon(-1920, -1080, 1920, 1080, 1.5);
        var p = CardPlacement.Place(-1000, -900, 200, 200, m);
        Assert.True(p.Bounds.X < 0 && p.Bounds.Y < 0);
        Assert.Equal(-1000 + 27, p.Bounds.X);
        Assert.Equal(300, p.Bounds.Width);
    }

    [Fact]
    public void Movement_Tolerance()
    {
        Assert.False(CardPlacement.HasMoved(100, 100, 101, 102));
        Assert.True(CardPlacement.HasMoved(100, 100, 104, 100));
    }
}

public class DismissPolicyTests
{
    private static readonly TimeSpan T = TarkovOverlay.Core.Geometry.DismissPolicy.DefaultTimeout;

    [Fact]
    public void JitterWithinTolerance_AndGracePeriod_DoNotDismiss()
    {
        Assert.Equal(TarkovOverlay.Core.Geometry.DismissReason.None,
            TarkovOverlay.Core.Geometry.DismissPolicy.Evaluate((100, 100), (104, 97), TimeSpan.FromSeconds(1), true, 6, T));
        // large move but still inside the grace period
        Assert.Equal(TarkovOverlay.Core.Geometry.DismissReason.None,
            TarkovOverlay.Core.Geometry.DismissPolicy.Evaluate((100, 100), (300, 100), TimeSpan.FromMilliseconds(100), true, 6, T));
        Assert.Equal(TarkovOverlay.Core.Geometry.DismissReason.Moved,
            TarkovOverlay.Core.Geometry.DismissPolicy.Evaluate((100, 100), (107, 100), TimeSpan.FromSeconds(1), true, 6, T));
    }

    [Fact]
    public void DismissOnMoveOff_OnlyTimesOut_AndToleranceScalesWithDpi()
    {
        Assert.Equal(TarkovOverlay.Core.Geometry.DismissReason.None,
            TarkovOverlay.Core.Geometry.DismissPolicy.Evaluate((0, 0), (500, 500), TimeSpan.FromSeconds(2), false, 6, T));
        Assert.Equal(TarkovOverlay.Core.Geometry.DismissReason.Timeout,
            TarkovOverlay.Core.Geometry.DismissPolicy.Evaluate((0, 0), (0, 0), TimeSpan.FromSeconds(7), false, 6, T));
        Assert.Equal(9, TarkovOverlay.Core.Geometry.DismissPolicy.TolerancePx(6, 1.5));
        Assert.Equal(6, TarkovOverlay.Core.Geometry.DismissPolicy.TolerancePx(6, 1.0));
    }
}
