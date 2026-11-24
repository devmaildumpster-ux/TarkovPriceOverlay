using TarkovOverlay.Core;
using TarkovOverlay.Core.Geometry;
using TarkovOverlay.Core.Settings;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>Auto-read on hover: dwell/trigger state machine and bounded tooltip wait (pure logic, simulated clock).</summary>
public class AutoReadTests
{
    static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Feeds positions every 40 ms and returns the times (ms) at which StartProbe fired.</summary>
    static List<int> Run(HoverDwellTracker t, Func<int, (int, int)> pos, int untilMs, Func<int, bool>? fg = null, Func<int, bool>? busy = null)
    {
        var fired = new List<int>();
        for (int ms = 0; ms <= untilMs; ms += 40)
            if (t.Tick(Ms(ms), pos(ms), fg?.Invoke(ms) ?? true, busy?.Invoke(ms) ?? false) == DwellAction.StartProbe) fired.Add(ms);
        return fired;
    }

    [Fact]
    public void FiresOnceAfterDwell_ThenNotAgainAtSamePosition()
    {
        var t = new HoverDwellTracker(Ms(350), 6);
        var fired = Run(t, _ => (500, 400), 3000);
        Assert.Single(fired);
        Assert.InRange(fired[0], 350, 400);
    }

    [Fact]
    public void ContinuousMovement_NeverFires_AndJitterWithinToleranceStillCounts()
    {
        var t = new HoverDwellTracker(Ms(350), 6);
        Assert.Empty(Run(t, ms => (100 + ms / 4, 300), 2000)); // 10 px per tick
        var t2 = new HoverDwellTracker(Ms(350), 6);
        Assert.Single(Run(t2, ms => (500 + (ms / 40 % 2) * 3, 400 - (ms / 40 % 3)), 1000)); // ±3 px jitter
    }

    [Fact]
    public void SlowDriftBeyondTolerance_Rearms_AndFiresForTheNewSpot()
    {
        var t = new HoverDwellTracker(Ms(350), 6);
        // still at A until 1000 ms, then jump to B (next item) and rest
        var fired = Run(t, ms => ms < 1000 ? (500, 400) : (564, 400), 2500);
        Assert.Equal(2, fired.Count);
        Assert.True(fired[1] >= 1000 + 350);
    }

    [Fact]
    public void GameNotForeground_NeverFires_AndReturningNeedsFreshDwell()
    {
        var t = new HoverDwellTracker(Ms(350), 6);
        var fired = Run(t, _ => (500, 400), 2000, fg: ms => ms >= 1000);
        Assert.Single(fired);
        Assert.InRange(fired[0], 1000 + 350, 1000 + 400);
    }

    [Fact]
    public void BusyLookup_DefersFiring_UntilFree()
    {
        var t = new HoverDwellTracker(Ms(350), 6);
        var fired = Run(t, _ => (500, 400), 2000, busy: ms => ms < 800);
        Assert.Single(fired);
        Assert.InRange(fired[0], 800, 840);
    }

    [Fact]
    public void Reset_AllowsAnotherReadAtSamePosition()
    {
        var t = new HoverDwellTracker(Ms(350), 6);
        Assert.Single(Run(t, _ => (500, 400), 1000));
        t.Reset();
        Assert.Single(Run(t, _ => (500, 400), 1000));
    }

    [Fact]
    public async Task TooltipWait_FoundOnThirdProbe_TimesOut_OrStopsOnMove()
    {
        int now = 0;
        Func<TimeSpan> clock = () => Ms(now);
        Func<TimeSpan, Task> delay = d => { now += (int)d.TotalMilliseconds; return Task.CompletedTask; };

        int calls = 0;
        var found = await TooltipWait.RunAsync(() => ++calls >= 3, () => false, Ms(50), Ms(600), clock, delay);
        Assert.Equal(TooltipWait.Outcome.Found, found.Result);
        Assert.Equal(3, found.Probes);

        now = 0;
        var timeout = await TooltipWait.RunAsync(() => false, () => false, Ms(50), Ms(600), clock, delay);
        Assert.Equal(TooltipWait.Outcome.TimedOut, timeout.Result);
        Assert.InRange(timeout.Probes, 11, 13);
        Assert.True(timeout.Waited <= Ms(600));

        now = 0; int k = 0;
        var moved = await TooltipWait.RunAsync(() => false, () => ++k > 2, Ms(50), Ms(600), clock, delay);
        Assert.Equal(TooltipWait.Outcome.Moved, moved.Result);
        Assert.Equal(2, moved.Probes);
    }

    [Fact]
    public void AutoReadSettings_DefaultOn_Persist_AndSanitize()
    {
        var root = Path.Combine(Path.GetTempPath(), "tpo-" + Guid.NewGuid());
        var paths = new AppPaths(root);
        var s = new SettingsStore(paths).Load();
        Assert.True(s.AutoReadEnabled);
        Assert.Equal(350, s.EffectiveAutoDwellMs);
        Assert.Equal(600, s.EffectiveAutoMaxTooltipWaitMs);
        s.AutoReadEnabled = false; s.AutoDwellMs = 500; s.AutoMaxTooltipWaitMs = 900;
        new SettingsStore(paths).Save(s);
        var r = new SettingsStore(paths).Load();
        Assert.False(r.AutoReadEnabled);
        Assert.Equal(500, r.EffectiveAutoDwellMs);
        Assert.Equal(900, r.EffectiveAutoMaxTooltipWaitMs);
        File.WriteAllText(paths.SettingsFile, """{"AutoReadEnabled":true,"AutoDwellMs":5,"AutoMaxTooltipWaitMs":99999}""");
        var bad = new SettingsStore(paths).Load();
        Assert.Equal(350, bad.EffectiveAutoDwellMs);
        Assert.Equal(600, bad.EffectiveAutoMaxTooltipWaitMs);
        File.WriteAllText(paths.SettingsFile, """{"SchemaVersion":1,"ModeId":"pve"}"""); // 1.0.2 settings file
        Assert.True(new SettingsStore(paths).Load().AutoReadEnabled);
        Directory.Delete(root, true);
    }
}

public class HoverSnapshotTimingTests
{
    [Fact]
    public void Tracker_ExposesAnchorAndStillTime_ForThePreTooltipSnapshot()
    {
        var t = new TarkovOverlay.Core.Geometry.HoverDwellTracker(TimeSpan.FromMilliseconds(350), 6);
        t.Tick(TimeSpan.Zero, (100, 100), true, false);
        Xunit.Assert.Equal((100, 100), t.Anchor);
        t.Tick(TimeSpan.FromMilliseconds(120), (102, 101), true, false);
        Xunit.Assert.Equal(TimeSpan.FromMilliseconds(120), t.StillFor(TimeSpan.FromMilliseconds(120)));
        t.Tick(TimeSpan.FromMilliseconds(160), (140, 100), true, false); // moved: new anchor, timer restarts
        Xunit.Assert.Equal((140, 100), t.Anchor);
        Xunit.Assert.Equal(TimeSpan.Zero, t.StillFor(TimeSpan.FromMilliseconds(160)));
        t.Tick(TimeSpan.FromMilliseconds(200), (140, 100), false, false); // left the game
        Xunit.Assert.Null(t.Anchor);
    }
}
