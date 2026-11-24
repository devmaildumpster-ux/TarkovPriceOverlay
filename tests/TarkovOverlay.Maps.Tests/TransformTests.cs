using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Definitions;
using TarkovOverlay.Maps.Geometry;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

public class TransformTests
{
    private static readonly List<MapDefinition> Defs = MapDataService.LoadFallbackDefinitions();
    private static MapDefinition Def(string key) => Defs.First(d => d.Key == key);

    // Expected values computed with tarkov-dev's getCRS/applyRotation/L.Transformation formula at zoom 0.
    [Theory]
    [InlineData("customs", -336.33, -86.39, 249.032870, 115.702790)]   // Crossroads (PMC)
    [InlineData("customs", 489.06, 221.39, 51.764660, 189.262210)]     // Railroad to Reserve
    [InlineData("factory", 10.0, -20.0, 152.48, 123.01)]               // rotation 90
    [InlineData("the-lab", -200.0, -350.0, 79.95, 78.70)]              // rotation 270, tile map
    [InlineData("woods", -389.16, 11.04, 185.139180, 169.897920)]      // ZB-016
    [InlineData("lighthouse", 100, -200, -20.0, -40.0)]                // transform margins 0
    public void Projects_like_tarkov_dev(string key, double x, double z, double px, double py)
    {
        var p = Def(key).Projection.ToMap(x, z);
        Assert.Equal(px, p.X, 6);
        Assert.Equal(py, p.Y, 6);
    }

    [Theory]
    [InlineData("customs")] [InlineData("factory")] [InlineData("the-lab")] [InlineData("woods")] [InlineData("interchange")]
    public void Inverse_round_trips(string key)
    {
        var t = Def(key).Projection;
        foreach (var (x, z) in new[] { (0.0, 0.0), (123.4, -56.7), (-300.0, 250.0) })
        {
            var (gx, gz) = t.ToGame(t.ToMap(x, z));
            Assert.Equal(x, gx, 9);
            Assert.Equal(z, gz, 9);
        }
    }

    [Fact]
    public void Customs_extracts_fall_inside_the_map_bounds()
    {
        var def = Def("customs");
        // tarkov.dev's bounds are hand-drawn and a few exits sit just past the edge (Railroad Passage), so allow 10 %.
        var rect = def.BoundsRect.Inflate(0.10);
        var customs = Fx.Maps.ByNormalizedName("customs")!;
        Assert.True(customs.Extracts.Count > 10);
        foreach (var e in customs.Extracts) Assert.True(rect.Contains(def.Projection.ToMap(e.Position)), $"{e.Name} {e.Position} → {def.Projection.ToMap(e.Position)} outside {rect}");
    }

    [Fact]
    public void Bounds_rect_matches_svg_aspect_for_customs()
    {
        // Customs.svg viewBox is 1062.48 × 535.17; the overlay rectangle has nearly the same aspect, and FitMeet centres it.
        var r = Def("customs").SvgRect;
        Assert.Equal(1.967, r.Width / r.Height, 2);
        var fit = MapTransform.FitMeet(r, 1062.4827, 535.17401);
        Assert.True(fit.Width <= r.Width + 1e-9 && fit.Height <= r.Height + 1e-9);
        Assert.Equal(r.X + r.Width / 2, fit.X + fit.Width / 2, 9);
        Assert.Equal(r.Y + r.Height / 2, fit.Y + fit.Height / 2, 9);
    }

    [Theory]
    // Customs is rotated 180°: facing +Z (yaw 0) points down the screen, +X (yaw 90) points left — same as tarkov.dev's
    // player icon (yaw + coordinateRotation).
    [InlineData("customs", 0, 180)]
    [InlineData("customs", 90, 270)]
    [InlineData("customs", 180, 0)]
    // Rotation 90/270 maps: tarkov.dev adds a further 180° (yaw + rotation + 180).
    [InlineData("the-lab", 0, 90)]
    [InlineData("factory", 0, 270)]
    [InlineData("factory", 90, 0)]
    public void Heading_matches_tarkov_dev_icon_rotation(string key, double yaw, double expected)
    {
        var def = Def(key);
        var h = def.Projection.ScreenHeading(10, 10, yaw);
        Assert.Equal(expected, h, 6);
        var site = MapTransform.Normalize(yaw + def.CoordinateRotation + (def.CoordinateRotation is 90 or 270 ? 180 : 0));
        Assert.Equal(site, h, 6);
    }

    [Theory]
    [InlineData(0, 0, 0, 1, 0)]
    [InlineData(0, 0.70711, 0, 0.70711, 90)]
    [InlineData(0, 1, 0, 0, 180)]
    [InlineData(0, -0.70711, 0, 0.70711, 270)]
    [InlineData(0, 0.38268, 0, 0.92388, 45)]
    public void Yaw_from_quaternion(double x, double y, double z, double w, double yaw)
    {
        Assert.Equal(yaw, Rotation.YawFromQuaternion(x, y, z, w), 2);
    }

    [Theory]
    [InlineData("night-factory", "factory")]
    [InlineData("ground-zero-21", "ground-zero")]
    [InlineData("ground-zero-tutorial", "ground-zero")]
    [InlineData("the-lab-dark", "the-lab")]
    [InlineData("customs", "customs")]
    [InlineData("streets-of-tarkov", "streets-of-tarkov")]
    public void Resolves_alternate_maps_to_drawings(string apiName, string key)
    {
        Assert.Equal(key, MapDefinitionParser.Resolve(Defs, apiName)?.Key);
    }

    [Fact]
    public void Fallback_definitions_cover_every_playable_map()
    {
        foreach (var k in new[] { "customs", "factory", "woods", "shoreline", "interchange", "reserve", "lighthouse", "streets-of-tarkov", "the-lab", "ground-zero", "terminal", "the-labyrinth", "icebreaker" })
            Assert.Contains(Defs, d => d.Key == k);
        Assert.All(Defs, d => Assert.True(d.HasSvg || d.HasTiles));
    }
}
