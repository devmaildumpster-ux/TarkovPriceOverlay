using System.Net;
using System.Text;
using TarkovOverlay.Core;
using TarkovOverlay.Core.Help;
using TarkovOverlay.Core.Settings;
using TarkovOverlay.Core.Updates;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>1.2.6: update check on launch against GitHub Releases.</summary>
public class UpdateCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tpo-upd-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(_dir, "update-check.json");
    public UpdateCheckTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly AppVersion V126 = new(1, 2, 6);

    private sealed class Fake : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new(HttpStatusCode.OK);
        public int Calls;
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++; Last = r;
            return Task.FromResult(Respond(r));
        }
    }

    private static HttpResponseMessage Release(string tag, bool draft = false, bool pre = false, string? url = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"tag_name\":\"{tag}\",\"html_url\":\"{url ?? "https://github.com/devmaildumpster-ux/TarkovPriceOverlay/releases/tag/" + tag}\",\"draft\":{(draft ? "true" : "false")},\"prerelease\":{(pre ? "true" : "false")},\"assets\":[]}}", Encoding.UTF8, "application/json"),
    };

    // ---------------- version compare ----------------
    [Theory]
    [InlineData("v1.2.6", 1, 2, 6, "")]
    [InlineData("1.2.6", 1, 2, 6, "")]
    [InlineData("V10.0.1", 10, 0, 1, "")]
    [InlineData("1.2", 1, 2, 0, "")]
    [InlineData("1.2.6.0", 1, 2, 6, "")]
    [InlineData("v1.3.0-beta.2", 1, 3, 0, "beta.2")]
    [InlineData("1.2.7+abc", 1, 2, 7, "")]
    public void Parse_AcceptsTagForms(string s, int a, int b, int c, string pre)
    {
        Assert.True(AppVersion.TryParse(s, out var v));
        Assert.Equal(new AppVersion(a, b, c, pre), v);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("latest")] [InlineData("v1")] [InlineData("1.x.3")] [InlineData("1.2.3-")] [InlineData("-1.2.3")] [InlineData("1.2.3.4.5")]
    public void Parse_RejectsJunk(string? s) => Assert.False(AppVersion.TryParse(s, out _));

    [Theory]
    [InlineData("1.2.10", "1.2.9")]   // numeric, not string compare
    [InlineData("1.10.0", "1.9.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.2.7", "1.2.7-rc.1")] // release beats its prerelease
    [InlineData("1.2.7-rc.2", "1.2.7-rc.1")]
    [InlineData("1.2.7-rc.10", "1.2.7-rc.9")]
    [InlineData("1.2.7-beta", "1.2.7-alpha")]
    [InlineData("1.2.7-alpha.1", "1.2.7-alpha")]
    public void Compare_IsSemVer(string hi, string lo)
    {
        AppVersion.TryParse(hi, out var h); AppVersion.TryParse(lo, out var l);
        Assert.True(h > l); Assert.True(l < h); Assert.Equal(0, h.CompareTo(h));
    }

    [Fact]
    public void CurrentVersion_IsTheAssemblyVersion_126()
    {
        Assert.Equal(V126, UpdateChecker.CurrentVersion);
        Assert.Equal(V126, AppVersion.From(new Version(1, 2, 6, 0)));
    }

    // ---------------- decision / skip ----------------
    [Fact]
    public void Decide_Rules()
    {
        AppVersion.TryParse("1.2.7", out var v127);
        Assert.Equal(UpdateStatus.Disabled, UpdateChecker.Decide(false, V126, v127, null));
        Assert.Equal(UpdateStatus.Failed, UpdateChecker.Decide(true, V126, null, null));
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Decide(true, V126, V126, null));
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Decide(true, V126, new AppVersion(1, 2, 5), null)); // older release never nags
        Assert.Equal(UpdateStatus.Available, UpdateChecker.Decide(true, V126, v127, null));
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.Decide(true, V126, new AppVersion(1, 3, 0, "beta"), null));
        Assert.Equal(UpdateStatus.Skipped, UpdateChecker.Decide(true, V126, v127, "1.2.7"));
        Assert.Equal(UpdateStatus.Skipped, UpdateChecker.Decide(true, V126, v127, "v1.2.7"));
        Assert.Equal(UpdateStatus.Available, UpdateChecker.Decide(true, V126, new AppVersion(1, 2, 8), "1.2.7")); // a later one shows again
        Assert.Equal(UpdateStatus.Available, UpdateChecker.Decide(true, V126, v127, "garbage"));
    }

    [Fact]
    public void Settings_DefaultOn_AndSkipPersists()
    {
        var paths = new AppPaths(_dir);
        var store = new SettingsStore(paths);
        var s = store.Load();
        Assert.True(s.CheckForUpdates);
        Assert.Null(s.SkippedUpdateVersion);
        s.CheckForUpdates = false; s.SkippedUpdateVersion = "1.2.7";
        store.Save(s);
        var back = store.Load();
        Assert.False(back.CheckForUpdates);
        Assert.Equal("1.2.7", back.SkippedUpdateVersion);
        // an older settings.json without the keys keeps the check on
        File.WriteAllText(paths.SettingsFile, "{\"SchemaVersion\":1}");
        Assert.True(store.Load().CheckForUpdates);
    }

    // ---------------- the request ----------------
    [Fact]
    public async Task UpToDate_AgainstTheSameRelease()
    {
        var f = new Fake { Respond = _ => Release("v1.2.6") };
        var r = await new UpdateChecker(Cache, f).CheckAsync(true, null, V126);
        Assert.Equal(UpdateStatus.UpToDate, r.Status);
        Assert.False(r.ShowBanner);
        Assert.Equal(V126, r.Latest);
        Assert.Equal(1, f.Calls);
        Assert.Equal(UpdateChecker.LatestApi, f.Last!.RequestUri);
        Assert.Contains("TarkovPriceOverlay", f.Last.Headers.UserAgent.ToString());
        Assert.Null(f.Last.Headers.Authorization); // unauthenticated, no token anywhere
    }

    [Fact]
    public async Task Newer_ShowsBanner_WithReleasePage()
    {
        var f = new Fake { Respond = _ => Release("v1.2.7") };
        var r = await new UpdateChecker(Cache, f).CheckAsync(true, null, V126);
        Assert.Equal(UpdateStatus.Available, r.Status);
        Assert.True(r.ShowBanner);
        Assert.Equal("1.2.7", r.Latest.ToString());
        Assert.Equal("https://github.com/devmaildumpster-ux/TarkovPriceOverlay/releases/tag/v1.2.7", r.Url);
    }

    [Fact]
    public async Task Skipped_NoBanner()
    {
        var f = new Fake { Respond = _ => Release("v1.2.7") };
        var r = await new UpdateChecker(Cache, f).CheckAsync(true, "1.2.7", V126);
        Assert.Equal(UpdateStatus.Skipped, r.Status);
        Assert.False(r.ShowBanner);
    }

    [Fact]
    public async Task ForeignReleaseUrl_FallsBackToReleasesPage()
    {
        var f = new Fake { Respond = _ => Release("v1.2.7", url: "https://evil.example/download.exe") };
        var r = await new UpdateChecker(Cache, f).CheckAsync(true, null, V126);
        Assert.Equal(UpdateChecker.ReleasesPage, r.Url);
        Assert.Equal(UpdateChecker.ReleasesPage, UpdateChecker.SafeUrl("http://github.com/devmaildumpster-ux/TarkovPriceOverlay/releases/tag/v9"));
        Assert.Equal(UpdateChecker.ReleasesPage, UpdateChecker.SafeUrl("https://github.com/someone/else/releases/tag/v9"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DraftOrPrerelease_Ignored(bool draft, bool pre)
    {
        var f = new Fake { Respond = _ => Release("v9.0.0", draft, pre) };
        var r = await new UpdateChecker(Cache, f).CheckAsync(true, null, V126);
        Assert.Equal(UpdateStatus.UpToDate, r.Status);
        Assert.False(r.ShowBanner);
    }

    [Fact]
    public async Task Disabled_SendsNothing()
    {
        var f = new Fake { Respond = _ => Release("v9.0.0") };
        var r = await new UpdateChecker(Cache, f).CheckAsync(false, null, V126);
        Assert.Equal(UpdateStatus.Disabled, r.Status);
        Assert.Equal(0, f.Calls);
    }

    // ---------------- failures are silent ----------------
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task HttpErrors_FailSilently(HttpStatusCode code)
    {
        var f = new Fake { Respond = _ => new HttpResponseMessage(code) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") } };
        var r = await new UpdateChecker(Cache, f).CheckAsync(true, null, V126);
        Assert.Equal(UpdateStatus.Failed, r.Status);
        Assert.False(r.ShowBanner);
    }

    [Fact]
    public async Task RateLimit_BacksOffFor6Hours()
    {
        var now = DateTimeOffset.Parse("2026-11-24T12:00:00Z");
        var f = new Fake { Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) };
        await new UpdateChecker(Cache, f, () => now).CheckAsync(true, null, V126);
        Assert.Equal(1, f.Calls);
        // next launch an hour later: nothing sent
        var r = await new UpdateChecker(Cache, f, () => now.AddHours(1)).CheckAsync(true, null, V126);
        Assert.Equal(UpdateStatus.Failed, r.Status);
        Assert.Equal(1, f.Calls);
        // after 6 h it asks again
        f.Respond = _ => Release("v1.2.6");
        r = await new UpdateChecker(Cache, f, () => now.AddHours(6.1)).CheckAsync(true, null, V126);
        Assert.Equal(UpdateStatus.UpToDate, r.Status);
        Assert.Equal(2, f.Calls);
    }

    [Fact]
    public async Task Offline_FailsSilently_AndDoesNotBlockTheNextLaunch()
    {
        var f = new Fake { Respond = _ => throw new HttpRequestException("No such host is known.", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)) };
        var r = await new UpdateChecker(Cache, f).CheckAsync(true, null, V126);
        Assert.Equal(UpdateStatus.Failed, r.Status);
        Assert.Contains("offline", r.Detail);
        Assert.False(File.Exists(Cache)); // not cached: the next launch tries again
        f.Respond = _ => Release("v1.2.7");
        Assert.Equal(UpdateStatus.Available, (await new UpdateChecker(Cache, f).CheckAsync(true, null, V126)).Status);
    }

    [Fact]
    public async Task Timeout_FailsSilently()
    {
        var f = new Fake { Respond = _ => throw new TaskCanceledException("timeout", new TimeoutException()) };
        var r = await new UpdateChecker(Cache, f).CheckAsync(true, null, V126);
        Assert.Equal(UpdateStatus.Failed, r.Status);
        Assert.Contains("timed out", r.Detail);
        Assert.Equal(TimeSpan.FromSeconds(5), UpdateChecker.Timeout);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"tag_name\":\"latest\"}")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task BadBody_FailsSilently(string body)
    {
        var f = new Fake { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) } };
        var r = await new UpdateChecker(Cache, f).CheckAsync(true, null, V126);
        Assert.Equal(UpdateStatus.Failed, r.Status);
        Assert.False(r.ShowBanner);
    }

    // ---------------- once per launch, 6 h cache ----------------
    [Fact]
    public async Task OncePerLaunch()
    {
        var f = new Fake { Respond = _ => Release("v1.2.7") };
        var u = new UpdateChecker(null, f);
        await u.CheckAsync(true, null, V126);
        var second = await u.CheckAsync(true, null, V126);
        Assert.Equal(1, f.Calls);
        Assert.False(second.ShowBanner);
        // Settings › Check now (force) may ask again
        Assert.Equal(UpdateStatus.Available, (await u.CheckAsync(true, null, V126, force: true)).Status);
        Assert.Equal(2, f.Calls);
    }

    [Fact]
    public async Task Cache_6Hours_ReusesTheLastAnswer()
    {
        var now = DateTimeOffset.Parse("2026-11-24T12:00:00Z");
        var f = new Fake { Respond = _ => Release("v1.2.7") };
        Assert.Equal(UpdateStatus.Available, (await new UpdateChecker(Cache, f, () => now).CheckAsync(true, null, V126)).Status);
        var cached = await new UpdateChecker(Cache, f, () => now.AddHours(5)).CheckAsync(true, null, V126);
        Assert.Equal(1, f.Calls);
        Assert.True(cached.FromCache);
        Assert.Equal(UpdateStatus.Available, cached.Status); // banner still shows from the cache
        // after updating to 1.2.7, the cached answer means "up to date"
        Assert.Equal(UpdateStatus.UpToDate, (await new UpdateChecker(Cache, f, () => now.AddHours(5)).CheckAsync(true, null, new AppVersion(1, 2, 7))).Status);
        // skip applies to the cached answer too
        Assert.Equal(UpdateStatus.Skipped, (await new UpdateChecker(Cache, f, () => now.AddHours(5)).CheckAsync(true, "1.2.7", V126)).Status);
        await new UpdateChecker(Cache, f, () => now.AddHours(7)).CheckAsync(true, null, V126);
        Assert.Equal(2, f.Calls);
        // a clock set backwards doesn't freeze the cache
        await new UpdateChecker(Cache, f, () => now.AddDays(-2)).CheckAsync(true, null, V126);
        Assert.Equal(3, f.Calls);
    }

    [Fact]
    public async Task CorruptCache_IsIgnored()
    {
        File.WriteAllText(Cache, "{{{ nope");
        var f = new Fake { Respond = _ => Release("v1.2.6") };
        Assert.Equal(UpdateStatus.UpToDate, (await new UpdateChecker(Cache, f).CheckAsync(true, null, V126)).Status);
        Assert.Equal(1, f.Calls);
    }

    [Fact]
    public void TestOverride_EnvVarPointsTheCheckElsewhere()
    {
        var old = Environment.GetEnvironmentVariable(UpdateChecker.UrlEnv);
        try
        {
            Environment.SetEnvironmentVariable(UpdateChecker.UrlEnv, "http://127.0.0.1:9/releases/latest");
            Assert.Equal("http://127.0.0.1:9/releases/latest", new UpdateChecker(null).Api.AbsoluteUri);
            Environment.SetEnvironmentVariable(UpdateChecker.UrlEnv, "file:///c:/x");
            Assert.Equal(UpdateChecker.LatestApi, new UpdateChecker(null).Api);
        }
        finally { Environment.SetEnvironmentVariable(UpdateChecker.UrlEnv, old); }
    }

    [Fact]
    public void ControlsHelp_MentionsTheUpdateBanner()
    {
        var all = ControlsHelp.Build(new AppSettings()).SelectMany(x => x.Rows).Select(r => r.Keys + " " + r.Action).ToList();
        Assert.Contains(all, r => r.Contains("Update available", StringComparison.Ordinal));
    }
}
