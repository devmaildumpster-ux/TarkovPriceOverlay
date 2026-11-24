using System.Globalization;
using System.IO.Compression;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Core.Recognition;
using TarkovOverlay.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace TarkovOverlay.Tests;

/// <summary>
/// 1.3.1 icon short-name tiebreaker: resolve "Possible match" / "Several" cards from the short name
/// painted on the item icon. Matching is string-level; cell isolation is pure pixels; OCR uses the
/// same Tesseract engine as the other fixture tests (skipped if the native runtime is missing).
/// FIXTURE KIND: synthetic rendered-font stash captures (tools/make_icon_name_fixtures.py).
/// </summary>
public class IconShortNameTests
{
    private readonly ITestOutputHelper _out;
    public IconShortNameTests(ITestOutputHelper o) => _out = o;

    private static CatalogItem ByName(string name) => LiveCatalog.Regular.Items.Values.First(i => i.Name == name);
    private static ItemIndex Index => LiveCatalog.RegularContext.Items;

    static MatchResult Match(string text) => Index.Match(text);

    // ---------------- string tiebreaker ----------------
    [Fact]
    public void UniqueIconShortName_PromotesAPossibleMatchToVerified()
    {
        var tip = Match("Chamlet"); // not an exact catalog name; should be a suggestion
        Assert.NotEqual(MatchKind.Verified, tip.Kind);
        var after = IconShortNameTiebreaker.Apply(Index, tip, new[] { "Chainlet" });
        Assert.Equal(MatchKind.Verified, after.Kind);
        Assert.Equal("Chainlet", after.Single!.Name);
        Assert.Contains(IconShortNameTiebreaker.Repair, after.Repair);
    }

    [Fact]
    public void UniqueIconShortName_IdentifiesAnItemWhenTheTooltipReadFailed()
    {
        var after = IconShortNameTiebreaker.Apply(Index, MatchResult.NoMatch(""), new[] { "Awl" });
        Assert.Equal(MatchKind.Verified, after.Kind);
        Assert.Equal("Awl", after.Single!.Name);
    }

    [Fact]
    public void SharedIconShortName_StillLeavesTheAmmoPackPicker()
    {
        var tip = Match("M995");
        Assert.Equal(MatchKind.Several, tip.Kind);
        Assert.True(tip.Items.Count >= 3);
        var after = IconShortNameTiebreaker.Apply(Index, tip, new[] { "M995" });
        Assert.Equal(MatchKind.Several, after.Kind);
        Assert.True(after.Items.Count >= 3);
        Assert.Contains(after.Items, i => i.Name == "5.56x45mm M995");
        Assert.Contains(after.Items, i => i.IsAmmoPack);
    }

    [Fact]
    public void IconShortName_PicksTheMatchingCandidate_AmongSeveral()
    {
        var m855 = ByName("5.56x45mm M855");
        var m995 = ByName("5.56x45mm M995");
        var tip = new MatchResult(MatchKind.Several, new[] { m995, m855, ByName("5.56x45mm M856A1") }, "M9x5", "shared short name");
        var after = IconShortNameTiebreaker.Apply(Index, tip, new[] { "M855" });
        Assert.Equal(MatchKind.Verified, after.Kind);
        Assert.Equal(m855.Id, after.Single!.Id);
    }

    [Fact]
    public void IconShortName_DoesNotOverrideAVerifiedFullName()
    {
        var tip = Match("5.56x45mm M995");
        Assert.Equal(MatchKind.Verified, tip.Kind);
        var after = IconShortNameTiebreaker.Apply(Index, tip, new[] { "M855" });
        Assert.Equal(MatchKind.Verified, after.Kind);
        Assert.Equal("5.56x45mm M995", after.Single!.Name);
    }

    [Fact]
    public void IconShortName_ThatDisagreesWithTheTooltipCandidates_IsIgnored()
    {
        var tip = Match("Salewa"); // unique short name, full name is "Salewa first aid kit"
        Assert.Equal(MatchKind.PossibleStrong, tip.Kind);
        var after = IconShortNameTiebreaker.Apply(Index, tip, new[] { "Splint" });
        Assert.Equal(tip.Kind, after.Kind);
        Assert.Equal("Salewa first aid kit", after.Single!.Name);
    }

    [Fact]
    public void TrailingStackCountOnTheIcon_IsIgnored()
    {
        var after = IconShortNameTiebreaker.Apply(Index, MatchResult.NoMatch(""), new[] { "Salewa 12" });
        Assert.Equal(MatchKind.Verified, after.Kind);
        Assert.Equal("Salewa first aid kit", after.Single!.Name);
    }

    [Fact]
    public void ConfirmingTheUniqueShortName_PromotesExactShortNameToVerified()
    {
        var tip = Match("Salewa");
        Assert.Equal(MatchKind.PossibleStrong, tip.Kind);
        var after = IconShortNameTiebreaker.Apply(Index, tip, new[] { "Salewa" });
        Assert.Equal(MatchKind.Verified, after.Kind);
        Assert.Equal("Salewa first aid kit", after.Single!.Name);
    }

    [Fact]
    public void EmptyOrJunkIconText_LeavesTheTooltipResultAlone()
    {
        var tip = Match("Salewa");
        Assert.Equal(tip, IconShortNameTiebreaker.Apply(Index, tip, Array.Empty<string>()));
        Assert.Equal(tip, IconShortNameTiebreaker.Apply(Index, tip, new[] { " ", "₽ 12 450", "x" }));
    }

    [Fact]
    public void Card_ForAResolvedIconMatch_IsAFullCardNotAPossibleMatch()
    {
        var tip = Match("Salewa");
        var after = IconShortNameTiebreaker.Apply(Index, tip, new[] { "Salewa" });
        var card = new CardBuilder(LiveCatalog.RegularContext, null, () => LiveCatalog.FetchedAt).ForMatch(after);
        Assert.Equal(CardKind.Full, card.Kind);
        Assert.Null(card.Heading);
        Assert.Equal("Salewa first aid kit", card.Title);
    }

    [Fact]
    public void ExactShort_SkipsPresets_AndFindsUniqueItems()
    {
        var chain = Index.ExactShort("Chainlet");
        Assert.Single(chain);
        Assert.Equal("Chainlet", chain[0].Name);
        Assert.True(Index.ExactShort("M995").Count >= 3);
    }

    // ---------------- pixel locate + Tesseract ----------------
    sealed record Case(string File, double Scale, int ScreenHeight, int CursorX, int CursorY, PixelRect Tooltip, string ShortName, bool Covered);

    static IReadOnlyList<Case> All()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "icon-name", "manifest.tsv");
        return File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0).Select(l =>
        {
            var p = l.Split('\t');
            return new Case(p[0], double.Parse(p[1], CultureInfo.InvariantCulture), int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4]),
                new PixelRect(int.Parse(p[5]), int.Parse(p[6]), int.Parse(p[7]), int.Parse(p[8])), p[9], p[10] is "1" or "true");
        }).ToList();
    }

    static RgbImage Load(string file)
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "icon-name", file)), CompressionMode.Decompress);
        return Ppm.Read(gz);
    }

    public static IEnumerable<object[]> Files() => All().Select(c => new object[] { c.File });

    [Theory]
    [MemberData(nameof(Files))]
    public void Locator_FindsTheCell_AndExtractsABandUnlessCovered(string file)
    {
        var c = All().Single(x => x.File == file);
        var img = Load(file);
        var loc = IconShortNameLocator.Locate(img, c.CursorX, c.CursorY, c.Tooltip, StackCountLocator.NominalPitch(c.ScreenHeight));
        _out.WriteLine($"{file}: ok={loc.Ok} pitch={loc.Pitch} cell={loc.Cell} bands={loc.Bands.Count} {loc.Note}");
        Assert.NotNull(loc.Cell);
        Assert.True(loc.Cell!.Value.Contains(c.CursorX, c.CursorY));
        if (c.Covered) Assert.False(loc.Ok);
        else
        {
            Assert.True(loc.Ok);
            Assert.NotEmpty(loc.Bands);
        }
    }

    [Theory]
    [InlineData("salewa_100.ppm.gz", "Salewa")]
    [InlineData("salewa_150.ppm.gz", "Salewa")]
    [InlineData("splint_100.ppm.gz", "Splint")]
    [InlineData("m855_icon_m995_tip_100.ppm.gz", "M855")]
    public async Task Reader_ReadsTheIconShortName_WithTesseract(string file, string expected)
    {
        var engine = TesseractTests.Engine.Value;
        if (engine == null) { _out.WriteLine("Tesseract native runtime unavailable; OCR half skipped."); return; }
        var c = All().Single(x => x.File == file);
        var img = Load(file);
        var reader = new IconShortNameReader(engine);
        var r = await reader.ReadAsync(img, c.CursorX, c.CursorY, c.Tooltip, c.ScreenHeight, CancellationToken.None);
        _out.WriteLine($"{file}: [{string.Join(" | ", r.Texts)}] {r.Note}");
        Assert.NotEmpty(r.Texts);
        // Exact transcription can slip a glyph; the read must still uniquely resolve the short name.
        var hits = r.Texts.SelectMany(t => IconShortNameTiebreaker.Hits(Index, t)).DistinctBy(i => i.Id).ToList();
        Assert.Contains(hits, i => string.Equals(i.ShortName, expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reader_CoveredShortName_ReturnsNoText()
    {
        var engine = TesseractTests.Engine.Value;
        if (engine == null) { _out.WriteLine("Tesseract native runtime unavailable; OCR half skipped."); return; }
        var c = All().Single(x => x.File == "salewa_covered_100.ppm.gz");
        var img = Load(c.File);
        var r = await new IconShortNameReader(engine).ReadAsync(img, c.CursorX, c.CursorY, c.Tooltip, c.ScreenHeight, CancellationToken.None);
        _out.WriteLine($"covered: [{string.Join(" | ", r.Texts)}] {r.Note}");
        Assert.Empty(r.Texts);
    }

    [Fact]
    public async Task EndToEnd_IconResolvesAPossibleMatch_OnASyntheticCapture()
    {
        var engine = TesseractTests.Engine.Value;
        if (engine == null) { _out.WriteLine("Tesseract native runtime unavailable; OCR half skipped."); return; }
        var c = All().Single(x => x.File == "salewa_100.ppm.gz");
        var img = Load(c.File);
        var tip = Match("Saleva");
        Assert.NotEqual(MatchKind.Verified, tip.Kind);
        var icon = await new IconShortNameReader(engine).ReadAsync(img, c.CursorX, c.CursorY, c.Tooltip, c.ScreenHeight, CancellationToken.None);
        var after = IconShortNameTiebreaker.Apply(Index, tip, icon.Texts);
        Assert.Equal(MatchKind.Verified, after.Kind);
        Assert.Equal("Salewa first aid kit", after.Single!.Name);
    }

    static bool NameNormalizerCompactEquals(string a, string b) =>
        TarkovOverlay.Core.Text.NameNormalizer.Compact(a) == TarkovOverlay.Core.Text.NameNormalizer.Compact(b);
}
