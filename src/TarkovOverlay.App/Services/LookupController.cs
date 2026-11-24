using System.Diagnostics;
using System.Windows.Threading;
using TarkovOverlay.App.Native;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Geometry;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Core.Recognition;
using TarkovOverlay.Core.Settings;

namespace TarkovOverlay.App.Services;

/// <summary>Everything about the last hover lookup, kept in memory only (for the diagnostics panel).</summary>
public sealed record LookupReport(
    DateTime At, (int X, int Y) Cursor, MonitorSnapshot Monitor, PixelRect FirstCrop, PixelRect? WideCrop,
    DetectionResult? Detection, RecognitionOutcome? Outcome, TimeSpan Elapsed, string Summary, RgbImage? Panel,
    StackReading? Stack = null);

/// <summary>
/// One hover lookup at a time: snapshot cursor + monitor → hide our card (50 ms compositor delay) → capture
/// around the cursor → detect the tooltip → (140 ms, wider crop retry) → hybrid OCR on the panel only → card.
/// Results are discarded if the cursor moved, the mode changed, or the app is closing.
/// </summary>
public sealed class LookupController
{
    private readonly CatalogService _catalog;
    private readonly HoverRecognizer _recognizer;
    private readonly StackCountReader? _stackReader;
    private readonly TraderOfferReader? _offerReader;
    private readonly CursorCardWindow _card;
    private readonly Func<AppSettings> _settings;
    private readonly Func<Tracker> _tracker;
    private readonly Dispatcher _ui;
    private int _busy;
    private volatile bool _closing;

    public event Action<LookupReport>? Completed;
    public event Action<MatchResult>? CandidatesFound;

    public LookupController(CatalogService catalog, HoverRecognizer recognizer, CursorCardWindow card, Func<AppSettings> settings, Func<Tracker> tracker, Dispatcher ui, StackCountReader? stackReader = null, TraderOfferReader? offerReader = null)
    {
        _stackReader = stackReader; _offerReader = offerReader;
        _catalog = catalog; _recognizer = recognizer; _card = card; _settings = settings; _tracker = tracker; _ui = ui;
    }

    public void Close() => _closing = true;

    /// <summary>The last verified full card, kept so a second press can expand/collapse it without new OCR.</summary>
    private sealed class Shown
    {
        public required CatalogContext Ctx { get; init; }
        public required MatchResult Match { get; init; }
        public StackReading? Stack { get; init; }
        public long? Offer { get; init; }
        public (int X, int Y) Origin { get; init; }
        public bool Expanded { get; set; }
    }
    private Shown? _shown;
    /// <summary>Expanded cards stay up longer (still hidden by mouse movement) so long lists can be read.</summary>
    public static readonly TimeSpan ExpandedTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Second press on the same visible card: toggle expanded ↔ compact. Returns true if handled.</summary>
    private bool TryToggle()
    {
        var s = _shown;
        if (s == null || !_card.IsShowingFor(s) || _catalog.Context != s.Ctx) return false;
        s.Expanded = !s.Expanded;
        var model = Builder(s.Ctx).ForMatch(s.Match, s.Stack, s.Expanded, s.Offer);
        var st = _settings();
        _card.ShowCard(model, s.Origin, st.EffectiveTooltipScale, st.DismissOnMove, st.EffectiveMoveTolerancePx,
            timeout: s.Expanded ? ExpandedTimeout : null, reason: s.Expanded ? "expand" : "collapse", key: s);
        Log.Write($"card {(s.Expanded ? "expanded" : "collapsed")} for '{s.Match.Items.FirstOrDefault()?.Name}' (no new OCR)");
        return true;
    }

    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <summary>Supplies a clean resting frame (taken before the tooltip appeared) for the stack count, if any.</summary>
    public Func<(int X, int Y), HoverSnapshot?>? Snapshot { get; set; }

    /// <summary>The auto-read probe's frame with the tooltip in it (used when the lookup's own capture misses it).</summary>
    public Func<(int X, int Y), ProbeFrame?>? ProbeFrame { get; set; }

    /// <summary>1.2.0: builds cards with the user's field/filter settings (default: plain builder).</summary>
    public Func<CatalogContext, CardBuilder>? Cards { get; set; }
    /// <summary>1.2.0: the item a card was just shown for (Wiki / tarkov.dev shortcuts use the last one).</summary>
    public Action<CatalogItem>? ItemShown { get; set; }
    private CardBuilder Builder(CatalogContext ctx) => Cards?.Invoke(ctx) ?? new CardBuilder(ctx, _tracker());

    /// <summary>Hotkey: expand/collapse the visible card, otherwise a manual lookup.</summary>
    public void Trigger() => Run(auto: false);

    /// <summary>
    /// Auto-read on hover (a tooltip was already seen): never toggles, and stays silent for "no tooltip" /
    /// "no match" / errors so game UI tooltips that are not items don't spam cards.
    /// </summary>
    public void TriggerAuto() => Run(auto: true);

    private async void Run(bool auto)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // one active hover lookup
        if (!auto)
            try { if (TryToggle()) { Interlocked.Exchange(ref _busy, 0); return; } }
        catch (Exception ex) { Log.Write("toggle failed: " + ex.Message); }
        _shown = null;
        var sw = Stopwatch.StartNew();
        var origin = Desktop.Cursor();
        var monitor = Desktop.MonitorAt(origin.X, origin.Y);
        var mode = _catalog.Mode;
        var ctx = _catalog.Context;
        PixelRect first = CaptureGeometry.First(origin.X, origin.Y, monitor);
        PixelRect? wide = null;
        DetectionResult? det = null;
        RecognitionOutcome? outcome = null;
        RgbImage? panel = null;
        StackReading? stack = null;
        PixelRect capRect = first;
        string summary = "";
        using var cts = new CancellationTokenSource();
        int staleTol = DismissPolicy.LookupTolerancePx(_settings().EffectiveMoveTolerancePx, monitor.DpiScale);
        bool Stale()
        {
            var c = Desktop.Cursor();
            return _closing || _catalog.Mode != mode || CardPlacement.HasMoved(origin.X, origin.Y, c.X, c.Y, staleTol);
        }
        try
        {
            if (ctx == null)
            {
                var av = FeedAvailability.Shared.State;
                if (!auto) Show(av.IsUp
                    ? CardBuilder.Message("Catalog not loaded yet", "Prices are still downloading.", "Check the main window status.")
                    : CardBuilder.Message(av.Kind == OutageKind.NoInternet ? "No internet connection" : "tarkov.dev is unavailable",
                        "No prices are saved on this PC yet.", "They download by themselves when it's back (see the main window)."), origin);
                summary = "no catalog";
                return;
            }
            if (_card.IsCardVisible)
            {
                _card.HideCard();
                await Task.Delay(50); // let the compositor remove our card before capturing
            }

            var img = await Task.Run(() => Desktop.Capture(first));
            det = await Task.Run(() => TooltipDetector.Detect(img, origin.X - first.X, origin.Y - first.Y, monitor.DpiScale, wideRetry: false));
            if (det.Status != DetectionStatus.Found)
            {
                await Task.Delay(140); // Tarkov's hover delay; also lets a long/left-opening tooltip appear
                if (Stale()) { summary = "discarded (cursor moved or mode changed)"; return; }
                wide = CaptureGeometry.Wide(origin.X, origin.Y, monitor);
                var w = wide.Value;
                capRect = w;
                img = await Task.Run(() => Desktop.Capture(w));
                det = await Task.Run(() => TooltipDetector.Detect(img, origin.X - w.X, origin.Y - w.Y, monitor.DpiScale, wideRetry: true));
            }
            if (det.Status != DetectionStatus.Found && auto && ProbeFrame?.Invoke(origin) is { } pf)
            {
                // The probe saw the tooltip a moment ago; read that frame instead of giving up silently.
                var pdet = TooltipDetector.Detect(pf.Image, origin.X - pf.Rect.X, origin.Y - pf.Rect.Y, monitor.DpiScale, wideRetry: false);
                Log.Write($"auto lookup: own capture missed the tooltip ({DetectionNotes(det)}); probe frame: {pdet.Status}");
                if (pdet.Status == DetectionStatus.Found) { det = pdet; img = pf.Image; capRect = pf.Rect; }
            }
            if (det.Status != DetectionStatus.Found)
            {
                summary = "no tooltip · " + DetectionNotes(det);
                if (!Stale() && !auto) Show(CardBuilder.NoTooltip(det.Title, det.Hint), origin);
                return;
            }

            panel = TooltipDetector.CropPanel(img, det.Best!.TitlePanel, origin.X - capRect.X, origin.Y - capRect.Y, monitor.DpiScale);
            var p = panel;
            outcome = await Task.Run(() => _recognizer.RecognizeAsync(p, ctx.Items, cts.Token, det.Best!.TitleLines));
            summary = $"{outcome.Result.Kind}: {outcome.Result.Items.FirstOrDefault()?.Name ?? outcome.Result.Text}"
                + (outcome.Result.Repair is { Length: > 0 } rep ? $" ({rep})" : "")
                // 1.2.4: what each OCR pass read, so a "possible match" can be explained from overlay.log
                + $" · title lines {det.Best?.TitleLines} · reads: " + string.Join(" | ", outcome.Attempts.Select(a =>
                    $"{a.Read.Engine}@{a.Read.Scale:0.##}× [{string.Join(" / ", a.Read.Lines.Select(l => l.Length > 80 ? l[..80] : l))}] → {a.Match.Kind}{(a.Match.Repair is { Length: > 0 } r ? " (" + r + ")" : "")}"));
            if (Stale()) { summary += " (discarded: cursor moved or mode changed during recognition)"; return; }

            // Stack count: only after a verified name, only for stackables; it cannot change the identified item.
            if (_stackReader != null && outcome.Result.Kind == MatchKind.Verified && outcome.Result.Single is { StackMaxSize: > 1 } stackItem)
            {
                var cap = img; var tip = det.Best!.Interior; var cr = capRect; int monH = monitor.Bounds.Height;
                var snap = Snapshot?.Invoke(origin);
                try
                {
                    stack = await Task.Run(() => _stackReader.ReadBestAsync(cap, origin.X - cr.X, origin.Y - cr.Y, tip,
                        snap?.Image, snap is null ? 0 : snap.Cursor.X - snap.Rect.X, snap is null ? 0 : snap.Cursor.Y - snap.Rect.Y,
                        snap?.Tooltip?.Offset(-snap.Rect.X, -snap.Rect.Y), monH, stackItem.StackMaxSize!.Value, cts.Token));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { stack = StackReading.NotRead("error: " + ex.Message); }
                summary += $" · stack {(stack.Count?.ToString() ?? "not read")} ({stack.Note})";
                if (Stale()) { summary += " (discarded: cursor moved)"; return; }
            }

            // Trader screen offer (gold "₽ 35 118" line): informational only, never changes the item.
            long? offer = null;
            if (_offerReader != null && outcome.Result.Kind == MatchKind.Verified && (det.Best!.SecondaryPanel != null || det.Best.Lines > det.Best.TitleLines || det.Best.Lines > 1))
            {
                var nameLines = outcome.Attempts.LastOrDefault(a => a.Match.Kind == MatchKind.Verified)?.Read.Lines;
                var sec = det.Best!.SecondaryPanel is { } sp ? img.Crop(sp) : null;
                try
                {
                    var (o, note) = await Task.Run(() => _offerReader.ReadAsync(nameLines, sec, cts.Token));
                    offer = o;
                    summary += $" · offer {(o?.ToString() ?? "none")} ({note})";
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { summary += " · offer error: " + ex.Message; }
                if (Stale()) { summary += " (discarded: cursor moved)"; return; }
            }

            var builder = Builder(ctx);
            bool expandAll = _settings().AlwaysShowFullList;
            var card0 = builder.ForMatch(outcome.Result, stack, expandAll, offer);
            // Verified items and merged same-name cards (dogtags…) can be expanded with the shortcut.
            if (outcome.Result.Kind == MatchKind.Verified || card0.Kind == CardKind.Full)
            {
                var shown = new Shown { Ctx = ctx, Match = outcome.Result, Stack = stack, Offer = offer, Origin = origin, Expanded = expandAll };
                Show(card0, origin, shown);
                _shown = shown;
                if (outcome.Result.Items.FirstOrDefault() is { } shownItem) ItemShown?.Invoke(shownItem);
            }
            else if (!auto || outcome.Result.Kind != MatchKind.None) Show(card0, origin);
            if (outcome.Result.Kind is MatchKind.Several or MatchKind.Conflict or MatchKind.PossibleStrong or MatchKind.PossibleWeak or MatchKind.Verified)
                CandidatesFound?.Invoke(outcome.Result);
        }
        catch (OperationCanceledException) { summary = "cancelled"; }
        catch (Exception ex)
        {
            summary = "error: " + ex.Message;
            if (!_closing && !auto) Show(CardBuilder.Message("Lookup failed", ex.Message), origin);
        }
        finally
        {
            Log.Write($"{(auto ? "auto " : "")}lookup at {origin.X},{origin.Y} mode {mode.Id}: {summary} ({sw.ElapsedMilliseconds} ms)");
            Completed?.Invoke(new LookupReport(DateTime.Now, origin, monitor, first, wide, det, outcome, sw.Elapsed, summary, panel, stack));
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>The nearest rejected tooltip candidates and why (for overlay.log when nothing was found).</summary>
    internal static string DetectionNotes(DetectionResult d) =>
        d.Considered.Count == 0 ? "no dark box near the cursor"
            : string.Join("; ", d.Considered.OrderByDescending(c => c.Interior.Width * c.Interior.Height).Take(3).Select(c => $"{c.Interior.Width}x{c.Interior.Height} at {c.Interior.X},{c.Interior.Y}: {c.Reason}"));

    private void Show(CardModel model, (int X, int Y) origin, object? key = null)
    {
        if (_closing) return;
        var st = _settings();
        _card.ShowCard(model, origin, st.EffectiveTooltipScale, st.DismissOnMove, st.EffectiveMoveTolerancePx, timeout: model.Expanded ? ExpandedTimeout : null, key: key);
    }
}
