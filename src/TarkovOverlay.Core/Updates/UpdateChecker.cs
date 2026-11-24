using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TarkovOverlay.Core.Updates;

/// <summary>Major.Minor.Patch[-prerelease] (build metadata ignored). Compares like SemVer 2.0.</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string Pre = "") : IComparable<AppVersion>
{
    public bool IsPrerelease => Pre.Length > 0;

    /// <summary>Parses "v1.2.6", "1.2.6", "1.2", "1.2.6.0" (4th part ignored), "1.2.7-beta.1", "1.2.7+build".</summary>
    public static bool TryParse(string? s, out AppVersion v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        int plus = s.IndexOf('+'); if (plus >= 0) s = s[..plus];
        string pre = "";
        int dash = s.IndexOf('-'); if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; if (pre.Length == 0) return false; }
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var n = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x) || x < 0) return false;
            if (i < 3) n[i] = x;
        }
        v = new AppVersion(n[0], n[1], n[2], pre);
        return true;
    }

    public static AppVersion From(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public int CompareTo(AppVersion o)
    {
        int c = Major.CompareTo(o.Major); if (c != 0) return c;
        c = Minor.CompareTo(o.Minor); if (c != 0) return c;
        c = Patch.CompareTo(o.Patch); if (c != 0) return c;
        if (Pre.Length == 0 && o.Pre.Length == 0) return 0;
        if (Pre.Length == 0) return 1;      // 1.2.7 > 1.2.7-beta
        if (o.Pre.Length == 0) return -1;
        var a = Pre.Split('.'); var b = o.Pre.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            bool bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Pre.Length > 0 ? "-" + Pre : "")}";
}

public enum UpdateStatus { Disabled, UpToDate, Available, Skipped, Failed }

public sealed record UpdateCheckResult(UpdateStatus Status, AppVersion Current, AppVersion? Latest, string Url, string Detail, bool FromCache)
{
    public bool ShowBanner => Status == UpdateStatus.Available;
}

/// <summary>Last successful (or rate-limited) check, saved in update-check.json so launches within 6 h send nothing.</summary>
public sealed class UpdateCheckCache
{
    public DateTimeOffset CheckedAt { get; set; }
    public string? LatestTag { get; set; }
    public string? Url { get; set; }
    /// <summary>True when the check was refused (403/429): no version known, just "don't ask again yet".</summary>
    public bool RateLimited { get; set; }
}

/// <summary>
/// 1.2.6: "is there a newer release?" against GitHub Releases. One unauthenticated GET of releases/latest with a 5 s
/// timeout, at most once per launch and at most every 6 h (cached). The check itself never downloads or installs anything (1.2.8: Update now does, see SelfUpdater); any failure
/// (offline, timeout, 403/429 rate limit, bad JSON) is silent and only logged.
/// </summary>
public sealed class UpdateChecker
{
    public const string Owner = "devmaildumpster-ux", Repo = "TarkovPriceOverlay";
    public static readonly Uri LatestApi = new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
    public static readonly string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases";
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    /// <summary>Test-only: point the check at another releases/latest URL (e.g. a local fake).</summary>
    public const string UrlEnv = "TPO_UPDATE_URL";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly HttpMessageHandler? _handler;
    private readonly Func<DateTimeOffset> _now;
    private readonly string? _cacheFile;
    private readonly Uri _api;
    private int _checked;

    public Action<string>? Log { get; set; }
    public Uri Api => _api;
    /// <summary>True only when <see cref="UrlEnv"/> (or a test) points the check somewhere other than GitHub.</summary>
    public bool IsOverridden => _api != LatestApi;

    public UpdateChecker(string? cacheFile, HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null, Uri? api = null)
    {
        _cacheFile = cacheFile; _handler = handler; _now = now ?? (() => DateTimeOffset.UtcNow);
        var env = Environment.GetEnvironmentVariable(UrlEnv);
        _api = api ?? (Uri.TryCreate(env, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : LatestApi);
    }

    public static AppVersion CurrentVersion => AppVersion.From(typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>The release page to open: GitHub's html_url when it is this repo's https page, else the releases list.</summary>
    public static string SafeUrl(string? htmlUrl) =>
        htmlUrl != null && Uri.TryCreate(htmlUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com"
            && u.AbsolutePath.StartsWith($"/{Owner}/{Repo}/releases", StringComparison.OrdinalIgnoreCase)
            ? u.AbsoluteUri : ReleasesPage;

    /// <summary>
    /// Pure decision from what's known: disabled / failed / up to date / available / skipped by the user.
    /// </summary>
    public static UpdateStatus Decide(bool enabled, AppVersion current, AppVersion? latest, string? skippedVersion)
    {
        if (!enabled) return UpdateStatus.Disabled;
        if (latest is not { } l) return UpdateStatus.Failed;
        if (l.IsPrerelease || l <= current) return UpdateStatus.UpToDate;
        if (AppVersion.TryParse(skippedVersion, out var sk) && sk.CompareTo(l) == 0) return UpdateStatus.Skipped;
        return UpdateStatus.Available;
    }

    /// <summary>
    /// The launch check. <paramref name="force"/> (Settings › Check now) ignores the 6 h cache and the once-per-launch
    /// rule but still sends at most one request per call.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(bool enabled, string? skippedVersion, AppVersion? current = null, bool force = false, CancellationToken ct = default)
    {
        var cur = current ?? CurrentVersion;
        if (!enabled) return new(UpdateStatus.Disabled, cur, null, ReleasesPage, "update check is off", false);
        var now = _now();
        var cache = LoadCache();
        if (!force && cache != null && now - cache.CheckedAt < CacheFor && now >= cache.CheckedAt)
        {
            if (cache.RateLimited) return Done(new(UpdateStatus.Failed, cur, null, ReleasesPage, "GitHub limited requests recently; not asking again yet", true));
            AppVersion? cv = AppVersion.TryParse(cache.LatestTag, out var cvv) ? cvv : null;
            return Done(new(Decide(true, cur, cv, skippedVersion), cur, cv, SafeUrl(cache.Url), $"cached from {cache.CheckedAt:yyyy-MM-dd HH:mm} UTC", true));
        }
        if (!force && Interlocked.Exchange(ref _checked, 1) == 1)
            return new(UpdateStatus.Failed, cur, null, ReleasesPage, "already checked this launch", false);

        try
        {
            using var http = NewClient(Timeout);
            using var req = NewRequest(cur);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                SaveCache(new UpdateCheckCache { CheckedAt = now, RateLimited = true });
                return Done(new(UpdateStatus.Failed, cur, null, ReleasesPage, $"GitHub answered {(int)resp.StatusCode} (rate limited); will try again in 6 h", false));
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return Done(new(UpdateStatus.Failed, cur, null, ReleasesPage, "no published release yet (404)", false));
            if (!resp.IsSuccessStatusCode)
                return Done(new(UpdateStatus.Failed, cur, null, ReleasesPage, $"GitHub answered {(int)resp.StatusCode}", false));
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            string? tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            string? html = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            bool draft = root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
            bool pre = root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            if (!AppVersion.TryParse(tag, out var latest))
                return Done(new(UpdateStatus.Failed, cur, null, ReleasesPage, $"release tag '{tag}' isn't a version", false));
            if (draft || pre)
                return Done(new(UpdateStatus.UpToDate, cur, latest, ReleasesPage, $"latest is a {(draft ? "draft" : "prerelease")} ({tag}); ignored", false));
            SaveCache(new UpdateCheckCache { CheckedAt = now, LatestTag = tag, Url = html });
            return Done(new(Decide(true, cur, latest, skippedVersion), cur, latest, SafeUrl(html), $"latest release {tag}", false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Done(new(UpdateStatus.Failed, cur, null, ReleasesPage, "timed out (5 s)", false));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException or NotSupportedException)
        {
            return Done(new(UpdateStatus.Failed, cur, null, ReleasesPage, $"offline or unreachable ({ex.GetType().Name}: {ex.Message})", false));
        }
    }

    private HttpClient NewClient(TimeSpan timeout)
    {
        var http = _handler != null ? new HttpClient(_handler, disposeHandler: false) : new HttpClient();
        http.Timeout = timeout;
        return http;
    }

    private HttpRequestMessage NewRequest(AppVersion cur)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, _api);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("TarkovPriceOverlay", cur.ToString()));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    /// <summary>
    /// 1.2.8 (Update now): one fresh releases/latest request with the asset list. No cache; throws
    /// <see cref="UpdateException"/> with a plain-words reason on any failure.
    /// </summary>
    public async Task<ReleaseInfo> FetchLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(15));
            using var req = NewRequest(CurrentVersion);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateException($"GitHub is limiting requests right now ({(int)resp.StatusCode}). Try again in a while, or use Open releases page.");
            if (!resp.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)resp.StatusCode} when asked for the latest release.");
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            return ReleaseInfo.Parse(doc.RootElement);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException("GitHub didn't answer in time (15 s)."); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or NotSupportedException)
        {
            throw new UpdateException($"Couldn't reach GitHub ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private UpdateCheckResult Done(UpdateCheckResult r)
    {
        Log?.Invoke($"update check: {r.Status} · this {r.Current} · latest {r.Latest?.ToString() ?? "?"} · {r.Detail}");
        return r;
    }

    private UpdateCheckCache? LoadCache()
    {
        if (_cacheFile == null) return null;
        try { return File.Exists(_cacheFile) ? JsonSerializer.Deserialize<UpdateCheckCache>(File.ReadAllText(_cacheFile)) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private void SaveCache(UpdateCheckCache c)
    {
        if (_cacheFile == null) return;
        try { AppPaths.AtomicWrite(_cacheFile, s => JsonSerializer.Serialize(s, c, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
