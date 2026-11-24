using System.Globalization;
using System.IO.Compression;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Geometry;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Recognition;
using TarkovOverlay.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace TarkovOverlay.Tests;

/// <summary>
/// 1.0.6 bug: hovering a 'Chainlet' gave no card. Tarkov's tooltip for a short name is small (~68x40 at 100%) and
/// sat above-right of the pointer. FIXTURE KIND: synthetic rendered-font captures (tools/make_short_fixtures.py),
/// trader/stash grid with price tags, 100% and 150%, Chainlet plus Bolts, Awl, Wrench, Salewa, TP-200.
/// </summary>
public class ShortNameTests
{
    private readonly ITestOutputHelper _out;
    public ShortNameTests(ITestOutputHelper o) => _out = o;

    public static IReadOnlyList<TraderScreenTests.Case> All() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "short", "manifest.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t'))
            .Select(p => new TraderScreenTests.Case(p[0], double.Parse(p[1], CultureInfo.InvariantCulture), int.Parse(p[2]), int.Parse(p[3]), p[4], int.Parse(p[5]), int.Parse(p[6]),
                new PixelRect(int.Parse(p[7]), int.Parse(p[8]), int.Parse(p[9]), int.Parse(p[10])))).ToList();

    public static IEnumerable<object[]> Files() => All().Select(c => new object[] { c.File });
    static TraderScreenTests.Case Get(string f) => All().Single(c => c.File == f);

    static RgbImage Load(string file)
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "short", file)), CompressionMode.Decompress);
        return Ppm.Read(gz);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void SmallTooltip_AboveOrAtTheCursor_IsFound(string file)
    {
        var c = Get(file);
        var det = TooltipDetector.Detect(Load(file), c.Cx, c.Cy, c.Scale, wideRetry: false);
        _out.WriteLine($"{file}: {det.Status} {det.Best?.Interior} title {det.Best?.TitlePanel} · " +
                       string.Join(" | ", det.Considered.Select(x => $"{x.Interior} {x.Reason}")));
        Assert.Equal(DetectionStatus.Found, det.Status);
        var b = det.Best!;
        Assert.InRange(Math.Abs(b.Interior.X - c.Box.X) + Math.Abs(b.Interior.Y - c.Box.Y), 0, 2);
        Assert.InRange(Math.Abs(b.Interior.Width - c.Box.Width) + Math.Abs(b.Interior.Height - c.Box.Height), 0, 3);
        if (!file.Contains("1line"))
        {
            Assert.Equal(1, b.TitleLines);
            Assert.NotNull(b.SecondaryPanel); // gold price line is cut from the name
        }
    }

    [Theory]
    [InlineData("bolts_above_100.ppm.gz")]
    [InlineData("awl_above_150.ppm.gz")]
    [InlineData("bolts_tip_1line_100.ppm.gz")]
    public void Version106Thresholds_MissedThese(string file)
    {
        // 1.0.6 required a 64 px wide interior (at 100%); short names are narrower.
        var c = Get(file);
        var old = TooltipDetector.Detect(Load(file), c.Cx, c.Cy, c.Scale, false, new DetectionParams { MinWidth = 64 });
        Assert.Equal(DetectionStatus.NotFound, old.Status);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void AutoReadProbe_SeesTheSmallTooltip(string file)
    {
        // The auto-read probe captures the same 670x360 region (cursor at 245,125) and only needs "a tooltip is there".
        var c = Get(file);
        Assert.Equal(DetectionStatus.Found, TooltipDetector.Detect(Load(file), c.Cx, c.Cy, c.Scale, wideRetry: false).Status);
        Assert.True(c.Box.Y > 0, "fixture tooltip lies inside the first capture");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task ShortName_ResolvesToTheRightItem(string file)
    {
        var c = Get(file); var img = Load(file);
        var det = TooltipDetector.Detect(img, c.Cx, c.Cy, c.Scale, wideRetry: false);
        Assert.Equal(DetectionStatus.Found, det.Status);
        var engine = TesseractTests.Engine.Value;
        if (engine == null) { _out.WriteLine("Tesseract unavailable; OCR half skipped."); return; }
        var outcome = await new HoverRecognizer(null, engine).RecognizeAsync(TooltipDetector.CropPanel(img, det.Best!.TitlePanel, c.Cx, c.Cy, c.Scale), LiveCatalog.RegularContext.Items, default);
        var read = outcome.Attempts.Last().Read;
        _out.WriteLine($"{file}: '{read.Text}' raw '{read.RawText}' -> {outcome.Result.Kind} {outcome.Result.Single?.Name}");
        Assert.Equal(MatchKind.Verified, outcome.Result.Kind);
        Assert.Equal(c.Id, outcome.Result.Single!.Id);
    }

    [Theory]
    [InlineData("Chainlet", "573474f924597738002c6174")]
    [InlineData("chainlet", "573474f924597738002c6174")]
    [InlineData("Chainlet.", "573474f924597738002c6174")]
    [InlineData("Bolts", "57347c5b245977448d35f6e1")]
    [InlineData("Awl", "62a0a098de7ac8199358053b")]
    [InlineData("Wrench", "590c311186f77424d1667482")]
    [InlineData("TP-200 TNT brick", "60391b0fb847c71012789415")]
    [InlineData("Salewa first aid kit", "544fb45d4bdc2dee738b4568")]
    public void ShortExactNames_Verify(string text, string id)
    {
        var m = LiveCatalog.RegularContext.Items.Match(text);
        Assert.Equal(MatchKind.Verified, m.Kind);
        Assert.Equal(id, m.Single!.Id);
    }

    [Theory]
    [InlineData("8olts", "57347c5b245977448d35f6e1")]   // pointer clipped the B
    [InlineData("8andana", "5b43271c5acfc432ff4dce65")]
    [InlineData("5oap", "5c13cd2486f774072c757944")]
    public void LeadingDigitForCapital_IsRepaired_ToAnExactName(string text, string id)
    {
        var m = LiveCatalog.RegularContext.Items.Match(text);
        Assert.Equal(MatchKind.Verified, m.Kind);
        Assert.Equal(id, m.Single!.Id);
        Assert.StartsWith("leading", m.Repair);
    }

    [Theory]
    [InlineData("8olt")]     // not a complete catalog name after repair -> never verified
    [InlineData("5.45x39")]  // numbers are left alone
    public void LeadingDigitRepair_NeverInventsAMatch(string text) =>
        Assert.NotEqual(MatchKind.Verified, LiveCatalog.RegularContext.Items.Match(text).Kind);

    [Theory]
    [InlineData("Chainlet", "₽ 3 689")]
    [InlineData("Bolts", "P6120")]
    public void NameWithPriceLine_MatchesOnlyTheName(string name, string price)
    {
        var read = new OcrRead("t", 1, new[] { name, price }, 1, 1, TimeSpan.Zero);
        Assert.Equal(name, read.Text);
        Assert.Equal(MatchKind.Verified, LiveCatalog.RegularContext.Items.Match(read.Text).Kind);
    }
}
