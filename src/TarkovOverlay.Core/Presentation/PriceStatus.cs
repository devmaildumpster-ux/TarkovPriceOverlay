using System.Globalization;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Economics;

namespace TarkovOverlay.Core.Presentation;

public enum StatusLevel { Ok, Warn, Error }

/// <summary>
/// 1.2.5: the wording for price-data health in the top bar, the quick search footer and on cards. Cards keep showing
/// the last prices; these lines only explain how old they are and why.
/// </summary>
public static class PriceStatus
{
    /// <summary>Newest flea scan in a snapshot (null when the feed has no scan times).</summary>
    public static DateTimeOffset? NewestScan(CatalogSnapshot? s) =>
        s == null ? null : s.Items.Values.Select(i => i.LastScan).Where(t => t != null).DefaultIfEmpty(null).Max();

    /// <summary>tarkov.dev answers but its flea scans stopped: the newest scan of ANY item is older than the stale cutoff.</summary>
    public static bool ScansPaused(DateTimeOffset? newestScan, DateTimeOffset now) => newestScan is { } t && now - t > PriceCalculator.StaleAfter;

    public static string Clock(DateTimeOffset t) => t.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);

    public static string In(TimeSpan d) =>
        d <= TimeSpan.FromSeconds(5) ? "now" : d < TimeSpan.FromMinutes(2) ? $"in {(int)Math.Ceiling(d.TotalSeconds)} s" : $"in {(int)Math.Round(d.TotalMinutes)} min";

    private static string Down(OutageKind k) => k switch
    {
        OutageKind.NoInternet => "No internet connection",
        OutageKind.RateLimited => "tarkov.dev is limiting requests",
        _ => "tarkov.dev is down",
    };

    /// <summary>Main window price status (text, level).</summary>
    public static (string Text, StatusLevel Level) Bar(DataState st, AvailabilitySnapshot a, DateTimeOffset? fetchedAt, DateTimeOffset? newestScan, DateTimeOffset now)
    {
        if (!a.IsUp)
        {
            string next = a.NextProbeAt is { } np ? " · next check " + In(np - now) : "";
            return fetchedAt is { } f
                ? ($"{Down(a.Kind)} — showing prices from {Clock(f)} ({Money.Age(now - f)}){next}", StatusLevel.Warn)
                : ($"{Down(a.Kind)} and no prices are saved yet — they load by themselves when it's back{next}", StatusLevel.Error);
        }
        if (fetchedAt != null && ScansPaused(newestScan, now))
            return ($"Prices downloaded {Money.Age(now - fetchedAt.Value)} · tarkov.dev's flea scans stopped at {Clock(newestScan!.Value)} ({Money.Age(now - newestScan.Value)})", StatusLevel.Warn);
        var level = st.Kind switch { DataStateKind.None => StatusLevel.Error, DataStateKind.CachedExpired or DataStateKind.Partial => StatusLevel.Warn, _ => StatusLevel.Ok };
        if (st.Kind == DataStateKind.Cached && st.Message.StartsWith("Refresh failed", StringComparison.Ordinal)) level = StatusLevel.Warn;
        return ("Prices: " + st.Describe(now), level);
    }

    /// <summary>Short line for cards and the search footer, or null when everything is normal.</summary>
    public static string? CardNote(AvailabilitySnapshot a, DateTimeOffset? fetchedAt, DateTimeOffset? newestScan, DateTimeOffset now)
    {
        if (!a.IsUp)
        {
            var who = a.Kind switch { OutageKind.NoInternet => "No internet", OutageKind.RateLimited => "tarkov.dev busy", _ => "tarkov.dev down" };
            return fetchedAt is { } f ? $"{who} · prices from {Money.Age(now - f)}" : who;
        }
        if (ScansPaused(newestScan, now)) return $"tarkov.dev not updating · last scan {Money.Age(now - newestScan!.Value)}";
        return null;
    }
}
