using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using TarkovOverlay.Core;
using TarkovOverlay.Core.Catalog;

namespace TarkovOverlay.Tests.Fixtures;

/// <summary>
/// FIXTURE KIND: recorded live catalog. Real json.tarkov.dev responses (regular + pve) saved gzip'd under
/// tests/fixtures/live with their fetch time in FETCHED_AT_UTC.txt. Assertions use real catalog IDs.
/// </summary>
public static class LiveCatalog
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures", "live");

    public static DateTimeOffset FetchedAt { get; } = DateTimeOffset.Parse(
        File.ReadAllText(Path.Combine(Dir, "FETCHED_AT_UTC.txt")).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public static JsonDocument Doc(string mode, string name)
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(Dir, $"{mode}_{name}.json.gz")), CompressionMode.Decompress);
        return JsonDocument.Parse(gz);
    }

    private static readonly Lazy<CatalogSnapshot> _regular = new(() => Build("regular"));
    private static readonly Lazy<CatalogSnapshot> _pve = new(() => Build("pve"));
    private static readonly Lazy<CatalogContext> _regularCtx = new(() => new CatalogContext(_regular.Value, () => FetchedAt));
    public static CatalogSnapshot Regular => _regular.Value;
    public static CatalogSnapshot Pve => _pve.Value;
    public static CatalogContext RegularContext => _regularCtx.Value;

    public static CatalogSnapshot Build(string mode)
    {
        var svc = new CatalogService(new FixtureFeed(mode), new CatalogStore(new AppPaths(Path.GetTempPath())), () => FetchedAt);
        using var items = Doc(mode, "items"); using var en = Doc(mode, "items_en"); using var tr = Doc(mode, "traders_en");
        using var t = Doc(mode, "tasks"); using var ten = Doc(mode, "tasks_en"); using var h = Doc(mode, "hideout");
        using var hen = Doc(mode, "hideout_en"); using var c = Doc(mode, "crafts"); using var b = Doc(mode, "barters");
        return svc.Build(mode, null, items, en, tr, t, ten, h, hen, c, b) ?? throw new InvalidOperationException("fixture build failed");
    }
}

/// <summary>Feed source backed by the recorded fixtures, with optional failing paths to simulate outages.</summary>
public sealed class FixtureFeed : IFeedSource
{
    private readonly string? _onlyMode;
    public HashSet<string> Failing { get; } = new();
    public bool Offline { get; set; }
    public List<string> Requested { get; } = new();

    public FixtureFeed(string? onlyMode = null) => _onlyMode = onlyMode;

    public Task<JsonDocument> GetJsonAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        lock (Requested) Requested.Add(path);
        if (Offline) throw new HttpRequestException("offline (simulated)");
        var parts = path.Trim('/').Split('/');
        if (Failing.Contains(parts[^1]) || Failing.Contains(path)) throw new HttpRequestException("simulated failure: " + path);
        if (parts.Length == 3 && parts[1] == "prices")
            return Task.FromResult(LiveCatalog.Doc(parts[0], $"prices_{parts[2]}"));
        return Task.FromResult(LiveCatalog.Doc(parts[0], parts[1]));
    }
}
