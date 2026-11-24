using System.Text.Json;

namespace TarkovOverlay.Maps.Logs;

/// <summary>
/// EFT logs its key bindings ("Control settings:" + JSON) at start-up. The position dot needs the "MakeScreenshot"
/// action bound to a key. A binding EFT recorded as "SysReq" (the Print Screen key's
/// alternate code, e.g. when bound while Alt was held) reportedly doesn't fire; TarkovMonitor warns about it too.
/// </summary>
public static class ScreenshotKeyCheck
{
    /// <summary>Returns (status, key names): status "ok" / "unbound" / "sysreq" / "axis", or null if not readable.</summary>
    public static (string? Status, string? Keys) FromControlSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        try
        {
            using var d = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (!d.RootElement.TryGetProperty("keyBindings", out var kb) || kb.ValueKind != JsonValueKind.Array) return (null, null);
            foreach (var b in kb.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("keyName", out var kn) || kn.GetString() != "MakeScreenshot") continue;
                var keys = new List<string>();
                bool axis = false;
                if (b.TryGetProperty("variants", out var vars) && vars.ValueKind == JsonValueKind.Array)
                    foreach (var v in vars.EnumerateArray())
                    {
                        if (v.ValueKind != JsonValueKind.Object) continue;
                        if (v.TryGetProperty("isAxis", out var ia) && ia.ValueKind == JsonValueKind.True) axis = true;
                        if (v.TryGetProperty("keyCode", out var kc) && kc.ValueKind == JsonValueKind.Array && kc.GetArrayLength() > 0)
                            keys.Add(string.Join("+", kc.EnumerateArray().Select(k => k.ValueKind == JsonValueKind.String ? k.GetString() : k.ToString())));
                    }
                var joined = keys.Count == 0 ? null : string.Join(" / ", keys);
                if (keys.Count == 0) return (axis ? "axis" : "unbound", joined);
                if (keys.Any(k => !k.Split('+').Contains("SysReq", StringComparer.OrdinalIgnoreCase))) return ("ok", joined);
                return ("sysreq", joined);
            }
            return ("unbound", null);
        }
        catch (JsonException) { return (null, null); }
    }

    public static string Describe(string? status, string? keys) => status switch
    {
        "ok" => $"Tarkov screenshot key: {Pretty(keys)}",
        "unbound" => "Tarkov has NO screenshot key bound (Settings › Controls › \"Screenshot\"), so no position screenshots are made",
        "sysreq" => $"Tarkov's screenshot key is bound as \"SysReq\" ({keys}), which usually doesn't work: rebind \"Screenshot\" in Settings › Controls (press PrtScn without Alt, or pick another key)",
        "axis" => "Tarkov's screenshot action is bound to a mouse axis",
        _ => "Tarkov screenshot key: not seen yet (it's logged when the game starts)",
    };

    private static string Pretty(string? keys) =>
        keys == null ? "?" : string.Join(" / ", keys.Split(" / ").Select(v => string.Join("+", v.Split('+').Select(k => k == "Print" ? "PrtScn" : k))));
}
