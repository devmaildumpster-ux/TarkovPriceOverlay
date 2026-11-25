using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.App.Services;

namespace TarkovOverlay.App;

/// <summary>
/// --card-preview out.png [--mode pve] [--expect-lines N] "line 1" ["line 2" ...]: matches the given OCR lines with
/// the cached catalog and renders the hover card the overlay would show (1.2.4 diagnostics; no capture, no window).
/// </summary>
internal static class CardPreview
{
    public static int Run(string[] args)
    {
        int i = Array.IndexOf(args, "--card-preview");
        var rest = args.Skip(i + 1).ToList();
        if (rest.Count < 2) { Console.WriteLine("usage: --card-preview out.png [--mode pve] [--expect-lines N] [--scale 1] [--icon-short NAME] \"line 1\" [\"line 2\"]"); return 1; }
        var outFile = rest[0]; rest.RemoveAt(0);
        string? Take(string name) { int k = rest.IndexOf(name); if (k < 0 || k + 1 >= rest.Count) return null; var v = rest[k + 1]; rest.RemoveRange(k, 2); return v; }
        var mode = GameMode.FromId(Take("--mode"));
        int expect = int.TryParse(Take("--expect-lines"), out var e) ? e : 0;
        double scale = double.TryParse(Take("--scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) && sc > 0 ? sc : 1.5;
        var iconShort = Take("--icon-short");
        bool refresh = rest.Remove("--refresh"); // 1.2.5: try a refresh first (shows the outage notice when tarkov.dev is down)
        bool noFlags = rest.Remove("--no-flags");
        bool expanded = rest.Remove("--expanded");
        var activeQuests = new List<string>();
        var doneQuests = new List<string>();
        var hideoutLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (Take("--active-quest") is { } aq) activeQuests.Add(aq);
        while (Take("--done-quest") is { } dq) doneQuests.Add(dq);
        while (Take("--hideout-level") is { } hl)
        {
            var eq = hl.IndexOf('=');
            if (eq > 0 && int.TryParse(hl[(eq + 1)..], out var lv)) hideoutLevels[hl[..eq].Trim()] = lv;
        }

        using var services = new AppServices();
        if (noFlags) services.Settings.CardShowNeedFlags = false;
        services.Catalog.SwitchModeAsync(mode, refresh: false).GetAwaiter().GetResult();
        if (services.Catalog.Context == null) services.Catalog.RefreshNowAsync().GetAwaiter().GetResult();
        var ctx = services.Catalog.Context;
        if (refresh) services.Catalog.RefreshNowAsync(manual: false).GetAwaiter().GetResult();
        ctx = services.Catalog.Context;
        if (ctx == null) { Console.WriteLine("no catalog"); return 1; }
        var bar = TarkovOverlay.Core.Presentation.PriceStatus.Bar(services.Catalog.State, services.Availability.State, ctx.Snapshot.FetchedAt, services.NewestScan(ctx), DateTimeOffset.UtcNow);
        Console.WriteLine($"status bar ({bar.Level}): {bar.Text}");
        var m = ctx.Items.MatchLines(rest, expect);
        Console.WriteLine($"lines [{string.Join(" / ", rest)}] → {m.Kind} {m.Repair} · {string.Join(" | ", m.Items.Take(3).Select(x => x.Name))} · alternatives {string.Join(" | ", m.Alternatives.Select(x => x.Name))}");
        if (!string.IsNullOrWhiteSpace(iconShort))
        {
            var after = IconShortNameTiebreaker.Apply(ctx.Items, m, new[] { iconShort });
            Console.WriteLine($"icon-short '{iconShort}' → {after.Kind} {after.Repair} · {string.Join(" | ", after.Items.Take(3).Select(x => x.Name))}");
            m = after;
        }
        if (activeQuests.Count > 0 || doneQuests.Count > 0 || hideoutLevels.Count > 0)
            services.Progress = new PreviewProgress(services.Progress, ctx, activeQuests, doneQuests, hideoutLevels);
        var card = services.Cards(ctx).ForMatch(m, expanded: expanded);
        Console.WriteLine($"notice: {card.Notice ?? "(none)"}");
        Console.WriteLine($"card {card.Kind} '{card.Heading}' '{card.Title}' rows {string.Join("; ", card.Rows.Select(r => r.Label + " " + r.Value))} msgs {string.Join("; ", card.Messages)}");
        var el = CardRenderer.Build(card, scale);
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        el.Arrange(new Rect(el.DesiredSize));
        el.UpdateLayout();
        // DesiredSize includes LayoutTransform; ActualWidth does not, and would clip a scaled card.
        int w = Math.Max(1, (int)Math.Ceiling(el.DesiredSize.Width)), h = Math.Max(1, (int)Math.Ceiling(el.DesiredSize.Height));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 44, 40)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outFile)) enc.Save(fs);
        Console.WriteLine($"saved {outFile} ({w}×{h})");
        return 0;
    }

    private sealed class PreviewProgress : TarkovOverlay.Core.Progress.IProgressView
    {
        private readonly TarkovOverlay.Core.Progress.IProgressView _inner;
        private readonly Dictionary<string, TarkovOverlay.Core.Progress.QuestProgressState> _q = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _h = new(StringComparer.OrdinalIgnoreCase);

        public PreviewProgress(TarkovOverlay.Core.Progress.IProgressView inner, CatalogContext ctx,
            IEnumerable<string> activeQuests, IEnumerable<string> doneQuests, Dictionary<string, int> hideoutLevels)
        {
            _inner = inner;
            foreach (var a in activeQuests)
                foreach (var id in ResolveQuests(ctx, a)) _q[id] = TarkovOverlay.Core.Progress.QuestProgressState.Active;
            foreach (var d in doneQuests)
                foreach (var id in ResolveQuests(ctx, d)) _q[id] = TarkovOverlay.Core.Progress.QuestProgressState.Complete;
            foreach (var (k, v) in hideoutLevels)
            {
                string? id = ctx.Snapshot.StationNames.FirstOrDefault(kv =>
                    kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase) || kv.Value.Equals(k, StringComparison.OrdinalIgnoreCase)).Key;
                _h[id ?? k] = v;
            }
        }

        private static IEnumerable<string> ResolveQuests(CatalogContext ctx, string nameOrId)
        {
            foreach (var kv in ctx.Snapshot.TaskNames)
                if (kv.Key.Equals(nameOrId, StringComparison.OrdinalIgnoreCase) || kv.Value.Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
                    yield return kv.Key;
        }

        public bool IsQuestCompleted(string modeId, string questId) =>
            QuestState(modeId, questId) == TarkovOverlay.Core.Progress.QuestProgressState.Complete;

        public TarkovOverlay.Core.Progress.QuestProgressState QuestState(string modeId, string questId) =>
            _q.TryGetValue(questId, out var st) ? st : _inner.QuestState(modeId, questId);

        public int StationLevel(string modeId, string stationId) =>
            _h.TryGetValue(stationId, out var lv) ? lv : _inner.StationLevel(modeId, stationId);
    }
}
