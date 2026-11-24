using TarkovOverlay.Maps.Position;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

public class ScreenshotTests
{
    [Fact]
    public void Parses_documented_example()
    {
        var p = ScreenshotName.TryParse("2024-01-01[12-00]_123.45, 6.78, -90.12_0.0, 0.7, 0.0, 0.7_12.34 (0).png");
        Assert.NotNull(p);
        Assert.Equal(123.45, p!.Position.X, 6);
        Assert.Equal(6.78, p.Position.Y, 6);
        Assert.Equal(-90.12, p.Position.Z, 6);
        Assert.Equal(90, p.YawDegrees, 3);
        Assert.Equal(new DateTime(2024, 1, 1, 12, 0, 0), p.TakenLocal);
    }

    [Theory]
    [InlineData("2023-11-13[17-27]_-185.6, 2.3, 113.7_0.00000, -0.81640, 0.00000, 0.57749_14.40 (0).png", -185.6, 113.7, 250.55)]
    [InlineData("2025-02-03[21-05]_311.19, -1.63, -174.95_0.01234, 0.38268, -0.00512, 0.92388_9.87 (1).png", 311.19, -174.95, 45.0)]
    [InlineData("2025-02-03[21-05]-12.00, 0.50, 7.25_0.0, 1.0, 0.0, 0.0 (0).png", -12.0, 7.25, 180.0)]
    [InlineData("2025-02-03[21-05]_1.00, 2.00, 3.00_0.0, 0.0, 0.0, 1.0.png", 1.0, 3.0, 0.0)]
    public void Parses_variants(string name, double x, double z, double yaw)
    {
        var p = ScreenshotName.TryParse(@"C:\Users\Player\Documents\Escape from Tarkov\Screenshots\" + name);
        Assert.NotNull(p);
        Assert.Equal(x, p!.Position.X, 6);
        Assert.Equal(z, p.Position.Z, 6);
        Assert.InRange(p.YawDegrees, yaw - 0.1, yaw + 0.1);
    }

    [Theory]
    [InlineData("2024-01-01[12-00] (0).png")]           // hideout / menu screenshot without coordinates
    [InlineData("screenshot.png")]
    [InlineData("2024-01-01[12-00]_1.0, 2.0_0, 0, 0, 1 (0).png")]
    [InlineData("2024-01-01[12-00]_1.0, 2.0, 3.0_0.0, 0.0, 0.0, 1.0 (0).txt")]
    public void Ignores_non_position_files(string name) => Assert.Null(ScreenshotName.TryParse(name));

    [Fact]
    public void Watcher_reports_new_screenshot()
    {
        var dir = Directory.CreateTempSubdirectory("eftshots");
        try
        {
            PositionFix? fix = null;
            using var w = new ScreenshotWatcher(f => fix = f);
            Assert.True(w.Start(dir.FullName));
            var file = Path.Combine(dir.FullName, "2025-02-03[21-05]_311.19, -1.63, -174.95_0.0, 0.0, 0.0, 1.0_9.87 (0).png");
            File.WriteAllBytes(file, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            Assert.True(SpinWait.SpinUntil(() => fix != null, 5000));
            Assert.Equal(311.19, fix!.Data.Position.X, 6);
            Assert.True(File.Exists(file)); // the watcher itself never deletes (ScreenshotCleaner does)
        }
        finally { dir.Delete(true); }
    }
}
