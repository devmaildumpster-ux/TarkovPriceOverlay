using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Text;
using TarkovOverlay.Tests.Fixtures;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>
/// Section 10A matching. FIXTURE KIND: text-only OCR fixtures matched against the recorded live catalog
/// (json.tarkov.dev regular). Expected values are real catalog IDs.
/// </summary>
public class MatchingTests
{
    static ItemIndex Index => LiveCatalog.RegularContext.Items;

    [Theory]
    [InlineData("9x19mm FMJ M882", "64b7bbb74b75259c590fa897")]
    [InlineData("9x39mm PAB-9 gs", "61962d879bb3d20b0946d385")]
    [InlineData("Molot Arms VPO-209 .366 TKM carbine", "59e6687d86f77411d949b251")]
    [InlineData("AK 7.62x39 Magpul PMAG 20 GEN M3 20-round magazine", "64b9cf0ac12b9c38db26923a")]
    [InlineData("WARTECH TV-109 + TV-106 chest rig (A-TACS FG)", "59e7643b86f7742cbf2c109a")]
    [InlineData("RPK-16 5.45x39 95-round drum magazine", "5bed625c0db834001c062946")]
    [InlineData("AK-74 5.45x39 6L31 60-round magazine", "55d482194bdc2d1d4e8b456b")]
    [InlineData("AR-15 5.56x45 SureFire MAG5-60 STANAG 60-round magazine", "544a37c44bdc2d25388b4567")]
    [InlineData("SIG Sauer TANGO6T 1-6x24 30mm riflescope", "6567e7681265c8a131069b0f")]
    public void RepresentativeTitles_VerifyLiterally(string text, string id)
    {
        var m = Index.Match(text);
        Assert.Equal(MatchKind.Verified, m.Kind);
        Assert.Equal(id, m.Single!.Id);
        Assert.Equal(0, m.Stage);
    }

    [Theory]
    // two-line titles joined with a space in reading order
    [InlineData(new[] { "AR-15 5.56x45 SureFire MAG5-60 STANAG", "60-round magazine" }, "544a37c44bdc2d25388b4567")]
    [InlineData(new[] { "WARTECH TV-109 + TV-106 chest rig", "(A-TACS FG)" }, "59e7643b86f7742cbf2c109a")]
    public void WrappedTitle_SecondLineNeeded(string[] lines, string id)
    {
        Assert.NotEqual(MatchKind.Verified, Index.Match(lines[0]).Kind); // first line alone is not the item
        var m = Index.Match(NameNormalizer.JoinLines(lines));
        Assert.Equal(MatchKind.Verified, m.Kind);
        Assert.Equal(id, m.Single!.Id);
    }

    [Theory]
    [InlineData("AK-74 5.45<39 6L31 60-round magazine", "55d482194bdc2d1d4e8b456b")]
    [InlineData("AK-74 5.45×39 6L31 60-round magazine", "55d482194bdc2d1d4e8b456b")]
    [InlineData("AK-74 5.45x39 6L31 6Ø-round magazine", "55d482194bdc2d1d4e8b456b")]
    [InlineData("AR-15 5S6x45 SureFire MAG5-60 STANAG 60-round magazine", "544a37c44bdc2d25388b4567")]
    [InlineData("AR-15 5.56x45 SureFire MAG5-6Ø STANAG 6Ø-round magazine", "544a37c44bdc2d25388b4567")]
    [InlineData("SIG Sauer TANGO6T 1-6x24 30rnrn riflescope", "6567e7681265c8a131069b0f")]
    [InlineData("SIG Sauer TANGO6T 1-6x24 3ømrn riflescope", "6567e7681265c8a131069b0f")]
    [InlineData("SIG Sauer TANG06T 1-6x24 30rnrn riflescope", "6567e7681265c8a131069b0f")]
    [InlineData("SIG Sauer TANG06T 1-6<24 30mm riflescope", "6567e7681265c8a131069b0f")]
    [InlineData("RPK-16 5.45x39 95–round drum magazine", "5bed625c0db834001c062946")] // en dash
    [InlineData("  ak 7.62x39   MAGPUL pmag 20 gen m3 20-round magazine ", "64b9cf0ac12b9c38db26923a")]
    public void Lookalikes_AreRepairedToTheCompleteName(string text, string id)
    {
        var m = Index.Match(text);
        Assert.Equal(MatchKind.Verified, m.Kind);
        Assert.Equal(id, m.Single!.Id);
    }

    [Fact]
    public void ModelLetterRepair_RunsAfterUnitRepair()
    {
        var m = Index.Match("SIG Sauer TANG06T 1-6x24 30rnrn riflescope");
        Assert.Equal(2, m.Stage);
        Assert.Contains("rn as m", m.Repair);
        Assert.Contains("model letter", m.Repair);
        var unitOnly = Index.Match("SIG Sauer TANGO6T 1-6x24 30rnrn riflescope");
        Assert.Equal(1, unitOnly.Stage);
    }

    [Theory]
    [InlineData("AR-15 5.56x45 SureFire MAG5-100 STANAG 100-round magazine", "5c6592372e221600133e47d7")]
    [InlineData("AR-15 5.56x45 SureFire MAG5-1ØØ STANAG 1ØØ-round magazine", "5c6592372e221600133e47d7")]
    public void HundredRoundMag_StaysDistinctFromSixty(string text, string id)
    {
        var m = Index.Match(text);
        Assert.Equal(MatchKind.Verified, m.Kind);
        Assert.Equal(id, m.Single!.Id);
        Assert.NotEqual("544a37c44bdc2d25388b4567", m.Single.Id);
    }

    [Theory]
    [InlineData("SIG Sauer TANGO6T 1-6x24 38mm riflescope")]   // different tube size
    [InlineData("AK-74 5.56x45 6L31 60-round magazine")]        // different caliber
    [InlineData("AK-74 5.45x39 6L31 100-round magazine")]       // different capacity
    [InlineData("9x18mm FMJ M882")]
    public void DifferentCaliberCapacityOrTube_IsNeverAutoMatched(string text)
    {
        var m = Index.Match(text);
        Assert.False(m.IsCredibleFullName);
        Assert.NotEqual(MatchKind.PossibleStrong, m.Kind); // digits differ -> not a strong suggestion
    }

    [Fact]
    public void LooseRound_VsPackagedAmmo_AreDistinct()
    {
        Assert.Equal("64b7bbb74b75259c590fa897", Index.Match("9x19mm FMJ M882").Single!.Id);
        Assert.Equal("64aceafcb5bf5e95f50a4c20", Index.Match("9x19mm FMJ M882 ammo pack (30 pcs)").Single!.Id);
    }

    [Fact]
    public void IdenticalCatalogNames_BecomeAChoice()
    {
        var m = Index.Match("Secure container Gamma");
        Assert.Equal(MatchKind.Conflict, m.Kind);
        Assert.True(m.Items.Count >= 2);
    }

    [Fact]
    public void OneCharacterOff_IsStrongPossibleMatch()
    {
        var m = Index.Match("Molot Arms VPO-209 .366 TKM carbne");
        Assert.Equal(MatchKind.PossibleStrong, m.Kind);
        Assert.Equal("59e6687d86f77411d949b251", m.Single!.Id);
    }

    [Fact]
    public void UniqueExactShortName_IsStrongPossibleMatch()
    {
        var item = LiveCatalog.Regular.Items["59e7643b86f7742cbf2c109a"];
        var m = Index.Match(item.ShortName!);
        Assert.True(m.Kind is MatchKind.PossibleStrong or MatchKind.Several);
        Assert.Contains(m.Items, i => i.Id == item.Id);
    }

    [Fact]
    public void CutOffTitle_GivesSuggestionsOnly()
    {
        var m = Index.Match("AR-15 5.56x45 SureFire MAG5");
        Assert.False(m.IsCredibleFullName);
        Assert.Contains(m.Items, i => i.Id == "544a37c44bdc2d25388b4567");
    }

    [Fact]
    public void Gibberish_NoMatch()
    {
        Assert.Equal(MatchKind.None, Index.Match("zzqx vvv 0000 qqq").Kind);
        Assert.Equal(MatchKind.None, Index.Match("").Kind);
    }

    [Fact]
    public void TypedSearch_FindsByNameAndShorthand()
    {
        var r = Index.Search("tango6t");
        Assert.Equal("6567e7681265c8a131069b0f", r[0].Id);
        var r2 = Index.Search("m882");
        Assert.Contains(r2, i => i.Id == "64b7bbb74b75259c590fa897");
        var r3 = Index.Search("mag5 60");
        Assert.Equal("544a37c44bdc2d25388b4567", r3[0].Id);
    }

    [Fact]
    public void Normalizer_UnifiesCaseSpacesDashesAndQuotes()
    {
        Assert.Equal("ak-74 5.45x39 'x'", NameNormalizer.Normalize("  AK‑74\u00A0 5.45x39  ‘X’ "));
        Assert.Equal("ak7454539", NameNormalizer.Compact("AK—74 5.45 39"));
    }

    [Fact]
    public void Index_CoversWholeCatalog()
    {
        Assert.True(Index.Count > 4000);
    }
}
