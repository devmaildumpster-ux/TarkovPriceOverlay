using TarkovOverlay.Core.Lifetime;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>1.0.5 bug: the process stayed in Task Manager after the main window closed.</summary>
public class ShutdownTests
{
    [Fact]
    public void Sequence_RunsEveryStep_EvenAfterOneThrows_AndLogsEach()
    {
        var log = new List<string>();
        var ran = new List<string>();
        var seq = new ShutdownSequence(log.Add);
        seq.Run("a", () => ran.Add("a"));
        seq.Run("b", () => throw new InvalidOperationException("boom"));
        seq.Run("c", () => ran.Add("c"));
        Assert.Equal(new[] { "a", "c" }, ran);
        Assert.Equal(3, log.Count);
        Assert.Contains("b FAILED", log[1]);
        Assert.Contains("boom", log[1]);
        Assert.Equal(new[] { true, false, true }, seq.Results.Select(r => r.Ok));
    }

    [Fact]
    public void Watchdog_IsABackgroundThread_SoItNeverKeepsTheProcessAlive()
    {
        using var w = ExitWatchdog.Start(TimeSpan.FromMinutes(5), () => { });
        Assert.True(w.Thread.IsBackground);
        w.Cancel();
        Assert.True(w.Thread.Join(2000));
        Assert.False(w.Fired);
    }

    [Fact]
    public void Watchdog_FiresAfterTimeout_WhenNotCancelled()
    {
        using var fired = new ManualResetEventSlim();
        var w = ExitWatchdog.Start(TimeSpan.FromMilliseconds(100), fired.Set);
        Assert.True(fired.Wait(3000));
        Assert.True(w.Fired);
    }

    [Fact]
    public void Watchdog_CancelledBeforeTimeout_DoesNotFire()
    {
        int calls = 0;
        var w = ExitWatchdog.Start(TimeSpan.FromMilliseconds(300), () => Interlocked.Increment(ref calls));
        w.Cancel();
        Thread.Sleep(600);
        Assert.Equal(0, calls);
        Assert.False(w.Fired);
    }

    [Fact]
    public void Tesseract_IdleRelease_FreesTheEngine_AndItReloadsOnDemand()
    {
        var e = TesseractTests.Engine.Value;
        if (e == null) return; // native libs not present on this machine
        Assert.True(e.Warmup(out var err), err);
        Assert.True(e.IsLoaded);
        Assert.False(e.ReleaseIfIdle(TimeSpan.FromHours(1)));
        Assert.True(e.IsLoaded);
        Assert.True(e.ReleaseIfIdle(TimeSpan.Zero));
        Assert.False(e.IsLoaded);
        Assert.True(e.Warmup(out err), err);
        Assert.True(e.IsLoaded);
    }
}
