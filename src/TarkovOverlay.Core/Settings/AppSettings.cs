using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovOverlay.Core.Catalog;

namespace TarkovOverlay.Core.Settings;

public sealed class TrackedGoal
{
    public required string GoalKey { get; init; }
    public required string Mode { get; init; }
    public required string Kind { get; init; }            // "quest" | "hideout"
    public required string Title { get; init; }           // quest name or "Station level N"
    public List<string> ItemIds { get; init; } = new();   // all accepted items for the objective
    public string ItemName { get; init; } = "";
    public int Required { get; init; }
    public int Remaining { get; set; }
    public bool FoundInRaid { get; init; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string ModeId { get; set; } = GameMode.Regular.Id;
    /// <summary>1, 2 or 3. Null (fresh or older file) means default 2×.</summary>
    public int? TooltipScale { get; set; }
    public List<TrackedGoal> Tracked { get; set; } = new();
    public double? MainLeft { get; set; }
    public double? MainTop { get; set; }

    /// <summary>Show every quest/craft/barter row on the first press instead of the compact card (default false).</summary>
    public bool AlwaysShowFullList { get; set; }

    /// <summary>Global shortcut registered at start (1.1.4: persisted; toggled in Settings…). Default on.</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>Auto-read on hover: no keypress; read when the mouse rests and a tooltip appears (default on).</summary>
    public bool AutoReadEnabled { get; set; } = true;
    public int? AutoDwellMs { get; set; }
    public int? AutoMaxTooltipWaitMs { get; set; }
    public const int DefaultAutoDwellMs = 350, DefaultAutoMaxTooltipWaitMs = 600;
    [JsonIgnore] public int EffectiveAutoDwellMs => AutoDwellMs is >= 100 and <= 3000 ? AutoDwellMs.Value : DefaultAutoDwellMs;
    [JsonIgnore] public int EffectiveAutoMaxTooltipWaitMs => AutoMaxTooltipWaitMs is >= 100 and <= 3000 ? AutoMaxTooltipWaitMs.Value : DefaultAutoMaxTooltipWaitMs;

    /// <summary>Hide the cursor card when the mouse moves (default true).</summary>
    public bool DismissOnMove { get; set; } = true;
    /// <summary>How far (physical pixels, at 100% scale) the mouse may drift before the card hides. Null = default.</summary>
    public int? MoveTolerancePx { get; set; }

    public const int DefaultMoveTolerancePx = 6;
    public const int MaxMoveTolerancePx = 200;
    [JsonIgnore] public int EffectiveMoveTolerancePx => MoveTolerancePx is { } t && t >= 0 && t <= MaxMoveTolerancePx ? t : DefaultMoveTolerancePx;

    [JsonIgnore] public int EffectiveTooltipScale => TooltipScale is 1 or 2 or 3 ? TooltipScale.Value : 2;

    // ---------------- 1.2.0 ----------------
    /// <summary>Hide quest needs for quests marked Completed and hideout needs for levels already built (default on).</summary>
    public bool FilterNeedsByProgress { get; set; } = true;
    /// <summary>Show quest/hideout needs that don't require Found-in-Raid (default on). Off: FIR needs only.</summary>
    public bool ShowNonFirNeeds { get; set; } = true;
    public bool ShowKappaBadge { get; set; } = true;

    // card fields (all on by default)
    public bool CardShowFlea { get; set; } = true;
    public bool CardShowFleaNet { get; set; } = true;
    public bool CardShowTrader { get; set; } = true;
    public bool CardShowBuyBack { get; set; } = true;
    public bool CardShowPerSlot { get; set; } = true;
    public bool CardShowStack { get; set; } = true;
    public bool CardShowNeeds { get; set; } = true;
    public bool CardShowRecipes { get; set; } = true;

    /// <summary>Cursor card opacity in percent (40–100). Null = default 95.</summary>
    public int? CardOpacityPercent { get; set; }
    public const int DefaultCardOpacityPercent = 95, MinCardOpacityPercent = 40;
    [JsonIgnore] public int EffectiveCardOpacityPercent => CardOpacityPercent is { } o && o >= MinCardOpacityPercent && o <= 100 ? o : DefaultCardOpacityPercent;

    /// <summary>Minimizing the main window hides it to a notification-area (tray) icon (default off).</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Follow PvP/PvE from Tarkov's log ("Session mode: …") (default on).</summary>
    public bool AutoDetectMode { get; set; } = true;

    /// <summary>Extra global shortcuts. Null = default chord; "" = cleared by the user.</summary>
    public string? WikiHotkey { get; set; }
    public bool WikiHotkeyEnabled { get; set; } = true;
    public string? DevHotkey { get; set; }
    public bool DevHotkeyEnabled { get; set; } = true;
    public string? SearchHotkey { get; set; }
    public bool SearchHotkeyEnabled { get; set; } = true;
    /// <summary>1.2.1: switch the map Satellite ↔ Vector from in game (no focus change).</summary>
    public string? MapStyleHotkey { get; set; }
    public bool MapStyleHotkeyEnabled { get; set; } = true;
    /// <summary>1.2.3: the Controls &amp; hotkeys window was shown once on first launch.</summary>
    public bool ShownControlsHelp { get; set; }
    public const string DefaultWikiHotkey = "Ctrl+Alt+W", DefaultDevHotkey = "Ctrl+Alt+D", DefaultSearchHotkey = "Alt+`", DefaultMapStyleHotkey = "Ctrl+Alt+M";
    [JsonIgnore] public string EffectiveMapStyleHotkey => MapStyleHotkey ?? DefaultMapStyleHotkey;

    /// <summary>The extra shortcuts that are on and set, by name (for spotting two actions on one chord).</summary>
    public IEnumerable<(string Name, string Chord)> ExtraHotkeys()
    {
        if (WikiHotkeyEnabled && EffectiveWikiHotkey.Length > 0) yield return ("wiki", EffectiveWikiHotkey);
        if (DevHotkeyEnabled && EffectiveDevHotkey.Length > 0) yield return ("tarkov.dev", EffectiveDevHotkey);
        if (SearchHotkeyEnabled && EffectiveSearchHotkey.Length > 0) yield return ("search", EffectiveSearchHotkey);
        if (MapStyleHotkeyEnabled && EffectiveMapStyleHotkey.Length > 0) yield return ("map style", EffectiveMapStyleHotkey);
    }

    /// <summary>Names of other shortcuts using the same chord as <paramref name="name"/> (case/spacing-insensitive).</summary>
    public List<string> HotkeyClashes(string name)
    {
        var all = ExtraHotkeys().ToList();
        var mine = all.FirstOrDefault(h => h.Name == name).Chord;
        if (mine == null || !Input.KeyChord.TryParse(mine, out var mc)) return new List<string>();
        return all.Where(h => h.Name != name && Input.KeyChord.TryParse(h.Chord, out var oc) && oc.Equals(mc)).Select(h => h.Name).ToList();
    }
    [JsonIgnore] public string EffectiveWikiHotkey => WikiHotkey ?? DefaultWikiHotkey;
    [JsonIgnore] public string EffectiveDevHotkey => DevHotkey ?? DefaultDevHotkey;
    [JsonIgnore] public string EffectiveSearchHotkey => SearchHotkey ?? DefaultSearchHotkey;
}

public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public SettingsStore(AppPaths paths) => _paths = paths;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_paths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_paths.SettingsFile), Json) ?? new AppSettings();
                if (!GameMode.IsKnown(s.ModeId)) s.ModeId = GameMode.Regular.Id;
                if (s.TooltipScale is not (1 or 2 or 3)) s.TooltipScale = null;
                s.Tracked ??= new();
                if (s.AutoDwellMs is not (>= 100 and <= 3000)) s.AutoDwellMs = null;
                if (s.AutoMaxTooltipWaitMs is not (>= 100 and <= 3000)) s.AutoMaxTooltipWaitMs = null;
                if (s.MoveTolerancePx is not { } mt || mt < 0 || mt > AppSettings.MaxMoveTolerancePx) s.MoveTolerancePx = null;
                if (s.CardOpacityPercent is { } op) s.CardOpacityPercent = Math.Clamp(op, AppSettings.MinCardOpacityPercent, 100);
                // a chord that no longer parses falls back to the default ("" = cleared stays cleared)
                if (s.WikiHotkey is { Length: > 0 } w && !Input.KeyChord.TryParse(w, out _)) s.WikiHotkey = null;
                if (s.DevHotkey is { Length: > 0 } d && !Input.KeyChord.TryParse(d, out _)) s.DevHotkey = null;
                if (s.SearchHotkey is { Length: > 0 } q && !Input.KeyChord.TryParse(q, out _)) s.SearchHotkey = null;
                if (s.MapStyleHotkey is { Length: > 0 } m && !Input.KeyChord.TryParse(m, out _)) s.MapStyleHotkey = null;
                foreach (var t in s.Tracked) t.Remaining = Math.Clamp(t.Remaining, 0, Math.Max(0, t.Required));
                return s;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save(AppSettings s)
    {
        lock (_gate)
            AppPaths.AtomicWrite(_paths.SettingsFile, st => JsonSerializer.Serialize(st, s, Json));
    }
}

/// <summary>Manual requirement tracking. Counts are clamped to [0, required]. Keyed by mode-scoped goal key.</summary>
public sealed class Tracker
{
    private readonly AppSettings _s;
    public Tracker(AppSettings s) => _s = s;

    public IEnumerable<TrackedGoal> ForMode(string mode) => _s.Tracked.Where(t => t.Mode == mode);

    public TrackedGoal? Get(string goalKey) => _s.Tracked.FirstOrDefault(t => t.GoalKey == goalKey);

    public TrackedGoal Track(TrackedGoal g)
    {
        var existing = Get(g.GoalKey);
        if (existing != null) return existing;
        g.Remaining = Math.Clamp(g.Remaining, 0, g.Required);
        _s.Tracked.Add(g);
        return g;
    }

    public bool Adjust(string goalKey, int delta)
    {
        var g = Get(goalKey);
        if (g == null) return false;
        g.Remaining = Math.Clamp(g.Remaining + delta, 0, g.Required);
        return true;
    }

    public bool Remove(string goalKey) => _s.Tracked.RemoveAll(t => t.GoalKey == goalKey) > 0;

    /// <summary>Tracked goals in this mode that accept the item and still need items.</summary>
    public IReadOnlyList<TrackedGoal> OpenFor(string mode, string itemId) =>
        _s.Tracked.Where(t => t.Mode == mode && t.Remaining > 0 && t.ItemIds.Contains(itemId)).ToList();
}
