using System.Windows;
using TarkovOverlay.Core.Lifetime;

namespace TarkovOverlay.App.Services;

/// <summary>
/// Closing the main window quits the app.
///
/// 1.0.0-1.0.5 bug: the hidden cursor-card window was created before the main window, and WPF makes the first
/// window it sees Application.MainWindow. With ShutdownMode.OnMainWindowClose the app therefore waited for the
/// (never closed) hidden card window, and the process stayed in Task Manager after the main window closed.
/// Now the real main window is set explicitly, its Closed event runs the shutdown steps and calls
/// Application.Shutdown, and a 3 s background watchdog forces Environment.Exit if anything still lingers.
/// </summary>
public static class AppLifetime
{
    public static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(3);

    /// <param name="steps">Named cleanup steps, run in order on the UI thread when the main window closes.</param>
    /// <param name="forceExit">Called by the watchdog (default Environment.Exit(0)).</param>
    public static void Wire(Application app, Window main, IReadOnlyList<(string Name, Action Action)> steps, Action<string> log, Action? forceExit = null)
    {
        app.MainWindow = main;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        bool done = false;
        void Quit(string why)
        {
            if (done) return;
            done = true;
            log($"shutdown: begin ({why}); {app.Windows.Count} window(s) open");
            ExitWatchdog.Start(WatchdogTimeout, () =>
            {
                log($"shutdown: watchdog: process still running {WatchdogTimeout.TotalSeconds:0} s after close; forcing exit");
                (forceExit ?? (() => Environment.Exit(0)))();
            });
            var seq = new ShutdownSequence(log);
            foreach (var (name, action) in steps) seq.Run(name, action);
            seq.Run("close remaining windows", () =>
            {
                foreach (var w in app.Windows.OfType<Window>().Where(w => w != main).ToList()) w.Close();
            });
            log($"shutdown: steps done in {seq.Elapsed.TotalMilliseconds:0} ms; stopping the dispatcher");
            app.Shutdown();
        }
        main.Closed += (_, _) => Quit("main window closed");
        app.SessionEnding += (_, _) => Quit("Windows session ending");
        app.Exit += (_, _) => Quit("application exit");
    }
}
