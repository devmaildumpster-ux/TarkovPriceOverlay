using System.Globalization;
using System.Text.Json;

namespace TarkovOverlay.Core.Updates;

/// <summary>One file attached to a GitHub release (from the releases API).</summary>
/// <param name="Sha256">Lower-case hex from GitHub's <c>digest</c> field ("sha256:…"), when GitHub provides it.</param>
public sealed record ReleaseAsset(string Name, long Size, string Url, string? Sha256);

/// <summary>1.2.8: the parts of a releases/latest answer that the update check and the self-updater use.</summary>
public sealed record ReleaseInfo(string? Tag, AppVersion? Version, string? HtmlUrl, bool Draft, bool Prerelease, IReadOnlyList<ReleaseAsset> Assets)
{
    public static ReleaseInfo Parse(JsonElement root)
    {
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? tag = Str(root, "tag_name");
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                string? name = Str(a, "name"), url = Str(a, "browser_download_url");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                long size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sz) ? sz : -1;
                assets.Add(new ReleaseAsset(name, size, url, ParseDigest(Str(a, "digest"))));
            }
        }
        return new ReleaseInfo(tag, AppVersion.TryParse(tag, out var v) ? v : null, Str(root, "html_url"),
            root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
            root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True, assets);
    }

    public static ReleaseInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    /// <summary>"sha256:ABC…" → "abc…" (64 hex chars); anything else (other algorithms, junk, null) → null.</summary>
    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        digest = digest.Trim();
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..];
        return IsSha256Hex(hex) ? hex.ToLowerInvariant() : null;
    }

    public static bool IsSha256Hex(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    /// <summary>
    /// Parses a SHA256SUMS.txt ("&lt;64 hex&gt;  &lt;file name&gt;" per line, as written by sha256sum; a '*' before the name
    /// for binary mode is allowed). Returns file name → lower-case hex. Malformed lines are ignored.
    /// </summary>
    public static Dictionary<string, string> ParseSha256Sums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66 || line.StartsWith('#')) continue;
            var hex = line[..64];
            if (!IsSha256Hex(hex) || !char.IsWhiteSpace(line[64])) continue;
            var name = line[65..].TrimStart();
            if (name.StartsWith('*')) name = name[1..];
            name = name.Trim();
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (name.Length > 0) map[name] = hex.ToLowerInvariant();
        }
        return map;
    }

    public static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => (b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => (b / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
    };
}
