using System.Text.Json;
using TarkovOverlay.Core;

namespace TarkovOverlay.Maps.Settings;

/// <summary>Map feature settings (map-settings.json, kept apart from the price overlay's settings.json).</summary>
public sealed class MapSettings
{
    /// <summary>2 = 1.1.2+: quests on the map are an explicit selection (see ModeProgress.OnMap).</summary>
    public int Schema { get; set; } = 2;

    // game files
    public string? EftInstallDir { get; set; }
    public bool WatchGameLogs { get; set; } = true;
    public bool UpdateQuestsFromLogs { get; set; } = true;
    public string? ScreenshotsDir { get; set; }
    public bool WatchScreenshots { get; set; } = true;
    /// <summary>1.1.0–1.1.2 checkbox; only used to pick the 1.1.3 cleanup mode on upgrade.</summary>
    public bool DeleteProcessedScreenshots { get; set; }
    /// <summary>"KeepNewest" (default), "DeleteImmediately", "MoveToRecycleBin" or "Off". Null in older files.</summary>
    public string? ScreenshotCleanup { get; set; }
    public int ScreenshotKeepCount { get; set; } = 5;

    [System.Text.Json.Serialization.JsonIgnore]
    public Position.ScreenshotCleanupMode CleanupMode
    {
        get => Enum.TryParse<Position.ScreenshotCleanupMode>(ScreenshotCleanup, true, out var m) ? m
             : DeleteProcessedScreenshots ? Position.ScreenshotCleanupMode.DeleteImmediately : Position.ScreenshotCleanupMode.KeepNewest;
        set { ScreenshotCleanup = value.ToString(); DeleteProcessedScreenshots = value == Position.ScreenshotCleanupMode.DeleteImmediately; }
    }

    // map panel (in the main window; optional normal pop-out window)
    public double? PopLeft { get; set; }
    public double? PopTop { get; set; }
    public double? PopWidth { get; set; }
    public double? PopHeight { get; set; }
    public bool PopMaximized { get; set; }
    /// <summary>Main window: width of the settings + map column relative to the detail column (splitter position).</summary>
    public double? MapColumnStar { get; set; }
    public bool PopFullscreen { get; set; }
    public bool AutoSwitchMap { get; set; } = true;
    public string? LastMap { get; set; }

    // what to draw
    /// <summary>"auto", "pmc" or "scav".</summary>
    public string Side { get; set; } = "auto";
    public bool ShowExtracts { get; set; } = true;
    public bool ShowTransits { get; set; } = true;
    public bool ShowQuests { get; set; } = true;
    /// <summary>Opt-in browse mode: also draw every available (unlocked, not started) quest on the map. Off by default.</summary>
    public bool ShowAllAvailableQuests { get; set; }
    public bool ShowLabels { get; set; } = true;
    /// <summary>1.2.1: draw tarkov.dev's satellite tiles where a map has them (else the vector drawing). Default on, also for upgrades.</summary>
    public bool PreferSatelliteMap { get; set; } = true;
    /// <summary>1.2.2: switch the floor from each new screenshot's height: "all" (default), "underground" or "off".</summary>
    public string MapAutoLayerMode { get; set; } = "all";
    [System.Text.Json.Serialization.JsonIgnore]
    public Definitions.AutoLayerMode AutoLayer
    {
        get => Definitions.AutoLayer.ParseMode(MapAutoLayerMode);
        set => MapAutoLayerMode = Definitions.AutoLayer.ModeId(value);
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public Definitions.MapStyle PreferredStyle => Definitions.MapStyles.FromPreference(PreferSatelliteMap);
    public bool ShowPlayer { get; set; } = true;
    public bool ShowAreaNames { get; set; } = true;
    /// <summary>1.1.0/1.1.1 per-quest "hide" list; folded into the 1.1.2 selection once and then emptied.</summary>
    public List<string> HiddenQuests { get; set; } = new();

    // O-key exit list reader (experimental)
    public bool ReadExitListWithOcr { get; set; }

    // TarkovTracker (optional, read-only)
    public string TrackerDomain { get; set; } = "tarkovtracker.org";
    public string? TrackerToken { get; set; }
    public bool TrackerAutoSync { get; set; } = true;
}

public sealed class MapSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly object _gate = new();
    public MapSettings Settings { get; }

    public MapSettingsStore(string file)
    {
        _file = file;
        Settings = Load(file);
    }

    private static MapSettings Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                var s = JsonSerializer.Deserialize<MapSettings>(File.ReadAllText(file), Json) ?? new MapSettings();
                s.HiddenQuests ??= new();
                if (s.Schema < 2) s.ShowAllAvailableQuests = false; // never carry the old "show everything" default over
                if (s.Side is not ("auto" or "pmc" or "scav")) s.Side = "auto";
                s.CleanupMode = s.CleanupMode; // upgrade: null → Keep newest 5 (or "delete right after" if that box was ticked)
                if (s.ScreenshotKeepCount is < 1 or > 1000) s.ScreenshotKeepCount = 5;
                s.MapAutoLayerMode = Definitions.AutoLayer.ModeId(Definitions.AutoLayer.ParseMode(s.MapAutoLayerMode));
                if (!Quests.TarkovTracker.Domains.Contains(s.TrackerDomain)) s.TrackerDomain = "tarkovtracker.org";
                return s;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new MapSettings();
    }

    public void Save()
    {
        lock (_gate)
        {
            try { AppPaths.AtomicWrite(_file, s => JsonSerializer.Serialize(s, Settings, Json)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
