using System.Text.Json;

namespace TarkovOverlay.Core.Progress;

/// <summary>Quest progress state visible on price/item cards (1.4.0; mirrors the map's quest tracker).</summary>
public enum QuestProgressState { None = 0, Active = 1, Complete = 2, Failed = 3 }

/// <summary>What the price card needs to know about the player's own progress (1.2.0 / 1.4.0).</summary>
public interface IProgressView
{
    /// <summary>True only when the quest is explicitly marked Completed for that mode.</summary>
    bool IsQuestCompleted(string modeId, string questId);
    /// <summary>Full quest state (Active / Complete / Failed / None) for that mode.</summary>
    QuestProgressState QuestState(string modeId, string questId) =>
        IsQuestCompleted(modeId, questId) ? QuestProgressState.Complete : QuestProgressState.None;
    /// <summary>Built level of a hideout station for that mode (0 = not built / unknown).</summary>
    int StationLevel(string modeId, string stationId);
}

/// <summary>Built hideout levels per game mode. hideout-progress.json in %LOCALAPPDATA%\TarkovPriceOverlay.</summary>
public sealed class HideoutProgressFile
{
    public int Schema { get; set; } = 1;
    /// <summary>mode id → station id → built level.</summary>
    public Dictionary<string, Dictionary<string, int>> Modes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class HideoutProgressStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly object _gate = new();
    public HideoutProgressFile Data { get; }
    public event Action<string>? Changed;

    public HideoutProgressStore(string file)
    {
        _file = file;
        Data = Load(file);
    }

    public static string DefaultFile(AppPaths p) => Path.Combine(p.Root, "hideout-progress.json");

    public int Level(string modeId, string stationId)
    {
        lock (_gate) return Data.Modes.TryGetValue(modeId, out var m) && m.TryGetValue(stationId, out var l) ? l : 0;
    }

    /// <summary>Sets a station's built level (clamped to 0..max). Saves and raises Changed.</summary>
    public void SetLevel(string modeId, string stationId, int level, int maxLevel = 10)
    {
        level = Math.Clamp(level, 0, Math.Max(0, maxLevel));
        lock (_gate)
        {
            if (!Data.Modes.TryGetValue(modeId, out var m)) Data.Modes[modeId] = m = new Dictionary<string, int>(StringComparer.Ordinal);
            if (level == 0) m.Remove(stationId); else m[stationId] = level;
        }
        Save();
        Changed?.Invoke(modeId);
    }

    public void Reset(string modeId)
    {
        lock (_gate) Data.Modes.Remove(modeId);
        Save();
        Changed?.Invoke(modeId);
    }

    public void Save()
    {
        lock (_gate)
        {
            try { AppPaths.AtomicWrite(_file, s => JsonSerializer.Serialize(s, Data, Json)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static HideoutProgressFile Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                var d = JsonSerializer.Deserialize<HideoutProgressFile>(File.ReadAllText(file), Json) ?? new HideoutProgressFile();
                d.Modes ??= new(StringComparer.OrdinalIgnoreCase);
                var clean = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (mode, st) in d.Modes)
                    if (st != null) clean[mode] = st.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => Math.Min(kv.Value, 10), StringComparer.Ordinal);
                d.Modes = clean;
                return d;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try { File.Copy(file, file + ".bad", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new HideoutProgressFile();
    }
}

/// <summary>Combines the quest tracker (Maps) and the hideout store into the card's view of progress.</summary>
public sealed class ProgressView : IProgressView
{
    private readonly Func<string, string, bool> _questDone;
    private readonly Func<string, string, QuestProgressState>? _questState;
    private readonly HideoutProgressStore? _hideout;

    public ProgressView(Func<string, string, bool> questCompleted, HideoutProgressStore? hideout,
        Func<string, string, QuestProgressState>? questState = null)
    {
        _questDone = questCompleted; _hideout = hideout; _questState = questState;
    }

    public bool IsQuestCompleted(string modeId, string questId) =>
        _questState != null ? _questState(modeId, questId) == QuestProgressState.Complete : _questDone(modeId, questId);
    public QuestProgressState QuestState(string modeId, string questId) =>
        _questState != null ? _questState(modeId, questId)
        : _questDone(modeId, questId) ? QuestProgressState.Complete : QuestProgressState.None;
    public int StationLevel(string modeId, string stationId) => _hideout?.Level(modeId, stationId) ?? 0;
}
