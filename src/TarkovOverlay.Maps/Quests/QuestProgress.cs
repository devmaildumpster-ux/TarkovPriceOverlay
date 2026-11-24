using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovOverlay.Core;

namespace TarkovOverlay.Maps.Quests;

public enum QuestState { None = 0, Active = 1, Complete = 2, Failed = 3 }

/// <summary>Per-mode quest states. Quests not listed are "None" (locked or available, decided by prerequisites).</summary>
public sealed class ModeProgress
{
    public Dictionary<string, QuestState> States { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Objectives marked done (from TarkovTracker or by hand); their spots are hidden on the map.</summary>
    public HashSet<string> DoneObjectives { get; set; } = new(StringComparer.Ordinal);
    public int? PlayerLevel { get; set; }
    /// <summary>"Any" (show both), "USEC" or "BEAR".</summary>
    public string Faction { get; set; } = "Any";
    public DateTimeOffset? LastTrackerSync { get; set; }
    public DateTimeOffset? LastLogEvent { get; set; }

    /// <summary>
    /// Quests the user wants on the map (1.1.2+). Only these are drawn, unless "show every available quest" is on.
    /// Null in files from 1.1.0/1.1.1: then it starts as the quests that are Active (never every available one).
    /// </summary>
    public HashSet<string>? OnMap { get; set; }

    /// <summary>The on-map set (created from the Active quests the first time it's needed).</summary>
    [JsonIgnore]
    public HashSet<string> Pinned => OnMap ??= new HashSet<string>(States.Where(kv => kv.Value == QuestState.Active).Select(kv => kv.Key), StringComparer.Ordinal);

    public QuestState Get(string id) => States.TryGetValue(id, out var s) ? s : QuestState.None;

    /// <summary>
    /// Sets a quest state. A quest that becomes Active (game log, "Mark active", "I'm on this quest", TarkovTracker)
    /// is put on the map; done, failed or reset quests come off it.
    /// </summary>
    public void Set(string id, QuestState s)
    {
        var pins = Pinned;
        var before = Get(id);
        if (s == QuestState.None) States.Remove(id); else States[id] = s;
        if (s == QuestState.Active && before != QuestState.Active) pins.Add(id);
        else if (s != QuestState.Active) pins.Remove(id);
    }
}

public sealed class QuestProgressFile
{
    public int Schema { get; set; } = 1;
    public Dictionary<string, ModeProgress> Modes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>quest-progress.json next to the map cache. Saved atomically; a corrupt file starts fresh (and is kept as .bad).</summary>
public sealed class QuestProgressStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _file;
    private readonly object _gate = new();
    public QuestProgressFile Data { get; }

    public QuestProgressStore(string file)
    {
        _file = file;
        Data = Load(file);
    }

    public event Action<string>? Changed;

    public ModeProgress For(string modeId)
    {
        lock (_gate)
        {
            if (!Data.Modes.TryGetValue(modeId, out var m)) Data.Modes[modeId] = m = new ModeProgress();
            return m;
        }
    }

    public void NotifyChanged(string modeId) { Save(); Changed?.Invoke(modeId); }

    public void Save()
    {
        lock (_gate)
        {
            try { AppPaths.AtomicWrite(_file, s => JsonSerializer.Serialize(s, Data, Json)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static QuestProgressFile Load(string file)
    {
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<QuestProgressFile>(File.ReadAllText(file), Json) ?? new QuestProgressFile();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try { File.Copy(file, file + ".bad", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new QuestProgressFile();
    }
}
