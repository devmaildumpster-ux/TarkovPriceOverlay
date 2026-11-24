using System.Diagnostics;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Recognition;
using TesseractOCR;
using TesseractOCR.Enums;
using TesseractOCR.InteropDotNet;

namespace TarkovOverlay.Ocr.Tesseract;

/// <summary>
/// Local Tesseract LSTM engine (second OCR engine). Lazily initialized once and reused; model and native
/// libraries are resolved relative to the executable, not the working directory.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine, IDigitOcr, IDisposable
{
    private readonly string _baseDir;
    private readonly object _gate = new();
    private Engine? _engine;
    private bool _initTried;
    private string? _unavailable;

    public TesseractOcrEngine(string? baseDirectory = null)
    {
        _baseDir = baseDirectory ?? AppContext.BaseDirectory;
    }

    public string Name => "Tesseract";
    public int? MaxImageDimension => 4000;
    public string TessdataDir => Path.Combine(_baseDir, "tessdata");
    public string NativeDir => Path.Combine(_baseDir, Environment.Is64BitProcess ? "x64" : "x86");

    public bool IsAvailable
    {
        get
        {
            if (!_initTried) Probe();
            return _unavailable == null;
        }
    }

    public string? UnavailableReason { get { _ = IsAvailable; return _unavailable; } }

    /// <summary>Cheap file checks; the native engine itself is created on first use.</summary>
    private void Probe()
    {
        if (!File.Exists(Path.Combine(TessdataDir, "eng.traineddata")))
            _unavailable = $"English model missing: {Path.Combine(TessdataDir, "eng.traineddata")}";
        else if (OperatingSystem.IsWindows() && !File.Exists(Path.Combine(NativeDir, "tesseract55.dll")))
            _unavailable = $"Tesseract native library missing: {Path.Combine(NativeDir, "tesseract55.dll")}";
        else if (OperatingSystem.IsWindows() && !File.Exists(Path.Combine(NativeDir, "leptonica-1.85.0.dll")))
            _unavailable = $"Leptonica native library missing: {Path.Combine(NativeDir, "leptonica-1.85.0.dll")}";
    }

    private DateTime _lastUse = DateTime.UtcNow;

    /// <summary>True while the native engine (model + buffers, tens of MB) is loaded.</summary>
    public bool IsLoaded { get { lock (_gate) return _engine != null; } }

    /// <summary>Frees the native engine if it has not been used for <paramref name="idle"/>; it reloads on the next read.</summary>
    public bool ReleaseIfIdle(TimeSpan idle)
    {
        lock (_gate)
        {
            if (_engine == null || DateTime.UtcNow - _lastUse < idle) return false;
            _engine.Dispose(); _engine = null;
            return true;
        }
    }

    private Engine GetEngine()
    {
        lock (_gate)
        {
            _lastUse = DateTime.UtcNow;
            if (_engine != null) return _engine;
            if (_unavailable != null) throw new InvalidOperationException(_unavailable);
            _initTried = true;
            try
            {
                LibraryLoader.Instance.CustomSearchPath = _baseDir;
                var e = new Engine(TessdataDir, Language.English, EngineMode.LstmOnly);
                e.SetVariable("user_defined_dpi", 300);
                e.SetVariable("preserve_interword_spaces", 1);
                _engine = e;
                return e;
            }
            catch (Exception ex)
            {
                _unavailable = "Tesseract failed to start: " + Describe(ex) +
                    (OperatingSystem.IsWindows() ? " (requires the Microsoft Visual C++ 2015-2022 x64 Redistributable: https://aka.ms/vs/17/release/vc_redist.x64.exe)" : "");
                throw new InvalidOperationException(_unavailable, ex);
            }
        }
    }

    private static string Describe(Exception ex) => ex.InnerException != null ? $"{ex.GetType().Name}: {ex.Message} / {ex.InnerException.Message}" : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>Tries to start the engine now (used for status display and packaged smoke checks).</summary>
    public bool Warmup(out string? error)
    {
        try { _initTried = true; Probe(); if (_unavailable != null) { error = _unavailable; return false; } GetEngine(); error = null; return true; }
        catch (Exception ex) { error = _unavailable ?? ex.Message; return false; }
    }

    public Task<OcrRead> RecognizeAsync(RgbImage panel, double scale, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        GetEngine(); // fail fast if unavailable
        // Cropped, enlarged, grayscale, light lettering inverted to dark-on-light.
        var scaled = panel.Scale(Math.Clamp(scale, 1, 6));
        if (Math.Max(scaled.Width, scaled.Height) > MaxImageDimension)
            scaled = panel.Scale((double)MaxImageDimension!.Value / Math.Max(panel.Width, panel.Height));
        var gray = scaled.ToInvertedGray(out int w, out int h);
        var pgm = ImageEncoding.Pgm(gray, w, h);
        string text;
        lock (_gate)
        {
            var engine = GetEngine(); // inside the lock: an idle release can't free it mid-read
            using var img = TesseractOCR.Pix.Image.LoadFromMemory(pgm);
            using var page = engine.Process(img, PageSegMode.SingleBlock); // block mode keeps wrapped names
            text = page.Text ?? "";
        }
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return new OcrRead(Name, scale, lines, w, h, sw.Elapsed);
    }, ct);

    /// <summary>Digits-only single-line read for stack counts (whitelist 0-9, restored afterwards).</summary>
    public Task<OcrRead> RecognizeDigitsAsync(RgbImage digits, double scale, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        GetEngine(); // fail fast if unavailable
        var scaled = digits.Scale(Math.Clamp(scale, 1, 8));
        var gray = scaled.ToInvertedGray(out int w, out int h, margin: 16);
        var pgm = ImageEncoding.Pgm(gray, w, h);
        string text;
        lock (_gate)
        {
            var engine = GetEngine();
            engine.SetVariable("tessedit_char_whitelist", "0123456789");
            try
            {
                using var img = TesseractOCR.Pix.Image.LoadFromMemory(pgm);
                using var page = engine.Process(img, PageSegMode.SingleLine);
                text = page.Text ?? "";
            }
            finally { engine.SetVariable("tessedit_char_whitelist", ""); }
        }
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return new OcrRead(Name + " digits", scale, lines, w, h, sw.Elapsed);
    }, ct);

    public void Dispose()
    {
        lock (_gate) { _engine?.Dispose(); _engine = null; }
    }
}

public static class ImageEncoding
{
    /// <summary>Binary 8-bit PGM (P5), decoded natively by Leptonica.</summary>
    public static byte[] Pgm(byte[] gray, int w, int h)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n");
        var buf = new byte[header.Length + gray.Length];
        Buffer.BlockCopy(header, 0, buf, 0, header.Length);
        Buffer.BlockCopy(gray, 0, buf, header.Length, w * h);
        return buf;
    }
}
