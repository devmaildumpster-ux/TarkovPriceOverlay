using System.Text.RegularExpressions;
using TarkovOverlay.Core.Detection;

namespace TarkovOverlay.Core.Recognition;

/// <summary>Optional digits-only recognition (Tesseract with a 0-9 whitelist, single line).</summary>
public interface IDigitOcr
{
    Task<OcrRead> RecognizeDigitsAsync(RgbImage digits, double scale, CancellationToken ct);
}

/// <summary>Result of reading the hovered stack's count. Count is null when it could not be read safely.</summary>
public sealed record StackReading(int? Count, string Note, IReadOnlyList<string> Reads)
{
    public static StackReading NotRead(string note) => new(null, note, Array.Empty<string>());
}

/// <summary>
/// Reads the small stack-count number of an already-identified stackable item. It runs only after the item
/// name is verified and can never change which item was identified. A number is accepted only if it is a
/// plain integer within 1..stackMaxSize and every engine that produced a number agrees; otherwise the card
/// falls back to per-unit prices with "count not read".
/// </summary>
public sealed class StackCountReader
{
    private readonly IReadOnlyList<IOcrEngine> _engines;

    public StackCountReader(params IOcrEngine?[] engines) => _engines = engines.Where(e => e != null).Select(e => e!).ToList();

    public async Task<StackReading> ReadAsync(RgbImage capture, int cursorX, int cursorY, PixelRect? tooltip, int screenHeight, int stackMaxSize, CancellationToken ct)
    {
        if (stackMaxSize <= 1) return StackReading.NotRead("not stackable");
        var loc = StackCountLocator.Locate(capture, cursorX, cursorY, tooltip, StackCountLocator.NominalPitch(screenHeight));
        if (loc.Status != StackLocateStatus.Found) return StackReading.NotRead(loc.Note);
        return await ReadDigitsAsync(loc.Digits!, stackMaxSize, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Prefers a clean frame grabbed while the mouse was resting but before the name tooltip appeared (so the
    /// tooltip cannot hide the count), then falls back to the lookup's own capture with the tooltip masked.
    /// </summary>
    public async Task<StackReading> ReadBestAsync(RgbImage capture, int cursorX, int cursorY, PixelRect? tooltip,
        RgbImage? before, int beforeCursorX, int beforeCursorY, PixelRect? beforeTooltip, int screenHeight, int stackMaxSize, CancellationToken ct)
    {
        StackReading? early = null;
        if (before != null)
        {
            early = await ReadAsync(before, beforeCursorX, beforeCursorY, beforeTooltip, screenHeight, stackMaxSize, ct).ConfigureAwait(false);
            if (early.Count != null) return early with { Note = early.Note + " (pre-tooltip frame)" };
        }
        var live = await ReadAsync(capture, cursorX, cursorY, tooltip, screenHeight, stackMaxSize, ct).ConfigureAwait(false);
        if (live.Count == null && early != null) return live with { Note = $"{live.Note}; pre-tooltip frame: {early.Note}" };
        return live;
    }

    public async Task<StackReading> ReadDigitsAsync(RgbImage digits, int stackMaxSize, CancellationToken ct)
    {
        double scale = Math.Clamp(48.0 / Math.Max(8, digits.Height), 2, 6); // aim for ~48 px tall
        var reads = new List<string>();
        var values = new List<int>();
        foreach (var e in _engines.Where(e => e.IsAvailable))
        {
            OcrRead r;
            try
            {
                r = e is IDigitOcr d ? await d.RecognizeDigitsAsync(digits, scale, ct).ConfigureAwait(false)
                                     : await e.RecognizeAsync(digits, scale, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { reads.Add($"{e.Name}: error {ex.Message}"); continue; }
            var v = Parse(r.Text);
            reads.Add($"{e.Name}: \"{r.Text}\"{(v is { } n ? $" → {n}" : "")}");
            if (v is { } val) values.Add(val);
        }
        if (values.Count == 0) return new StackReading(null, "count not read", reads);
        if (values.Distinct().Count() > 1) return new StackReading(null, "count reads disagree", reads);
        int c = values[0];
        if (c < 1 || c > stackMaxSize) return new StackReading(null, $"count {c} outside 1–{stackMaxSize}", reads);
        return new StackReading(c, "read", reads);
    }

    /// <summary>Accepts only a single plain integer (common OCR confusions O→0, l/I/|→1 inside digits).</summary>
    public static int? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().Replace(" ", "");
        t = Regex.Replace(t, @"(?<=\d)[Oo]|[Oo](?=\d)", "0");
        t = Regex.Replace(t, @"(?<=\d)[lI|]|[lI|](?=\d)", "1");
        if (!Regex.IsMatch(t, @"^\d{1,7}$")) return null;
        return int.TryParse(t, out var n) ? n : null;
    }
}
