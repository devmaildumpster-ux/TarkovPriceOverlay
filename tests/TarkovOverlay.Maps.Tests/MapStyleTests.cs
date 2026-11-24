using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Definitions;
using TarkovOverlay.Maps.Geometry;
using TarkovOverlay.Maps.Settings;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

/// <summary>1.2.1: Satellite (tiles) vs Vector (SVG) choice, floors, and tile placement vs the marker projection.</summary>
public class MapStyleTests
{
    private static readonly List<MapDefinition> Defs = MapDataService.LoadFallbackDefinitions();
    private static MapDefinition D(string key) => Defs.Single(d => d.Key == key);

    [Theory]
    [InlineData("customs"), InlineData("woods"), InlineData("shoreline"), InlineData("interchange"), InlineData("reserve"), InlineData("factory"), InlineData("ground-zero")]
    public void Dual_maps_have_both_and_follow_the_preference(string key)
    {
        var d = D(key);
        Assert.True(MapStyles.HasBoth(d));
        Assert.Null(MapStyles.SingleModeReason(d, key));
        Assert.Equal(MapStyle.Satellite, MapStyles.Effective(d, MapStyle.Satellite));
        Assert.Equal(MapStyle.Vector, MapStyles.Effective(d, MapStyle.Vector));
    }

    [Theory]
    [InlineData("streets-of-tarkov", MapStyle.Vector), InlineData("lighthouse", MapStyle.Vector), InlineData("terminal", MapStyle.Vector)]
    [InlineData("the-lab", MapStyle.Satellite), InlineData("the-labyrinth", MapStyle.Satellite), InlineData("icebreaker", MapStyle.Satellite)]
    public void Single_style_maps_fall_back(string key, MapStyle only)
    {
        var d = D(key);
        Assert.False(MapStyles.HasBoth(d));
        Assert.Equal(only, MapStyles.Effective(d, MapStyle.Satellite));
        Assert.Equal(only, MapStyles.Effective(d, MapStyle.Vector));
        Assert.Contains(only == MapStyle.Vector ? "vector-only" : "satellite-only", MapStyles.SingleModeReason(d, key));
    }

    [Fact]
    public void Floors_use_tiles_in_satellite_and_svg_in_vector_with_fallback()
    {
        var customs = D("customs");
        var floors = MapLayerChoice.For(customs);
        Assert.Equal(new[] { "Main", "2nd Floor", "3rd Floor", "4th Floor", "Underground" }, floors.Select(f => f.Name));
        var second = floors.Single(f => f.Name == "2nd Floor");
        Assert.Equal("Second_Floor", second.SvgLayer);
        Assert.Contains("customs_0.16/2nd", second.TilePath);
        Assert.Equal(MapStyle.Satellite, MapStyles.Effective(customs, MapStyle.Satellite, second));
        Assert.Equal(MapStyle.Vector, MapStyles.Effective(customs, MapStyle.Vector, second));
        // 4th floor exists only as tiles → drawn as satellite even in Vector mode
        var fourth = floors.Single(f => f.Name == "4th Floor");
        Assert.Null(fourth.SvgLayer);
        Assert.Equal(MapStyle.Satellite, MapStyles.Effective(customs, MapStyle.Vector, fourth));
        // Interchange floors exist only in the SVG → vector even in Satellite mode
        var ic = D("interchange");
        var icFloor = MapLayerChoice.For(ic).Skip(1).First();
        Assert.Null(icFloor.TilePath);
        Assert.Equal(MapStyle.Vector, MapStyles.Effective(ic, MapStyle.Satellite, icFloor));
        // Icebreaker repeats its base tile set as the first floor: not listed twice
        var ib = MapLayerChoice.For(D("icebreaker"));
        Assert.Single(ib, f => f.TilePath == D("icebreaker").TilePath || f.IsMain);
        // Streets: SVG floors only
        Assert.All(MapLayerChoice.For(D("streets-of-tarkov")).Skip(1), f => Assert.Null(f.TilePath));
    }

    [Fact]
    public void Preference_defaults_to_satellite_and_persists()
    {
        Assert.True(new MapSettings().PreferSatelliteMap);
        Assert.Equal(MapStyle.Satellite, new MapSettings().PreferredStyle);
        var dir = Directory.CreateTempSubdirectory("tpo-style");
        try
        {
            var file = Path.Combine(dir.FullName, "map-settings.json");
            // a 1.2.0 file has no PreferSatelliteMap → satellite after the update
            File.WriteAllText(file, "{\"Schema\":2,\"ShowLabels\":true}");
            Assert.True(new MapSettingsStore(file).Settings.PreferSatelliteMap);
            var st = new MapSettingsStore(file);
            st.Settings.PreferSatelliteMap = false;
            st.Save();
            var back = new MapSettingsStore(file).Settings;
            Assert.False(back.PreferSatelliteMap);
            Assert.Equal(MapStyle.Vector, back.PreferredStyle);
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData("customs", 4), InlineData("woods", 4), InlineData("shoreline", 4), InlineData("interchange", 4), InlineData("reserve", 4),
     InlineData("factory", 4), InlineData("ground-zero", 4), InlineData("the-lab", 4), InlineData("icebreaker", 3)]
    public void Tile_zoom_is_sharp_but_bounded(string key, int z)
    {
        var g = TileGrid.Choose(D(key));
        Assert.Equal(z, g.Z);
        Assert.True(g.Pixels <= TileGrid.DefaultMaxPixels);
        Assert.True(Math.Max(g.PixelWidth, g.PixelHeight) >= 2500); // 1.2.0 used about 2000 px across
    }

    /// <summary>
    /// The view draws the stitched bitmap stretched into <see cref="TileGrid.Placement"/> and markers at
    /// Projection.ToMap. For every exit-like point the bitmap pixel under the marker must be exactly the pixel Leaflet
    /// would show there: tile = floor(px·2^z / ts), offset = px·2^z mod ts.
    /// </summary>
    [Theory]
    [InlineData("customs"), InlineData("ground-zero"), InlineData("woods"), InlineData("factory"), InlineData("reserve"), InlineData("the-lab")]
    public void Tile_placement_matches_leaflet_and_the_marker_projection(string key)
    {
        var d = D(key);
        var g = TileGrid.Choose(d);
        var place = g.Placement;
        var b = d.BoundsRect;
        Assert.True(place.X <= b.X + 1e-9 && place.Y <= b.Y + 1e-9 && place.Right >= b.Right - 1e-9 && place.Bottom >= b.Bottom - 1e-9);
        var rnd = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            // random game point inside the bounds
            double gx = d.Bounds[0][0] + (d.Bounds[1][0] - d.Bounds[0][0]) * rnd.NextDouble();
            double gz = d.Bounds[0][1] + (d.Bounds[1][1] - d.Bounds[0][1]) * rnd.NextDouble();
            var mp = d.Projection.ToMap(gx, gz);
            // where the view samples the bitmap under the marker
            double bx = (mp.X - place.X) / place.Width * g.PixelWidth, by = (mp.Y - place.Y) / place.Height * g.PixelHeight;
            var (ex, ey) = g.BitmapPixel(mp);
            Assert.Equal(ex, bx, 6); Assert.Equal(ey, by, 6);
            // Leaflet: which tile and where inside it
            double lx = mp.X * g.Scale, ly = mp.Y * g.Scale;
            var (tx, ty) = g.TileOf(mp);
            Assert.Equal(tx, g.X0 + (int)Math.Floor(bx / g.TileSize));
            Assert.Equal(ty, g.Y0 + (int)Math.Floor(by / g.TileSize));
            Assert.Equal(lx - tx * g.TileSize, bx - (tx - g.X0) * g.TileSize, 6);
            Assert.Equal(ly - ty * g.TileSize, by - (ty - g.Y0) * g.TileSize, 6);
            // and the projection round-trips (the same point the SVG view uses)
            var back = d.Projection.ToGame(mp);
            Assert.Equal(gx, back.X, 6); Assert.Equal(gz, back.Z, 6);
        }
    }

    /// <summary>
    /// The SVG is fitted into svgBounds with "xMidYMid meet" (like tarkov.dev's svgOverlay); the tiles cover bounds.
    /// Using the real viewBoxes of tarkov.dev's SVGs (Nov 2026), both pictures put a game point within a fraction
    /// of a zoom-0 pixel (≈2 m on Customs) of each other — the letterbox of the SVG, same as on tarkov.dev.
    /// </summary>
    [Theory]
    [InlineData("customs", 1062.4827, 535.17401), InlineData("ground-zero", 348.92543, 488.44792), InlineData("woods", 1472.7926, 1420.5995),
     InlineData("reserve", 827.28742, 761.16437), InlineData("factory", 130.81831, 141.23242), InlineData("shoreline", 1559.5717, 1032.4935)]
    public void Svg_and_tiles_agree_within_the_letterbox(string key, double vbW, double vbH)
    {
        var d = D(key);
        var svg = MapTransform.FitMeet(d.SvgRect, vbW, vbH);
        Assert.True(Math.Abs(svg.X - d.SvgRect.X) < 0.7 && Math.Abs(svg.Y - d.SvgRect.Y) < 0.7, $"letterbox {svg} vs {d.SvgRect}");
        Assert.True(Math.Abs(svg.Width - d.SvgRect.Width) < 1.4 && Math.Abs(svg.Height - d.SvgRect.Height) < 1.4);
        // markers use one projection for both styles: a point's zoom-0 position doesn't depend on the style
        var g = TileGrid.Choose(d);
        var p = d.Projection.ToMap((d.Bounds[0][0] + d.Bounds[1][0]) / 2, (d.Bounds[0][1] + d.Bounds[1][1]) / 2);
        Assert.True(g.Placement.Contains(p) && svg.Contains(p));
    }

    [Fact]
    public void Attribution_names_the_author()
    {
        Assert.Equal("Map © Tarkov.dev / Shebuka", D("customs").Attribution);
        Assert.Equal("Map © Tarkov.dev", D("the-lab").Attribution);
        Assert.Equal("Map © Tarkov.dev / TarkovBOT.eu", D("icebreaker").Attribution);
    }
}
