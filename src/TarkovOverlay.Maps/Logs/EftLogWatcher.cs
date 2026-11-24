using System.Text;

namespace TarkovOverlay.Maps.Logs;

/// <summary>
/// Tails the newest EFT session's application and notifications logs. Polls file sizes once a second (two
/// FileInfo calls, no file handles kept open) and reads only what was appended. When a new session folder
/// appears (game restarted) it switches to it and replays that folder from the start.
/// </summary>
public sealed class EftLogWatcher : IDisposable
{
    private readonly Func<string?> _logsDir;
    private readonly Action<LogEvent, bool> _onEvent;
    private readonly Action<string> _log;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private string? _folder;
    private readonly Dictionary<string, (long Offset, EftLogParser Parser)> _files = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextFolderScan = DateTime.MinValue;
    private bool _busy, _disposed;

    public string? CurrentFolder => _folder;
    public string Status { get; private set; } = "not started";

    /// <param name="logsDir">Returns the Logs folder (re-evaluated when none is known).</param>
    /// <param name="onEvent">Called on the timer thread; the bool is true while replaying a folder from its start.</param>
    public EftLogWatcher(Func<string?> logsDir, Action<LogEvent, bool> onEvent, Action<string>? log = null)
    {
        _logsDir = logsDir; _onEvent = onEvent; _log = log ?? (_ => { });
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() { lock (_gate) if (!_disposed) _timer.Change(200, 1000); }
    public void Stop() { lock (_gate) if (!_disposed) _timer.Change(Timeout.Infinite, Timeout.Infinite); }

    /// <summary>Forget the folder (e.g. install path changed) so the next tick re-locates it.</summary>
    public void Reset() { lock (_gate) { _folder = null; _files.Clear(); _nextFolderScan = DateTime.MinValue; } }

    private void Tick()
    {
        lock (_gate) { if (_busy || _disposed) return; _busy = true; }
        try
        {
            var now = DateTime.UtcNow;
            if (_folder == null || now >= _nextFolderScan)
            {
                _nextFolderScan = now + TimeSpan.FromSeconds(_folder == null ? 30 : 10);
                var logs = _logsDir();
                if (logs == null) { Status = "EFT logs folder not found"; return; }
                var newest = EftInstallLocator.NewestSessionFolder(logs);
                if (newest == null) { Status = "no game sessions in " + logs; return; }
                if (!string.Equals(newest, _folder, StringComparison.OrdinalIgnoreCase))
                {
                    _folder = newest;
                    _files.Clear();
                    _log("map: watching EFT log folder " + newest);
                }
            }
            var (app, notes) = EftInstallLocator.SessionFiles(_folder!);
            var batch = new List<(LogEvent E, bool Initial)>();
            foreach (var f in new[] { app, notes })
                if (f != null) ReadAppended(f, batch);
            // Merge application + notifications entries in time order.
            foreach (var (e, initial) in batch.OrderBy(b => b.E.Time ?? DateTimeOffset.MinValue)) _onEvent(e, initial);
            Status = "watching " + Path.GetFileName(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "log read error: " + ex.Message;
        }
        finally { lock (_gate) _busy = false; }
    }

    private void ReadAppended(string file, List<(LogEvent, bool)> batch)
    {
        var len = new FileInfo(file).Length;
        if (!_files.TryGetValue(file, out var st))
        {
            st = (0, new EftLogParser());
            if (EftInstallLocator.FolderStamp(Path.GetDirectoryName(file)!) is { } stamp) st.Parser.AssumeDate(DateOnly.FromDateTime(stamp.ToLocalTime()));
        }
        bool initial = st.Offset == 0;
        if (len < st.Offset) st = (0, new EftLogParser()); // truncated/replaced
        if (len == st.Offset) { _files[file] = st; return; }
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(st.Offset, SeekOrigin.Begin);
        var buf = new byte[Math.Min(len - st.Offset, 8 * 1024 * 1024)];
        int read = 0, n;
        while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
        // Only decode up to the last newline so a multi-byte character is never split.
        int cut = Array.LastIndexOf(buf, (byte)'\n', Math.Max(0, read - 1));
        if (cut < 0) { _files[file] = st; return; }
        var text = Encoding.UTF8.GetString(buf, 0, cut + 1);
        foreach (var e in st.Parser.Feed(text)) batch.Add((e, initial));
        _files[file] = (st.Offset + cut + 1, st.Parser);
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        _timer.Dispose();
    }
}
