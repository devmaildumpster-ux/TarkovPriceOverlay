using System.Text.Json;

namespace TarkovOverlay.Core.Catalog;

public enum DataStateKind { None, Loading, Cached, CachedExpired, Fresh, Partial }

public sealed record DataState(string ModeId, DataStateKind Kind, DateTimeOffset? FetchedAt, string Message, IReadOnlyList<string> MissingSections)
{
    public string Describe(DateTimeOffset now)
    {
        var age = FetchedAt is { } f ? Economics.Money.Age(now - f) : "never";
        return Kind switch
        {
            DataStateKind.None => "No catalog yet. " + Message,
            DataStateKind.Loading => "Loading… " + Message,
            DataStateKind.Cached => $"Cached data (fetched {age}). {Message}".Trim(),
            DataStateKind.CachedExpired => $"Old cached data (fetched {age}). {Message}".Trim(),
            DataStateKind.Fresh => $"Fresh (fetched {age}).",
            DataStateKind.Partial => $"Partial (fetched {age}); unavailable: {string.Join(", ", MissingSections)}. {Message}".Trim(),
            _ => Message,
        };
    }
}

/// <summary>
/// Loads a valid cache first for immediate lookup, then refreshes in the background. Every async result is
/// bound to the mode that started it; stale results are discarded when the mode changes.
/// </summary>
public sealed class CatalogService : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan CacheExpiresAfter = TimeSpan.FromHours(24);

    private readonly IFeedSource _feed;
    private readonly CatalogStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _sync = new();
    private CancellationTokenSource _modeCts = new();
    private GameMode _mode = GameMode.Regular;
    private CatalogContext? _context;
    private DataState _state = new(GameMode.Regular.Id, DataStateKind.None, null, "", Array.Empty<string>());
    private int _generation;

    public event Action<CatalogContext>? ContextChanged;
    public event Action<DataState>? StateChanged;

    private readonly FeedAvailability? _gate;
    private readonly OutageMonitor? _monitor;

    /// <param name="gate">1.2.5: shared tarkov.dev availability gate (null = no outage handling, as before).</param>
    /// <param name="monitor">Probes while down; its recovery triggers exactly one full refresh.</param>
    public CatalogService(IFeedSource feed, CatalogStore store, Func<DateTimeOffset>? now = null, FeedAvailability? gate = null, OutageMonitor? monitor = null)
    {
        _feed = feed; _store = store; _now = now ?? (() => DateTimeOffset.UtcNow);
        _gate = gate; _monitor = monitor;
        if (_monitor != null) _monitor.Recovered += OnRecovered;
    }

    public FeedAvailability? Availability => _gate;
    /// <summary>Full refreshes started (tests: recovery must start exactly one).</summary>
    public int RefreshesStarted => _refreshes;
    private int _refreshes;
    /// <summary>Raised after a manual refresh while down: (probe result or null when rate-limited, wait before the next manual check).</summary>
    public event Action<bool?, TimeSpan>? ManualCheckDone;
    /// <summary>The task of the automatic post-recovery refresh (tests).</summary>
    public Task? RecoveryRefresh { get; private set; }

    private void OnRecovered()
    {
        int gen; CancellationToken token;
        lock (_sync) { gen = _generation; token = _modeCts.Token; }
        RecoveryRefresh = Task.Run(async () =>
        {
            await RefreshAsync(gen, token).ConfigureAwait(false);
            _gate?.Log?.Invoke($"tarkov.dev: prices refreshed after the outage ({State.Kind})");
        });
    }


    public GameMode Mode { get { lock (_sync) return _mode; } }
    public CatalogContext? Context { get { lock (_sync) return _context; } }
    public DataState State { get { lock (_sync) return _state; } }

    /// <summary>Switches mode: cancels in-flight work for the old mode, loads the cache, then refreshes.</summary>
    public async Task SwitchModeAsync(GameMode mode, bool refresh = true)
    {
        CancellationToken token; int gen;
        lock (_sync)
        {
            _modeCts.Cancel();
            _modeCts.Dispose();
            _modeCts = new CancellationTokenSource();
            _mode = mode;
            _context = null;
            gen = ++_generation;
            token = _modeCts.Token;
        }
        SetState(gen, new DataState(mode.Id, DataStateKind.Loading, null, "reading cache", Array.Empty<string>()));
        var cached = await Task.Run(() => _store.TryLoad(mode.Id, out _), token).ConfigureAwait(false);
        if (cached != null)
        {
            var ctx = await Task.Run(() => new CatalogContext(cached, _now), token).ConfigureAwait(false);
            Publish(gen, ctx, CachedState(cached, "Updating in background…"));
        }
        else
        {
            SetState(gen, new DataState(mode.Id, DataStateKind.Loading, null, "no usable cache; downloading", Array.Empty<string>()));
        }
        if (refresh) await RefreshAsync(gen, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicit refresh action (<paramref name="manual"/>) and the 20-minute timer for the current mode. 1.2.5: while
    /// tarkov.dev is down the timer sends nothing and a manual refresh is one availability check (max once per 30 s);
    /// a successful check then triggers the single full refresh.
    /// </summary>
    public async Task RefreshNowAsync(bool manual = true)
    {
        if (_gate is { IsUp: false })
        {
            if (manual && _monitor != null)
            {
                var (ok, wait) = await _monitor.ProbeNowAsync().ConfigureAwait(false);
                _gate.Log?.Invoke(ok is null ? $"tarkov.dev: Refresh prices pressed while down; last check was under 30 s ago (next manual check in {(int)Math.Ceiling(wait.TotalSeconds)} s)"
                    : $"tarkov.dev: Refresh prices pressed while down; check {(ok == true ? "succeeded" : "failed")}");
                ManualCheckDone?.Invoke(ok, wait);
                if (ok == true && RecoveryRefresh is { } rr) await rr.ConfigureAwait(false);
            }
            return;
        }
        int gen; CancellationToken token;
        lock (_sync) { gen = _generation; token = _modeCts.Token; }
        await RefreshAsync(gen, token).ConfigureAwait(false);
    }

    private DataState CachedState(CatalogSnapshot s, string msg)
    {
        bool expired = _now() - s.FetchedAt > CacheExpiresAfter;
        var missing = s.Sections.All.Where(x => x.State == SectionState.Missing).Select(x => x.Name).ToArray();
        var m = missing.Length > 0 ? $"{msg} Unavailable: {string.Join(", ", missing)}." : msg;
        return new DataState(s.ModeId, expired ? DataStateKind.CachedExpired : DataStateKind.Cached, s.FetchedAt, m, missing);
    }

    private async Task RefreshAsync(int gen, CancellationToken ct)
    {
        string mode = Mode.Id;
        CatalogSnapshot? previous = Context?.Snapshot;
        Interlocked.Increment(ref _refreshes);
        try
        {
            var snap = await FetchSnapshotAsync(mode, previous, ct).ConfigureAwait(false);
            if (snap == null)
            {
                // items failed: keep the last good snapshot
                bool down = _gate is { IsUp: false };
                if (previous != null) Publish(gen, null, CachedState(previous, down ? "tarkov.dev unavailable; using last good data." : "Refresh failed; using last good data."));
                else SetState(gen, new DataState(mode, DataStateKind.None, null, down
                    ? "tarkov.dev is unavailable and no prices are saved yet; they load by themselves when it's back."
                    : "Download failed and no cache is available. Check your connection and press Refresh.", Array.Empty<string>()));
                return;
            }
            ct.ThrowIfCancellationRequested();
            var ctx = await Task.Run(() => new CatalogContext(snap, _now), ct).ConfigureAwait(false);
            try { _store.Save(snap); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var missing = snap.Sections.All.Where(x => x.State != SectionState.Fresh).Select(x => x.Name + (x.State == SectionState.Cached ? " (cached)" : "")).ToArray();
            var kind = missing.Length == 0 ? DataStateKind.Fresh : DataStateKind.Partial;
            Publish(gen, ctx, new DataState(mode, kind, snap.FetchedAt, "", missing));
        }
        catch (OperationCanceledException) { /* mode changed or app closing: discard */ }
    }

    /// <summary>Fetches all endpoints. Returns null when the item catalog itself is unavailable.</summary>
    internal async Task<CatalogSnapshot?> FetchSnapshotAsync(string mode, CatalogSnapshot? previous, CancellationToken ct)
    {
        var big = TimeSpan.FromSeconds(60);
        var small = TimeSpan.FromSeconds(20);
        async Task<JsonDocument?> Get(string path, TimeSpan t)
        {
            try { return await _feed.GetJsonAsync(path, t, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return null; }
        }

        var itemsT = Get($"/{mode}/items", big);
        var itemsEnT = Get($"/{mode}/items_en", big);
        var tradersEnT = Get($"/{mode}/traders_en", small);
        var tasksT = Get($"/{mode}/tasks", big);
        var tasksEnT = Get($"/{mode}/tasks_en", small);
        var hideoutT = Get($"/{mode}/hideout", small);
        var hideoutEnT = Get($"/{mode}/hideout_en", small);
        var craftsT = Get($"/{mode}/crafts", small);
        var bartersT = Get($"/{mode}/barters", small);
        await Task.WhenAll(itemsT, itemsEnT, tradersEnT, tasksT, tasksEnT, hideoutT, hideoutEnT, craftsT, bartersT).ConfigureAwait(false);

        using var items = itemsT.Result; using var itemsEn = itemsEnT.Result; using var tradersEn = tradersEnT.Result;
        using var tasks = tasksT.Result; using var tasksEn = tasksEnT.Result; using var hideout = hideoutT.Result;
        using var hideoutEn = hideoutEnT.Result; using var crafts = craftsT.Result; using var barters = bartersT.Result;

        if (items == null || itemsEn == null) return null; // names are required for lookup
        return await Task.Run(() => Build(mode, previous, items, itemsEn, tradersEn, tasks, tasksEn, hideout, hideoutEn, crafts, barters), ct).ConfigureAwait(false);
    }

    internal CatalogSnapshot? Build(string mode, CatalogSnapshot? previous, JsonDocument items, JsonDocument itemsEn, JsonDocument? tradersEn,
        JsonDocument? tasks, JsonDocument? tasksEn, JsonDocument? hideout, JsonDocument? hideoutEn, JsonDocument? crafts, JsonDocument? barters)
    {
        FeedParser.ItemsResult parsed;
        try { parsed = FeedParser.ParseItems(items, itemsEn); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException) { return null; }
        if (parsed.Items.Count == 0) return null;

        var snap = new CatalogSnapshot { ModeId = mode, FetchedAt = _now(), Market = parsed.Market, Items = parsed.Items };
        snap.Sections.Items = SectionState.Fresh;

        // Traders: reuse verified cached names if localization is temporarily missing.
        Dictionary<string, string>? traderNames = null;
        if (tradersEn != null) { try { traderNames = FeedParser.ParseTraderNames(tradersEn); } catch (Exception) { } }
        if (traderNames is { Count: > 0 }) { snap.TraderNames = traderNames; snap.Sections.Traders = SectionState.Fresh; }
        else if (previous?.TraderNames is { Count: > 0 }) { snap.TraderNames = previous.TraderNames; snap.Sections.Traders = SectionState.Cached; }

        if (tasks != null)
        {
            try
            {
                var (q, names) = FeedParser.ParseTasks(tasks, tasksEn);
                snap.Quests = q; snap.TaskNames = names; snap.Sections.Quests = SectionState.Fresh;
            }
            catch (Exception) { }
        }
        if (snap.Quests == null && previous?.Quests != null)
        { snap.Quests = previous.Quests; snap.TaskNames = previous.TaskNames; snap.Sections.Quests = SectionState.Cached; }

        if (hideout != null)
        {
            try
            {
                var (h, names) = FeedParser.ParseHideout(hideout, hideoutEn);
                snap.Hideout = h; snap.StationNames = names; snap.Sections.Hideout = SectionState.Fresh;
            }
            catch (Exception) { }
        }
        if (snap.Hideout == null && previous?.Hideout != null)
        { snap.Hideout = previous.Hideout; snap.StationNames = previous.StationNames; snap.Sections.Hideout = SectionState.Cached; }

        if (crafts != null) { try { snap.Crafts = FeedParser.ParseCrafts(crafts); snap.Sections.Crafts = SectionState.Fresh; } catch (Exception) { } }
        if (snap.Crafts == null && previous?.Crafts != null) { snap.Crafts = previous.Crafts; snap.Sections.Crafts = SectionState.Cached; }

        if (barters != null) { try { snap.Barters = FeedParser.ParseBarters(barters); snap.Sections.Barters = SectionState.Fresh; } catch (Exception) { } }
        if (snap.Barters == null && previous?.Barters != null) { snap.Barters = previous.Barters; snap.Sections.Barters = SectionState.Cached; }

        return snap;
    }

    /// <summary>Price history for the main window; caller binds the result to (mode, item) and ignores obsolete responses.</summary>
    public async Task<List<FeedParser.PricePoint>?> GetPriceHistoryAsync(string mode, string itemId, CancellationToken ct)
    {
        try
        {
            using var doc = await _feed.GetJsonAsync($"/{mode}/prices/{Uri.EscapeDataString(itemId)}", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            return FeedParser.ParsePriceHistory(doc);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    private void Publish(int gen, CatalogContext? ctx, DataState state)
    {
        Action<CatalogContext>? changed = null; Action<DataState>? st;
        lock (_sync)
        {
            if (gen != _generation) return; // obsolete (mode switched)
            if (ctx != null) { _context = ctx; changed = ContextChanged; }
            _state = state;
            st = StateChanged;
        }
        if (ctx != null) changed?.Invoke(ctx);
        st?.Invoke(state);
    }

    private void SetState(int gen, DataState state) => Publish(gen, null, state);

    public void Dispose()
    {
        lock (_sync) { _modeCts.Cancel(); _modeCts.Dispose(); }
    }
}
