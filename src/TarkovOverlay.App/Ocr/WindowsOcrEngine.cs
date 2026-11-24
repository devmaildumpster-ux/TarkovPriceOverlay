using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Recognition;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace TarkovOverlay.App.Ocr;

/// <summary>Windows.Media.Ocr English recognizer (fast path). Unavailable when no English OCR language is installed.</summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    public const string InstallHint =
        "Install the English OCR language: Settings › Time & language › Language & region › Add a language › English (United States), " +
        "or in an admin PowerShell: Add-WindowsCapability -Online -Name \"Language.OCR~~~en-US~0.0.1.0\"";

    private readonly OcrEngine? _engine;

    public WindowsOcrEngine()
    {
        try
        {
            _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (_engine == null)
            {
                var en = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                if (en != null) _engine = OcrEngine.TryCreateFromLanguage(en);
            }
            if (_engine == null) UnavailableReason = "No English Windows OCR recognizer. " + InstallHint;
            else LanguageTag = _engine.RecognizerLanguage.LanguageTag;
        }
        catch (Exception ex)
        {
            UnavailableReason = $"Windows OCR could not start ({ex.GetType().Name}: {ex.Message}). " + InstallHint;
        }
    }

    public string Name => "Windows";
    public string? LanguageTag { get; }
    public bool IsAvailable => _engine != null;
    public string? UnavailableReason { get; }
    public int? MaxImageDimension => (int)OcrEngine.MaxImageDimension;

    public async Task<OcrRead> RecognizeAsync(RgbImage panel, double scale, CancellationToken ct)
    {
        if (_engine == null) throw new InvalidOperationException(UnavailableReason);
        var sw = Stopwatch.StartNew();
        var (w, h) = HoverRecognizer.ScaledSize(panel.Width, panel.Height, scale, MaxImageDimension);
        var img = await Task.Run(() => (w == panel.Width && h == panel.Height) ? panel : panel.Resize(w, h), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(img.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, img.Width, img.Height, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        // Order lines top-to-bottom (then left-to-right) before joining.
        var lines = result.Lines
            .Select(l => (Text: l.Text, Top: l.Words.Count > 0 ? l.Words.Min(x => x.BoundingRect.Y) : 0, Left: l.Words.Count > 0 ? l.Words.Min(x => x.BoundingRect.X) : 0))
            .OrderBy(l => Math.Round(l.Top / 4.0)).ThenBy(l => l.Left)
            .Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        return new OcrRead(Name, scale, lines, img.Width, img.Height, sw.Elapsed);
    }
}
