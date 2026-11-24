using System.Diagnostics;

namespace TarkovOverlay.Core.Lifetime;

/// <summary>
/// Runs named shutdown steps in order. Every step is timed and logged; a step that throws is logged and the
/// rest still run, so one failing component can never keep the process alive.
/// </summary>
public sealed class ShutdownSequence
{
    private readonly Action<string> _log;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    public List<(string Step, bool Ok, TimeSpan Took, string? Error)> Results { get; } = new();

    public ShutdownSequence(Action<string> log) => _log = log;

    public void Run(string step, Action action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            action();
            Results.Add((step, true, sw.Elapsed, null));
            _log($"shutdown: {step} ok ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Results.Add((step, false, sw.Elapsed, ex.Message));
            _log($"shutdown: {step} FAILED after {sw.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public TimeSpan Elapsed => _total.Elapsed;
}

/// <summary>
/// Last-resort exit: a background thread that calls <paramref name="onExpire"/> (Environment.Exit in the app)
/// if the process is still alive after the timeout. Background, so it never keeps the process alive itself.
/// </summary>
public sealed class ExitWatchdog : IDisposable
{
    private readonly ManualResetEventSlim _cancel = new(false);
    public Thread Thread { get; }
    public bool Fired { get; private set; }

    private ExitWatchdog(TimeSpan timeout, Action onExpire)
    {
        Thread = new Thread(() =>
        {
            if (_cancel.Wait(timeout)) return;
            Fired = true;
            onExpire();
        }) { IsBackground = true, Name = "exit watchdog" };
    }

    public static ExitWatchdog Start(TimeSpan timeout, Action onExpire)
    {
        var w = new ExitWatchdog(timeout, onExpire);
        w.Thread.Start();
        return w;
    }

    public void Cancel() => _cancel.Set();
    public void Dispose() => Cancel();
}
