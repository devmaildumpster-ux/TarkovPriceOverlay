using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovOverlay.Core.Catalog;

/// <summary>Atomic last-good normalized catalog per mode.</summary>
public sealed class CatalogStore
{
    private readonly AppPaths _paths;
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public CatalogStore(AppPaths paths) => _paths = paths;

    public void Save(CatalogSnapshot snapshot) =>
        AppPaths.AtomicWrite(_paths.CatalogFile(snapshot.ModeId), s => JsonSerializer.Serialize(s, snapshot, Json));

    /// <summary>Loads a valid cache or returns null (absent, corrupt, wrong schema, wrong mode, or empty).</summary>
    public CatalogSnapshot? TryLoad(string modeId, out string? problem)
    {
        problem = null;
        var path = _paths.CatalogFile(modeId);
        if (!File.Exists(path)) { problem = "no cache"; return null; }
        try
        {
            using var fs = File.OpenRead(path);
            var snap = JsonSerializer.Deserialize<CatalogSnapshot>(fs, Json);
            if (snap == null || snap.SchemaVersion != CatalogSnapshot.CurrentSchema) { problem = "cache schema changed"; return null; }
            if (!string.Equals(snap.ModeId, modeId, StringComparison.Ordinal)) { problem = "cache mode mismatch"; return null; }
            if (snap.Items.Count == 0) { problem = "cache has no items"; return null; }
            // Everything loaded from disk is "cached" until a refresh replaces it.
            var s = snap.Sections;
            s.Items = Cachedify(s.Items); s.Traders = Cachedify(s.Traders); s.Quests = Cachedify(s.Quests);
            s.Hideout = Cachedify(s.Hideout); s.Crafts = Cachedify(s.Crafts); s.Barters = Cachedify(s.Barters);
            return snap;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problem = "cache unreadable: " + ex.GetType().Name;
            return null;
        }
    }

    private static SectionState Cachedify(SectionState s) => s == SectionState.Missing ? SectionState.Missing : SectionState.Cached;
}
