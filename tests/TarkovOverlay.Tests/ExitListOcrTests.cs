using System.IO.Compression;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Maps.Extracts;
using TarkovOverlay.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace TarkovOverlay.Tests;

/// <summary>
/// End-to-end OCR regression on synthetic 1920x1080 Shoreline exit-list captures
/// (tools/make_exit_list_fixtures.py): bright sky + treeline, green timed V-Ex row ("0:00:28"),
/// "?????" timer row, and orange transit row with requirement subline.
/// </summary>
public class ExitListOcrTests
{
    private readonly ITestOutputHelper _out;
    public ExitListOcrTests(ITestOutputHelper o) => _out = o;

    private static readonly (string Id, string Name)[] ShorelinePool =
    {
        ("1", "Road to North V-Ex"),
        ("2", "Road to Customs"),
        ("3", "Railway Bridge"),
        ("4", "Path to Lighthouse"),
        ("5", "Smugglers' Path (Co-op)"),
        ("6", "Pier Boat"),
        ("7", "Climber's Trail"),
        ("8", "Tunnel"),
        ("9", "Rock Passage"),
        ("10", "CCP Temporary"),
        ("11", "Mountain Bunker"),
        ("12", "Cliff Descent"),
        ("13", "Lighthouse"),
        ("14", "Admin Basement"),
        ("15", "Svetliy Dead End"),
        ("16", "Ruined Road"),
        ("17", "East Wing Gym Entrance"),
        ("18", "Ruined House Fence"),
        ("t1", "Transit to Lighthouse"),
        ("t4", "Transit to Icebreaker"),
    };

    private static RgbImage Load(string file)
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "exit-list", file)), CompressionMode.Decompress);
        return Ppm.Read(gz);
    }

    public static IEnumerable<object[]> Cases() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "exit-list", "manifest.tsv"))
            .Where(l => l.Length > 0).Select(l => l.Split('\t')).Select(p => new object[] { p[0], p[1], p[2] });

    [Fact]
    public async Task Raw_1x_capture_reproduces_missed_shoreline_vex_over_sky()
    {
        var engine = TesseractTests.Engine.Value;
        if (engine == null) { _out.WriteLine("Tesseract unavailable; skipped."); return; }

        var img = Load("shoreline_vex_sky_100.ppm.gz");
        var rawRead = await engine.RecognizeAsync(img, 1.0, CancellationToken.None);
        var rawMatches = ExitListMatcher.MatchLines(rawRead.Lines, ShorelinePool);
        _out.WriteLine($"Raw 1x lines ({rawRead.Lines.Count}): {string.Join(" | ", rawRead.Lines)}");
        Assert.DoesNotContain(rawMatches, m => m.ExtractName == "Road to North V-Ex");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Scaled_HudIsolated_exit_list_matches_all_shoreline_exfils_and_transits(string file, string mode, string expectedPipe)
    {
        var engine = TesseractTests.Engine.Value;
        if (engine == null) { _out.WriteLine("Tesseract unavailable; skipped."); return; }

        var img = Load(file);
        double scale = HudTextIsolator.ExitListScaleFor(img.Height);
        var isolated = HudTextIsolator.IsolateBrightHudText(img.Scale(scale));
        var read = await engine.RecognizeAsync(isolated, 1.0, CancellationToken.None);
        var ms = ExitListMatcher.MatchLines(read.Lines, ShorelinePool);
        _out.WriteLine($"{mode} ({read.Lines.Count} lines): {string.Join(" | ", read.Lines)}");
        _out.WriteLine($"  matched ({ms.Count}): {string.Join(", ", ms.Select(m => $"{m.ExtractName} ({m.Score:0.00})"))}");

        var expected = expectedPipe.Split('|');
        var matchedNames = ms.Select(m => m.ExtractName).ToHashSet();
        foreach (var name in expected) Assert.Contains(name, matchedNames);
        Assert.Equal(expected.Length, matchedNames.Count);
    }
}
