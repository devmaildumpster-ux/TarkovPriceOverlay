using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Services;
using TarkovOverlay.App.Views;
using TarkovOverlay.Core.Ammo;
using TarkovOverlay.Core.Catalog;

namespace TarkovOverlay.App;

/// <summary>
/// --ammo-chart-preview out.png [--mode pve] [--caliber Caliber556x45NATO] [--item M995]: renders the ammo chart
/// (1.3.0 diagnostics; no game, no focus). Uses the cached catalog, or downloads one if it's missing ammo stats.
/// </summary>
internal static class AmmoChartPreview
{
    public static int Run(string[] args)
    {
        int i = Array.IndexOf(args, "--ammo-chart-preview");
        var rest = args.Skip(i + 1).ToList();
        if (rest.Count < 1) { Console.WriteLine("usage: --ammo-chart-preview out.png [--mode pve] [--caliber Caliber556x45NATO] [--item short-name]"); return 1; }
        var outFile = rest[0]; rest.RemoveAt(0);
        string? Take(string name) { int k = rest.IndexOf(name); if (k < 0 || k + 1 >= rest.Count) return null; var v = rest[k + 1]; rest.RemoveRange(k, 2); return v; }
        var mode = GameMode.FromId(Take("--mode"));
        var caliber = Take("--caliber");
        var itemQ = Take("--item");

        using var services = new AppServices();
        services.Catalog.SwitchModeAsync(mode, refresh: false).GetAwaiter().GetResult();
        var ctx = services.Catalog.Context;
        bool hasAmmo = ctx?.Snapshot.Items.Values.Any(x => x.Ammo != null) == true;
        if (ctx == null || !hasAmmo)
        {
            Console.WriteLine("catalog has no ammo stats; refreshing…");
            services.Catalog.RefreshNowAsync(manual: false).GetAwaiter().GetResult();
            ctx = services.Catalog.Context;
        }
        if (ctx == null) { Console.WriteLine("no catalog"); return 1; }
        hasAmmo = ctx.Snapshot.Items.Values.Any(x => x.Ammo != null);
        Console.WriteLine($"catalog {ctx.Snapshot.ModeId} · items {ctx.Snapshot.Items.Count} · ammo {ctx.Snapshot.Items.Values.Count(x => x.Ammo != null)} · {services.Catalog.State.Kind}");
        if (!hasAmmo) { Console.WriteLine("still no ammo stats (tarkov.dev down or an old cache)"); return 2; }

        string? highlight = null;
        if (!string.IsNullOrWhiteSpace(itemQ))
        {
            var hit = ctx.Snapshot.Items.Values.FirstOrDefault(x =>
                string.Equals(x.ShortName, itemQ, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Name, itemQ, StringComparison.OrdinalIgnoreCase)
                || (x.ShortName?.Contains(itemQ, StringComparison.OrdinalIgnoreCase) ?? false)
                || x.Name.Contains(itemQ, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                var round = AmmoChart.RoundOf(hit, ctx.Snapshot);
                if (round?.Ammo != null) { caliber ??= round.Ammo.Caliber; highlight = round.Id; services.SetLastItem(round); }
                Console.WriteLine($"item '{itemQ}' → {hit.Name} ({caliber})");
            }
            else Console.WriteLine($"item '{itemQ}' not found");
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var win = AmmoChartWindow.Open(services, owner: null, caliber: caliber, highlightId: highlight, fromHotkey: false);
        win.ShowActivated = false;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -2000; win.Top = -2000;
        win.UpdateLayout();
        var el = (FrameworkElement)win.Content;
        el.Measure(new Size(win.Width, win.Height - 40));
        el.Arrange(new Rect(el.DesiredSize));
        el.UpdateLayout();
        int w = Math.Max(1, (int)Math.Ceiling(el.ActualWidth));
        int h = Math.Max(1, (int)Math.Ceiling(el.ActualHeight));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Theme.Window, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        using (var fs = File.Create(outFile)) enc.Save(fs);
        Console.WriteLine($"saved {outFile} ({w}×{h}) · caliber {caliber ?? "(first)"}");
        win.Close();
        app.Shutdown();
        return 0;
    }
}
