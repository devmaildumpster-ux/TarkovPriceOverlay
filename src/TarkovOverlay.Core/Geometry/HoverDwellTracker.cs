namespace TarkovOverlay.Core.Geometry;

public enum DwellAction { None, StartProbe }

/// <summary>
/// Pure state machine for "auto-read on hover". Fed with the cursor position every poll (~40 ms). It fires
/// <see cref="DwellAction.StartProbe"/> once the cursor has stayed within <c>tolerancePx</c> of where it settled
/// for at least the dwell time, while the game is in the foreground and no lookup is running. After firing
/// it stays quiet until the cursor moves beyond the tolerance (so the same item/position never re-triggers).
/// </summary>
public sealed class HoverDwellTracker
{
    private (int X, int Y)? _anchor;
    private TimeSpan _stillSince;
    private bool _consumed;

    public TimeSpan Dwell { get; set; }
    public int TolerancePx { get; set; }

    public HoverDwellTracker(TimeSpan dwell, int tolerancePx)
    {
        Dwell = dwell; TolerancePx = tolerancePx;
    }

    /// <summary>Why the last tick did not fire (for logging decisions); null when it fired or was simply moving/waiting.</summary>
    public string? LastSkip { get; private set; }
    public bool Consumed => _consumed;
    /// <summary>Where the cursor settled (null while not tracking).</summary>
    public (int X, int Y)? Anchor => _anchor;
    /// <summary>How long the cursor has rested at <see cref="Anchor"/>.</summary>
    public TimeSpan StillFor(TimeSpan now) => _anchor == null ? TimeSpan.Zero : now - _stillSince;

    public DwellAction Tick(TimeSpan now, (int X, int Y) cursor, bool gameForeground, bool busy)
    {
        LastSkip = null;
        if (!gameForeground)
        {
            // Leaving the game resets everything; coming back needs a fresh dwell.
            if (_anchor != null) LastSkip = "game not in foreground";
            _anchor = null; _consumed = false;
            return DwellAction.None;
        }
        if (_anchor is not { } a || CardPlacement.HasMoved(a.X, a.Y, cursor.X, cursor.Y, TolerancePx))
        {
            _anchor = cursor; _stillSince = now; _consumed = false;
            return DwellAction.None;
        }
        if (_consumed) return DwellAction.None;
        if (now - _stillSince < Dwell) return DwellAction.None;
        if (busy) { LastSkip = "lookup already running"; return DwellAction.None; }
        _consumed = true;
        return DwellAction.StartProbe;
    }

    /// <summary>Forget the current position so the next still period can fire again (e.g. after the setting is toggled).</summary>
    public void Reset() { _anchor = null; _consumed = false; }
}

/// <summary>Bounded wait for Tarkov's tooltip after a dwell: probe every interval until found, moved, or timed out.</summary>
public static class TooltipWait
{
    public enum Outcome { Found, Moved, TimedOut }

    public static async Task<(Outcome Result, int Probes, TimeSpan Waited)> RunAsync(
        Func<bool> probe, Func<bool> cursorMoved, TimeSpan interval, TimeSpan maxWait, Func<TimeSpan> clock, Func<TimeSpan, Task> delay)
    {
        var start = clock();
        int probes = 0;
        while (true)
        {
            if (cursorMoved()) return (Outcome.Moved, probes, clock() - start);
            probes++;
            if (probe()) return (Outcome.Found, probes, clock() - start);
            if (clock() - start + interval > maxWait) return (Outcome.TimedOut, probes, clock() - start);
            await delay(interval).ConfigureAwait(true);
        }
    }
}
