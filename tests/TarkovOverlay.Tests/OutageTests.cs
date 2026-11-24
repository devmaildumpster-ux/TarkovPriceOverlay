using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TarkovOverlay.Core;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Tests.Fixtures;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>
/// 1.2.5: tarkov.dev outage handling through a fake HTTP handler (real TarkovDevFeedSource, recorded feed bodies).
/// Down detection, backoff, Retry-After, manual-check rate limit, one refresh on recovery, no traffic while down.
/// </summary>
public class OutageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tpo-outage-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private DateTimeOffset _clock = LiveCatalog.FetchedAt;
    private DateTimeOffset Now() => _clock;

    /// <summary>Fake json.tarkov.dev: serves recorded fixtures, or whatever <see cref="Mode"/> says.</summary>
    public sealed class FakeHttp : HttpMessageHandler
    {
        public enum State { Up, Http503, Http502, Http504, Http500, Http429, GraphQlDown, Timeout, Refused, NoDns }
        public State Mode = State.Up;
        public string? RetryAfter;
        public readonly ConcurrentQueue<string> Calls = new();
        private static readonly ConcurrentDictionary<string, byte[]> Bodies = new();

        public static byte[]? Body(string path)
        {
            var parts = path.Trim('/').Split('/');
            var name = parts.Length == 3 ? $"{parts[0]}_prices_{parts[2]}" : $"{parts[0]}_{parts[1]}";
            var file = Path.Combine(LiveCatalog.Dir, name + ".json.gz");
            if (!File.Exists(file)) return null;
            return Bodies.GetOrAdd(file, f => { using var gz = new GZipStream(File.OpenRead(f), CompressionMode.Decompress); using var ms = new MemoryStream(); gz.CopyTo(ms); return ms.ToArray(); });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            Calls.Enqueue($"{req.Method} {path}");
            HttpResponseMessage R(HttpStatusCode c, string body = "") { var r = new HttpResponseMessage(c) { Content = new StringContent(body, Encoding.UTF8, "application/json") }; if (RetryAfter != null) r.Headers.TryAddWithoutValidation("Retry-After", RetryAfter); return r; }
            switch (Mode)
            {
                case State.Http503: return R(HttpStatusCode.ServiceUnavailable, "<html>503</html>");
                case State.Http502: return R(HttpStatusCode.BadGateway);
                case State.Http504: return R(HttpStatusCode.GatewayTimeout);
                case State.Http500: return R(HttpStatusCode.InternalServerError);
                case State.Http429: return R((HttpStatusCode)429);
                case State.GraphQlDown: return R(HttpStatusCode.OK, "{\"errors\":[\"GraphQL server unavailable. Try again later.\"]}");
                case State.Timeout: await Task.Delay(Timeout.Infinite, ct); break;
                case State.Refused: throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
                case State.NoDns: throw new HttpRequestException("No such host is known", new SocketException((int)SocketError.HostNotFound));
            }
            var bytes = Body(path);
            if (bytes == null) return R(HttpStatusCode.NotFound, "{}");
            if (req.Method == HttpMethod.Head)
            {
                var h = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                h.Content.Headers.ContentLength = bytes.Length;
                return h;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }

    private (FakeHttp Http, FeedAvailability Gate, TarkovDevFeedSource Feed) Make(double random = 0.5)
    {
        var http = new FakeHttp();
        var gate = new FeedAvailability(Now, () => random);
        var feed = new TarkovDevFeedSource(http, gate, "https://fake.tarkov.test");
        return (http, gate, feed);
    }

    private static async Task<OutageKind> KindAfterGet(TarkovDevFeedSource feed, string path = "/regular/traders_en")
    {
        try { using var _ = await feed.GetJsonAsync(path, TimeSpan.FromMilliseconds(300), default); }
        catch (Exception) { }
        return feed.Availability.State.Kind;
    }

    [Theory]
    [InlineData(FakeHttp.State.Http503, OutageKind.ServiceDown)]
    [InlineData(FakeHttp.State.Http502, OutageKind.ServiceDown)]
    [InlineData(FakeHttp.State.Http504, OutageKind.ServiceDown)]
    [InlineData(FakeHttp.State.Http500, OutageKind.ServiceDown)]
    [InlineData(FakeHttp.State.Http429, OutageKind.RateLimited)]
    [InlineData(FakeHttp.State.GraphQlDown, OutageKind.ServiceDown)]
    [InlineData(FakeHttp.State.Timeout, OutageKind.ServiceDown)]
    [InlineData(FakeHttp.State.Refused, OutageKind.ServiceDown)]
    [InlineData(FakeHttp.State.NoDns, OutageKind.NoInternet)]
    public async Task Down_detection(FakeHttp.State state, OutageKind expected)
    {
        var (http, gate, feed) = Make();
        http.Mode = state;
        Assert.Equal(expected, await KindAfterGet(feed));
        Assert.False(gate.IsUp);
        Assert.NotNull(gate.State.NextProbeAt);
    }

    [Fact]
    public async Task Not_found_and_partial_errors_are_not_outages()
    {
        var (http, gate, feed) = Make();
        Assert.Equal(OutageKind.None, await KindAfterGet(feed, "/regular/prices/doesnotexist")); // 404 = that one item
        Assert.True(gate.IsUp);
        // GraphQL partial result: data plus errors
        using var partial = System.Text.Json.JsonDocument.Parse("{\"data\":{\"items\":[{\"id\":\"a\"}]},\"errors\":[{\"message\":\"one field failed\"}]}");
        Assert.Null(FeedAvailability.ErrorBodyReason(partial.RootElement));
        using var down = System.Text.Json.JsonDocument.Parse("{\"errors\":[\"GraphQL server unavailable. Try again later.\"]}");
        Assert.Equal("GraphQL server unavailable. Try again later.", FeedAvailability.ErrorBodyReason(down.RootElement));
        using var nullData = System.Text.Json.JsonDocument.Parse("{\"data\":null,\"errors\":[{\"message\":\"Internal server error\"}]}");
        Assert.Equal("Internal server error", FeedAvailability.ErrorBodyReason(nullData.RootElement));
        using var normal = System.Text.Json.JsonDocument.Parse("{\"data\":{\"x\":1}}");
        Assert.Null(FeedAvailability.ErrorBodyReason(normal.RootElement));
    }

    [Fact]
    public void Backoff_schedule_cap_and_jitter_bounds()
    {
        var mid = new FeedAvailability(Now, () => 0.5);
        Assert.Equal(new[] { 1, 2, 5, 10, 15, 15, 15 }, Enumerable.Range(0, 7).Select(i => (int)mid.Delay(i, null).TotalMinutes));
        var hi = new FeedAvailability(Now, () => 0.999999);
        var lo = new FeedAvailability(Now, () => 0.0);
        for (int i = 0; i < 8; i++)
        {
            var b = FeedAvailability.Schedule[Math.Min(i, 4)];
            Assert.InRange(hi.Delay(i, null), b, TimeSpan.FromTicks((long)(b.Ticks * 1.2)) < FeedAvailability.Cap ? TimeSpan.FromTicks((long)(b.Ticks * 1.2)) : FeedAvailability.Cap);
            Assert.True(hi.Delay(i, null) <= FeedAvailability.Cap);
            Assert.InRange(lo.Delay(i, null), TimeSpan.FromTicks((long)(b.Ticks * 0.8)) - TimeSpan.FromMilliseconds(1), b);
        }
        // random spread really varies
        var rng = new Random(7);
        var real = new FeedAvailability(Now, () => rng.NextDouble());
        var spread = Enumerable.Range(0, 50).Select(_ => real.Delay(0, null).TotalSeconds).ToList();
        Assert.True(spread.Max() - spread.Min() > 5);
        Assert.All(spread, s => Assert.InRange(s, 48, 72));
    }

    [Fact]
    public async Task Retry_after_is_respected_and_clamped()
    {
        var (http, gate, feed) = Make();
        http.Mode = FakeHttp.State.Http503; http.RetryAfter = "300";
        await KindAfterGet(feed);
        Assert.Equal(TimeSpan.FromSeconds(300), gate.State.NextProbeAt!.Value - _clock);

        var (http2, gate2, feed2) = Make();
        http2.Mode = FakeHttp.State.Http429; http2.RetryAfter = "7200";
        await KindAfterGet(feed2);
        Assert.Equal(FeedAvailability.MaxRetryAfter, gate2.State.NextProbeAt!.Value - _clock);

        var (http3, gate3, feed3) = Make();
        http3.Mode = FakeHttp.State.Http503; http3.RetryAfter = "5"; // shorter than the schedule: schedule wins
        await KindAfterGet(feed3);
        Assert.Equal(TimeSpan.FromMinutes(1), gate3.State.NextProbeAt!.Value - _clock);
    }

    [Fact]
    public async Task No_requests_while_down_except_probes()
    {
        var (http, gate, feed) = Make();
        var svc = new CatalogService(feed, new CatalogStore(new AppPaths(_root)), Now, gate);
        http.Mode = FakeHttp.State.GraphQlDown;
        await svc.SwitchModeAsync(GameMode.Regular);
        Assert.False(gate.IsUp);
        int afterBurst = http.Calls.Count;
        Assert.InRange(afterBurst, 1, 9); // the one refresh in flight when it went down; never more

        for (int i = 0; i < 5; i++) await svc.RefreshNowAsync(manual: false); // the 20-min timer
        Assert.Null(await svc.GetPriceHistoryAsync("regular", "64b7bbb74b75259c590fa897", default));
        await Assert.ThrowsAsync<FeedUnavailableException>(() => feed.GetJsonAsync("/regular/maps", TimeSpan.FromSeconds(5), default));
        await svc.SwitchModeAsync(GameMode.Pve); // mode switch while down
        Assert.Equal(afterBurst, http.Calls.Count);

        Assert.False(await feed.ProbeAsync(default));
        Assert.All(http.Calls.Skip(afterBurst), c => Assert.Contains("/regular/items", c));
        Assert.InRange(http.Calls.Count - afterBurst, 1, 2); // HEAD (+ a GET only for a tiny body)
    }

    [Fact]
    public async Task Manual_refresh_while_down_is_one_probe_at_most_every_30_s()
    {
        var (http, gate, feed) = Make();
        using var mon = new OutageMonitor(gate, feed.ProbeAsync, (d, ct) => Task.Delay(Timeout.Infinite, ct));
        var svc = new CatalogService(feed, new CatalogStore(new AppPaths(_root)), Now, gate, mon);
        http.Mode = FakeHttp.State.Http503;
        await KindAfterGet(feed);
        int before = http.Calls.Count;
        bool? last = true; TimeSpan wait = default;
        svc.ManualCheckDone += (ok, w) => { last = ok; wait = w; };

        await svc.RefreshNowAsync();
        Assert.False(last);
        Assert.Equal(before + 1, http.Calls.Count); // HEAD → 503
        await svc.RefreshNowAsync(); await svc.RefreshNowAsync(); // double clicks
        Assert.Null(last);
        Assert.InRange(wait, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(30));
        Assert.Equal(before + 1, http.Calls.Count);
        _clock += TimeSpan.FromSeconds(31);
        await svc.RefreshNowAsync();
        Assert.Equal(before + 2, http.Calls.Count);
        Assert.Equal(0, svc.RefreshesStarted);
    }

    [Fact]
    public async Task Recovery_triggers_exactly_one_full_refresh()
    {
        var (http, gate, feed) = Make();
        var go = new TaskCompletionSource();
        var mon = new OutageMonitor(gate, feed.ProbeAsync, async (d, ct) => { await go.Task.WaitAsync(ct); lock (this) _clock += d; });
        var svc = new CatalogService(feed, new CatalogStore(new AppPaths(_root)), Now, gate, mon);
        var logs = new ConcurrentQueue<string>(); gate.Log = logs.Enqueue;
        await svc.SwitchModeAsync(GameMode.Regular); // fresh, cached on disk
        Assert.Equal(DataStateKind.Fresh, svc.State.Kind);
        var good = svc.Context;

        http.Mode = FakeHttp.State.Http503;
        await svc.RefreshNowAsync(manual: false);
        Assert.False(gate.IsUp);
        Assert.Same(good, svc.Context); // last prices still served
        Assert.Contains("unavailable; using last good data", svc.State.Message);
        int refreshes = svc.RefreshesStarted, callsDown = http.Calls.Count;

        http.Mode = FakeHttp.State.Up;
        go.SetResult(); // let time pass: the monitor probes when due
        for (int i = 0; i < 400 && svc.RecoveryRefresh == null; i++) await Task.Delay(10);
        await svc.RecoveryRefresh!;
        await Task.Delay(100);
        Assert.True(gate.IsUp);
        Assert.Equal(refreshes + 1, svc.RefreshesStarted);
        Assert.Equal(1, mon.ProbesRun);
        Assert.Equal(DataStateKind.Fresh, svc.State.Kind);
        Assert.NotSame(good, svc.Context);
        Assert.Contains(logs, l => l.Contains("DOWN"));
        Assert.Contains(logs, l => l.Contains("RECOVERED"));
        Assert.Contains(logs, l => l.Contains("refreshed after the outage"));
        // probe = HEAD items (big) → no GET; then the 9 feed paths once
        Assert.Equal(1 + 9, http.Calls.Count - callsDown);
        mon.Dispose();
    }

    [Fact]
    public async Task Probe_sees_a_200_error_body_as_down()
    {
        var (http, gate, feed) = Make();
        http.Mode = FakeHttp.State.GraphQlDown;
        await KindAfterGet(feed);
        Assert.False(await feed.ProbeAsync(default));
        Assert.False(gate.IsUp);
        Assert.Equal(1, gate.State.FailedProbes);
        http.Mode = FakeHttp.State.Up;
        Assert.True(await feed.ProbeAsync(default));
        Assert.True(gate.IsUp);
    }

    [Fact]
    public async Task Startup_with_cache_during_outage_shows_last_prices_and_banner()
    {
        var store = new CatalogStore(new AppPaths(_root));
        var snap = LiveCatalog.Build("regular");
        snap.FetchedAt = _clock - TimeSpan.FromMinutes(70);
        store.Save(snap);
        _clock += TimeSpan.FromSeconds(1);

        var (http, gate, feed) = Make();
        http.Mode = FakeHttp.State.GraphQlDown;
        var svc = new CatalogService(feed, store, Now, gate);
        await svc.SwitchModeAsync(GameMode.Regular);
        Assert.NotNull(svc.Context);
        Assert.Equal(snap.Items.Count, svc.Context!.Snapshot.Items.Count);
        Assert.Equal(snap.FetchedAt, svc.Context.Snapshot.FetchedAt); // age survives the restart
        Assert.False(gate.IsUp);
        var (text, level) = PriceStatus.Bar(svc.State, gate.State, svc.Context.Snapshot.FetchedAt, PriceStatus.NewestScan(svc.Context.Snapshot), _clock);
        Assert.Equal(StatusLevel.Warn, level);
        Assert.StartsWith("tarkov.dev is down — showing prices from ", text);
        Assert.Contains("(1 h ago) · next check in 60 s", text);
        Assert.Equal("tarkov.dev down · prices from 1 h ago", PriceStatus.CardNote(gate.State, svc.Context.Snapshot.FetchedAt, null, _clock));

        // cards keep their prices and carry the notice
        var card = new CardBuilder(svc.Context, null, Now, CardOptions.Default with { Notice = "tarkov.dev down · prices from 1 h ago" })
            .Full(svc.Context.Snapshot.Items["64b7bbb74b75259c590fa897"], compact: true);
        Assert.NotEmpty(card.Rows);
        Assert.Equal("tarkov.dev down · prices from 1 h ago", card.Notice);
    }

    [Fact]
    public async Task No_cache_during_outage_gives_a_clear_message()
    {
        var (http, gate, feed) = Make();
        http.Mode = FakeHttp.State.NoDns;
        var svc = new CatalogService(feed, new CatalogStore(new AppPaths(_root)), Now, gate);
        await svc.SwitchModeAsync(GameMode.Regular);
        Assert.Null(svc.Context);
        Assert.Equal(DataStateKind.None, svc.State.Kind);
        Assert.Contains("no prices are saved yet", svc.State.Message);
        var (text, level) = PriceStatus.Bar(svc.State, gate.State, null, null, _clock);
        Assert.Equal(StatusLevel.Error, level);
        Assert.StartsWith("No internet connection and no prices are saved yet", text);
        Assert.Equal("No internet", PriceStatus.CardNote(gate.State, null, null, _clock));
    }

    [Fact]
    public void Scans_paused_wording()
    {
        var up = new AvailabilitySnapshot(OutageKind.None, "", null, null, 0, null);
        var st = new DataState("pve", DataStateKind.Fresh, _clock, "", Array.Empty<string>());
        var scan = _clock - TimeSpan.FromHours(10);
        var (text, level) = PriceStatus.Bar(st, up, _clock - TimeSpan.FromMinutes(3), scan, _clock);
        Assert.Equal(StatusLevel.Warn, level);
        Assert.Contains("tarkov.dev's flea scans stopped at", text);
        Assert.Equal("tarkov.dev not updating · last scan 10 h ago", PriceStatus.CardNote(up, _clock, scan, _clock));
        Assert.Null(PriceStatus.CardNote(up, _clock, _clock - TimeSpan.FromMinutes(30), _clock));
        Assert.Equal(StatusLevel.Ok, PriceStatus.Bar(st, up, _clock, _clock - TimeSpan.FromMinutes(30), _clock).Level);
    }
}
