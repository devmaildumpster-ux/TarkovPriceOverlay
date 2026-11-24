namespace TarkovOverlay.Maps.Position;

public sealed record PositionFix(ScreenshotName.Parsed Data, string File, DateTimeOffset SeenAt);

/// <summary>
/// Watches the EFT screenshots folder (Documents\Escape from Tarkov\Screenshots, OneDrive-aware). Each new in-raid
/// screenshot gives a position fix. Only the file NAME is used (the image is never opened), so a file that is still
/// being written is fine. Belt and braces: FileSystemWatcher (Created/Renamed/Changed, 64 KB buffer, restarted on
/// overflow) plus an optional poll for the newest files while the map is in use. Every step is logged with "map:".
/// Cleanup of old screenshots is done by <see cref="ScreenshotCleaner"/>, not here.
/// </summary>
public sealed class ScreenshotWatcher : IDisposable
{
    private static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp" };
    private readonly Action<PositionFix> _onFix;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _fsw;
    private Timer? _poll, _missing;
    private TimeSpan? _pollInterval;
    private DateTime _pollSinceUtc;
    private string? _override;
    private bool _disposed;

    public string? Folder { get; private set; }
    public string? FolderSource { get; private set; }
    public bool FolderExists { get; private set; }
    public IReadOnlyList<ScreenshotFolders.Candidate> Candidates { get; private set; } = Array.Empty<ScreenshotFolders.Candidate>();
    /// <summary>Last screenshot file seen (any kind) and what came of it, for the status line / test report.</summary>
    public string? LastFile { get; private set; }
    public string? LastResult { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }
    /// <summary>For tests: how often to look again for a missing folder.</summary>
    public TimeSpan MissingRetry { get; set; } = TimeSpan.FromSeconds(30);

    public ScreenshotWatcher(Action<PositionFix> onFix, Action<string>? log = null) { _onFix = onFix; _log = log ?? (_ => { }); }

    /// <summary>Default folder shown in settings (first existing candidate).</summary>
    public static string DefaultFolder() => ScreenshotFolders.Resolve(ScreenshotFolders.Candidates()).Path;

    /// <summary>
    /// (Re)starts watching. <paramref name="overrideDir"/> = folder from Map settings (empty = find it).
    /// Returns false when the folder does not exist yet; it is then looked for again every 30 s.
    /// </summary>
    public bool Start(string? overrideDir)
    {
        lock (_gate)
        {
            StopCore();
            _override = string.IsNullOrWhiteSpace(overrideDir) ? null : overrideDir;
            Candidates = ScreenshotFolders.Candidates(_override);
            var pick = ScreenshotFolders.Resolve(Candidates);
            Folder = pick.Path; FolderSource = pick.Source; FolderExists = pick.Exists;
            _log("map: screenshot folder candidates: " + string.Join(" | ", Candidates.Select(c => $"{c.Path} [{c.Source}, {(c.Exists ? "exists" : "missing")}]")));
            if (!pick.Exists)
            {
                _log($"map: screenshots folder not found: {pick.Path} ({pick.Source}); Tarkov creates it on the first screenshot, checking again every {MissingRetry.TotalSeconds:0} s");
                _missing = new Timer(_ => RetryMissing(), null, MissingRetry, MissingRetry);
                return false;
            }
            try
            {
                _fsw = new FileSystemWatcher(pick.Path)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                    Filter = "*",
                };
                _fsw.Created += (_, e) => Handle(e.FullPath, "created");
                _fsw.Renamed += (_, e) => Handle(e.FullPath, "renamed");
                _fsw.Changed += (_, e) => Handle(e.FullPath, "changed");
                _fsw.Error += (_, e) => OnWatcherError(e.GetException());
                _fsw.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                _log($"map: could not watch {pick.Path} ({ex.Message}); using polling only");
                _fsw?.Dispose(); _fsw = null;
                _pollInterval ??= TimeSpan.FromSeconds(2);
            }
            _pollSinceUtc = DateTime.UtcNow;
            ApplyPoll();
            _log($"map: watching screenshots in {pick.Path} ({pick.Source}){(_pollInterval is { } pi ? $", also polling every {pi.TotalSeconds:0.#} s" : "")}");
            return true;
        }
    }

    /// <summary>Polls the folder for new files (in addition to the watcher) while set; null = watcher only.</summary>
    public TimeSpan? PollInterval
    {
        get => _pollInterval;
        set { lock (_gate) { if (_pollInterval == value) return; _pollInterval = value; ApplyPoll(); } }
    }

    private void ApplyPoll()
    {
        _poll?.Dispose(); _poll = null;
        if (_pollInterval is { } i && FolderExists && !_disposed)
        {
            if (_pollSinceUtc == default) _pollSinceUtc = DateTime.UtcNow;
            _poll = new Timer(_ => Poll(), null, i, i);
        }
    }

    private void RetryMissing()
    {
        if (_disposed) return;
        var c = ScreenshotFolders.Resolve(ScreenshotFolders.Candidates(_override));
        if (c.Exists) { _log($"map: screenshots folder appeared: {c.Path}"); Start(_override); ScanSince(DateTime.UtcNow.AddMinutes(-2)); }
    }

    private void OnWatcherError(Exception ex)
    {
        _log($"map: screenshot watcher error ({ex.GetType().Name}: {ex.Message}); restarting and rescanning");
        var since = DateTime.UtcNow.AddSeconds(-30);
        try { Start(_override); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
        ScanSince(since);
    }

    /// <summary>One poll step: handles every image written since the last poll (public for tests).</summary>
    public void Poll()
    {
        DateTime since;
        lock (_gate) since = _pollSinceUtc.AddSeconds(-1); // 1 s overlap; duplicates are filtered by name
        var newest = ScanSince(since);
        if (newest is { } n) lock (_gate) if (n > _pollSinceUtc) _pollSinceUtc = n;
    }

    private DateTime? ScanSince(DateTime sinceUtc)
    {
        var dir = Folder;
        if (dir == null || !Directory.Exists(dir)) return null;
        DateTime? newest = null;
        try
        {
            var files = new DirectoryInfo(dir).EnumerateFiles()
                .Where(f => Exts.Contains(f.Extension.ToLowerInvariant()))
                .Select(f => (f, t: Max(f.LastWriteTimeUtc, f.CreationTimeUtc)))
                .Where(x => x.t > sinceUtc)
                .OrderBy(x => x.t).ToList();
            foreach (var (f, t) in files) { newest = t; Handle(f.FullName, "poll"); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log("map: screenshot folder scan failed: " + ex.Message); }
        return newest;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>Newest in-raid screenshot already in the folder (to show the last known spot on start).</summary>
    public PositionFix? Latest(TimeSpan maxAge)
    {
        var dir = Folder;
        if (dir == null || !Directory.Exists(dir)) return null;
        try
        {
            var f = new DirectoryInfo(dir).EnumerateFiles()
                .Select(x => (x, t: Max(x.LastWriteTimeUtc, x.CreationTimeUtc)))
                .Where(x => DateTime.UtcNow - x.t < maxAge)
                .OrderByDescending(x => x.t).FirstOrDefault(x => ScreenshotName.TryParse(x.x.Name) != null);
            return f.x == null ? null : new PositionFix(ScreenshotName.TryParse(f.x.Name)!, f.x.FullName, new DateTimeOffset(f.t).ToLocalTime());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Newest image file in the folder (any name) and its time, for the "Test position" report.</summary>
    public (string File, DateTimeOffset Time)? NewestImage()
    {
        var dir = Folder;
        if (dir == null || !Directory.Exists(dir)) return null;
        try
        {
            var f = new DirectoryInfo(dir).EnumerateFiles().Where(x => Exts.Contains(x.Extension.ToLowerInvariant()))
                .Select(x => (x, t: Max(x.LastWriteTimeUtc, x.CreationTimeUtc))).OrderByDescending(x => x.t).FirstOrDefault();
            return f.x == null ? null : (f.x.FullName, new DateTimeOffset(f.t).ToLocalTime());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void Handle(string path, string via)
    {
        var name = Path.GetFileName(path);
        if (!Exts.Contains(Path.GetExtension(name).ToLowerInvariant())) return;
        lock (_gate)
        {
            if (!_handled.Add(name)) return; // Created + Changed + poll for the same file
            if (_handled.Count > 2000) _handled.Clear();
        }
        LastFile = name; LastSeenAt = DateTimeOffset.Now;
        var parsed = ScreenshotName.TryParse(name);
        if (parsed == null)
        {
            LastResult = ScreenshotName.Explain(name);
            _log($"map: screenshot seen ({via}): {name} → ignored: {LastResult}");
            return;
        }
        LastResult = $"position x {parsed.Position.X:0.##}, z {parsed.Position.Z:0.##}, heading {parsed.YawDegrees:0}°";
        _log($"map: screenshot seen ({via}): {name} → {LastResult}");
        try { _onFix(new PositionFix(parsed, path, DateTimeOffset.Now)); }
        catch (Exception ex) { _log("map: position handler failed: " + ex); }
    }

    private void StopCore()
    {
        _missing?.Dispose(); _missing = null;
        _poll?.Dispose(); _poll = null;
        if (_fsw != null) { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); _fsw = null; }
    }

    public void Stop() { lock (_gate) StopCore(); }

    public void Dispose() { _disposed = true; Stop(); }
}
