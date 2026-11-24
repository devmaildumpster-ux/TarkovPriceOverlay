using System.Text.Json;
using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Definitions;
using TarkovOverlay.Maps.Geometry;
using TarkovOverlay.Maps.Settings;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

/// <summary>1.2.2: tarkov.dev layer extents and automatic floor switching from a screenshot's (x, y, z).</summary>
public class AutoLayerTests
{
    private static readonly List<MapDefinition> Defs = MapDataService.LoadFallbackDefinitions();
    private static MapDefinition D(string key) => Defs.Single(d => d.Key == key);

    [Theory]
    [InlineData("customs", "2nd Floor,3rd Floor,4th Floor,Underground")]
    [InlineData("reserve", "2nd Floor,3rd Floor,4th Floor,5th Floor,Bunkers")]
    [InlineData("factory", "2nd Floor,3rd Floor,Tunnels")]
    [InlineData("ground-zero", "2nd Floor,3rd Floor,Garage")]
    [InlineData("streets-of-tarkov", "2nd Floor,3rd Floor,4th Floor,5th Floor,Underground")]
    [InlineData("shoreline", "2nd Floor,3rd Floor,Underground")]
    [InlineData("interchange", "2nd Floor,3rd Floor")]
    [InlineData("the-lab", "Second Level,Technical")]
    public void Fallback_keeps_extents_on_every_layer(string key, string layers)
    {
        var d = D(key);
        foreach (var name in layers.Split(','))
        {
            var l = d.Layers.Single(x => x.Name == name);
            Assert.NotNull(l.Extents);
            Assert.NotEmpty(l.Extents!);
            Assert.All(l.Extents!, e => Assert.True(e.HeightMin < e.HeightMax, $"{key}/{name}"));
        }
    }

    [Fact]
    public void Icebreaker_has_all_its_decks_and_maps_without_floors_have_none()
    {
        Assert.Equal(16, D("icebreaker").Layers.Count(l => l.Extents is { Count: > 0 }));
        foreach (var k in new[] { "woods", "lighthouse", "terminal" }) Assert.Empty(D(k).Layers);
    }

    [Fact]
    public void Bounds_parse_x_z_corners_and_ignore_the_note()
    {
        var bunkers = D("reserve").Layers.Single(l => l.Name == "Bunkers").Extents!;
        var storage = bunkers[0].Boxes[0];
        Assert.Equal((128d, -208d, 18d, -33d, "storage bunker"), (storage.X1, storage.Z1, storage.X2, storage.Z2, storage.Note));
        Assert.Equal(-10000, bunkers[0].HeightMin);
        Assert.Equal(-7.27, bunkers[0].HeightMax);
        // Labs' Second Level box has no note
        var lab = D("the-lab").Layers.Single(l => l.Name == "Second Level").Extents![0].Boxes.Single();
        Assert.Null(lab.Note);
        Assert.True(lab.Contains(-200, -350));
    }

    [Fact]
    public void Height_band_is_min_inclusive_max_exclusive_and_boxes_are_inclusive_in_any_corner_order()
    {
        var e = new LayerExtent(-1, 3, new[] { new ExtentBox(10, 5, 0, -5) });
        Assert.True(e.Contains(0, -1, -5));   // min height, box corner
        Assert.False(e.Contains(0, 3, 0));    // max height excluded (like tarkov.dev's y < height[1])
        Assert.True(e.Contains(10, 2.99, 5)); // other corner
        Assert.False(e.Contains(10.01, 0, 0));
        Assert.True(new LayerExtent(0, 1, Array.Empty<ExtentBox>()).Contains(99999, 0.5, -99999)); // no bounds = whole map
    }

    [Fact]
    public void Parses_extents_straight_from_json()
    {
        var json = """
        [{"normalizedName":"x","maps":[{"key":"x","projection":"interactive","transform":[1,0,1,0],"bounds":[[0,0],[10,10]],"svgPath":"a.svg",
          "layers":[{"name":"Up","svgLayer":"U","show":true,"extents":[{"height":[10,15]},{"height":[5,10],"bounds":[[[-190,222],[-162,237.5],"accountant"]]}]},
                    {"name":"NoExtents","svgLayer":"N"}]}]}]
        """;
        var d = MapDefinitionParser.Parse(json).Single();
        var up = d.Layers[0];
        Assert.True(up.Show);
        Assert.Equal(2, up.Extents!.Count);
        Assert.Empty(up.Extents[0].Boxes);
        Assert.Equal(237.5, up.Extents[1].Boxes[0].Z2);
        Assert.Null(d.Layers[1].Extents);
        Assert.Null(AutoLayer.Match(d, new Vec3(0, 0, 0)));
        Assert.Equal("Up", AutoLayer.Match(d, new Vec3(0, 12, 0))!.Name);
        Assert.Equal("Up", AutoLayer.Match(d, new Vec3(-170, 7, 230))!.Name);
        Assert.Null(AutoLayer.Match(d, new Vec3(0, 7, 0)));
    }

    [Theory]
    // Reserve
    [InlineData("reserve", 60, -10, -100, "Bunkers")]          // storage bunker
    [InlineData("reserve", -100, -8, 50, "Bunkers")]           // command bunkers (under the pawns)
    [InlineData("reserve", -80, -20, 150, "Bunkers")]          // D2
    [InlineData("reserve", 50, -5, -170, "Bunkers")]           // hermetic door band
    [InlineData("reserve", 60, 2, -100, null)]                 // above the storage bunker = surface
    [InlineData("reserve", -100, -2, 50, "2nd Floor")]         // pawns 2nd
    [InlineData("reserve", -100, 1, 50, "3rd Floor")]
    [InlineData("reserve", -100, 3, 50, "4th Floor")]
    [InlineData("reserve", -100, 7, 50, "5th Floor")]
    [InlineData("reserve", -8, 24, 180, "2nd Floor")]          // dome
    [InlineData("reserve", -8, 10, 180, "Bunkers")]            // dome tunnels (height < 18)
    // Customs
    [InlineData("customs", 105, -2, -50, "Underground")]       // boiler room
    [InlineData("customs", 340, -3, -60, "Underground")]       // switch basement
    [InlineData("customs", 105, 1, -50, null)]
    [InlineData("customs", 105, 5, -50, "2nd Floor")]          // boiler 2nd
    [InlineData("customs", 105, 8, -50, "3rd Floor")]          // boiler 3rd
    [InlineData("customs", 200, 8, 150, "3rd Floor")]          // dorms 3rd
    [InlineData("customs", 200, 4, 150, "2nd Floor")]          // dorms 2nd
    [InlineData("customs", 260, 20, -40, "4th Floor")]         // oil rig top
    [InlineData("customs", 0, -5, 0, null)]                    // low ground outside any basement box
    // Factory (no bounds: height only, map-wide)
    [InlineData("factory", 0, -3, 0, "Tunnels")]
    [InlineData("factory", 0, -1, 0, null)]                    // Tunnels' max is exclusive
    [InlineData("factory", 0, 1, 0, null)]
    [InlineData("factory", 0, 3, 0, "2nd Floor")]              // 2nd Floor's min is inclusive
    [InlineData("factory", 0, 7, 0, "3rd Floor")]
    // Ground Zero
    [InlineData("ground-zero", 80, 15, 0, "Garage")]
    [InlineData("ground-zero", 130, 15, 60, "Garage")]         // underpass
    [InlineData("ground-zero", 80, 24, 0, null)]
    [InlineData("ground-zero", 0, 30, 0, "2nd Floor")]
    [InlineData("ground-zero", 0, 40, 0, "3rd Floor")]
    // Streets
    [InlineData("streets-of-tarkov", 0, -8, 0, "Underground")]
    [InlineData("streets-of-tarkov", 20, -1, 10, "Underground")]  // center parking (height < 0)
    [InlineData("streets-of-tarkov", 200, 1, 380, "Underground")] // Concordia (height < 2)
    [InlineData("streets-of-tarkov", 0, 0, 0, null)]
    [InlineData("streets-of-tarkov", 0, 12, 0, "2nd Floor")]
    [InlineData("streets-of-tarkov", -175, 7, 230, "2nd Floor")]  // accountant building
    [InlineData("streets-of-tarkov", 0, 30, 0, "5th Floor")]
    // Shoreline
    [InlineData("shoreline", -180, -8, -80, "Underground")]    // west wing
    [InlineData("shoreline", -250, -8, -150, "Underground")]   // admin
    [InlineData("shoreline", -180, -3, -80, null)]
    [InlineData("shoreline", 300, -30, 0, null)]               // low ground elsewhere is the surface
    [InlineData("shoreline", 0, 0, 0, "2nd Floor")]
    [InlineData("shoreline", 0, 5, 0, "3rd Floor")]
    // Labs
    [InlineData("the-lab", -200, -3, -350, "Technical")]
    [InlineData("the-lab", -200, 5, -350, "Second Level")]
    [InlineData("the-lab", -50, 5, -350, null)]                // outside the Second Level box
    [InlineData("the-lab", -200, 0, -350, null)]
    // Interchange
    [InlineData("interchange", 0, 30, 0, "2nd Floor")]
    [InlineData("interchange", 0, 40, 0, "3rd Floor")]
    [InlineData("interchange", 0, 22, 0, null)]
    public void Matches_like_tarkov_dev(string key, double x, double y, double z, string? layer)
    {
        Assert.Equal(layer, AutoLayer.Match(D(key), new Vec3(x, y, z))?.Name);
        Assert.Equal(layer ?? AutoLayer.Main, AutoLayer.Target(D(key), new Vec3(x, y, z), AutoLayerMode.All));
    }

    [Theory]
    [InlineData("reserve", "Bunkers", true), InlineData("reserve", "2nd Floor", false), InlineData("reserve", "5th Floor", false)]
    [InlineData("customs", "Underground", true), InlineData("customs", "4th Floor", false)]
    [InlineData("factory", "Tunnels", true), InlineData("factory", "2nd Floor", false)]
    [InlineData("ground-zero", "Garage", true), InlineData("ground-zero", "3rd Floor", false)]
    [InlineData("streets-of-tarkov", "Underground", true), InlineData("streets-of-tarkov", "2nd Floor", false)]
    [InlineData("shoreline", "Underground", true), InlineData("shoreline", "2nd Floor", false)]
    [InlineData("the-lab", "Technical", true), InlineData("the-lab", "Second Level", false)]
    [InlineData("interchange", "2nd Floor", false)]
    [InlineData("icebreaker", "Control Room", true), InlineData("icebreaker", "Engine Room", true), InlineData("icebreaker", "Storage/Security", true)]
    [InlineData("icebreaker", "Helipad", false), InlineData("icebreaker", "Accommodation (lower)", false), InlineData("icebreaker", "Infirmary", false)]
    public void Classifies_underground_layers(string key, string layer, bool underground) =>
        Assert.Equal(underground, AutoLayer.IsUnderground(D(key), D(key).Layers.Single(l => l.Name == layer)));

    [Fact]
    public void Modes_all_underground_off()
    {
        var reserve = D("reserve");
        var bunker = new Vec3(60, -10, -100);
        var pawns2 = new Vec3(-100, -2, 50);
        var surface = new Vec3(60, 2, -100);
        Assert.Equal("Bunkers", AutoLayer.Target(reserve, bunker, AutoLayerMode.All));
        Assert.Equal("Bunkers", AutoLayer.Target(reserve, bunker, AutoLayerMode.Underground));
        Assert.Equal("2nd Floor", AutoLayer.Target(reserve, pawns2, AutoLayerMode.All));
        Assert.Equal("Main", AutoLayer.Target(reserve, pawns2, AutoLayerMode.Underground));
        Assert.Equal("Main", AutoLayer.Target(reserve, surface, AutoLayerMode.All));
        Assert.Equal("Main", AutoLayer.Target(reserve, surface, AutoLayerMode.Underground));
        Assert.Null(AutoLayer.Target(reserve, bunker, AutoLayerMode.Off));
    }

    [Fact]
    public void Decide_picks_the_floor_choice_and_returns_to_main()
    {
        var d = D("reserve");
        var choices = MapLayerChoice.For(d);
        var b = AutoLayer.Decide(d, choices, new Vec3(60, -10, -100), AutoLayerMode.All, "fix1", null);
        Assert.Equal("Bunkers", b!.Name);
        var m = AutoLayer.Decide(d, choices, new Vec3(60, 2, -100), AutoLayerMode.All, "fix2", "fix1");
        Assert.True(m!.IsMain);
        Assert.Null(AutoLayer.Decide(d, choices, new Vec3(60, -10, -100), AutoLayerMode.Off, "fix3", "fix2"));
    }

    [Fact]
    public void A_hand_picked_floor_stays_until_a_newer_screenshot()
    {
        var d = D("reserve");
        var choices = MapLayerChoice.For(d);
        var p = new Vec3(60, -10, -100);
        Assert.NotNull(AutoLayer.Decide(d, choices, p, AutoLayerMode.All, "shot1", null));
        // the user then picks 3rd Floor by hand; redraws for the same screenshot change nothing
        Assert.Null(AutoLayer.Decide(d, choices, p, AutoLayerMode.All, "shot1", "shot1"));
        // a newer screenshot switches again
        Assert.Equal("Bunkers", AutoLayer.Decide(d, choices, p, AutoLayerMode.All, "shot2", "shot1")!.Name);
    }

    [Fact]
    public void Icebreaker_infirmary_is_the_base_map()
    {
        var d = D("icebreaker");
        var choices = MapLayerChoice.For(d);
        Assert.Equal("Infirmary", AutoLayer.Match(d, new Vec3(0, 19, 0))!.Name);
        Assert.True(AutoLayer.Decide(d, choices, new Vec3(0, 19, 0), AutoLayerMode.All, "a", null)!.IsMain);
        Assert.Equal("Helipad", AutoLayer.Decide(d, choices, new Vec3(0, 20, 0), AutoLayerMode.All, "b", "a")!.Name);
        Assert.Equal("Control Room", AutoLayer.Decide(d, choices, new Vec3(0, 1, 0), AutoLayerMode.Underground, "c", "b")!.Name);
        Assert.True(AutoLayer.Decide(d, choices, new Vec3(0, 20, 0), AutoLayerMode.Underground, "d", "c")!.IsMain);
    }

    [Fact]
    public void Style_returns_to_satellite_on_main_and_vector_only_floors_are_flagged()
    {
        var shore = D("shoreline");
        var choices = MapLayerChoice.For(shore);
        var under = AutoLayer.Decide(shore, choices, new Vec3(-180, -8, -80), AutoLayerMode.All, "1", null)!;
        Assert.Equal("Underground", under.Name);
        Assert.Equal(MapStyle.Vector, MapStyles.Effective(shore, MapStyle.Satellite, under));
        var main = AutoLayer.Decide(shore, choices, new Vec3(-180, -3, -80), AutoLayerMode.All, "2", "1")!;
        Assert.True(main.IsMain);
        Assert.Equal(MapStyle.Satellite, MapStyles.Effective(shore, MapStyle.Satellite, main));
        // Reserve Bunkers has both
        var r = D("reserve");
        var bunkers = AutoLayer.Decide(r, MapLayerChoice.For(r), new Vec3(60, -10, -100), AutoLayerMode.All, "1", null)!;
        Assert.Equal(MapStyle.Satellite, MapStyles.Effective(r, MapStyle.Satellite, bunkers));
        Assert.Equal(MapStyle.Vector, MapStyles.Effective(r, MapStyle.Vector, bunkers));
    }

    [Theory]
    [InlineData("customs"), InlineData("reserve"), InlineData("factory"), InlineData("ground-zero"), InlineData("streets-of-tarkov"), InlineData("shoreline"), InlineData("the-lab")]
    public void Every_multi_level_map_has_an_underground_floor_to_switch_to(string key)
    {
        var d = D(key);
        var choices = MapLayerChoice.For(d);
        Assert.Contains(choices, c => !c.IsMain && AutoLayer.IsUnderground(d, d.Layers.Single(l => l.Name == c.Name)));
    }

    [Fact]
    public void Setting_defaults_to_all_persists_and_normalises()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autolayer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "map-settings.json");
            var st = new MapSettingsStore(file);
            Assert.Equal("all", st.Settings.MapAutoLayerMode);
            Assert.Equal(AutoLayerMode.All, st.Settings.AutoLayer);
            st.Settings.AutoLayer = AutoLayerMode.Underground;
            st.Save();
            Assert.Equal(AutoLayerMode.Underground, new MapSettingsStore(file).Settings.AutoLayer);
            st.Settings.MapAutoLayerMode = "off"; st.Save();
            Assert.Equal("off", new MapSettingsStore(file).Settings.MapAutoLayerMode);
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"off\"", "\"bogus\""));
            Assert.Equal("all", new MapSettingsStore(file).Settings.MapAutoLayerMode);
            Assert.DoesNotContain("AutoLayer\"", File.ReadAllText(file)); // computed property isn't saved
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("ALL", AutoLayerMode.All), InlineData("underground", AutoLayerMode.Underground), InlineData(" Off ", AutoLayerMode.Off), InlineData(null, AutoLayerMode.All)]
    public void Parses_mode_ids(string? id, AutoLayerMode mode) => Assert.Equal(mode, AutoLayer.ParseMode(id));
}
