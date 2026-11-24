using System.Diagnostics;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Text;

namespace TarkovOverlay.Core.Recognition;

public sealed record OcrRead(string Engine, double Scale, IReadOnlyList<string> Lines, int ImageWidth, int ImageHeight, TimeSpan Elapsed, string? Error = null)
{
    /// <summary>The item-name lines joined with spaces (trader price / "cannot buy" lines after the first are dropped).</summary>
    public string Text => NameNormalizer.JoinLines(NameNormalizer.TitleLines(Lines));
    /// <summary>Every recognized line, for diagnostics.</summary>
    public string RawText => NameNormalizer.JoinLines(Lines);
}

public interface IOcrEngine
{
    string Name { get; }
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    /// <summary>Largest allowed image side (Windows: OcrEngine.MaxImageDimension); null = unbounded.</summary>
    int? MaxImageDimension { get; }
    /// <summary>Recognizes the title panel enlarged by <paramref name="scale"/>. Lines are in reading order.</summary>
    Task<OcrRead> RecognizeAsync(RgbImage panel, double scale, CancellationToken ct);
}

public sealed record ReadAttempt(OcrRead Read, MatchResult Match);

public sealed record RecognitionOutcome(MatchResult Result, IReadOnlyList<ReadAttempt> Attempts, TimeSpan Elapsed, IReadOnlyList<string> Notes)
{
    public string EngineSummary => string.Join(" → ", Attempts.Select(a => $"{a.Read.Engine}@{a.Read.Scale:0.##}×"));
}

/// <summary>
/// Hybrid OCR order (section 5): Windows 2× → Windows 1.5× → Tesseract 3× → Windows up to 4× (only when no
/// credible full name yet). Conflicting credible reads become a choice; a later read clarifies an earlier
/// ambiguous one only when all credible reads are compatible.
/// </summary>
public sealed class HoverRecognizer
{
    private readonly IOcrEngine? _windows;
    private readonly IOcrEngine? _tesseract;

    public HoverRecognizer(IOcrEngine? windows, IOcrEngine? tesseract)
    {
        _windows = windows; _tesseract = tesseract;
    }

    public static (int W, int H) ScaledSize(int w, int h, double scale, int? maxDim)
    {
        double s = scale;
        if (maxDim is > 0) s = Math.Min(s, (double)maxDim.Value / Math.Max(w, h));
        return ((int)Math.Round(w * s), (int)Math.Round(h * s));
    }

    public async Task<RecognitionOutcome> RecognizeAsync(RgbImage panel, ItemIndex index, CancellationToken ct, int expectedTitleLines = 0)
    {
        var sw = Stopwatch.StartNew();
        var attempts = new List<ReadAttempt>();
        var notes = new List<string>();
        var winSizes = new List<(int, int)>();
        bool winOk = _windows?.IsAvailable == true;
        bool tessOk = _tesseract?.IsAvailable == true;
        if (!winOk) notes.Add("Windows OCR unavailable: " + (_windows?.UnavailableReason ?? "not configured"));
        if (!tessOk) notes.Add("Tesseract unavailable: " + (_tesseract?.UnavailableReason ?? "not configured"));

        async Task<MatchResult?> Run(IOcrEngine e, double scale)
        {
            ct.ThrowIfCancellationRequested();
            OcrRead read;
            try { read = await e.RecognizeAsync(panel, scale, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { read = new OcrRead(e.Name, scale, Array.Empty<string>(), 0, 0, TimeSpan.Zero, ex.Message); }
            var m = string.IsNullOrWhiteSpace(read.Text) ? MatchResult.NoMatch("") : index.MatchLines(NameNormalizer.TitleLines(read.Lines), expectedTitleLines); // 1.2.4: line order + lost-line guard
            attempts.Add(new ReadAttempt(read, m));
            return m;
        }

        // 1. Windows 2×
        if (winOk)
        {
            winSizes.Add(ScaledSize(panel.Width, panel.Height, 2.0, _windows!.MaxImageDimension));
            var m1 = await Run(_windows, 2.0).ConfigureAwait(false);
            if (m1 is { Kind: MatchKind.Verified }) return Done(m1);

            // 2. Windows 1.5×
            winSizes.Add(ScaledSize(panel.Width, panel.Height, 1.5, _windows.MaxImageDimension));
            var m2 = await Run(_windows, 1.5).ConfigureAwait(false);
            if (m2 is { Kind: MatchKind.Verified } && m2.Single is { } x2 &&
                (m1 is null || !m1.IsCredibleFullName || m1.Items.Any(i => i.Id == x2.Id)))
                return Done(m2);
        }

        var agg = Aggregate(attempts);
        if (agg is { Kind: MatchKind.Verified }) return Done(agg);

        // 3. Tesseract 3× on the same panel
        if (tessOk)
        {
            await Run(_tesseract!, 3.0).ConfigureAwait(false);
            agg = Aggregate(attempts);
            if (agg is { Kind: MatchKind.Verified }) return Done(agg);
        }

        // 4. Larger Windows read only when no credible full-name match at all.
        if (winOk && !attempts.Any(a => a.Match.IsCredibleFullName))
        {
            var size = ScaledSize(panel.Width, panel.Height, 4.0, _windows!.MaxImageDimension);
            if (winSizes.Contains(size)) notes.Add("4× pass skipped (capped to an earlier size)");
            else
            {
                double s = (double)size.W / panel.Width;
                await Run(_windows, s).ConfigureAwait(false);
                agg = Aggregate(attempts);
                if (agg is { Kind: MatchKind.Verified }) return Done(agg);
            }
        }

        if (agg != null) return Done(agg); // conflict between credible full names
        return Done(BestSuggestion(attempts));

        RecognitionOutcome Done(MatchResult r) => new(r, attempts, sw.Elapsed, notes);
    }

    /// <summary>
    /// Combines credible full-name reads. Intersection of every credible set: one item → verified;
    /// otherwise a conflict listing the candidates. Null when no read produced a credible full name.
    /// </summary>
    public static MatchResult? Aggregate(IReadOnlyList<ReadAttempt> attempts)
    {
        var credible = attempts.Where(a => a.Match.IsCredibleFullName).Select(a => a.Match).ToList();
        if (credible.Count == 0) return null;
        var inter = credible[0].Items.Select(i => i.Id).ToHashSet();
        foreach (var c in credible.Skip(1)) inter.IntersectWith(c.Items.Select(i => i.Id));
        var all = credible.SelectMany(c => c.Items).GroupBy(i => i.Id).Select(g => g.First()).ToList();
        if (inter.Count == 1)
        {
            var id = inter.First();
            var src = credible.FirstOrDefault(c => c.Items.Count == 1 && c.Items[0].Id == id) ?? credible[^1];
            return new MatchResult(MatchKind.Verified, all.Where(i => i.Id == id).ToList(), src.Text, src.Repair, src.Stage);
        }
        var items = inter.Count > 1 ? all.Where(i => inter.Contains(i.Id)).ToList() : all;
        return new MatchResult(MatchKind.Conflict, items, credible[^1].Text, inter.Count > 1 ? "identical catalog names" : "OCR reads disagree");
    }

    private static MatchResult BestSuggestion(IReadOnlyList<ReadAttempt> attempts)
    {
        static int Rank(MatchKind k) => k switch
        {
            MatchKind.PossibleStrong => 0, MatchKind.Several => 1, MatchKind.PossibleWeak => 2, _ => 3,
        };
        var ranked = attempts.Select(a => a.Match).Where(m => m.Kind != MatchKind.None).OrderBy(m => Rank(m.Kind)).ToList();
        if (ranked.Count == 0)
            return MatchResult.NoMatch(attempts.Select(a => a.Read.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "");
        var strong = ranked.Where(m => m.Kind == MatchKind.PossibleStrong).Select(m => m.Items[0].Id).Distinct().ToList();
        if (strong.Count > 1)
        {
            var items = ranked.Where(m => m.Kind == MatchKind.PossibleStrong).SelectMany(m => m.Items).GroupBy(i => i.Id).Select(g => g.First()).ToList();
            return new MatchResult(MatchKind.Several, items, ranked[0].Text, "reads suggest different items");
        }
        return ranked[0];
    }
}
