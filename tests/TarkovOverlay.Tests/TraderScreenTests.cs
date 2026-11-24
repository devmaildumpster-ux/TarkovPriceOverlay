using System.Globalization;
using System.IO.Compression;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Recognition;
using TarkovOverlay.Core.Text;
using TarkovOverlay.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace TarkovOverlay.Tests;

/// <summary>
/// 1.0.3 bug: trader sell screen gave "Item tooltip not visible". FIXTURE KIND: synthetic rendered-font captures
/// (tools/make_trader_fixtures.py): ~#1a1a1a tooltip at the pointer tip with a red "The trader cannot buy this
/// item." or price second line, price tags and check marks in cells, a drawn mouse arrow, 100% and 150%.
/// </summary>
public class TraderScreenTests
{
    private readonly ITestOutputHelper _out;
    public TraderScreenTests(ITestOutputHelper o) => _out = o;

    public sealed record Case(string File, double Scale, int Cx, int Cy, string Id, int NameLines, int Stack, PixelRect Box);

    public static IReadOnlyList<Case> All() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "trader", "manifest.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t'))
            .Select(p => new Case(p[0], double.Parse(p[1], CultureInfo.InvariantCulture), int.Parse(p[2]), int.Parse(p[3]), p[4], int.Parse(p[5]), int.Parse(p[6]),
                new PixelRect(int.Parse(p[7]), int.Parse(p[8]), int.Parse(p[9]), int.Parse(p[10])))).ToList();

    public static IEnumerable<object[]> Tooltips() => All().Where(c => c.Id != "-").Select(c => new object[] { c.File });
    public static IEnumerable<object[]> NoTooltips() => All().Where(c => c.File.StartsWith("no_tooltip") || c.File.Contains("_pre_")).Select(c => new object[] { c.File });
    static Case Get(string f) => All().Single(c => c.File == f);

    internal static RgbImage Load(string file)
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "trader", file)), CompressionMode.Decompress);
        return Ppm.Read(gz);
    }

    [Theory]
    [MemberData(nameof(Tooltips))]
    public void TraderTooltip_AtOrAboveTheTip_IsFound_AndTitlePanelIsNameOnly(string file)
    {
        var c = Get(file); var img = Load(file);
        var det = TooltipDetector.Detect(img, c.Cx, c.Cy, c.Scale, wideRetry: file.Contains("wide"));
        _out.WriteLine($"{file}: {det.Status} {det.Best?.Interior} title {det.Best?.TitlePanel} lines {det.Best?.Lines}/{det.Best?.TitleLines} · " +
                       string.Join(" | ", det.Considered.Select(x => $"{x.Interior} {x.Reason}")));
        Assert.Equal(DetectionStatus.Found, det.Status);
        var b = det.Best!;
        Assert.InRange(Math.Abs(b.Interior.X - c.Box.X) + Math.Abs(b.Interior.Y - c.Box.Y), 0, 2);
        Assert.InRange(Math.Abs(b.Interior.Width - c.Box.Width) + Math.Abs(b.Interior.Height - c.Box.Height), 0, 3);
        Assert.Equal(2, b.Lines);
        if (file.Contains("price") || file.StartsWith("bp_"))
            Assert.Equal(b.Interior, b.TitlePanel); // neutral price line: dropped from the OCR text instead
        else
        {
            Assert.Equal(1, b.TitleLines);   // red line cut off before OCR
            Assert.True(b.TitlePanel.Height < b.Interior.Height * 0.7, $"title panel {b.TitlePanel} should exclude the red line");
            Assert.Equal(b.Interior.Y, b.TitlePanel.Y);
        }
    }

    [Theory]
    [MemberData(nameof(Tooltips))]
    public void AutoReadProbe_SeesTheTraderTooltip_InTheFirstCapture(string file)
    {
        var c = Get(file); var img = Load(file);
        if (file.Contains("wide"))
        {
            // the auto-read probe uses the first (670-wide) capture: a left-opening tooltip is clipped there and
            // must report ClippedNeedsWider (the probe treats that as "tooltip present"; the lookup retries wide).
            int s(double v) => (int)Math.Round(v * c.Scale);
            var first = new PixelRect(c.Cx - s(245), c.Cy - s(125), s(670), s(360));
            img = img.Crop(first);
            var r = TooltipDetector.Detect(img, s(245), s(125), c.Scale, wideRetry: false);
            Assert.NotEqual(DetectionStatus.NotFound, r.Status);
            return;
        }
        Assert.Equal(DetectionStatus.Found, TooltipDetector.Detect(img, c.Cx, c.Cy, c.Scale, wideRetry: false).Status);
    }

    [Theory]
    [MemberData(nameof(NoTooltips))]
    public void PriceTags_CheckMarks_EmptyCells_AreNotATooltip(string file)
    {
        var c = Get(file);
        var r = TooltipDetector.Detect(Load(file), c.Cx, c.Cy, c.Scale, wideRetry: false);
        Assert.Equal(DetectionStatus.NotFound, r.Status);
        Assert.Equal(DetectionStatus.NotFound, TooltipDetector.Detect(Load(file), c.Cx, c.Cy, c.Scale, wideRetry: true).Status);
    }

    [Theory]
    [MemberData(nameof(Tooltips))]
    public async Task TraderTooltip_ResolvesToTheRightItem(string file)
    {
        var c = Get(file); var img = Load(file);
        var det = TooltipDetector.Detect(img, c.Cx, c.Cy, c.Scale, wideRetry: file.Contains("wide"));
        Assert.Equal(DetectionStatus.Found, det.Status);
        var engine = TesseractTests.Engine.Value;
        if (engine == null) { _out.WriteLine("Tesseract unavailable; OCR half skipped."); return; }
        var outcome = await new HoverRecognizer(null, engine).RecognizeAsync(TooltipDetector.CropPanel(img, det.Best!.TitlePanel, c.Cx, c.Cy, c.Scale), LiveCatalog.RegularContext.Items, default);
        var read = outcome.Attempts.Last().Read;
        _out.WriteLine($"{file}: '{read.Text}' raw '{read.RawText}' -> {outcome.Result.Kind} {outcome.Result.Single?.Name}");
        // Never a wrong item. In the 150% helmet fixtures the drawn mouse pointer covers the "H" of "HighCom"
        // (worst case: Windows BitBlt normally does not capture the pointer); there the right item must at least
        // be the possible match. Every other case must verify.
        if (outcome.Result.Kind == MatchKind.Verified) Assert.Equal(c.Id, outcome.Result.Single!.Id);
        else
        {
            Assert.True(file.StartsWith("ulach_") && c.Scale > 1.2, $"{file} must verify");
            Assert.Equal(MatchKind.PossibleStrong, outcome.Result.Kind);
            Assert.Equal(c.Id, outcome.Result.Items[0].Id);
        }
    }

    [Fact]
    public void WholeTooltipText_WithSecondaryLine_StillMatchesOnlyTheName()
    {
        var items = LiveCatalog.RegularContext.Items;
        foreach (var second in new[] { "The trader cannot buy this item.", "₽ 12 450", "P 12450", "12 450 P", "7578" })
        {
            var lines = new[] { "HighCom Striker ULACH IIIA helmet (Black)", second };
            var read = new OcrRead("t", 1, lines, 1, 1, TimeSpan.Zero);
            Assert.Equal("HighCom Striker ULACH IIIA helmet (Black)", read.Text);
            var m = items.Match(read.Text);
            Assert.Equal(MatchKind.Verified, m.Kind);
            Assert.Equal("5b40e1525acfc4771e1c6611", m.Single!.Id);
        }
    }

    [Theory]
    [InlineData("₽ 35 118", 35118L)]
    [InlineData("P35 118", 35118L)]
    [InlineData("₽ 142,317", 142317L)]
    [InlineData("R 98", 98L)]
    [InlineData("£35118", 35118L)]
    [InlineData("(P35 118", 35118L)]    // stray bracket from the ₽ glyph
    [InlineData("35118", 35118L)]
    [InlineData("₽ 35 11", null)]          // badly grouped digits: not a clean price
    [InlineData("The trader cannot buy this item.", null)]
    [InlineData("₽ 35 118 x2", null)]
    public void ParseRublePrice_OnlyCleanAmounts(string line, long? expected) => Assert.Equal(expected, NameNormalizer.ParseRublePrice(line));

    [Theory]
    [InlineData("nightforce_offer_100.ppm.gz")]
    [InlineData("nightforce_offer_150.ppm.gz")]
    public async Task GoldOfferLine_IsCutFromTheName_AndReadAsTheTraderOffer(string file)
    {
        var c = Get(file); var img = Load(file);
        var det = TooltipDetector.Detect(img, c.Cx, c.Cy, c.Scale, wideRetry: false);
        Assert.Equal(DetectionStatus.Found, det.Status);
        Assert.Equal(1, det.Best!.TitleLines);
        Assert.NotNull(det.Best.SecondaryPanel);
        var engine = TesseractTests.Engine.Value;
        if (engine == null) return;
        var (offer, note) = await new TraderOfferReader(engine).ReadAsync(null, img.Crop(det.Best.SecondaryPanel!.Value), default);
        _out.WriteLine($"{file}: offer {offer} · {note}");
        Assert.Equal(35118L, offer);
    }

    [Fact]
    public void Card_ShowsTraderOffer_FirstRow_OnlyWhenRead()
    {
        var ctx = LiveCatalog.RegularContext;
        var item = ctx.Snapshot.Items["5aa66be6e5b5b0214e506e97"];
        var m = new MatchResult(MatchKind.Verified, new[] { item }, item.Name);
        var with = new TarkovOverlay.Core.Presentation.CardBuilder(ctx, null).ForMatch(m, traderOffer: 35118);
        Assert.Equal("This trader offers", with.Rows[0].Label);
        Assert.Contains("35,118", with.Rows[0].Value);
        var without = new TarkovOverlay.Core.Presentation.CardBuilder(ctx, null).ForMatch(m);
        Assert.DoesNotContain(without.Rows, r => r.Label == "This trader offers");
    }

    [Theory]
    [InlineData(new[] { "AR-15 5.56x45 SureFire MAG5-60 STANAG", "60-round magazine" }, 2)]
    [InlineData(new[] { "5.56x45mm M855 ammo pack", "(50 pcs)" }, 2)]
    [InlineData(new[] { "HighCom Striker ULACH IIIA helmet (Black)", "The trader cannot buy this item." }, 1)]
    [InlineData(new[] { "Bottle of water (0.6L)", "₽ 9 000", "anything after" }, 1)]
    [InlineData(new[] { "5.45x39mm BP gs", "P98" }, 1)]
    [InlineData(new[] { "The trader cannot buy this item." }, 1)] // never drops the first line
    public void TitleLines_KeepWrappedNames_DropSecondaryLines(string[] lines, int kept)
    {
        Assert.Equal(kept, NameNormalizer.TitleLines(lines).Count);
    }

    [Fact]
    public async Task StackCount_TraderLayout_ReadsBelowTooltip_HiddenWhenCovered_PreTooltipFrameRecovers()
    {
        foreach (var s in new[] { 100, 150 })
        {
            var top = Get($"bp_top_{s}.ppm.gz"); var mid = Get($"bp_mid_{s}.ppm.gz"); var pre = Get($"bp_mid_pre_{s}.ppm.gz");
            int sh = (int)Math.Round(1080 * top.Scale * 63.0 / 63.5);
            int pitch = StackCountLocator.NominalPitch(sh);
            var lt = StackCountLocator.Locate(Load(top.File), top.Cx, top.Cy, top.Box, pitch);
            var lm = StackCountLocator.Locate(Load(mid.File), mid.Cx, mid.Cy, mid.Box, pitch);
            var lp = StackCountLocator.Locate(Load(pre.File), pre.Cx, pre.Cy, null, pitch);
            _out.WriteLine($"{s}: top {lt.Status} {lt.Note} · mid {lm.Status} {lm.Note} · pre {lp.Status} {lp.Note}");
            // Near the top of the cell the 2-line trader tooltip ends just above the count: found or (if the
            // padded tooltip touches it) hidden, never a partial number.
            Assert.Contains(lt.Status, new[] { StackLocateStatus.Found, StackLocateStatus.HiddenByTooltip });
            Assert.Equal(StackLocateStatus.HiddenByTooltip, lm.Status);
            Assert.Equal(StackLocateStatus.Found, lp.Status);

            var engine = TesseractTests.Engine.Value;
            if (engine == null) continue;
            var reader = new StackCountReader(engine);
            var rt = await reader.ReadAsync(Load(top.File), top.Cx, top.Cy, top.Box, sh, 60, default);
            _out.WriteLine($"{s}: top read {rt.Count} {rt.Note} {string.Join(" | ", rt.Reads)} digits {lt.DigitBounds} cell {lt.Cell}");
            Assert.True(rt.Count is null or 43, $"never a wrong count (got {rt.Count})");
            var rm = await reader.ReadAsync(Load(mid.File), mid.Cx, mid.Cy, mid.Box, sh, 120, default);
            Assert.Null(rm.Count);
            var best = await reader.ReadBestAsync(Load(mid.File), mid.Cx, mid.Cy, mid.Box, Load(pre.File), pre.Cx, pre.Cy, null, sh, 120, default);
            _out.WriteLine($"{s}: best {best.Count} {best.Note}");
            Assert.Equal(120, best.Count);
            Assert.Contains("pre-tooltip", best.Note);
            // no pre frame: falls back to the live (hidden) read, never guesses
            var none = await reader.ReadBestAsync(Load(mid.File), mid.Cx, mid.Cy, mid.Box, null, 0, 0, null, sh, 120, default);
            Assert.Null(none.Count);
        }
    }

    [Fact]
    public void StackCount_PartiallyCoveredNumber_IsHidden_NotATruncatedRead()
    {
        // "120" with the tooltip starting between "1" and "20": the visible "1" must not be accepted.
        var pre = Get("bp_mid_pre_100.ppm.gz");
        var img = Load(pre.File);
        int pitch = StackCountLocator.NominalPitch(1071);
        var clean = StackCountLocator.Locate(img, pre.Cx, pre.Cy, null, pitch);
        Assert.Equal(StackLocateStatus.Found, clean.Status);
        var d = clean.DigitBounds!.Value;
        var cover = new PixelRect(d.X + d.Width / 3, d.Y - 30, 300, 44);
        var r = StackCountLocator.Locate(img, pre.Cx, pre.Cy, cover, pitch);
        Assert.Equal(StackLocateStatus.HiddenByTooltip, r.Status);
    }
}
