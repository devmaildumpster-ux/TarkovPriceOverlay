using TarkovOverlay.Core.Detection;
using TarkovOverlay.Tests.Fixtures;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>Section 10A capture cases. FIXTURE KIND: synthetic inventory-like pixel fixtures (SyntheticScene).</summary>
public class DetectionTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void OneLineTitleNearCursor_IsFound(double scale)
    {
        int W = (int)(670 * scale), H = (int)(360 * scale);
        var scene = new SyntheticScene(W, H, scale);
        int cx = (int)(245 * scale), cy = (int)(125 * scale);
        var rect = scene.DrawTooltip(cx + (int)(12 * scale), cy + (int)(18 * scale), "9X19MM FMJ M882");
        var r = TooltipDetector.Detect(scene.Image, cx, cy, scale, wideRetry: false);
        Assert.Equal(DetectionStatus.Found, r.Status);
        Assert.Equal(rect, r.Best!.Interior);
        Assert.Equal(1, r.Best.Lines);
        Assert.Equal(Sides.None, r.Best.ClippedSides);
    }

    [Fact]
    public void TwoLineWrappedTitle_IsFoundWithBothLines()
    {
        var scene = new SyntheticScene(670, 360);
        var rect = scene.DrawTooltip(260, 140, "AR-15 5.56X45 SUREFIRE MAG5-60 STANAG", "60-ROUND MAGAZINE");
        var r = TooltipDetector.Detect(scene.Image, 245, 125, 1.0, false);
        Assert.Equal(DetectionStatus.Found, r.Status);
        Assert.Equal(rect, r.Best!.Interior);
        Assert.Equal(2, r.Best.Lines);
    }

    [Fact]
    public void InventoryWithAbbreviationsAndNumbers_NoTooltip_YieldsNoHover()
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            var scene = new SyntheticScene(670, 360, 1.0, seed);
            var r = TooltipDetector.Detect(scene.Image, 245, 125, 1.0, false);
            Assert.NotEqual(DetectionStatus.Found, r.Status);
            Assert.Equal("Item tooltip not visible", r.Title);
            Assert.Equal("Wait for Tarkov’s item name and retry.", r.Hint);
        }
    }

    [Fact]
    public void PanelWithTitleCutThroughByTopEdge_IsRejected()
    {
        var scene = new SyntheticScene(670, 360);
        scene.DrawTooltip(300, -8, "SIG SAUER TANGO6T 1-6X24 30MM RIFLESCOPE"); // lettering cut by top edge
        var r = TooltipDetector.Detect(scene.Image, 245, 125, 1.0, wideRetry: true);
        Assert.NotEqual(DetectionStatus.Found, r.Status);
    }

    [Fact]
    public void EmptyFramedDarkRectangle_IsRejected()
    {
        var scene = new SyntheticScene(670, 360);
        var rect = new PixelRect(280, 150, 200, 30);
        scene.Image.Fill(rect.Inflate(1), 118, 120, 118);
        scene.Image.Fill(rect, 9, 10, 10);
        var r = TooltipDetector.Detect(scene.Image, 245, 125, 1.0, false);
        Assert.NotEqual(DetectionStatus.Found, r.Status);
        Assert.Contains(r.Considered, c => c.Reason.Contains("empty"));
    }

    [Fact]
    public void TooltipBlockedByFirstCropBoundary_RetriesSuccessfullyInWiderCrop()
    {
        // Full desktop region; the first crop cuts the tooltip at its right edge, the wide crop contains it.
        var desk = new SyntheticScene(1600, 400, 1.0, seed: 3);
        int cursorX = 700, cursorY = 150;
        var tip = desk.DrawTooltip(cursorX + 100, cursorY + 18, "WARTECH TV-109 + TV-106 CHEST RIG (A-TACS FG)");
        Assert.True(tip.Right > cursorX - 245 + 670, "fixture must overflow the first crop");

        var first = new PixelRect(cursorX - 245, 25, 670, 360);
        var firstImg = desk.Image.Crop(first);
        var r1 = TooltipDetector.Detect(firstImg, 245, 125, 1.0, wideRetry: false);
        Assert.NotEqual(DetectionStatus.Found, r1.Status);

        var wide = new PixelRect(cursorX - 530, 25, 1060, 360);
        var r2 = TooltipDetector.Detect(desk.Image.Crop(wide), 530, 125, 1.0, wideRetry: true);
        Assert.Equal(DetectionStatus.Found, r2.Status);
        Assert.Equal(tip.Offset(-wide.X, -wide.Y), r2.Best!.Interior);
    }

    [Fact]
    public void TooltipExtendingLeftOfCursor_FoundInWideCrop()
    {
        var desk = new SyntheticScene(1060, 360, 1.0, seed: 4);
        int cx = 530, cy = 125;
        var tip = desk.DrawTooltip(cx - 420, cy + 18, "MOLOT ARMS VPO-209 .366 TKM CARBINE");
        var r = TooltipDetector.Detect(desk.Image, cx, cy, 1.0, wideRetry: true);
        Assert.Equal(DetectionStatus.Found, r.Status);
        Assert.Equal(tip, r.Best!.Interior);
    }

    [Fact]
    public void TooltipAtRightMonitorEdge_WithPadding_IsUsableOnRetry()
    {
        // Capture clamped to the monitor: the tooltip's frame and part of its interior are off-screen.
        var scene = new SyntheticScene(700, 360, 1.0, seed: 5);
        var tip = scene.DrawTooltip(420, 160, "AK-74 5.45X39 6L31");
        // simulate the monitor edge: crop so the right frame column is outside, leaving 5px of padding
        int cut = tip.Right - 2;
        var img = scene.Image.Crop(new PixelRect(0, 0, cut, 360));
        var first = TooltipDetector.Detect(img, 400, 125, 1.0, wideRetry: false);
        Assert.Equal(DetectionStatus.ClippedNeedsWider, first.Status);
        var retry = TooltipDetector.Detect(img, 400, 125, 1.0, wideRetry: true);
        Assert.Equal(DetectionStatus.Found, retry.Status);
        Assert.Equal(Sides.Right, retry.Best!.ClippedSides);
    }

    [Fact]
    public void TooltipWhoseLetteringIsCutByEdge_IsRejectedEvenOnRetry()
    {
        var scene = new SyntheticScene(700, 360, 1.0, seed: 6);
        var tip = scene.DrawTooltip(420, 160, "AK-74 5.45X39 6L31");
        var img = scene.Image.Crop(new PixelRect(0, 0, tip.Right - 20, 360)); // cuts through the letters
        var retry = TooltipDetector.Detect(img, 400, 125, 1.0, wideRetry: true);
        Assert.NotEqual(DetectionStatus.Found, retry.Status);
    }

    [Fact]
    public void NarrowBlackMonitorMargin_DoesNotMergeWithTooltip()
    {
        var scene = new SyntheticScene(670, 360, 1.0, seed: 8);
        scene.BlackMargin(6, left: true);
        // tooltip pressed against the margin: its left frame is hidden by the margin
        var tip = scene.DrawTooltip(6, 150, "9X39MM PAB-9 GS");
        scene.Image.Fill(new PixelRect(0, 0, 6, 360), 0, 0, 0);
        var retry = TooltipDetector.Detect(scene.Image, 120, 125, 1.0, wideRetry: true);
        Assert.Equal(DetectionStatus.Found, retry.Status);
        Assert.Equal(Sides.Left, retry.Best!.ClippedSides);
        Assert.Equal(tip.Y, retry.Best.Interior.Y);
    }

    [Fact]
    public void PrefersFramedTooltipNearPointerOverEdgeCandidate()
    {
        var scene = new SyntheticScene(1060, 360, 1.0, seed: 9);
        var near = scene.DrawTooltip(560, 150, "RPK-16 5.45X39 95-ROUND DRUM MAGAZINE");
        scene.DrawTooltip(-2, 40, "AK 7.62X39 MAGPUL PMAG");
        var r = TooltipDetector.Detect(scene.Image, 530, 125, 1.0, wideRetry: true);
        Assert.Equal(near, r.Best!.Interior);
    }

    [Fact]
    public void TextCrop_InvertedGrayForTesseract_HasDarkTextOnLight()
    {
        var scene = new SyntheticScene(300, 100, 1.0, inventory: false);
        var tip = scene.DrawTooltip(20, 20, "ABC");
        var panel = scene.Image.Crop(tip);
        var g = panel.ToInvertedGray(out int w, out int h);
        Assert.Equal(panel.Width + 20, w);
        Assert.Equal(255, g[0]);                       // margin is light
        Assert.True(g.Count(b => b < 64) > 20);        // lettering became dark
        Assert.True(g.Count(b => b > 200) > g.Length / 2);
    }
}
