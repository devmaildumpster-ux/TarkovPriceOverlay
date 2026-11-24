using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Links;

namespace TarkovOverlay.App.Services;

/// <summary>Opens an item's Tarkov Wiki / tarkov.dev page in the default browser (validated https only).</summary>
public static class LinkLauncher
{
    public static bool OpenWiki(CatalogItem? item) => item != null && Open(ItemLinks.Wiki(item), "wiki", item.Name);
    public static bool OpenTarkovDev(CatalogItem? item) => item != null && Open(ItemLinks.TarkovDev(item), "tarkov.dev", item.Name);

    private static bool Open(Uri u, string what, string name)
    {
        if (!ItemLinks.IsLaunchable(u)) { Log.Write($"link: refused {what} URL for '{name}': {u}"); return false; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
            Log.Write($"link: opened {what} for '{name}': {u.AbsoluteUri}");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"link: couldn't open {u.AbsoluteUri}: {ex.Message}");
            return false;
        }
    }
}
