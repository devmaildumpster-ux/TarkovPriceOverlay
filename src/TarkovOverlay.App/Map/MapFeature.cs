using System.Windows;
using System.Windows.Threading;
using TarkovOverlay.App.Services;
using TarkovOverlay.Maps;
using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Definitions;
using TarkovOverlay.Maps.Extracts;
using TarkovOverlay.Maps.Logs;
using TarkovOverlay.Maps.Position;
using TarkovOverlay.Maps.Quests;
using TarkovOverlay.Maps.Settings;

namespace TarkovOverlay.App.Map;

/// <summary>
/// Composition root for the 1.1 map / quest features. Idle cost: one 1 s log poll (a stat + small read) and a
/// FileSystemWatcher on the screenshots folder. Map drawings and quest data load only while a map or quest window
/// is open and are released when both are closed.
/// </summary>
public sealed class MapFeature : IDisposable
{
    public static MapFeature? Instance { get; private set; }

    private readonly AppServices _app;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _saveDebounce;
    private EftLogWatcher? _logs;
    private ScreenshotWatcher? _shots;
    private readonly ScreenshotCleaner _cleaner;
    private ScreenshotCleanupMode _cleanupApplied;
    private MapPanel? _panel;
    private MapPopoutWindow? _popout;
    private QuestsWindow? _questsWindow;
    private ExitListReader? _exitReader;
    private CancellationTokenSource _cts = new();

    public MapPaths Paths { get; }
    public MapSettingsStore SettingsStore { get; }
    public MapSettings Settings => SettingsStore.Settings;
    public QuestProgressStore Progress { get; }
    public MapDataService Data { get; }
    public HttpClient Http { get; }
    public RaidTracker Raid { get; } = new();
    public EftInstallLocator.Found? Install { get; private set; }
    public PositionFix? LastFix { get; private set; }
    public string? LastFixMap { get; private set; }
    /// <summary>Screenshot key binding from Tarkov's "Control settings" log entry (null until seen).</summary>
    public string? ScreenshotKeyStatus { get; private set; }
    public string? ScreenshotKeys { get; private set; }
    public ScreenshotWatcher? Screenshots => _shots;
    public IReadOnlySet<string>? ListedExtracts { get; private set; }
    public DateTimeOffset? ListedExtractsAt { get; private set; }
    public string LogStatus => _logs?.Status ?? (Settings.WatchGameLogs ? "looking for the game's Logs folder" : "off");

    /// <summary>Cached feeds for the current mode (null until a window asked for them).</summary>
    public MapFeed? MapFeed { get; private set; }
    public TaskFeed? TaskFeed { get; private set; }
    public IReadOnlyList<MapDefinition>? Definitions { get; private set; }

    public event Action? RaidChanged;
    public event Action? PositionChanged;
    public event Action? ExtractListChanged;
    /// <summary>Raised on the UI thread after quest progress changed (argument: mode id).</summary>
    public event Action<string>? ProgressChanged;
    public event Action? FeedsChanged;

    public MapFeature(AppServices app, Dispatcher ui)
    {
        _app = app;
        _ui = ui;
        Paths = new MapPaths(app.Paths);
        SettingsStore = new MapSettingsStore(Paths.SettingsFile);
        _cleaner = new ScreenshotCleaner(new PlainFileDisposer(RecycleBin.Send), Log.Write);
        Progress = new QuestProgressStore(Paths.ProgressFile);
        if (QuestPins.MigrateFrom11(Settings, Progress.Data))
        {
            Log.Write("map: quest map selection upgraded to 1.1.2: " + string.Join(", ", Progress.Data.Modes.Select(kv => $"{kv.Key}: {kv.Value.Pinned.Count} on map")));
            Progress.Save();
            try { SettingsStore.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Progress.Changed += m => _ui.BeginInvoke(() => ProgressChanged?.Invoke(m));
        Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"TarkovPriceOverlay/{typeof(MapFeature).Assembly.GetName().Version?.ToString(3)} (+desktop companion)");
        Data = new MapDataService(Paths, app.Feed, GetTextAsync, Log.Write);
        _saveDebounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveNow(); };
        MapIds.FeedByScene = scene => MapFeed?.ByScenePath(scene)?.NormalizedName;
        MapIds.FeedByLocation = loc => MapFeed?.ByNameId(loc)?.NormalizedName;
        Instance = this;
    }

    // ---------------- lifetime ----------------
    public void Start()
    {
        ApplyWatchers();
        Log.Write($"map: feature started (logs {(Settings.WatchGameLogs ? "on" : "off")}, screenshots {(Settings.WatchScreenshots ? "on" : "off")})");
    }

    /// <summary>(Re)starts the log and screenshot watchers from the current settings.</summary>
    public void ApplyWatchers()
    {
        _logs?.Dispose(); _logs = null;
        Install = null;
        _nextLocate = DateTime.MinValue;
        if (Settings.WatchGameLogs)
        {
            _logs = new EftLogWatcher(LogsDir, OnLogEvent, Log.Write);
            _logs.Start();
        }
        _shots?.Dispose(); _shots = null;
        if (Settings.WatchScreenshots)
        {
            // Start() logs every candidate folder (known Documents folder, OneDrive, %USERPROFILE%) and keeps
            // looking every 30 s if the folder doesn't exist yet.
            _shots = new ScreenshotWatcher(OnFix, Log.Write);
            _shots.Start(Settings.ScreenshotsDir);
            UpdatePolling();
            // last known spot (e.g. the overlay was restarted mid-raid): newest of the saved position and the folder
            if (LastFix == null)
            {
                var saved = SavedPosition.Load(Paths.LastPositionFile, TimeSpan.FromHours(3), _shots.Folder);
                var latest = _shots.Latest(TimeSpan.FromHours(3));
                if (latest != null && (saved == null || latest.SeenAt > saved.Value.Fix.SeenAt))
                {
                    Log.Write($"map: latest position screenshot on start: {Path.GetFileName(latest.File)} ({latest.SeenAt:HH:mm})");
                    LastFix = latest; LastFixMap = null;
                    SavePosition(latest, null);
                }
                else if (saved is { } sp)
                {
                    Log.Write($"map: last saved position on start: {Path.GetFileName(sp.Fix.File)} ({sp.Fix.SeenAt:HH:mm}, map {sp.Map ?? "?"})");
                    LastFix = sp.Fix; LastFixMap = sp.Map;
                }
            }
            ConfigureCleaner(startup: true);
        }
        else Log.Write("map: screenshot watching is off (Map settings)");
        UpdateExitReader();
    }

    private DateTime _nextLocate = DateTime.MinValue;
    private string? LogsDir()
    {
        // called on the watcher's timer thread every folder rescan
        if (Install != null && Directory.Exists(Install.LogsDir)) return Install.LogsDir;
        if (DateTime.UtcNow < _nextLocate) return null;
        _nextLocate = DateTime.UtcNow.AddSeconds(30);
        var found = EftInstallLocator.Locate(Settings.EftInstallDir, Log.Write);
        if (found != null && Install?.LogsDir != found.LogsDir) Log.Write($"map: game logs at {found.LogsDir} (from {found.Source})");
        Install = found;
        return found?.LogsDir;
    }

    /// <summary>Forces a new install search (after the user picked a folder).</summary>
    public void RelocateGame()
    {
        Install = null;
        _nextLocate = DateTime.MinValue;
        _logs?.Reset();
    }

    public void SaveSoon() { _saveDebounce.Stop(); _saveDebounce.Start(); }
    public void SaveNow()
    {
        try { SettingsStore.Save(); } catch (IOException ex) { Log.Write("map: settings save failed: " + ex.Message); } catch (UnauthorizedAccessException ex) { Log.Write("map: settings save failed: " + ex.Message); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _saveDebounce.Stop();
        SaveNow();
        _exitReader?.Dispose();
        _logs?.Dispose();
        _shots?.Dispose();
        try { _popout?.Close(); } catch (InvalidOperationException) { }
        try { _questsWindow?.Close(); } catch (InvalidOperationException) { }
        try { Progress.Save(); } catch (IOException) { }
        Http.Dispose();
        MapIds.FeedByScene = null;
        MapIds.FeedByLocation = null;
        if (Instance == this) Instance = null;
    }

    // ---------------- mode ----------------
    /// <summary>Game mode for quests and map data: the mode the game reported in its logs, else the overlay's mode.</summary>
    public string ModeId => Raid.SessionMode ?? _app.Settings.ModeId;
    public string ModeLabel => Core.Catalog.GameMode.FromId(ModeId).DisplayName;
    public string? ItemName(string id) => _app.Catalog.Context?.Snapshot.Item(id) is { } i ? i.ShortName ?? i.Name : null;

    /// <summary>Side for the exit filter; null = unknown (menus, PvE, no raid yet) → every exit is shown.</summary>
    public PlayerSide? CurrentSide => Settings.Side switch
    {
        "pmc" => PlayerSide.Pmc,
        "scav" => PlayerSide.Scav,
        _ => Raid.ExtractSide,
    };

    // ---------------- log events ----------------
    private void OnLogEvent(LogEvent e, bool initial)
    {
        _ui.BeginInvoke(() =>
        {
            string? modeBefore = Raid.SessionMode;
            var phase = Raid.Phase;
            var map = Raid.Map;
            bool changed = Raid.Apply(e);
            if (e.Kind == LogEventKind.SessionMode && e.Value != null) SessionModeLogged?.Invoke(e.Value, initial); // 1.2.0 auto PvP/PvE
            if (e.Kind == LogEventKind.ScreenshotKey && (e.Value != ScreenshotKeyStatus || e.Extra != ScreenshotKeys))
            {
                ScreenshotKeyStatus = e.Value; ScreenshotKeys = e.Extra;
                Log.Write($"map: {ScreenshotKeyCheck.Describe(e.Value, e.Extra)}");
                RaidChanged?.Invoke(); // refreshes the status line
                return;
            }
            if (e.Kind == LogEventKind.TaskStatus && Settings.UpdateQuestsFromLogs && e.Value != null && e.Extra != null)
            {
                var mode = Raid.SessionMode ?? _app.Settings.ModeId;
                var p = Progress.For(mode);
                if (QuestLog.Apply(new QuestLogEntry(mode, e.Value, e.Extra, e.Time), p, TaskFeed?.ModeId == mode ? TaskFeed : null))
                {
                    Log.Write($"map: quest {e.Value} {e.Extra} ({mode}) from the game log");
                    Progress.NotifyChanged(mode);
                }
            }
            if (!changed) return;
            if (Raid.Phase != phase && Raid.Phase == RaidPhase.Menu) { ListedExtracts = null; ExtractListChanged?.Invoke(); }
            if (!initial)
            {
                Log.Write($"map: raid {Raid.Phase} map={Raid.Map ?? "?"} mode={Raid.SessionMode ?? "?"} side={Raid.Side?.ToString() ?? "?"}");
            }
            if (modeBefore != Raid.SessionMode && !MapInUse && _questsWindow == null) { MapFeed = null; TaskFeed = null; }
            UpdateExitReader();
            RaidChanged?.Invoke();
        });
    }

    private void OnFix(PositionFix f)
    {
        _ui.BeginInvoke(() =>
        {
            LastFix = f;
            // Only trust the log's map while a raid is on; a stale map from an earlier raid would hide the dot.
            LastFixMap = Raid.Phase != RaidPhase.Menu ? Raid.Map : null;
            Log.Write($"map: position fix x={f.Data.Position.X:0.##} z={f.Data.Position.Z:0.##} yaw={f.Data.YawDegrees:0} raid={Raid.Phase} raidMap={Raid.Map ?? "?"} → for map {LastFixMap ?? "(whatever map is shown)"}");
            SavePosition(f, LastFixMap); // before any cleanup, so the dot survives a restart without the file
            PositionChanged?.Invoke();
            var folder = _shots?.Folder;
            if (folder != null && _cleaner.Mode != ScreenshotCleanupMode.Off)
                _ = Task.Run(() => { try { _cleaner.AfterRead(folder, f.File); } catch (Exception ex) { Log.Write("map: screenshot cleanup failed: " + ex.Message); } });
        });
    }

    private void AddSavedPosition(List<string> lines)
    {
        if (SavedPosition.Load(Paths.LastPositionFile, TimeSpan.FromDays(3650), _shots?.Folder) is { } sp)
        {
            var p = sp.Fix.Data.Position;
            lines.Add($"Last saved position: x {p.X:0.##}, z {p.Z:0.##}, heading {sp.Fix.Data.YawDegrees:0}° on {sp.Map ?? "an unknown map"}, from {Path.GetFileName(sp.Fix.File)} ({MapPanel.Ago(DateTimeOffset.Now - sp.Fix.SeenAt)}).");
            if (DateTimeOffset.Now - sp.Fix.SeenAt <= TimeSpan.FromHours(3) && LastFix == null) { LastFix = sp.Fix; LastFixMap = sp.Map; PositionChanged?.Invoke(); }
        }
        else lines.Add("No saved position yet.");
    }

    private void SavePosition(PositionFix f, string? map)
    {
        try { SavedPosition.Save(Paths.LastPositionFile, f, map); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("map: couldn't save last position: " + ex.Message); }
    }

    /// <summary>Applies the cleanup setting; trims the backlog once at start-up or when the mode changes.</summary>
    private void ConfigureCleaner(bool startup)
    {
        var mode = Settings.CleanupMode;
        bool changed = mode != _cleanupApplied || startup;
        _cleaner.Mode = mode;
        _cleaner.KeepCount = Settings.ScreenshotKeepCount;
        _cleanupApplied = mode;
        if (startup) Log.Write($"map: screenshot cleanup: {ScreenshotCleaner.Describe(mode, Settings.ScreenshotKeepCount)}");
        var folder = _shots?.Folder;
        if (changed && mode != ScreenshotCleanupMode.Off && folder != null && _shots!.FolderExists)
            _ = Task.Run(() => { try { _cleaner.Trim(folder, startup ? "start-up" : "setting changed"); } catch (Exception ex) { Log.Write("map: screenshot cleanup failed: " + ex.Message); } });
    }

    /// <summary>1.2.1: Satellite/Vector preference changed (button, shortcut or Settings).</summary>
    public event Action? MapStyleChanged;

    /// <summary>1.2.2: auto layer mode changed (Settings combo); the map re-applies it on the next screenshot.</summary>
    public event Action? AutoLayerModeChanged;

    /// <summary>Folder the live screenshot watcher is reading (self-tests).</summary>
    internal string? ScreenshotFolder => _shots?.Folder;

    public void SetAutoLayerMode(string mode, string via)
    {
        var id = AutoLayer.ModeId(AutoLayer.ParseMode(mode));
        if (Settings.MapAutoLayerMode != id)
        {
            Settings.MapAutoLayerMode = id;
            SaveSoon();
            Log.Write($"map: auto layer = {id} ({via})");
        }
        AutoLayerModeChanged?.Invoke();
    }

    public void SetPreferSatellite(bool satellite, string via)
    {
        if (Settings.PreferSatelliteMap == satellite) { MapStyleChanged?.Invoke(); return; }
        Settings.PreferSatelliteMap = satellite;
        SaveSoon();
        Log.Write($"map: style = {(satellite ? "Satellite" : "Vector")} ({via})");
        MapStyleChanged?.Invoke();
    }

    /// <summary>Global shortcut: toggles the map style wherever the map is (docked or popped out). Never activates a window.</summary>
    public void ToggleMapStyle(string via)
    {
        if (_panel != null) _panel.ToggleStyle(via);
        else SetPreferSatellite(!Settings.PreferSatelliteMap, via);
    }

    internal string CleanupDescription => ScreenshotCleaner.Describe(Settings.CleanupMode, Settings.ScreenshotKeepCount);

    /// <summary>Polls the screenshots folder every 2 s while a map is shown (in case the watcher misses events).</summary>
    private void UpdatePolling()
    {
        if (_shots != null) _shots.PollInterval = MapInUse ? TimeSpan.FromSeconds(2) : null;
    }

    /// <summary>
    /// "Test position from latest screenshot": re-reads the newest file in the screenshots folder and returns a
    /// plain-language report. A position found is applied as the current fix (for the map currently shown).
    /// </summary>
    internal string TestLatestScreenshot()
    {
        var lines = new List<string>();
        _panel?.ResetAutoLayer();
        if (!Settings.WatchScreenshots) lines.Add("Screenshot watching is OFF in Map settings (turn it on for live updates).");
        var w = _shots ?? new ScreenshotWatcher(_ => { }, Log.Write);
        if (_shots == null) w.Start(Settings.ScreenshotsDir);
        lines.Add($"Folder: {w.Folder} ({w.FolderSource}, {(w.FolderExists ? "exists" : "NOT FOUND")}).");
        lines.Add($"Screenshot cleanup: {CleanupDescription} (only Tarkov position screenshots are ever removed).");
        if (!w.FolderExists)
        {
            var others = w.Candidates.Where(c => c.Path != w.Folder).Select(c => $"{c.Path} ({(c.Exists ? "exists" : "missing")})");
            lines.Add("Tarkov hasn't saved a screenshot there yet, or saves elsewhere. Also checked: " + string.Join("; ", others) + ". Set the folder in Map settings if yours is different.");
            AddSavedPosition(lines);
        }
        else if (w.NewestImage() is not { } newest)
        {
            lines.Add("The folder has no screenshots right now" + (Settings.CleanupMode != ScreenshotCleanupMode.Off ? $" (cleanup: {CleanupDescription})" : "") + ". Press your screenshot key in raid; if no file appears, check the key (below) and Windows' Print Screen setting.");
            AddSavedPosition(lines);
        }
        else
        {
            var name = Path.GetFileName(newest.File);
            lines.Add($"Newest file: {name} (saved {newest.Time:HH:mm:ss}, {MapPanel.Ago(DateTimeOffset.Now - newest.Time)}).");
            var parsed = ScreenshotName.TryParse(name);
            if (parsed == null) { lines.Add("No position in it: " + ScreenshotName.Explain(name) + "."); AddSavedPosition(lines); }
            else
            {
                lines.Add($"Position read: x {parsed.Position.X:0.##}, y {parsed.Position.Y:0.##}, z {parsed.Position.Z:0.##}, heading {parsed.YawDegrees:0}°.");
                LastFix = new PositionFix(parsed, newest.File, newest.Time);
                LastFixMap = null; // test: show it on whatever map is open
                PositionChanged?.Invoke();
                if (_panel?.LastPositionNote is { } note) lines.Add(note);
            }
        }
        lines.Add(ScreenshotKeyCheck.Describe(ScreenshotKeyStatus, ScreenshotKeys) + ".");
        if (w != _shots) w.Dispose();
        var report = string.Join(" ", lines);
        Log.Write("map: test position: " + report);
        return report;
    }

    // ---------------- data ----------------
    public async Task<bool> EnsureFeedsAsync(bool quests, bool force = false)
    {
        var mode = ModeId;
        var ct = _cts.Token;
        try
        {
            if (Definitions == null || force) Definitions = await Data.GetDefinitionsAsync(ct, force);
            if (MapFeed == null || MapFeed.ModeId != mode || force) MapFeed = await Data.GetMapFeedAsync(mode, ct, force) ?? MapFeed;
            if (quests && (TaskFeed == null || TaskFeed.ModeId != mode || force)) TaskFeed = await Data.GetTaskFeedAsync(mode, ct, force) ?? TaskFeed;
        }
        catch (OperationCanceledException) { return false; }
        FeedsChanged?.Invoke();
        if (quests) _ = AutoSyncTrackerAsync();
        return MapFeed != null;
    }

    /// <summary>Drops the large data when neither window is open.</summary>
    private void ReleaseIfIdle()
    {
        if (MapInUse || _questsWindow != null) return;
        MapFeed = null; TaskFeed = null; Definitions = null;
        Data.ReleaseMemory();
        Program.TrimMemory("map/quest windows closed");
    }

    private async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        using var resp = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    // ---------------- TarkovTracker (optional, read-only) ----------------
    public string? TrackerStatus { get; private set; }

    public async Task<string> SyncTrackerAsync()
    {
        var token = Settings.TrackerToken?.Trim();
        if (string.IsNullOrEmpty(token)) return TrackerStatus = "No TarkovTracker token set.";
        var tokenMode = TarkovTracker.ModeFromToken(token);
        var mode = tokenMode ?? ModeId;
        try
        {
            var pr = await TarkovTracker.FetchAsync(Http, Settings.TrackerDomain, token, _cts.Token);
            if (pr.GameMode is { } gm && EftLogParser.ModeId(gm) is { } m2) mode = m2;
            var p = Progress.For(mode);
            int n = TarkovTracker.Apply(pr, p);
            p.LastTrackerSync = DateTimeOffset.Now;
            Progress.NotifyChanged(mode);
            Log.Write($"map: TarkovTracker sync ({Settings.TrackerDomain}, {mode}): {pr.Tasks.Count} tasks, {n} changed");
            return TrackerStatus = $"Synced {pr.Tasks.Count} quests from {Settings.TrackerDomain} ({Core.Catalog.GameMode.FromId(mode).DisplayName}) at {DateTime.Now:HH:mm}; {n} changed.";
        }
        catch (UnauthorizedAccessException) { return TrackerStatus = "TarkovTracker rejected the token (check it has the 'Get progression' permission and matches the site you picked)."; }
        catch (OperationCanceledException) { return TrackerStatus = "TarkovTracker sync timed out."; }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or FormatException)
        {
            Log.Write("map: TarkovTracker sync failed: " + ex.Message);
            return TrackerStatus = "TarkovTracker sync failed: " + ex.Message;
        }
    }

    private DateTimeOffset _lastAutoSync = DateTimeOffset.MinValue;
    private async Task AutoSyncTrackerAsync()
    {
        if (!Settings.TrackerAutoSync || string.IsNullOrWhiteSpace(Settings.TrackerToken)) return;
        if (DateTimeOffset.Now - _lastAutoSync < TimeSpan.FromMinutes(10)) return;
        _lastAutoSync = DateTimeOffset.Now;
        await SyncTrackerAsync();
    }

    // ---------------- quest history import ----------------
    public async Task<string> ImportQuestHistoryAsync(Action<string> progress)
    {
        var logs = Install?.LogsDir ?? await Task.Run(() => EftInstallLocator.Locate(Settings.EftInstallDir, Log.Write)?.LogsDir);
        if (logs == null) return "Couldn't find the game's Logs folder. Set the Escape from Tarkov folder in Map settings first.";
        var defaultMode = _app.Settings.ModeId;
        var entries = await Task.Run(() => QuestLog.ReadHistory(logs, defaultMode, s => _ui.BeginInvoke(() => progress(s)), _cts.Token));
        int changed = 0;
        var touched = new HashSet<string>();
        foreach (var g in entries.GroupBy(e => e.ModeId))
        {
            TaskFeed? feed = TaskFeed?.ModeId == g.Key ? TaskFeed : await Data.GetTaskFeedAsync(g.Key, _cts.Token);
            var p = Progress.For(g.Key);
            foreach (var e in g) if (QuestLog.Apply(e, p, feed)) { changed++; touched.Add(g.Key); }
        }
        foreach (var m in touched) Progress.NotifyChanged(m);
        var modes = string.Join(", ", entries.GroupBy(e => e.ModeId).Select(g => $"{Core.Catalog.GameMode.FromId(g.Key).DisplayName}: {g.Count()}"));
        Log.Write($"map: quest history import from {logs}: {entries.Count} events, {changed} changes ({modes})");
        return entries.Count == 0
            ? $"No quest messages found in {logs}. (The game deletes old log folders, so only recent sessions are available.)"
            : $"Read {entries.Count} quest events ({modes}); {changed} quest states updated.";
    }

    // ---------------- exit list OCR (experimental) ----------------
    internal void SetListedExtracts(IReadOnlySet<string>? ids)
    {
        ListedExtracts = ids;
        ListedExtractsAt = ids == null ? null : DateTimeOffset.Now;
        ExtractListChanged?.Invoke();
    }

    internal AppServices App => _app;

    private void UpdateExitReader()
    {
        bool want = Settings.ReadExitListWithOcr && MapInUse && Raid.Phase == RaidPhase.InRaid;
        if (want && _exitReader == null) { _exitReader = new ExitListReader(this, _ui); _exitReader.Start(); }
        else if (!want && _exitReader != null) { _exitReader.Dispose(); _exitReader = null; }
    }

    /// <summary>
    /// 1.1.4: the app's PvP/PvE mode was changed in Settings…. Outside a raid the map, its extracts/spawns and the
    /// quest pins follow the app mode, so the mode's feeds are dropped and the shown map is reloaded. During a raid the
    /// game log's mode wins (ModeId), so nothing changes until the raid ends.
    /// </summary>
    /// <summary>1.2.0: Tarkov's log said "Session mode: …" (mode id, true while catching up on the existing log).</summary>
    public event Action<string, bool>? SessionModeLogged;

    public void AppModeChanged()
    {
        if (Raid.SessionMode != null) { Log.Write($"map: app mode -> {_app.Settings.ModeId}; raid mode {Raid.SessionMode} kept until the raid ends"); return; }
        MapFeed = null; TaskFeed = null;
        Log.Write($"map: app mode -> {_app.Settings.ModeId}; reloading map data");
        if (_panel?.CurrentMap is { } m) _ = _panel.LoadMapAsync(m);
        else FeedsChanged?.Invoke();
    }

    internal void SettingsChanged()
    {
        SaveSoon();
        ConfigureCleaner(startup: false);
        UpdateExitReader();
    }

    // ---------------- windows ----------------
    /// <summary>The map panel that lives in the main window's left column (created on first use of the layout).</summary>
    public MapPanel Panel => _panel ??= new MapPanel(this);

    /// <summary>True while the panel has a map drawing loaded (data stays in memory only then).</summary>
    public bool MapInUse => _panel?.HasMap == true;

    /// <summary>Name and tarkov.dev ids of the map shown in the panel (for the Quests window's "this map only").</summary>
    public string? CurrentMapName => _panel is { CurrentMap: { } m, CurrentMapIds: not null } ? MapPanel.DisplayName(m) : null;
    public IReadOnlyCollection<string>? CurrentMapIds => _panel?.CurrentMapIds;
    public event Action? MapShown;
    internal void RaiseMapShown() => MapShown?.Invoke();

    internal void MapUseChanged() { UpdateExitReader(); UpdatePolling(); ReleaseIfIdle(); }

    /// <summary>Moves the map panel into its own normal (not topmost) window, e.g. maximized on another monitor.</summary>
    public void PopOutMap()
    {
        if (_popout != null) { _popout.Activate(); return; }
        PanelHostRequested?.Invoke(false); // main window lets go of the panel
        _popout = new MapPopoutWindow(this, Panel);
        _popout.Closed += (_, _) => { _popout = null; PanelHostRequested?.Invoke(true); };
        _popout.Show();
    }

    public bool MapPoppedOut => _popout != null;
    public void DockMap() => _popout?.Close();

    /// <summary>true: the main window should host the panel again; false: it must release it.</summary>
    public event Action<bool>? PanelHostRequested;

    public void OpenQuests()
    {
        if (_questsWindow == null)
        {
            _questsWindow = new QuestsWindow(this);
            _questsWindow.Closed += (_, _) => { _questsWindow = null; ReleaseIfIdle(); };
            _questsWindow.Show();
        }
        else
        {
            if (_questsWindow.WindowState == WindowState.Minimized) _questsWindow.WindowState = WindowState.Normal;
            _questsWindow.Activate();
        }
    }

}
