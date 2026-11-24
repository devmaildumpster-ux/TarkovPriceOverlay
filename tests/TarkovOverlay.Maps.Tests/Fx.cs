using System.Text.Json;
using TarkovOverlay.Maps.Data;

namespace TarkovOverlay.Maps.Tests;

internal static class Fx
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);
    public static JsonDocument Doc(string name) => JsonDocument.Parse(File.ReadAllText(Path(name)));

    private static MapFeed? _maps;
    public static MapFeed Maps => _maps ??= Load();
    private static MapFeed Load()
    {
        using var m = Doc("maps_sample.json");
        using var en = Doc("maps_en_sample.json");
        return MapFeed.Parse(m, en, "regular", DateTimeOffset.UtcNow);
    }

    public static TaskFeed Tasks()
    {
        using var t = Doc("tasks_sample.json");
        using var en = Doc("tasks_en_sample.json");
        using var tr = Doc("traders_en_sample.json");
        return TaskFeed.Parse(t, en, tr, "regular", DateTimeOffset.UtcNow);
    }

    public static TaskInfo Named(this TaskFeed f, string name) => f.Tasks.First(t => t.Name == name);
}
