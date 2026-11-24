using System.Text.Json;
using TarkovOverlay.Maps.Geometry;

namespace TarkovOverlay.Maps.Data;

/// <summary>Tolerant JSON accessors for the json.tarkov.dev feeds (missing/malformed → null/empty).</summary>
internal static class J
{
    public static JsonElement? Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public static string? Str(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static double? Num(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    public static bool Bool(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.True };

    public static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static IEnumerable<string> Strs(JsonElement e, string name) =>
        Arr(e, name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!);

    /// <summary>"data.{name}" may be an object keyed by id or an array.</summary>
    public static IEnumerable<JsonElement> Rows(JsonElement container) => container.ValueKind switch
    {
        JsonValueKind.Array => container.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object),
        JsonValueKind.Object => container.EnumerateObject().Select(p => p.Value).Where(x => x.ValueKind == JsonValueKind.Object),
        _ => Enumerable.Empty<JsonElement>(),
    };

    public static JsonElement Data(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var d) ? d : default;

    public static Dictionary<string, string> Translations(JsonDocument? doc)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc == null) return map;
        var data = Data(doc);
        if (data.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in data.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString() ?? "";
        return map;
    }

    /// <summary>Translation join; keys without a translation fall back to the key unless it looks like an id key.</summary>
    public static string Tr(string? key, IReadOnlyDictionary<string, string> tr, string fallback)
    {
        if (string.IsNullOrWhiteSpace(key)) return fallback;
        if (tr.TryGetValue(key, out var t) && !string.IsNullOrWhiteSpace(t)) return t.Trim();
        if (key.Length >= 24 && key.Take(24).All(Uri.IsHexDigit)) return fallback;
        return key.Trim();
    }

    public static Vec3? Vec(JsonElement e, string name)
    {
        if (Prop(e, name) is not { ValueKind: JsonValueKind.Object } v) return null;
        return Vec(v);
    }

    public static Vec3? Vec(JsonElement v)
    {
        var x = Num(v, "x"); var z = Num(v, "z");
        if (x == null || z == null) return null;
        return new Vec3(x.Value, Num(v, "y") ?? 0, z.Value);
    }

    public static List<Vec3> Outline(JsonElement e, string name = "outline")
    {
        var res = new List<Vec3>();
        foreach (var p in Arr(e, name)) if (Vec(p) is { } v) res.Add(v);
        return res;
    }
}
