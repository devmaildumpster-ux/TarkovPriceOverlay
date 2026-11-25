using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Extracts;
using TarkovOverlay.Maps.Geometry;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

/// <summary>
/// 1.3.1 regression suite (Shoreline, PvE):
///   Stay in the extraction point 0:34:07
///   EXFIL01 Road to North V-Ex        0:00:28
///   EXFIL02 Road to Customs
///   EXFIL03 Railway Bridge
///   EXFIL04 Path to Lighthouse
///   EXFIL05 Smugglers' Path (Co-op)
///   EXFIL06 Pier Boat                 ?????
///   EXFIL07 Climber's Trail
///   TRANSIT01 Transit to Lighthouse
///   TRANSIT04 Transit to Icebreaker (orange, second line "Ssssh-Tuuuu tool set required")
/// Extract/transit ids and names match json.tarkov.dev/pve/maps (+ maps_en).
/// </summary>
public class ExitListShorelineTests
{
    private static ExtractInfo X(string id, string raw, string name, string faction, double x, double y, double z) =>
        new(id, name, raw, faction, new Vec3(x, y, z), Array.Empty<Vec3>(), null, null, Array.Empty<string>());

    internal static readonly MapInfo Shoreline = new("5704e554d2720bac5b8b456e", "Shoreline", "shoreline", "Shoreline", null, new[]
    {
        X("f7da3681df4ae3bd7b3d5ba0e0f960dc1722d23f", "RedRebel_alp", "Climber's Trail", "pmc", -214.3, -1, -361.8),
        X("3475b9df1bf661d3c746ff1a1a8493bae828b939", "Adm Basement", "Admin Basement", "scav", -256.87, -7.71, -149.78),
        X("cf857a4cc48ed3c8c6dd27f7d55d6348ffbdbc68", "Tunnel", "Tunnel", "pmc", 376.36, -55.94, 319.25),
        X("fd880bdc668a7a177b5f2eedde4b40a9661e68b1", "Rock Passage", "Rock Passage", "pmc", -200.8, -13.35, -358.34),
        X("566161efb75d27f6347b9d0f610cd423dcdf319f", "Lighthouse", "Lighthouse", "scav", -458.15, -54.29, 567.29),
        X("40f2f8a82926d016855443fba325070fa8b5dafb", "Smugglers_Trail_coop", "Smugglers' Path (Co-op)", "shared", -729.4, -26, -255.1),
        X("9daba46bc5e06708a54c6274678e34e6bf13de12", "Svetliy Dead End", "Svetliy Dead End", "scav", 461.36, -52.83, 120.6),
        X("ef41c42e56e8f67e5835245dd4f66c4ed8684db9", "Lighthouse_pass", "Path to Lighthouse", "pmc", 448.9, -44.7, -254.6),
        X("aa2c447aa7f49a1795efd56aad7ef0670888425f", "Wrecked Road", "Ruined Road", "scav", 366.62, -61.13, 332.51),
        X("818e7812793c8e47a09c62f670ae189c8bd61901", "CCP Temporary", "CCP Temporary", "pmc", -855.85, -59.44, 476.82),
        X("95406652decb52abdd763626e88024931990da7d", "Scav Road to Customs", "Road to Customs", "scav", -859.05, -42.07, 0.49),
        X("a048afd75fd279f62ee3da4fb0f38c42d8b7a934", "RWing Gym Entrance", "East Wing Gym Entrance", "scav", -296.95, -6.63, -100.89),
        X("a302bfd6b8b040202ce873db7386f930318c3f0b", "Road to Customs", "Road to Customs", "pmc", -859.05, -42.07, 0.49),
        X("04d0f85f6abb2d77d803449be6918c26efc58478", "Road_at_railbridge", "Railway Bridge", "pmc", -1025.05, -60.79, 297.65),
        X("1c7d8b060c38232059ce879b560a5979d12d7239", "Ruined House Fence", "Ruined House Fence", "scav", 477.78, -51.55, -66.83),
        X("aef6de6ce5e7c898a2a6a65c4433e683f4e06baa", "Pier Boat", "Pier Boat", "pmc", -335.89, -66.12, 564.14),
        X("e9f00531aa0d2933a41d4a6c8446a952736b304d", "shoreline_secret_heartbeat", "Mountain Bunker", "pmc", -390.96, -2.55, -385.72),
        X("1dc6fca97ac062b2363fe53479748fb0bc6e2d6b", "Shorl_V-Ex", "Road to North V-Ex", "pmc", -543.13, -16.97, -378.59),
        X("eeddbe9a8476361059a69eab4c0deeadac93ab5c", "Alpinist", "Cliff Descent", "pmc", -196.08, -10.27, -360.04),
    }, new[]
    {
        new TransitInfo("24", "Transit to Lighthouse", null, new Vec3(416.78, -56.06, -214.15), Array.Empty<Vec3>()),
        new TransitInfo("25", "Transit to Terminal", null, new Vec3(-965.56, -57.48, 373.79), Array.Empty<Vec3>()),
        new TransitInfo("43", "Transit to Labyrinth?", null, new Vec3(-195.13, -9.83, -82.93), Array.Empty<Vec3>()),
        new TransitInfo("51", "Transit to Icebreaker", null, new Vec3(-315.7, -63.66, 525.71), Array.Empty<Vec3>()),
    }, Array.Empty<SwitchInfo>());

    private static readonly string[] ListedExfils =
    {
        "Road to North V-Ex", "Road to Customs", "Railway Bridge", "Path to Lighthouse",
        "Smugglers' Path (Co-op)", "Pier Boat", "Climber's Trail",
    };

    private static readonly string[] ListedTransits =
    {
        "Transit to Lighthouse", "Transit to Icebreaker",
    };

    private static readonly string[] NotListedExfils =
    {
        "Tunnel", "Rock Passage", "CCP Temporary", "Mountain Bunker", "Cliff Descent",
        "Admin Basement", "Lighthouse", "Svetliy Dead End", "Ruined Road", "East Wing Gym Entrance", "Ruined House Fence",
    };

    [Fact]
    public void Exact_shoreline_list_matches_all_seven_exfils_and_both_transits()
    {
        var markers = ExtractFilter.For(Shoreline, null, includeTransits: true);
        var lines = new[]
        {
            "Stay in the extraction point 0:34:07",
            "EXFIL01 Road to North V-Ex 0:00:28",
            "EXFIL02 Road to Customs",
            "EXFIL03 Railway Bridge",
            "EXFIL04 Path to Lighthouse",
            "EXFIL05 Smugglers' Path (Co-op)",
            "EXFIL06 Pier Boat ?????",
            "EXFIL07 Climber's Trail",
            "TRANSIT01 Transit to Lighthouse",
            "TRANSIT04 Transit to Icebreaker",
            "Ssssh-Tuuuu tool set required",
            "Extraction in 27.4",
        };
        var ids = ExitListMatcher.MatchLines(lines, markers.Select(m => (m.Id, m.Name))).Select(m => m.ExtractId).ToHashSet();

        foreach (var n in ListedExfils.Concat(ListedTransits))
            Assert.True(markers.Where(m => m.Name == n).All(m => ids.Contains(m.Id)), $"{n} should be marked listed");
        foreach (var n in NotListedExfils)
            Assert.True(markers.Where(m => m.Name == n).All(m => !ids.Contains(m.Id)), $"{n} should stay crossed out");
        Assert.DoesNotContain(markers.Where(m => m.Name is "Transit to Terminal" or "Transit to Labyrinth?"), m => ids.Contains(m.Id));
    }

    [Fact]
    public void Ocr_mangled_shoreline_lines_still_match_every_row()
    {
        var markers = ExtractFilter.For(Shoreline, null, includeTransits: true);
        var lines = new[]
        {
            "Stay in the exiraction point 0:34:07",
            "EXFILOL Road to Norih V-Ex 0:00:28",
            "“EXFILO2 RoadtoCustoms",
            "EXFILO3 = Railway Bridge",
            "“ExFilodPathtoLighthouse",
            "EXFILO5 = Smugglers* Path (Co-op)",
            "EXFILO6 = Pier Boat 7722?",
            "ExFiILO7 = Climmber's trail",
            "IRANSIIO1 Iransit to Lighthouse",
            "TRANSIN04 Iransii to Icebreaker",
            "Ssssh-Tuuuu too! set required",
        };
        var ids = ExitListMatcher.MatchLines(lines, markers.Select(m => (m.Id, m.Name))).Select(m => m.ExtractId).ToHashSet();
        foreach (var n in ListedExfils.Concat(ListedTransits))
            Assert.True(markers.Where(m => m.Name == n).All(m => ids.Contains(m.Id)), $"{n} should match despite OCR noise");
        foreach (var n in NotListedExfils)
            Assert.True(markers.Where(m => m.Name == n).All(m => !ids.Contains(m.Id)), $"{n} should not false-match");
    }

    [Fact]
    public void Transit_to_lighthouse_never_false_matches_scav_lighthouse_when_transits_omitted()
    {
        var markers = ExtractFilter.For(Shoreline, null, includeTransits: false);
        var ms = ExitListMatcher.MatchLines(
            new[] { "TRANSIT01 Transit to Lighthouse", "IRANSIIO1] Iransit to Lighthouse", "TRANSIT04 Transit to Icebreaker" },
            markers.Select(m => (m.Id, m.Name)));
        Assert.Empty(ms);
    }

    [Fact]
    public void One_typo_path_to_lighthouse_does_not_lose_to_scav_lighthouse()
    {
        var markers = ExtractFilter.For(Shoreline, null, includeTransits: false);
        var ms = ExitListMatcher.MatchLines(new[] { "EXFIL04 Paht to Lighthouse" }, markers.Select(m => (m.Id, m.Name)));
        var hit = Assert.Single(ms);
        Assert.Equal("Path to Lighthouse", hit.ExtractName);
    }

    [Theory]
    [InlineData("Dorms V-Ex", "EXFIL01 Dorms V-Ex 0:00:28")]
    [InlineData("Dorms V-Ex", "ExFil01 Dorms V-Ex 0.00.28")]
    [InlineData("Bridge V-Ex", "EXFIL03 Bridge V-Ex O:OO:28")]
    [InlineData("Bridge V-Ex", "EXFIL 03 Bridge V-Ex 0 : 00 : 28")]
    [InlineData("Road to North V-Ex", "EXFIL01 Road to North V-Ex 0:00:28")]
    [InlineData("Road to North V-Ex", "ExFil01 Read to North V-Ex 0:00:28")]
    [InlineData("Road to North V-Ex", "EXFIL01 Road to North V-Ex (0:00:28)")]
    [InlineData("Power Station V-Ex", "EXFIL04 Power Station V-Ex ??:??:??")]
    [InlineData("Power Station V-Ex", "EXFIL04 Power Station V-Ex 0:00:59")]
    [InlineData("Primorsky Ave Taxi V-Ex", "EXFIL02 Primorsky Ave Taxi V-Ex 0:00:42")]
    [InlineData("Police Cordon V-Ex", "EXFIL01 Police Cordon V-Ex 0:00:15")]
    public void Timed_vex_rows_match_across_all_maps(string expectedName, string ocrLine)
    {
        var candidates = new[]
        {
            ("1", "Dorms V-Ex"),
            ("2", "Bridge V-Ex"),
            ("3", "Road to North V-Ex"),
            ("4", "Power Station V-Ex"),
            ("5", "Primorsky Ave Taxi V-Ex"),
            ("6", "Police Cordon V-Ex"),
            ("7", "Road to Customs"),
            ("8", "Crossroads"),
        };
        var ms = ExitListMatcher.MatchLines(new[] { ocrLine }, candidates);
        var hit = Assert.Single(ms);
        Assert.Equal(expectedName, hit.ExtractName);
    }

    [Theory]
    [InlineData("EXFIL01 Road to North V-Ex 0:00:28", "Road to North V-Ex")]
    [InlineData("EXFIL 01 Road to North V-Ex 0.00.28", "Road to North V-Ex")]
    [InlineData("EXFILOL Road to North V-Ex O:OO:28", "Road to North V-Ex")]
    [InlineData("EXFIL06 Pier Boat ?????", "Pier Boat")]
    [InlineData("EXFILO6 = Pier Boat 7722?", "Pier Boat")]
    [InlineData("“EXFILO2 RoadtoCustoms", "RoadtoCustoms")]
    [InlineData("“ExFilodPathtoLighthouse", "PathtoLighthouse")]
    [InlineData("EXFIL01Old Gas Station", "Old Gas Station")]
    [InlineData("EXFIL01D-2", "D-2")]
    [InlineData("EXFIL03 Railway Exfil", "Railway Exfil")]
    [InlineData("Expo Checkpoint", "Expo Checkpoint")]
    [InlineData("Exit to Woods", "Exit to Woods")]
    [InlineData("Gate 3", "Gate 3")]
    [InlineData("ZB-1011", "ZB-1011")]
    [InlineData("TRANSIT04 Transit to Labyrinth?", "Transit to Labyrinth?")]
    public void StripDecorations_handles_timed_and_mangled_rows_without_hurting_names(string raw, string expected) =>
        Assert.Equal(expected, ExitListMatcher.StripDecorations(raw));
}
