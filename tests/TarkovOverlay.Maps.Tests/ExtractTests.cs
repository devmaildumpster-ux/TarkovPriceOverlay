using TarkovOverlay.Maps.Extracts;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

public class ExtractTests
{
    [Fact]
    public void Pmc_and_scav_get_their_own_extracts_plus_shared()
    {
        var customs = Fx.Maps.ByNormalizedName("customs")!;
        var pmc = ExtractFilter.For(customs, PlayerSide.Pmc, includeTransits: false);
        var scav = ExtractFilter.For(customs, PlayerSide.Scav, includeTransits: false);
        Assert.Contains(pmc, e => e.Name == "Crossroads" && e.Kind == ExtractKind.Shared); // pmc + scav copies → both
        Assert.Contains(pmc, e => e.Name == "Dorms V-Ex" && e.Kind == ExtractKind.Pmc);
        Assert.DoesNotContain(pmc, e => e.Kind == ExtractKind.Scav);
        Assert.Contains(scav, e => e.Name == "Old Road Gate" && e.Kind == ExtractKind.Scav);
        Assert.DoesNotContain(scav, e => e.Name == "Dorms V-Ex");
        Assert.DoesNotContain(scav, e => e.Kind == ExtractKind.Pmc);
        // co-op extract is usable by both and labelled
        var coop = Assert.Single(pmc, e => e.Name.Contains("Co-op"));
        Assert.Equal(ExtractKind.CoOp, coop.Kind);
        Assert.Contains(scav, e => e.Id == coop.Id);
        Assert.Contains(coop.Notes, n => n.StartsWith("Co-op"));
    }

    [Fact]
    public void Requirement_notes()
    {
        var customs = Fx.Maps.ByNormalizedName("customs")!;
        var pmc = ExtractFilter.For(customs, PlayerSide.Pmc, id => id == "675aaab74bca0b001d02f356" ? "Customs marked key" : null);
        Assert.Contains("Paid: ₽20,000", pmc.Single(e => e.Name == "Dorms V-Ex").Notes);
        Assert.Contains(pmc.Single(e => e.Name.Contains("(Flare)")).Notes, n => n.Contains("flare"));
        var boat = pmc.Single(e => e.Name == "Smugglers' Boat");
        Assert.True(boat.Secret);
        Assert.Contains("Requires item: Customs marked key", boat.Notes);
        // the feed's switch list is identical on every Customs extract → not shown as a requirement
        Assert.DoesNotContain(pmc, e => e.Notes.Any(n => n.StartsWith("Needs switch")));
        // transits are added for both sides
        Assert.Contains(pmc, e => e.Kind == ExtractKind.Transit && e.Name == "Transit to Reserve");
        var reserve = Fx.Maps.ByNormalizedName("reserve")!;
        var cliff = ExtractFilter.For(reserve, PlayerSide.Pmc).FirstOrDefault(e => e.Name.Contains("Cliff Descent"));
        if (cliff != null) Assert.Contains(cliff.Notes, n => n.Contains("Paracord"));
    }

    [Fact]
    public void Duplicate_pmc_and_scav_copies_are_merged()
    {
        var customs = Fx.Maps.ByNormalizedName("customs")!;
        var scav = ExtractFilter.For(customs, PlayerSide.Scav, includeTransits: false);
        Assert.Equal(scav.Count, scav.Select(e => e.Name).Distinct().Count() + scav.GroupBy(e => e.Name).Sum(g => g.Count() - 1));
        Assert.Single(scav, e => e.Name == "Crossroads");
    }

    [Fact]
    public void Night_factory_extracts_without_faction_count_as_shared()
    {
        var nf = Fx.Maps.ByNormalizedName("night-factory")!;
        Assert.Contains(ExtractFilter.For(nf, PlayerSide.Pmc), e => e.Name == "Gate 3");
        Assert.Contains(ExtractFilter.For(nf, PlayerSide.Scav), e => e.Name == "Gate 3");
    }

    [Fact]
    public void Exit_list_ocr_lines_match_extract_names()
    {
        var customs = Fx.Maps.ByNormalizedName("customs")!;
        var ex = ExtractFilter.For(customs, PlayerSide.Pmc, includeTransits: false).Select(e => (e.Id, e.Name));
        var lines = new[] { "EXFIL01 Crossroads", "EXFIL02 Trai1er Park  Pending...", "EXFIL03  RUAF Roadblock 00:12:30", "random hud text", "ZB-1011" };
        var m = ExitListMatcher.MatchLines(lines, ex);
        Assert.Equal(new[] { "Crossroads", "Trailer Park", "RUAF Roadblock", "ZB-1011" }, m.Select(x => x.ExtractName));
    }
}
