using System.IO.Compression;
using System.Runtime.InteropServices;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Recognition;
using TarkovOverlay.Ocr.Tesseract;
using TarkovOverlay.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace TarkovOverlay.Tests;

/// <summary>
/// End-to-end: detection → Tesseract (pinned tessdata_fast eng model) → catalog match.
/// FIXTURE KIND: synthetic rendered-font captures (tools/make_ocr_fixtures.py, DejaVu Sans, 100% and 150%).
/// On Linux this uses the distro's libtesseract 5.5.0 through symlinks named like the Windows binaries;
/// on Windows it uses the packaged tesseract55.dll / leptonica-1.85.0.dll.
/// </summary>
public class TesseractTests
{
    private readonly ITestOutputHelper _out;
    public TesseractTests(ITestOutputHelper o) => _out = o;

    internal static readonly Lazy<TesseractOcrEngine?> Engine = new(() =>
    {
        var baseDir = AppContext.BaseDirectory;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var x64 = Path.Combine(baseDir, "x64");
            Directory.CreateDirectory(x64);
            void Link(string name, string target)
            {
                var p = Path.Combine(x64, name);
                if (!File.Exists(p) && File.Exists(target)) File.CreateSymbolicLink(p, target);
            }
            Link("libtesseract55.dll.so", "/usr/lib/x86_64-linux-gnu/libtesseract.so.5");
            Link("libleptonica-1.85.0.dll.so", "/usr/lib/x86_64-linux-gnu/libleptonica.so.6");
            // the wrapper P/Invokes "libdl"; modern glibc only ships libdl.so.2
            var dl = Path.Combine(baseDir, "libdl.so");
            if (!File.Exists(dl) && File.Exists("/lib/x86_64-linux-gnu/libdl.so.2")) File.CreateSymbolicLink(dl, "/lib/x86_64-linux-gnu/libdl.so.2");
        }
        var e = new TesseractOcrEngine(baseDir);
        if (e.Warmup(out var err)) return e; Console.Error.WriteLine("TESSERACT WARMUP: " + err); return null;
    });

    public static IEnumerable<object[]> Cases() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "ocr", "manifest.tsv"))
            .Where(l => l.Length > 0).Select(l => l.Split('\t')).Select(p => new object[] { p[0], double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), p[2] });

    static RgbImage Load(string file)
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "ocr", file)), CompressionMode.Decompress);
        return Ppm.Read(gz);
    }

    [Fact]
    public void Engine_ResolvesModelRelativeToBaseDirectory_NotWorkingDirectory()
    {
        var old = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Path.GetTempPath());
            var e = new TesseractOcrEngine(AppContext.BaseDirectory);
            Assert.True(File.Exists(Path.Combine(e.TessdataDir, "eng.traineddata")));
            Assert.StartsWith(AppContext.BaseDirectory, e.TessdataDir);
        }
        finally { Directory.SetCurrentDirectory(old); }
    }

    [Fact]
    public void MissingModel_ReportsSpecificSetupInfo()
    {
        var e = new TesseractOcrEngine(Path.Combine(Path.GetTempPath(), "nowhere-" + Guid.NewGuid()));
        Assert.False(e.IsAvailable);
        Assert.Contains("eng.traineddata", e.UnavailableReason);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SyntheticCapture_DetectsAndReadsTitle(string file, double scale, string expectedId)
    {
        var img = Load(file);
        var det = TooltipDetector.Detect(img, (int)(245 * scale), (int)(125 * scale), scale, wideRetry: false);
        Assert.Equal(DetectionStatus.Found, det.Status);
        if (file.Contains("wrapped")) Assert.Equal(2, det.Best!.Lines);

        var engine = Engine.Value;
        if (engine == null) { _out.WriteLine("Tesseract native runtime unavailable on this machine; OCR half skipped."); return; }
        var panel = img.Crop(det.Best!.Interior);
        var outcome = await new HoverRecognizer(null, engine).RecognizeAsync(panel, LiveCatalog.RegularContext.Items, default);
        var read = outcome.Attempts.Single().Read;
        _out.WriteLine($"{file}: '{read.Text}' in {read.Elapsed.TotalMilliseconds:0} ms -> {outcome.Result.Kind} {outcome.Result.Single?.Name}");
        // Priority: never a wrong automatic identification; the right item must at least be suggested.
        if (outcome.Result.Kind == MatchKind.Verified) Assert.Equal(expectedId, outcome.Result.Single!.Id);
        Assert.Contains(outcome.Result.Items, i => i.Id == expectedId);
    }

    /// <summary>
    /// Measures a preprocessing/scale change the way section 5 asks: exact correct, wrong automatic matches,
    /// and misses, counted separately over all synthetic captures. Results are printed for the README.
    /// </summary>
    [Fact]
    public async Task Benchmark_TesseractScales_CorrectWrongMiss()
    {
        var engine = Engine.Value;
        if (engine == null) { _out.WriteLine("Tesseract unavailable; benchmark skipped."); return; }
        var cases = Cases().ToList();
        foreach (var scale in new[] { 2.0, 3.0, 4.0 })
        {
            int correct = 0, wrong = 0, miss = 0; double ms = 0;
            foreach (var c in cases)
            {
                var file = (string)c[0]; var dpi = (double)c[1]; var id = (string)c[2];
                var img = Load(file);
                var det = TooltipDetector.Detect(img, (int)(245 * dpi), (int)(125 * dpi), dpi, false);
                if (det.Status != DetectionStatus.Found) { miss++; continue; }
                var read = await engine.RecognizeAsync(img.Crop(det.Best!.Interior), scale, default);
                ms += read.Elapsed.TotalMilliseconds;
                var m = LiveCatalog.RegularContext.Items.Match(read.Text);
                if (m.Kind == MatchKind.Verified) { if (m.Single!.Id == id) correct++; else wrong++; }
                else miss++;
            }
            _out.WriteLine($"BENCH tesseract {scale:0}x: correct {correct}/{cases.Count}, wrong {wrong}, miss {miss}, avg {ms / cases.Count:0} ms");
            Assert.Equal(0, wrong);
        }
    }
}
