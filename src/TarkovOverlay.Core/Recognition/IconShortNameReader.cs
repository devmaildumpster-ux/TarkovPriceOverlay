using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Text;

namespace TarkovOverlay.Core.Recognition;

/// <summary>What was read from the short name painted on the hovered item icon.</summary>
public sealed record IconNameReading(IReadOnlyList<string> Texts, string Note)
{
    public static IconNameReading None(string note) => new(Array.Empty<string>(), note);
}

/// <summary>
/// 1.3.1: OCR of the icon short-name bands. Prefers a frame taken before the name tooltip appeared
/// (so the tooltip cannot hide the label), then falls back to the lookup capture with the tooltip masked.
/// The text is only a tiebreaker; it never overrides a verified full-name match.
/// </summary>
public sealed class IconShortNameReader
{
    private readonly IReadOnlyList<IOcrEngine> _engines;
    public IconShortNameReader(params IOcrEngine?[] engines) => _engines = engines.Where(e => e != null).Select(e => e!).ToList();

    public async Task<IconNameReading> ReadAsync(RgbImage capture, int cursorX, int cursorY, PixelRect? tooltip, int screenHeight, CancellationToken ct)
    {
        var loc = IconShortNameLocator.Locate(capture, cursorX, cursorY, tooltip, StackCountLocator.NominalPitch(screenHeight));
        if (!loc.Ok) return IconNameReading.None(loc.Note);
        return await ReadBandsAsync(loc, ct).ConfigureAwait(false);
    }

    /// <summary>Prefers a clean pre-tooltip frame, then the lookup capture.</summary>
    public async Task<IconNameReading> ReadBestAsync(RgbImage capture, int cursorX, int cursorY, PixelRect? tooltip,
        RgbImage? before, int beforeCursorX, int beforeCursorY, PixelRect? beforeTooltip, int screenHeight, CancellationToken ct)
    {
        IconNameReading? early = null;
        if (before != null)
        {
            early = await ReadAsync(before, beforeCursorX, beforeCursorY, beforeTooltip, screenHeight, ct).ConfigureAwait(false);
            if (early.Texts.Count > 0) return early with { Note = early.Note + " (pre-tooltip frame)" };
        }
        var live = await ReadAsync(capture, cursorX, cursorY, tooltip, screenHeight, ct).ConfigureAwait(false);
        if (live.Texts.Count == 0 && early != null) return live with { Note = $"{live.Note}; pre-tooltip frame: {early.Note}" };
        return live;
    }

    public async Task<IconNameReading> ReadBandsAsync(IconNameLocateResult loc, CancellationToken ct)
    {
        var texts = new List<string>();
        var notes = new List<string>();
        foreach (var band in loc.Bands)
        {
            int h = Math.Max(8, band.Image.Height);
            double scale = Math.Clamp(48.0 / h, 2, 6);
            foreach (var e in _engines.Where(e => e.IsAvailable))
            {
                OcrRead r;
                try { r = await e.RecognizeAsync(band.Image, scale, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { notes.Add($"{band.Name}/{e.Name}: error {ex.Message}"); continue; }
                var t = Clean(r.Text);
                notes.Add($"{band.Name}/{e.Name}: \"{r.Text}\"{(t != null ? $" → {t}" : "")}");
                if (t != null && !texts.Contains(t, StringComparer.OrdinalIgnoreCase)) texts.Add(t);
            }
        }
        if (texts.Count == 0) return new IconNameReading(texts, notes.Count == 0 ? loc.Note : string.Join("; ", notes));
        return new IconNameReading(texts, string.Join("; ", notes));
    }

    /// <summary>A short icon label: letters/digits (and a bit of punctuation), not a price or empty read.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return null;
        // Prefer the longest line; icon labels are a single short name, not a paragraph.
        var t = lines.OrderByDescending(l => l.Length).First();
        // Don't use IsSecondaryLine: real short names look like "M995" / "7N1" (digit-heavy).
        if (t.IndexOfAny(new[] { '₽', '$', '€', '£' }) >= 0) return null;
        var compact = NameNormalizer.Compact(t);
        if (compact.Length < 2 || compact.Length > 24) return null;
        return t.Trim();
    }
}
