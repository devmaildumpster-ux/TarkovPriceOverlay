using System.Net;
using System.Net.Sockets;

namespace TarkovOverlay.Core.Catalog;

/// <summary>Why tarkov.dev can't be used right now.</summary>
public enum OutageKind { None, ServiceDown, RateLimited, NoInternet }

/// <summary>
/// Thrown instead of a request while tarkov.dev is marked down (no network traffic), and for a request that showed
/// the outage. Derives from <see cref="HttpRequestException"/> so existing "download failed" handlers keep working.
/// </summary>
public sealed class FeedUnavailableException : HttpRequestException
{
    public OutageKind Kind { get; }
    public TimeSpan? RetryAfter { get; }
    /// <summary>True when no request was sent (the gate refused it).</summary>
    public bool Suppressed { get; }
    public FeedUnavailableException(OutageKind kind, string message, TimeSpan? retryAfter = null, bool suppressed = false, Exception? inner = null)
        : base(message, inner) { Kind = kind; RetryAfter = retryAfter; Suppressed = suppressed; }
}

public sealed record AvailabilitySnapshot(OutageKind Kind, string Reason, DateTimeOffset? DownSince, DateTimeOffset? NextProbeAt, int FailedProbes, DateTimeOffset? LastProbeAt)
{
    public bool IsUp => Kind == OutageKind.None;
}

/// <summary>
/// 1.2.5: the one availability gate for every tarkov.dev request (prices, items, quests, hideout, crafts, barters,
/// maps/tasks feeds, price history). While down, normal requests are refused without touching the network; only
/// a single lightweight probe runs, on an exponential schedule with jitter (1, 2, 5, 10, then every 15 min),
/// honouring Retry-After. A manual refresh may trigger one immediate probe, at most once per 30 s.
/// </summary>
public sealed class FeedAvailability
{
    public static readonly TimeSpan[] Schedule = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15) };
    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(15);
    /// <summary>Retry-After longer than this is clamped (a misconfigured header must not park the app for a day).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan ManualProbeMinInterval = TimeSpan.FromSeconds(30);
    public const double Jitter = 0.2; // ±20 %, never past the cap

    /// <summary>Process-wide gate used by the app (tests pass their own).</summary>
    public static FeedAvailability Shared { get; } = new();

    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<double> _random;
    private AvailabilitySnapshot _s = new(OutageKind.None, "", null, null, 0, null);

    public FeedAvailability(Func<DateTimeOffset>? now = null, Func<double>? random = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        var rng = new Random();
        _random = random ?? (() => { lock (rng) return rng.NextDouble(); });
    }

    /// <summary>Raised after every state change (outside the lock). Argument: the new state.</summary>
    public event Action<AvailabilitySnapshot>? Changed;
    /// <summary>Raised once when a down state ends (a probe or request succeeded).</summary>
    public event Action? Recovered;
    /// <summary>Log sink (overlay.log in the app).</summary>
    public Action<string>? Log { get; set; }

    public AvailabilitySnapshot State { get { lock (_lock) return _s; } }
    public bool IsUp => State.IsUp;
    public DateTimeOffset Now => _now();

    /// <summary>Delay before probe number <paramref name="failedProbes"/>+1 (0 = first probe after going down).</summary>
    public TimeSpan Delay(int failedProbes, TimeSpan? retryAfter)
    {
        var baseDelay = Schedule[Math.Clamp(failedProbes, 0, Schedule.Length - 1)];
        double f = 1 + (_random() * 2 - 1) * Jitter;
        var d = TimeSpan.FromTicks((long)(baseDelay.Ticks * f));
        if (d > Cap) d = Cap;
        if (retryAfter is { } ra && ra > d) d = ra > MaxRetryAfter ? MaxRetryAfter : ra;
        return d;
    }

    /// <summary>A normal request failed in a way that means the service (or the internet) is unavailable.</summary>
    public void ReportOutage(OutageKind kind, string reason, TimeSpan? retryAfter = null)
    {
        if (kind == OutageKind.None) return;
        AvailabilitySnapshot next; bool wasUp;
        lock (_lock)
        {
            wasUp = _s.IsUp;
            if (!wasUp && _s.NextProbeAt is { } np && retryAfter is null) return; // already down and scheduled
            var now = _now();
            next = new AvailabilitySnapshot(kind, reason, _s.DownSince ?? now, now + Delay(wasUp ? 0 : _s.FailedProbes, retryAfter), _s.FailedProbes, _s.LastProbeAt);
            _s = next;
        }
        Log?.Invoke($"tarkov.dev: {(wasUp ? "DOWN" : "still down")} ({Describe(kind)}: {reason}); normal requests paused; next check {Clock(next.NextProbeAt)}");
        Changed?.Invoke(next);
    }

    /// <summary>Result of a probe (or of a normal request that went through).</summary>
    public void ReportProbe(bool ok, OutageKind kind = OutageKind.ServiceDown, string reason = "", TimeSpan? retryAfter = null)
    {
        AvailabilitySnapshot next; bool recovered = false;
        lock (_lock)
        {
            var now = _now();
            if (ok)
            {
                if (_s.IsUp) { _s = _s with { LastProbeAt = now }; return; }
                recovered = true;
                var downFor = _s.DownSince is { } ds ? now - ds : TimeSpan.Zero;
                Log?.Invoke($"tarkov.dev: RECOVERED after {Economics.Money.Age(downFor).Replace(" ago", "")} ({_s.FailedProbes + 1} check(s)); refreshing prices once");
                next = new AvailabilitySnapshot(OutageKind.None, "", null, null, 0, now);
            }
            else
            {
                int failed = _s.IsUp ? 0 : _s.FailedProbes + 1;
                next = new AvailabilitySnapshot(kind == OutageKind.None ? OutageKind.ServiceDown : kind, reason, _s.DownSince ?? now, now + Delay(failed, retryAfter), failed, now);
                Log?.Invoke($"tarkov.dev: check failed ({Describe(next.Kind)}: {reason}); next check {Clock(next.NextProbeAt)}");
            }
            _s = next;
        }
        Changed?.Invoke(next);
        if (recovered) Recovered?.Invoke();
    }

    /// <summary>True when a scheduled probe is due.</summary>
    public bool ProbeDue { get { lock (_lock) return !_s.IsUp && _s.NextProbeAt is { } t && _now() >= t; } }

    /// <summary>
    /// Manual "Refresh prices" while down: allowed when the last probe was at least 30 s ago. Returns the time
    /// until a manual check is allowed again otherwise.
    /// </summary>
    public bool TryBeginManualProbe(out TimeSpan wait)
    {
        lock (_lock)
        {
            wait = TimeSpan.Zero;
            if (_s.IsUp) return true;
            var since = _s.LastProbeAt is { } lp ? _now() - lp : TimeSpan.MaxValue;
            if (since < ManualProbeMinInterval) { wait = ManualProbeMinInterval - since; return false; }
            _s = _s with { LastProbeAt = _now() }; // reserve the slot so a double click doesn't send two probes
            return true;
        }
    }

    public static string Describe(OutageKind k) => k switch
    {
        OutageKind.ServiceDown => "tarkov.dev is down",
        OutageKind.RateLimited => "tarkov.dev is limiting requests",
        OutageKind.NoInternet => "no internet connection",
        _ => "up",
    };

    private string Clock(DateTimeOffset? t) => t is { } v ? v.ToLocalTime().ToString("HH:mm:ss") + $" (in {(int)Math.Round((v - _now()).TotalSeconds)} s)" : "—";

    // ---------------- classification ----------------

    /// <summary>Outage kind for an HTTP status, or None when the status isn't an outage (404 = that one path).</summary>
    public static OutageKind Classify(HttpStatusCode status) => (int)status switch
    {
        429 => OutageKind.RateLimited,
        >= 500 and <= 599 => OutageKind.ServiceDown,
        408 => OutageKind.ServiceDown,
        _ => OutageKind.None,
    };

    /// <summary>Outage kind for a transport failure (DNS / no route = no internet; refused / reset = service down).</summary>
    public static OutageKind Classify(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is FeedUnavailableException fu) return fu.Kind;
            if (e is SocketException se)
                return se.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData or SocketError.NetworkUnreachable
                        or SocketError.NetworkDown or SocketError.HostUnreachable => OutageKind.NoInternet,
                    _ => OutageKind.ServiceDown,
                };
            if (e is HttpRequestException { StatusCode: { } sc }) return Classify(sc);
            if (e is TimeoutException) return OutageKind.ServiceDown;
        }
        return ex is HttpRequestException ? OutageKind.ServiceDown : OutageKind.None;
    }

    /// <summary>
    /// GraphQL-style failure body: {"errors":[…]} with no usable "data" ("GraphQL server unavailable. Try again
    /// later."). A body with data AND errors is a partial result and is not an outage.
    /// </summary>
    public static string? ErrorBodyReason(System.Text.Json.JsonElement root)
    {
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!root.TryGetProperty("errors", out var errs)) return null;
        bool hasData = root.TryGetProperty("data", out var data) && data.ValueKind is System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array
            && (data.ValueKind != System.Text.Json.JsonValueKind.Object || data.EnumerateObject().Any(p => p.Value.ValueKind != System.Text.Json.JsonValueKind.Null));
        if (hasData) return null;
        string msg = "error response";
        if (errs.ValueKind == System.Text.Json.JsonValueKind.Array && errs.GetArrayLength() > 0)
        {
            var e0 = errs[0];
            msg = e0.ValueKind == System.Text.Json.JsonValueKind.String ? e0.GetString() ?? msg
                : e0.ValueKind == System.Text.Json.JsonValueKind.Object && e0.TryGetProperty("message", out var m) ? m.GetString() ?? msg : msg;
        }
        return msg;
    }

    public static TimeSpan? RetryAfter(HttpResponseMessage resp, DateTimeOffset now)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra == null) return null;
        if (ra.Delta is { } d) return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        if (ra.Date is { } dt) { var x = dt - now; return x < TimeSpan.Zero ? TimeSpan.Zero : x; }
        return null;
    }
}
