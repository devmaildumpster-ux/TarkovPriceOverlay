using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace TarkovOverlay.Tests;

/// <summary>1.0.4 bug: "Dogtag BEAR" gave a "Same name — choose in app" picker of 24 identical names. Recorded tarkov.dev catalogs.</summary>
public class SameNameTests
{
    private readonly ITestOutputHelper _out;
    public SameNameTests(ITestOutputHelper o) => _out = o;

    static CardModel CardFor(string name, out MatchResult m, bool pve = false)
    {
        var ctx = pve ? new TarkovOverlay.Core.Catalog.CatalogContext(LiveCatalog.Pve, () => LiveCatalog.FetchedAt) : LiveCatalog.RegularContext;
        m = ctx.Items.Match(name);
        return new CardBuilder(ctx, null).ForMatch(m);
    }

    void Dump(CardModel c)
    {
        _out.WriteLine($"{c.Kind} heading '{c.Heading}' title '{c.Title}' cue {c.ModeCue}");
        foreach (var r in c.Rows) _out.WriteLine($"  {r.Label}: {r.Value} ({r.Detail})");
        foreach (var f in c.Facts) _out.WriteLine($"  fact: {f}");
        foreach (var x in c.Messages) _out.WriteLine($"  msg: {x}");
    }

    [Theory]
    [InlineData("Dogtag BEAR", false)]
    [InlineData("Dogtag USEC", false)]
    [InlineData("Dogtag BEAR", true)]
    [InlineData("Marked dogtag USEC", false)]
    public void Dogtags_AreOneCard_WithRange_BaseTag_AndLevelNote(string name, bool pve)
    {
        var c = CardFor(name, out var m, pve);
        Dump(c);
        Assert.Equal(MatchKind.Conflict, m.Kind);           // the catalog really has many entries with this name
        Assert.True(m.Items.Count >= 4);
        Assert.Equal(CardKind.Full, c.Kind);                 // …but the card is one item, not a picker
        Assert.Null(c.Heading);
        Assert.Equal(name, c.Title);
        Assert.Equal(new[] { "Flea", "Trader", "Base tag" }, c.Rows.Select(r => r.Label).ToArray());
        Assert.Contains(c.Facts, f => f.StartsWith("Value depends on level"));
        Assert.Equal(m.Items.Count, c.CandidateIds.Count);
        // base tag = tarkov.dev's base entry ("dogtag-bear"), not a "-n" variant
        Assert.Equal(0, CardBuilder.VariantIndex(LiveCatalog.RegularContext.Snapshot.Items[c.ItemId!]));
    }

    [Fact]
    public void DogtagCase_IsNotPartOfTheDogtagFamily_AndStillVerifies()
    {
        var c = CardFor("Dogtag case", out var m);
        Assert.Equal(MatchKind.Verified, m.Kind);
        Assert.DoesNotContain(c.Facts, f => f.StartsWith("Value depends on level"));
    }

    [Fact]
    public void SameNameEntries_WithNoPrices_AreMerged()
    {
        var c = CardFor("Sealed weapon case", out var m);
        Dump(c);
        Assert.Equal(MatchKind.Conflict, m.Kind);
        Assert.Equal(CardKind.Full, c.Kind);
        Assert.Contains(c.Facts, f => f.Contains("catalog entries share this name"));
    }

    [Fact]
    public void SameNameEntries_WithDifferentPrices_KeepThePicker_WithDistinguishingInfo_AndNoRepeatedHeader()
    {
        var c = CardFor("Girl poster", out var m); // flea averages differ by far more than 10%
        Dump(c);
        Assert.Equal(CardKind.Conflict, c.Kind);
        Assert.Equal("Same name — pick in app", c.Heading);
        Assert.Equal("Girl poster", c.Title);
        Assert.NotEqual(c.Heading, c.Title);
        var shown = c.Messages.Take(3).ToList();
        Assert.Equal(3, shown.Distinct().Count());           // no more identical lines
        Assert.All(shown, l => Assert.Matches(@"^#\d+ · (flea|trader) ₽", l));
    }

    [Fact]
    public void MergeRule_TenPercent()
    {
        var ctx = LiveCatalog.RegularContext;
        var b = new CardBuilder(ctx, null);
        var posters = ctx.Items.Match("Girl poster").Items;
        Assert.False(b.ShouldMergeSameName(posters));
        // priced + unpriced mix (BT ammo pack: one flea-listed, one not) is not merged
        Assert.False(b.ShouldMergeSameName(ctx.Items.Match("5.45x39mm BT gs ammo pack (120 pcs)").Items));
        // two copies of one item are within 0% → merged
        var one = posters[0];
        Assert.True(b.ShouldMergeSameName(new[] { one, one }));
    }

    [Fact]
    public void ModeCatalogs_AreSeparate_SoPvEEntriesNeverJoinPvPMatches()
    {
        var reg = LiveCatalog.RegularContext.Items.Match("Dogtag BEAR").Items.Count;
        var regNames = LiveCatalog.Regular.Items.Values.Count(i => i.Name == "Dogtag BEAR");
        Assert.Equal(regNames, reg);
        var pveCtx = new TarkovOverlay.Core.Catalog.CatalogContext(LiveCatalog.Pve, () => LiveCatalog.FetchedAt);
        Assert.Equal(LiveCatalog.Pve.Items.Values.Count(i => i.Name == "Dogtag BEAR"), pveCtx.Items.Match("Dogtag BEAR").Items.Count);
        Assert.Equal("regular", new TarkovOverlay.Core.Settings.AppSettings().ModeId); // default PvP
    }

    [Fact]
    public void ReadsDisagree_Choice_HasDistinctHeadingAndTitle()
    {
        var ctx = LiveCatalog.RegularContext;
        var a = ctx.Items.Match("9x19mm FMJ M882").Items[0];
        var b2 = ctx.Items.Match("9x39mm PAB-9 gs").Items[0];
        var c = new CardBuilder(ctx, null).ForMatch(new MatchResult(MatchKind.Conflict, new[] { a, b2 }, "x", "OCR reads disagree"));
        Assert.Equal("Reads disagree — pick in app", c.Heading);
        Assert.Equal("2 candidates", c.Title);
        Assert.Contains(c.Messages, l => l.StartsWith("9x19mm FMJ M882"));
    }
}
