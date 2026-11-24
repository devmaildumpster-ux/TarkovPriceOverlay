using System.Net;
using System.Text.Json;

namespace TarkovOverlay.Core.Catalog;

/// <summary>Abstraction over json.tarkov.dev so tests can simulate offline / partial responses.</summary>
public interface IFeedSource
{
    /// <summary>Fetches and parses one JSON path (e.g. "/regular/items"). Throws on failure.</summary>
    Task<JsonDocument> GetJsonAsync(string path, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// The app's single HTTP client for Tarkov.dev with bounded per-request timeouts. 1.2.5: every request goes
/// through <see cref="Availability"/>: while tarkov.dev is down nothing is sent (a
/// <see cref="FeedUnavailableException"/> is thrown at once) and <see cref="ProbeAsync"/> is the only traffic.
/// </summary>
public sealed class TarkovDevFeedSource : IFeedSource, IDisposable
{
    public const string BaseUrl = "https://json.tarkov.dev";
    /// <summary>Test-only override of the feed host (e.g. a local fake endpoint): TPO_FEED_BASE_URL.</summary>
    public const string BaseUrlEnv = "TPO_FEED_BASE_URL";
    private readonly HttpClient _http;

    public FeedAvailability Availability { get; }
    /// <summary>Path checked by the probe (HEAD, then a GET only when the body is tiny).</summary>
    public string ProbePath { get; set; } = "/regular/items";
    /// <summary>Requests actually sent (normal + probes), for diagnostics and tests.</summary>
    public int RequestsSent => _sent;
    private int _sent;

    public TarkovDevFeedSource(HttpMessageHandler? handler = null, FeedAvailability? availability = null, string? baseUrl = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        Availability = availability ?? FeedAvailability.Shared;
        var env = Environment.GetEnvironmentVariable(BaseUrlEnv);
        var url = baseUrl ?? (Uri.TryCreate(env, UriKind.Absolute, out _) ? env! : BaseUrl);
        _http = new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovPriceOverlay/1.0 (+desktop companion)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public Uri BaseAddress => _http.BaseAddress!;

    public async Task<JsonDocument> GetJsonAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var st = Availability.State;
        if (!st.IsUp)
            throw new FeedUnavailableException(st.Kind, $"{FeedAvailability.Describe(st.Kind)} — request not sent ({path})", suppressed: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            Interlocked.Increment(ref _sent);
            using var resp = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var kind = FeedAvailability.Classify(resp.StatusCode);
            if (kind != OutageKind.None)
            {
                var ra = FeedAvailability.RetryAfter(resp, Availability.Now);
                var reason = $"HTTP {(int)resp.StatusCode} on {path}";
                Availability.ReportOutage(kind, reason, ra);
                throw new FeedUnavailableException(kind, reason, ra);
            }
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var doc = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 256 }, cts.Token).ConfigureAwait(false);
            if (FeedAvailability.ErrorBodyReason(doc.RootElement) is { } err)
            {
                doc.Dispose();
                var reason = $"\"{err}\" on {path}";
                Availability.ReportOutage(OutageKind.ServiceDown, reason);
                throw new FeedUnavailableException(OutageKind.ServiceDown, reason);
            }
            return doc;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var reason = $"{path} timed out after {timeout.TotalSeconds:0}s";
            Availability.ReportOutage(OutageKind.ServiceDown, reason);
            throw new TimeoutException(reason);
        }
        catch (HttpRequestException ex) when (ex is not FeedUnavailableException && ex.StatusCode is null)
        {
            // transport failure: DNS / no route (no internet) or refused / reset (service down)
            var kind = FeedAvailability.Classify(ex);
            var reason = $"{ex.Message} ({path})";
            Availability.ReportOutage(kind, reason);
            throw new FeedUnavailableException(kind, reason, inner: ex);
        }
    }

    /// <summary>
    /// One lightweight availability check: HEAD of the item feed (the resource that matters), plus a GET only when
    /// it is tiny (an error body such as {"errors":["… unavailable …"]} served with 200). Reports to the gate.
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            Interlocked.Increment(ref _sent);
            using var head = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, ProbePath), HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var kind = FeedAvailability.Classify(head.StatusCode);
            if (kind != OutageKind.None) { Availability.ReportProbe(false, kind, $"HTTP {(int)head.StatusCode}", FeedAvailability.RetryAfter(head, Availability.Now)); return false; }
            if (!head.IsSuccessStatusCode && head.StatusCode != HttpStatusCode.MethodNotAllowed) { Availability.ReportProbe(false, OutageKind.ServiceDown, $"HTTP {(int)head.StatusCode}"); return false; }
            long? len = head.Content.Headers.ContentLength;
            if (head.IsSuccessStatusCode && len is > 64 * 1024) { Availability.ReportProbe(true); return true; }
            // small or unknown size: read it (an outage body is a few dozen bytes)
            Interlocked.Increment(ref _sent);
            using var get = await _http.GetAsync(ProbePath, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            kind = FeedAvailability.Classify(get.StatusCode);
            if (kind != OutageKind.None || !get.IsSuccessStatusCode) { Availability.ReportProbe(false, kind == OutageKind.None ? OutageKind.ServiceDown : kind, $"HTTP {(int)get.StatusCode}", FeedAvailability.RetryAfter(get, Availability.Now)); return false; }
            await using var stream = await get.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            // only the start matters: an error object is tiny; a real feed starts with {"data":{
            var buf = new byte[4096];
            int n = 0, r;
            while (n < buf.Length && (r = await stream.ReadAsync(buf.AsMemory(n), cts.Token).ConfigureAwait(false)) > 0) n += r;
            var head4k = System.Text.Encoding.UTF8.GetString(buf, 0, n);
            string? err = null;
            if (n < buf.Length)
            {
                try { using var d = JsonDocument.Parse(head4k); err = FeedAvailability.ErrorBodyReason(d.RootElement) ?? (d.RootElement.TryGetProperty("data", out _) ? null : "no data in response"); }
                catch (JsonException) { err = "unreadable response"; }
            }
            else if (!head4k.TrimStart().StartsWith("{\"data\"", StringComparison.Ordinal)) err = "unexpected response";
            if (err != null) { Availability.ReportProbe(false, OutageKind.ServiceDown, err); return false; }
            Availability.ReportProbe(true);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Availability.ReportProbe(false, OutageKind.ServiceDown, "check timed out"); return false; }
        catch (HttpRequestException ex) { Availability.ReportProbe(false, FeedAvailability.Classify(ex), ex.Message); return false; }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// 1.2.5: runs the probe while tarkov.dev is down (scheduled by <see cref="FeedAvailability"/>), and on recovery
/// raises <see cref="Recovered"/> once so the caller does a single full refresh.
/// </summary>
public sealed class OutageMonitor : IDisposable
{
    private readonly FeedAvailability _gate;
    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _one = new(1, 1);
    private Task? _loop;
    private readonly object _lock = new();

    public event Action? Recovered;
    public int ProbesRun => _probes;
    private int _probes;

    public OutageMonitor(FeedAvailability gate, Func<CancellationToken, Task<bool>> probe, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _gate = gate; _probe = probe;
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
        _gate.Changed += s => { if (!s.IsUp) EnsureLoop(); };
        _gate.Recovered += () => Recovered?.Invoke();
        if (!_gate.IsUp) EnsureLoop();
    }

    /// <summary>The background loop (for tests).</summary>
    public Task? Loop { get { lock (_lock) return _loop; } }

    private void EnsureLoop()
    {
        lock (_lock)
        {
            if (_loop is { IsCompleted: false } || _cts.IsCancellationRequested) return;
            _loop = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var s = _gate.State;
                if (s.IsUp) return;
                var wait = (s.NextProbeAt ?? _gate.Now) - _gate.Now;
                if (wait > TimeSpan.Zero) { await _delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct).ConfigureAwait(false); continue; }
                await ProbeOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> ProbeOnceAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gate.IsUp) return true;
            Interlocked.Increment(ref _probes);
            return await _probe(ct).ConfigureAwait(false);
        }
        finally { _one.Release(); }
    }

    /// <summary>
    /// Manual "Refresh prices" while down: one immediate probe unless one ran in the last 30 s.
    /// Returns null when rate-limited (with the wait), else the probe result.
    /// </summary>
    public async Task<(bool? Ok, TimeSpan Wait)> ProbeNowAsync()
    {
        if (_gate.IsUp) return (true, TimeSpan.Zero);
        if (!_gate.TryBeginManualProbe(out var wait)) return (null, wait);
        try { return (await ProbeOnceAsync(_cts.Token).ConfigureAwait(false), TimeSpan.Zero); }
        catch (OperationCanceledException) { return (false, TimeSpan.Zero); }
    }

    public void Dispose() { _cts.Cancel(); }
}
