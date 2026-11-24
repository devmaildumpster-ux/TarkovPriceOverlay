using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Tests.Fixtures;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>1.2.4: the "AR-10 KAC QDC 7.62x51 Flash Suppressor Kit" possible-match report and its siblings.</summary>
public class Release124Tests
{
    private const string Qdc10 = "AR-10 KAC QDC 7.62x51 Flash Suppressor Kit";
    private const string Qdc15 = "AR-15 KAC QDC 5.56x45 Flash Suppressor Kit";
    private static readonly Lazy<ItemIndex> Idx = new(() => new ItemIndex(LiveCatalog.Pve.Items.Values));

    [Theory]
    [InlineData("AR-10 KAC QDC 7.62x51 Flash Suppressor Kit")]
    [InlineData("AR-10 KAC QDC 7.62x5l Flash Suppressor Kit")]   // l for 1 at the end of a number (was "1 characters off")
    [InlineData("AR-10 KAC QDC 7.62x5I Flash Suppressor Kit")]
    [InlineData("AR-10 KAC ODC 7.62x51 Flash Suppressor Kit")]   // Q read as O
    [InlineData("AR-10 KAC ODC 7.62x5l Flash Suppressor Kit")]   // both (was "2 characters off")
    [InlineData("AR-1O KAC QDC 7.62x51 Flash Suppressor Kit")]
    [InlineData("AR-10 KAC QDC 7.62×51 Flash Suppressor Kit")]
    [InlineData("AR-10 KAC QDC 7.62x51 FIash Suppressor Klt")]
    [InlineData("AR-10 KAC QDC 7.62x51 Flash 5uppressor Kit")]
    [InlineData("AR-10 KAC QDC 7.62x51 Flash Suppres- sor Kit")]
    public void Qdc_variants_verify_as_the_right_kit(string read)
    {
        var m = Idx.Value.Match(read);
        Assert.Equal(MatchKind.Verified, m.Kind);
        Assert.Equal(Qdc10, m.Single!.Name);
    }

    [Fact]
    public void Wrapped_tooltip_lines_in_either_order_verify()
    {
        Assert.Equal(Qdc10, Idx.Value.MatchLines(new[] { "AR-10 KAC QDC 7.62x51 Flash", "Suppressor Kit" }).Single!.Name);
        var swapped = Idx.Value.MatchLines(new[] { "Suppressor Kit", "AR-10 KAC QDC 7.62x51 Flash" });
        Assert.Equal(MatchKind.Verified, swapped.Kind);
        Assert.Equal(Qdc10, swapped.Single!.Name);
        Assert.Contains("lines reordered", swapped.Repair);
    }

    [Fact]
    public void Lost_second_line_is_a_possible_match_with_price_not_a_guess()
    {
        var m = Idx.Value.MatchLines(new[] { "AR-10 KAC QDC 7.62x51 Flash" }, expectedLines: 2);
        Assert.Equal(MatchKind.PossibleStrong, m.Kind);
        Assert.Equal(Qdc10, m.Items[0].Name);
        Assert.Equal("first line of a longer name", m.Repair);
    }

    [Fact]
    public void The_two_kits_never_cross()
    {
        foreach (var read in new[] { "AR-15 KAC QDC 5.56x45 Flash Suppressor Kit", "AR-15 KAC ODC 5.56x45 Flash Suppressor Klt", "AR-l5 KAC QDC 5.56x45 Flash Suppressor Kit" })
            Assert.Equal(Qdc15, Idx.Value.Match(read).Single!.Name);
        // a different number is a different item, never "close enough"
        var m = Idx.Value.Match("AR-10 KAC QDC 7.62x39 Flash Suppressor Kit");
        Assert.NotEqual(MatchKind.Verified, m.Kind);
    }

    [Fact]
    public void Read_that_is_a_shorter_real_name_is_downgraded_when_a_line_is_missing()
    {
        var items = new[]
        {
            new CatalogItem { Id = "a", Name = "ELCAN SpecterDR 1x/4x scope", ShortName = "SpecterDR" },
            new CatalogItem { Id = "b", Name = "ELCAN SpecterDR 1x/4x scope (FDE)", ShortName = "SpecterDR FDE" },
        };
        var idx = new ItemIndex(items);
        Assert.Equal(MatchKind.Verified, idx.MatchLines(new[] { "ELCAN SpecterDR 1x/4x scope" }).Kind);
        Assert.Equal(MatchKind.Verified, idx.MatchLines(new[] { "ELCAN SpecterDR 1x/4x scope" }, expectedLines: 1).Kind);
        var m = idx.MatchLines(new[] { "ELCAN SpecterDR 1x/4x scope" }, expectedLines: 2);
        Assert.Equal(MatchKind.PossibleStrong, m.Kind);
        Assert.Equal("b", Assert.Single(m.Alternatives).Id);
    }

    [Fact]
    public void Fold_keys_only_merge_look_alikes()
    {
        Assert.Equal(ItemIndex.Fold("ar10kacqdc76251"), ItemIndex.Fold("arl0kac0dc7625l"));
        Assert.NotEqual(ItemIndex.Fold("76251"), ItemIndex.Fold("76239"));
    }

    [Fact]
    public void Possible_match_card_shows_price_and_alternatives()
    {
        var ctx = new CatalogContext(LiveCatalog.Pve, () => LiveCatalog.FetchedAt);
        var b = new CardBuilder(ctx, null);
        var m = ctx.Items.Match("AR-10 KAC QOC 7.62x51 Flash Suppressor Kit");
        Assert.True(m.Kind is MatchKind.PossibleStrong or MatchKind.PossibleWeak);
        var card = b.ForMatch(m);
        Assert.Equal("Possible match", card.Heading);
        Assert.Equal(Qdc10, card.Title);
        Assert.NotEmpty(card.Rows);   // 1.2.3 and earlier: no rows for a weak possible match
        Assert.Contains(card.Messages, x => x.Contains("verify", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Synthetic two-line tooltip (tools/make_qdc_fixtures.py) through detector → Tesseract → matcher.</summary>
    [Theory]
    [InlineData("qdc_wrapped_100.ppm.gz", 1.0)]
    [InlineData("qdc_wrapped_150.ppm.gz", 1.5)]
    public async Task Synthetic_wrapped_tooltip_end_to_end(string file, double scale)
    {
        if (TesseractTests.Engine.Value is not { IsAvailable: true } engine) return;
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "qdc", file)), System.IO.Compression.CompressionMode.Decompress);
        var img = TarkovOverlay.Core.Detection.Ppm.Read(gz);
        var det = TarkovOverlay.Core.Detection.TooltipDetector.Detect(img, (int)(245 * scale), (int)(125 * scale), scale, wideRetry: false);
        Assert.Equal(TarkovOverlay.Core.Detection.DetectionStatus.Found, det.Status);
        Assert.Equal(2, det.Best!.TitleLines);
        var o = await new TarkovOverlay.Core.Recognition.HoverRecognizer(null, engine).RecognizeAsync(img.Crop(det.Best.TitlePanel), Idx.Value, default, det.Best.TitleLines);
        Assert.Equal(MatchKind.Verified, o.Result.Kind);
        Assert.Equal(Qdc10, o.Result.Single!.Name);
    }
}
