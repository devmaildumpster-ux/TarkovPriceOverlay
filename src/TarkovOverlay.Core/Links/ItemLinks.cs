using TarkovOverlay.Core.Catalog;

namespace TarkovOverlay.Core.Links;

/// <summary>
/// 1.2.0: Tarkov Wiki / tarkov.dev pages for an item. Uses the feed's own "wikiLink" / "link" when they are safe
/// https URLs on the expected site, otherwise a search URL built from the item name (never a raw feed string).
/// </summary>
public static class ItemLinks
{
    public const string WikiHost = "escapefromtarkov.fandom.com";
    public const string DevHost = "tarkov.dev";

    public static Uri Wiki(CatalogItem item) =>
        Safe(item.WikiLink, WikiHost) ?? new Uri($"https://{WikiHost}/wiki/Special:Search?query={Uri.EscapeDataString(item.Name)}");

    public static Uri TarkovDev(CatalogItem item) =>
        Safe(item.Link, DevHost)
        ?? (IsSlug(item.NormalizedName) ? new Uri($"https://{DevHost}/item/{item.NormalizedName}") : null)
        ?? new Uri($"https://{DevHost}/?search={Uri.EscapeDataString(item.Name)}");

    /// <summary>
    /// The URL only if it is absolute https, on <paramref name="host"/> (or a subdomain), with no credentials and the
    /// default port. Anything else (http, file:, javascript:, other hosts, blank) → null.
    /// </summary>
    public static Uri? Safe(string? url, string host)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || !string.IsNullOrEmpty(u.UserInfo)) return null;
        var h = u.IdnHost.ToLowerInvariant();
        if (h != host && !h.EndsWith("." + host, StringComparison.Ordinal)) return null;
        return u;
    }

    /// <summary>True for a URL this app is willing to hand to the shell (https on one of the two sites).</summary>
    public static bool IsLaunchable(Uri u) => Safe(u.AbsoluteUri, WikiHost) != null || Safe(u.AbsoluteUri, DevHost) != null;

    private static bool IsSlug(string? s) => !string.IsNullOrEmpty(s) && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
