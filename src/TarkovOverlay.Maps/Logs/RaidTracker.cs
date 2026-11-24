using TarkovOverlay.Maps.Extracts;

namespace TarkovOverlay.Maps.Logs;

public enum RaidPhase { Menu, Loading, Matching, Countdown, InRaid }

public sealed record RaidSnapshot(RaidPhase Phase, string? MapNormalizedName, string? SessionMode, PlayerSide? DetectedSide,
    string? ShortId, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);

/// <summary>
/// Raid state machine over <see cref="LogEvent"/>s: which map is loading/loaded, when the raid starts and ends,
/// and a PMC/Scav guess. PMC raids log "GameStarting" (countdown) a few seconds before "GameStarted"; Scav raids
/// start without the countdown (same heuristic TarkovMonitor uses).
/// </summary>
public sealed class RaidTracker
{
    private DateTimeOffset? _startingAt;
    public RaidPhase Phase { get; private set; } = RaidPhase.Menu;
    public string? Map { get; private set; }
    public string? SessionMode { get; private set; }
    public PlayerSide? Side { get; private set; }
    public string? ShortId { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>
    /// The side to filter exits by when the user left the side on Auto: null (= show every exit) in menus — the last
    /// raid's side is stale there — and in PvE, where the countdown heuristic isn't trustworthy (TarkovMonitor doesn't
    /// guess PMC/Scav in PvE either).
    /// </summary>
    public PlayerSide? ExtractSide => Phase == RaidPhase.Menu || IsPve(SessionMode) ? null : Side;

    public static bool IsPve(string? sessionMode) => sessionMode != null && sessionMode.Contains("pve", StringComparison.OrdinalIgnoreCase);

    public RaidSnapshot Snapshot => new(Phase, Map, SessionMode, Side, ShortId, StartedAt, EndedAt);

    /// <summary>Applies one event. Returns true when the snapshot changed.</summary>
    public bool Apply(LogEvent e)
    {
        var before = Snapshot;
        switch (e.Kind)
        {
            case LogEventKind.SessionMode:
                SessionMode = e.Value;
                break;
            case LogEventKind.MapLoading:
                Phase = RaidPhase.Loading;
                Map = MapIds.FromScene(e.Value, e.Extra) ?? Map;
                Side = null; ShortId = null; StartedAt = null; EndedAt = null; _startingAt = null;
                break;
            case LogEventKind.MapLoaded:
                Map = MapIds.FromLocation(e.Value) ?? Map;
                ShortId = e.Extra;
                if (Phase is RaidPhase.Menu or RaidPhase.Loading) Phase = RaidPhase.Matching;
                break;
            case LogEventKind.RaidStarting:
                _startingAt = e.Time;
                Phase = RaidPhase.Countdown;
                break;
            case LogEventKind.RaidStarted:
                StartedAt = e.Time;
                Side = _startingAt is { } s && e.Time is { } t && (t - s).TotalSeconds > 3 ? PlayerSide.Pmc : PlayerSide.Scav;
                Phase = RaidPhase.InRaid;
                break;
            case LogEventKind.MatchingAborted:
                Phase = RaidPhase.Menu;
                break;
            case LogEventKind.RaidExited:
                if (Phase != RaidPhase.Menu) { EndedAt = e.Time; Phase = RaidPhase.Menu; }
                break;
            case LogEventKind.ProfileSelected:
                if (Phase == RaidPhase.InRaid) EndedAt = e.Time;
                if (Phase is RaidPhase.InRaid or RaidPhase.Countdown) Phase = RaidPhase.Menu;
                break;
        }
        return Snapshot != before;
    }
}

/// <summary>EFT location ids / scene bundles → tarkov.dev normalizedName (fallback when the map feed isn't loaded yet).</summary>
public static class MapIds
{
    private static readonly Dictionary<string, string> Locations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["factory4_day"] = "factory", ["factory4"] = "factory", ["factory4_night"] = "night-factory",
        ["bigmap"] = "customs", ["Woods"] = "woods", ["Lighthouse"] = "lighthouse", ["Shoreline"] = "shoreline",
        ["RezervBase"] = "reserve", ["Rezerv_Base"] = "reserve", ["Interchange"] = "interchange", ["Shopping_Mall"] = "interchange",
        ["TarkovStreets"] = "streets-of-tarkov", ["city"] = "streets-of-tarkov", ["laboratory"] = "the-lab",
        ["Sandbox"] = "ground-zero", ["Sandbox_high"] = "ground-zero-21", ["Sandbox_start"] = "ground-zero-tutorial",
        ["Terminal"] = "terminal", ["Labyrinth"] = "the-labyrinth", ["Icebreaker"] = "icebreaker",
        ["laboratory_dark"] = "the-lab-dark", ["laboratory_event"] = "infected-lab", ["factory_event"] = "infected-factory",
        ["factory_day"] = "factory", ["factory_night"] = "night-factory",
    };

    private static readonly Dictionary<string, string> Scenes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["maps/factory_day_preset.bundle"] = "factory", ["maps/factory_preset.bundle"] = "factory",
        ["maps/factory_night_preset.bundle"] = "night-factory", ["maps/customs_preset.bundle"] = "customs",
        ["maps/woods_preset.bundle"] = "woods", ["maps/lighthouse_preset.bundle"] = "lighthouse",
        ["maps/shoreline_preset.bundle"] = "shoreline", ["maps/rezerv_base_preset.bundle"] = "reserve",
        ["maps/shopping_mall.bundle"] = "interchange", ["maps/city_preset.bundle"] = "streets-of-tarkov",
        ["maps/laboratory_preset.bundle"] = "the-lab", ["maps/sandbox_preset.bundle"] = "ground-zero",
        ["maps/sandbox_high_preset.bundle"] = "ground-zero-21", ["maps/sandbox_start_preset.bundle"] = "ground-zero-tutorial",
        ["maps/terminal_preset.bundle"] = "terminal", ["maps/labyrinth_preset.bundle"] = "the-labyrinth",
        ["maps/icebreaker.bundle"] = "icebreaker", ["maps/laboratory_dark_preset.bundle"] = "the-lab-dark",
        ["maps/laboratory_event_preset.bundle"] = "infected-lab", ["maps/factory_event_preset.bundle"] = "infected-factory",
    };

    /// <summary>Optional live lookups from the map feed (scenePath / nameId), consulted first.</summary>
    public static Func<string, string?>? FeedByScene { get; set; }
    public static Func<string, string?>? FeedByLocation { get; set; }

    public static string? FromLocation(string? nameId)
    {
        if (string.IsNullOrWhiteSpace(nameId)) return null;
        return FeedByLocation?.Invoke(nameId) ?? (Locations.TryGetValue(nameId, out var n) ? n : null);
    }

    public static string? FromScene(string? scenePath, string? rcid)
    {
        if (!string.IsNullOrWhiteSpace(scenePath))
        {
            var f = FeedByScene?.Invoke(scenePath);
            if (f != null) return f;
            if (Scenes.TryGetValue(scenePath, out var n)) return n;
        }
        return FromLocation(rcid);
    }
}
