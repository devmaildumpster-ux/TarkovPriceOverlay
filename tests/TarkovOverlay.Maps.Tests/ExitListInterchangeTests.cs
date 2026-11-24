using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Extracts;
using TarkovOverlay.Maps.Geometry;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

/// <summary>
/// 1.2.7 report (Interchange, PvE, PMC): the in-game exit list was
///   EXFIL01 Scav Camp (Co-Op) · EXFIL02 Saferoom Exfil · EXFIL03 Railway Exfil · EXFIL04 Power Station V-Ex ??:??:??
///   EXFIL05 Path to River (Flare) · TRANSIT01 Transit to Customs · TRANSIT02 Transit to Streets of Tarkov
/// but the map crossed out Railway Exfil, Saferoom Exfil and Scav Camp (Co-Op) as "not on your list" (and, correctly,
/// Hole in the Fence and Emercom Checkpoint). Extract ids/names/factions/positions as in json.tarkov.dev/pve/maps.
/// </summary>
public class ExitListInterchangeTests
{
    private static ExtractInfo X(string id, string name, string faction, double x, double y, double z) =>
        new(id, name, name, faction, new Vec3(x, y, z), Array.Empty<Vec3>(), null, null, Array.Empty<string>());

    internal static readonly MapInfo Interchange = new("5714dbc024597771384a510d", "Interchange", "interchange", "Interchange", null, new[]
    {
        X("4cf9e8c499daa3f18291b2fce601e6dd0be66882", "Railway Exfil", "scav", 474.57, 19.92, -433.74),
        X("7bdf4ff52b59f4bb8d60cc0ec90a932596b0f6ac", "Saferoom Exfil", "pmc", -47.73, 22.97, 44.06),
        X("714a00d27d6ac36ca35a714c54b5517b32373914", "Railway Exfil", "pmc", 474.82, 20.92, -433.99),
        X("56bd754c4ce1864b212e908114bd6190940e01c2", "Emercom Checkpoint", "pmc", -321.56, 22.45, 266.74),
        X("44bd9f5882e7a6044c3d6e15abb6560306575d96", "Scav Camp (Co-Op)", "shared", -97.48, 18.12, -73.09),
        X("1e65f8bedbd9322074ae387892f0972883bdb794", "Path to River (Flare)", "pmc", 613.9, 27.9, 71.3),
        X("f60ee7a996094e3a9c0cb1155a6e20558a4386db", "Emercom Checkpoint", "scav", -321.56, 24.29, 266.74),
        X("12448fe61862391cd784b298a6c3081f24de39d6", "Power Station V-Ex", "pmc", -302.89, 21.65, -367.73),
        X("9aa4d38482dc1996ba1f191e2f897e193e05f982", "Hole in the Fence", "pmc", -219.92, 13.79, -37.02),
    }, new[]
    {
        new TransitInfo("6", "Transit to Customs", null, new Vec3(0, 0, 0), Array.Empty<Vec3>()),
        new TransitInfo("7", "Transit to Streets of Tarkov", null, new Vec3(0, 0, 0), Array.Empty<Vec3>()),
    }, Array.Empty<SwitchInfo>());

    private static readonly string[] Listed = { "Scav Camp (Co-Op)", "Saferoom Exfil", "Railway Exfil", "Power Station V-Ex", "Path to River (Flare)" };
    private static readonly string[] NotListed = { "Hole in the Fence", "Emercom Checkpoint" };

    /// <summary>What the map draws for a PMC (no transits: the reader never matches those) and what gets crossed out.</summary>
    private static (List<ExtractMarker> Markers, HashSet<string> ListedIds) Run(IEnumerable<string> lines)
    {
        var markers = ExtractFilter.For(Interchange, PlayerSide.Pmc, includeTransits: false);
        var ids = ExitListMatcher.MatchLines(lines, markers.Select(m => (m.Id, m.Name))).Select(m => m.ExtractId).ToHashSet();
        return (markers, ids);
    }

    private static void AssertListed(IEnumerable<string> lines)
    {
        var (markers, ids) = Run(lines);
        foreach (var n in Listed) Assert.True(markers.Where(m => m.Name == n).All(m => ids.Contains(m.Id)), $"{n} should be on the list");
        foreach (var n in NotListed) Assert.True(markers.Where(m => m.Name == n).All(m => !ids.Contains(m.Id)), $"{n} should be crossed out");
    }

    [Fact]
    public void Map_draws_seven_pmc_exits_with_railway_and_emercom_merged()
    {
        var markers = ExtractFilter.For(Interchange, PlayerSide.Pmc, includeTransits: false);
        Assert.Equal(new[] { "Railway Exfil", "Saferoom Exfil", "Emercom Checkpoint", "Scav Camp (Co-Op)", "Path to River (Flare)", "Power Station V-Ex", "Hole in the Fence" },
            markers.Select(m => m.Name));
        Assert.Equal(ExtractKind.Shared, markers.Single(m => m.Name == "Railway Exfil").Kind);
        Assert.Equal(ExtractKind.Shared, markers.Single(m => m.Name == "Emercom Checkpoint").Kind);
        Assert.Equal(ExtractKind.CoOp, markers.Single(m => m.Name == "Scav Camp (Co-Op)").Kind);
    }

    [Fact]
    public void Exact_list_clean_ocr()
    {
        AssertListed(new[]
        {
            "Find an extraction point 8:24:47",
            "EXFIL01 Scav Camp (Co-Op)", "EXFIL02 Saferoom Exfil", "EXFIL03 Railway Exfil", "EXFIL04 Power Station V-Ex ??:??:??",
            "EXFIL05 Path to River (Flare)", "TRANSIT01 Transit to Customs", "TRANSIT02 Transit to Streets of Tarkov",
        });
    }

    [Fact]
    public void Exact_list_as_tesseract_read_it_from_the_report_screenshot()
    {
        // Tesseract on the report screenshot (rows cut at the monitor edge, "0" read as "@")
        AssertListed(new[]
        {
            "<3 Find an extractiq", "EXFIL@1 Scav Camp (Co-0", "EXFIL@2 Saferoom Exfil", "EXFIL@3 Railway Exfil", "EXFIL@4 Power Station V-",
            "EXFIL@5 Path to River (Fi", "eeiratr an  -_ 4  oe a  TRANSITOI oc", "=z | me ‘ry ea) TRANSIT@2 Transit to st",
        });
    }

    [Fact]
    public void Exact_list_labels_split_glued_or_misread()
    {
        AssertListed(new[]
        {
            "EXFIL01", "Scav Camp (Co-Op)", "EXFILO2 Saferoom Exfil", "EXF1L03Railway Exfil", "EXFlL04 Power Station V-Ex", "EXFIL05Path to River (Flare)",
            "TRANSIT01 Transit to Customs", "TRANSITQ2 Transit to Streets of Tarkov",
        });
    }

    [Theory]
    [InlineData("EXFIL03 Railway Exfil", "Railway Exfil")]
    [InlineData("EXFIL02 Saferoom Exfil", "Saferoom Exfil")]
    [InlineData("EXFIL03Railway Exfil", "Railway Exfil")]
    [InlineData("EXFIL01Scav Camp (Co-Op)", "Scav Camp (Co-Op)")]
    [InlineData("EXFIL@4 Power Station V-Ex ??:??:??", "Power Station V-Ex")]
    [InlineData("EXFIL04 Power Station V-Ex 00:07:30", "Power Station V-Ex")]
    [InlineData("TRANSIT01 Transit to Customs", "Transit to Customs")]
    [InlineData("Exfil", "Exfil")]                 // a lone word "Exfil" is a name word, not a row label
    [InlineData("EXFIL02 Trai1er Park  Pending...", "Trai1er Park")]
    public void StripDecorations_removes_only_the_row_label(string raw, string expected) =>
        Assert.Equal(expected, ExitListMatcher.StripDecorations(raw));

    [Fact]
    public void Same_name_listed_twice_in_the_feed_marks_both_copies()
    {
        // whichever Railway Exfil copy (PMC or Scav id) the map draws, it counts as listed
        var raw = Interchange.Extracts.Select(e => (e.Id, e.Name));
        var ids = ExitListMatcher.MatchLines(new[] { "EXFIL03 Railway Exfil" }, raw).Select(m => m.ExtractId).ToHashSet();
        Assert.Contains("4cf9e8c499daa3f18291b2fce601e6dd0be66882", ids);
        Assert.Contains("714a00d27d6ac36ca35a714c54b5517b32373914", ids);
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void Other_rows_never_match_the_exits_that_are_not_listed()
    {
        var (markers, ids) = Run(new[] { "EXFIL02 Saferoom Exfil", "EXFIL03 Railway Exfil" });
        Assert.Equal(new[] { "Railway Exfil", "Saferoom Exfil" }, markers.Where(m => ids.Contains(m.Id)).Select(m => m.Name));
    }
}
