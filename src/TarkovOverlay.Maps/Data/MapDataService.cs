using System.Reflection;
using System.Text.Json;
using TarkovOverlay.Core;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Maps.Definitions;

namespace TarkovOverlay.Maps.Data;

/// <summary>
/// Downloads and caches what the map window needs. Nothing here runs until the map or quest window asks for it,
/// so the feature adds no startup or idle cost to the price overlay.
///   • maps.json (drawing transforms) from the tarkov-dev GitHub repo, cached 3 days, embedded fallback.
///   • json.tarkov.dev/{mode}/maps + maps_en (extracts, transits), cached 12 h, trimmed to ~1% of the feed.
///   • json.tarkov.dev/{mode}/tasks + tasks_en + traders_en (quest objectives), cached 12 h.
/// A failed refresh keeps using the last good cache of any age.
/// </summary>
public sealed class MapDataService
{
    public const string DefinitionsUrl = "https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json";
    public static readonly TimeSpan FeedMaxAge = TimeSpan.FromHours(12);
    public static readonly TimeSpan DefinitionsMaxAge = TimeSpan.FromDays(3);

    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly MapPaths _paths;
    private readonly IFeedSource _feed;
    private readonly Func<string, CancellationToken, Task<string>> _getText;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, MapFeed> _maps = new();
    private readonly Dictionary<string, TaskFeed> _tasks = new();
    private IReadOnlyList<MapDefinition>? _defs;

    public MapDataService(MapPaths paths, IFeedSource feed, Func<string, CancellationToken, Task<string>> getText, Action<string>? log = null)
    {
        _paths = paths; _feed = feed; _getText = getText; _log = log ?? (_ => { });
    }

    public string? LastProblem { get; private set; }

    // ---------------- definitions ----------------
    public async Task<IReadOnlyList<MapDefinition>> GetDefinitionsAsync(CancellationToken ct, bool force = false)
    {
        if (_defs != null && !force) return _defs;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_defs != null && !force) return _defs;
            var file = _paths.DefinitionsFile;
            List<MapDefinition>? cached = null;
            if (File.Exists(file))
            {
                try { cached = MapDefinitionParser.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)); }
                catch (Exception ex) when (ex is JsonException or FormatException or IOException) { _log($"map: cached maps.json unreadable ({ex.Message})"); }
            }
            bool stale = cached == null || force || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > DefinitionsMaxAge;
            if (stale)
            {
                try
                {
                    var text = await _getText(DefinitionsUrl, ct).ConfigureAwait(false);
                    var parsed = MapDefinitionParser.Parse(text);
                    if (parsed.Count >= 5)
                    {
                        AppPaths.AtomicWrite(file, s => { using var w = new StreamWriter(s, new System.Text.UTF8Encoding(false), leaveOpen: true); w.Write(text); });
                        cached = parsed;
                        _log($"map: maps.json refreshed ({parsed.Count} interactive maps)");
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or FormatException or IOException or TaskCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    LastProblem = "maps.json download failed: " + ex.Message;
                    _log("map: " + LastProblem);
                }
            }
            _defs = cached ?? LoadFallbackDefinitions();
            return _defs;
        }
        finally { _gate.Release(); }
    }

    public static List<MapDefinition> LoadFallbackDefinitions()
    {
        using var s = typeof(MapDataService).Assembly.GetManifestResourceStream("TarkovOverlay.Maps.maps.fallback.json")
            ?? throw new InvalidOperationException("embedded maps.fallback.json missing");
        using var r = new StreamReader(s);
        return MapDefinitionParser.Parse(r.ReadToEnd());
    }

    // ---------------- map feed ----------------
    public async Task<MapFeed?> GetMapFeedAsync(string modeId, CancellationToken ct, bool force = false)
    {
        if (!force && _maps.TryGetValue(modeId, out var mem) && DateTimeOffset.UtcNow - mem.FetchedAt < FeedMaxAge) return mem;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = _paths.MapFeedFile(modeId);
            var cached = !force && _maps.TryGetValue(modeId, out var m2) ? m2 : LoadCache<MapFeed>(file, f => f.Schema == MapFeed.CurrentSchema && f.ModeId == modeId && f.Maps.Count > 0);
            if (cached != null && !force && DateTimeOffset.UtcNow - cached.FetchedAt < FeedMaxAge) { _maps[modeId] = cached; return cached; }
            try
            {
                var mapsT = _feed.GetJsonAsync($"/{modeId}/maps", TimeSpan.FromSeconds(60), ct);
                var enT = _feed.GetJsonAsync($"/{modeId}/maps_en", TimeSpan.FromSeconds(30), ct);
                using var maps = await mapsT.ConfigureAwait(false);
                using var en = await enT.ConfigureAwait(false);
                var feed = MapFeed.Parse(maps, en, modeId, DateTimeOffset.UtcNow);
                AppPaths.AtomicWrite(file, s => JsonSerializer.Serialize(s, feed, Json));
                _maps[modeId] = feed;
                _log($"map: map feed refreshed for {modeId} ({feed.Maps.Count} maps, {feed.Maps.Sum(x => x.Extracts.Count)} extracts)");
                return feed;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or FormatException or IOException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                LastProblem = $"map data download failed ({ex.Message}); " + (cached != null ? "using the cached copy" : "no cached copy yet");
                _log("map: " + LastProblem);
                if (cached != null) _maps[modeId] = cached;
                return cached;
            }
        }
        finally { _gate.Release(); }
    }

    // ---------------- tasks ----------------
    public async Task<TaskFeed?> GetTaskFeedAsync(string modeId, CancellationToken ct, bool force = false)
    {
        if (!force && _tasks.TryGetValue(modeId, out var mem) && DateTimeOffset.UtcNow - mem.FetchedAt < FeedMaxAge) return mem;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = _paths.TaskFeedFile(modeId);
            var cached = !force && _tasks.TryGetValue(modeId, out var t2) ? t2 : LoadCache<TaskFeed>(file, f => f.Schema == TaskFeed.CurrentSchema && f.ModeId == modeId && f.Tasks.Count > 0);
            if (cached != null && !force && DateTimeOffset.UtcNow - cached.FetchedAt < FeedMaxAge) { _tasks[modeId] = cached; return cached; }
            try
            {
                var tT = _feed.GetJsonAsync($"/{modeId}/tasks", TimeSpan.FromSeconds(60), ct);
                var enT = _feed.GetJsonAsync($"/{modeId}/tasks_en", TimeSpan.FromSeconds(30), ct);
                var trT = _feed.GetJsonAsync($"/{modeId}/traders_en", TimeSpan.FromSeconds(30), ct);
                using var t = await tT.ConfigureAwait(false);
                using var en = await enT.ConfigureAwait(false);
                using var trd = await trT.ConfigureAwait(false);
                var feed = TaskFeed.Parse(t, en, trd, modeId, DateTimeOffset.UtcNow);
                AppPaths.AtomicWrite(file, s => JsonSerializer.Serialize(s, feed, Json));
                _tasks[modeId] = feed;
                _log($"map: quest feed refreshed for {modeId} ({feed.Tasks.Count} quests)");
                return feed;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or FormatException or IOException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                LastProblem = $"quest data download failed ({ex.Message}); " + (cached != null ? "using the cached copy" : "no cached copy yet");
                _log("map: " + LastProblem);
                if (cached != null) _tasks[modeId] = cached;
                return cached;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Drops in-memory copies (keeps the disk cache). Called when the map/quest windows close.</summary>
    public void ReleaseMemory()
    {
        _maps.Clear();
        _tasks.Clear();
    }

    private T? LoadCache<T>(string file, Func<T, bool> valid) where T : class
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var fs = File.OpenRead(file);
            var v = JsonSerializer.Deserialize<T>(fs, Json);
            return v != null && valid(v) ? v : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log($"map: cache {Path.GetFileName(file)} unreadable ({ex.GetType().Name})");
            return null;
        }
    }
}
