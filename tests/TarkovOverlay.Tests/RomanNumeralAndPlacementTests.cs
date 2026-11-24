using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Geometry;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace TarkovOverlay.Tests;

/// <summary>
/// 1.1.3 report: "Peltor ComTac II headset (OD Green)" near the top of the stash. Roman-numeral OCR repairs and card
/// placement near the top edge of a 1920x1080 screen (no image needed).
/// </summary>
public class RomanNumeralAndPlacementTests
{
    private readonly ITestOutputHelper _out;
    public RomanNumeralAndPlacementTests(ITestOutputHelper o) => _out = o;

    static readonly MonitorSnapshot Screen = new(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0);
    const string ComTacII = "5645bcc04bdc2d363b8b4572";

    [Theory]
    [InlineData("Peltor ComTac IT headset (OD Green)", ComTacII)]
    [InlineData("Peltor ComTac Il headset (OD Green)", ComTacII)]
    [InlineData("Peltor ComTac ll headset (OD Green)", ComTacII)]
    [InlineData("Peltor ComTac 11 headset (OD Green)", ComTacII)]
    [InlineData("Peltor ComTac lI headset (OD Green)", ComTacII)]
    [InlineData("Peltor ComTac || headset (OD Green)", ComTacII)]
    [InlineData("Peltor ComTac Vl headset (Black)", "69c1632ca078dbb51e0e31b5")]
    public void RomanNumeral_Misreads_AreRepaired_ToTheExactName(string text, string id)
    {
        var m = LiveCatalog.RegularContext.Items.Match(text);
        Assert.Equal(MatchKind.Verified, m.Kind);
        Assert.Equal(id, m.Single!.Id);
        Assert.StartsWith("roman numeral", m.Repair);
    }

    [Theory]
    [InlineData("Peltor ComTac lV headset (OD Green)")]   // "IV (OD Green)" doesn't exist: never invented
    [InlineData("Peltor ComTac IIl headset (OD Green)")]  // not a 2-4 char numeral repair -> suggestion only
    public void RomanNumeral_Repair_NeverInventsAName(string text) =>
        Assert.NotEqual(MatchKind.Verified, LiveCatalog.RegularContext.Items.Match(text).Kind);

    [Fact]
    public void RomanNumeral_Variants_OnlyTouchShortNumeralTokens()
    {
        var v = OcrRepairs.RomanNumeralVariants("peltor comtac it headset (od green)").Select(x => x.Text).ToList();
        Assert.Contains("peltor comtac ii headset (od green)", v);
        Assert.Empty(OcrRepairs.RomanNumeralVariants("bottle of water (0.6l)"));
        Assert.Empty(OcrRepairs.RomanNumeralVariants("tt pistol 7.62x25 tt"));   // "tt" has no i/v/x: not a numeral
    }

    // ---------------- card placement near the top of the screen ----------------
    [Theory]
    [InlineData(1445, 311, 320, 420)]
    [InlineData(1445, 40, 320, 420)]   // pointer at the very top
    [InlineData(1445, 5, 320, 900)]    // tall expanded card
    [InlineData(1900, 8, 320, 420)]    // top-right corner
    [InlineData(10, 3, 320, 420)]      // top-left corner
    [InlineData(1445, 1030, 320, 420)] // bottom: flips above
    public void Card_NearTheTop_StaysOnScreen_AndNeverCoversThePointer(int cx, int cy, double w, double h)
    {
        var p = CardPlacement.Place(cx, cy, w, h, Screen);
        _out.WriteLine($"{cx},{cy} -> {p.Bounds} fit {p.FitScale:0.##} right {p.RightSide} below {p.Below}");
        var work = Screen.WorkArea;
        Assert.True(p.Bounds.X >= work.X && p.Bounds.Y >= work.Y && p.Bounds.Right <= work.Right && p.Bounds.Bottom <= work.Bottom, "card inside the work area");
        Assert.True(p.Bounds.Width > 0 && p.Bounds.Height > 0);
        Assert.False(p.Bounds.Contains(cx, cy), "the card must not sit under the pointer");
        // a click-through card beside the pointer never trips its own move check
        Assert.Equal(DismissReason.None, DismissPolicy.Evaluate((cx, cy), (cx, cy), TimeSpan.FromMilliseconds(400), true, 6, DismissPolicy.DefaultTimeout));
    }

    [Fact]
    public void Card_ForTheComTacHover_OpensBesideThePointer_BelowTheTooltipRow()
    {
        var p = CardPlacement.Place(1445, 311, 320, 420, Screen);
        Assert.True(p.RightSide);
        Assert.True(p.Below);
        Assert.True(p.Bounds.X >= 1445 + 18);
    }
}
