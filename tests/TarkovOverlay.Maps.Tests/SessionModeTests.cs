using TarkovOverlay.Core.Settings;
using TarkovOverlay.Maps.Logs;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

/// <summary>1.2.0: auto PvP/PvE from Tarkov's log ("Session mode: …" written when the game connects).</summary>
public class SessionModeTests
{
    [Theory]
    [InlineData("2025-07-29 03:54:40.001 +02:00|0.16.8.1.38114|Info|application|Session mode: Pve\n", "pve")]
    [InlineData("2025-07-29 03:54:40.001 +02:00|0.16.8.1.38114|Info|application|Session mode: Regular\n", "regular")]
    [InlineData("2025-07-29 03:54:40.001 +02:00|0.16.8.1.38114|Info|application|Session mode: pvpseason\n", "pvp-season")]
    [InlineData("2026-01-02 10:00:00.000 +00:00|1.0.0.1.40000|Info|application|Session mode: PVE|\n", "pve")]
    [InlineData("17:56:53.884|Info|application|Session mode: Regular\n", "regular")]
    public void Parses(string line, string mode)
    {
        var e = Assert.Single(new EftLogParser().Feed(line));
        Assert.Equal(LogEventKind.SessionMode, e.Kind);
        Assert.Equal(mode, e.Value);
    }

    [Theory]
    [InlineData("2025-07-29 03:54:40.001 +02:00|0.16.8.1.38114|Info|application|Session mode: Arena\n")]
    [InlineData("2025-07-29 03:54:40.001 +02:00|0.16.8.1.38114|Info|application|Session mode: \n")]
    [InlineData("2025-07-29 03:54:40.001 +02:00|0.16.8.1.38114|Info|application|Session started\n")]
    public void IgnoresUnknown(string line) =>
        Assert.DoesNotContain(new EftLogParser().Feed(line), e => e.Kind == LogEventKind.SessionMode);

    [Fact]
    public void Log_To_AppMode_EndToEnd()
    {
        // the player relaunches in PvE after a PvP session; the last line wins
        var log = "2025-07-29 03:00:00.000 +02:00|0.16|Info|application|Session mode: Regular\n" +
                  "2025-07-29 04:00:00.000 +02:00|0.16|Info|application|Session mode: Pve\n";
        var raid = new RaidTracker();
        string? last = null;
        foreach (var e in new EftLogParser().Feed(log)) { raid.Apply(e); if (e.Kind == LogEventKind.SessionMode) last = e.Value; }
        Assert.Equal("pve", raid.SessionMode);
        Assert.Equal("pve", ModeAutoSwitch.Decide(true, "regular", last).SwitchTo);
        Assert.Null(ModeAutoSwitch.Decide(true, "pve", last).SwitchTo);
        Assert.Null(ModeAutoSwitch.Decide(false, "regular", last).SwitchTo);
    }
}
