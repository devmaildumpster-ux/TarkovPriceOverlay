using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Recognition;
using TarkovOverlay.Tests.Fixtures;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>
/// Hybrid OCR ordering and conflict handling. FIXTURE KIND: scripted OCR engines returning recorded text
/// (text fixtures) against the recorded live catalog.
/// </summary>
public class RecognizerTests
{
    sealed class ScriptedEngine : IOcrEngine
    {
        private readonly Func<double, string[]> _script;
        public List<double> Calls { get; } = new();
        public ScriptedEngine(string name, Func<double, string[]> script, bool available = true, int? max = null)
        { Name = name; _script = script; IsAvailable = available; MaxImageDimension = max; }
        public string Name { get; }
        public bool IsAvailable { get; }
        public string? UnavailableReason => IsAvailable ? null : "not installed (test)";
        public int? MaxImageDimension { get; }
        public Task<OcrRead> RecognizeAsync(RgbImage panel, double scale, CancellationToken ct)
        {
            Calls.Add(scale);
            return Task.FromResult(new OcrRead(Name, scale, _script(scale), (int)(panel.Width * scale), (int)(panel.Height * scale), TimeSpan.FromMilliseconds(5)));
        }
    }

    static readonly RgbImage Panel = new(300, 40);
    static ItemIndex Index => LiveCatalog.RegularContext.Items;

    [Fact]
    public async Task FastPath_Windows2x_VerifiedStopsImmediately()
    {
        var win = new ScriptedEngine("Windows", _ => new[] { "9x39mm PAB-9 gs" });
        var tess = new ScriptedEngine("Tesseract", _ => new[] { "x" });
        var o = await new HoverRecognizer(win, tess).RecognizeAsync(Panel, Index, default);
        Assert.Equal(MatchKind.Verified, o.Result.Kind);
        Assert.Equal(new[] { 2.0 }, win.Calls);
        Assert.Empty(tess.Calls);
    }

    [Fact]
    public async Task SecondWindowsRead_ConfirmsWhenFirstHadNoCandidate()
    {
        var win = new ScriptedEngine("Windows", s => s == 2.0 ? new[] { "S1G Sauer ~~~" } : new[] { "SIG Sauer TANGO6T 1-6x24 30mm riflescope" });
        var o = await new HoverRecognizer(win, null).RecognizeAsync(Panel, Index, default);
        Assert.Equal(MatchKind.Verified, o.Result.Kind);
        Assert.Equal("6567e7681265c8a131069b0f", o.Result.Single!.Id);
        Assert.Equal(new[] { 2.0, 1.5 }, win.Calls);
    }

    [Fact]
    public async Task WrappedTitle_LinesJoinedInReadingOrder()
    {
        var win = new ScriptedEngine("Windows", _ => new[] { "AR-15 5.56x45 SureFire MAG5-60 STANAG", "60-round magazine" });
        var o = await new HoverRecognizer(win, null).RecognizeAsync(Panel, Index, default);
        Assert.Equal("544a37c44bdc2d25388b4567", o.Result.Single!.Id);
    }

    [Fact]
    public async Task EnginesDisagreeOnCredibleFullNames_PresentsChoice()
    {
        // Windows reads one credible full name (an identical-name group), Tesseract reads a different one.
        var winA = new ScriptedEngine("Windows", _ => new[] { "Secure container Gamma" });
        var tessB = new ScriptedEngine("Tesseract", _ => new[] { "Secure container Kappa" });
        var o = await new HoverRecognizer(winA, tessB).RecognizeAsync(Panel, Index, default);
        Assert.Equal(MatchKind.Conflict, o.Result.Kind);
        Assert.Contains(o.Result.Items, i => i.Name == "Secure container Kappa");
        Assert.Contains(o.Result.Items, i => i.Name == "Secure container Gamma");
    }

    [Fact]
    public async Task WindowsAndTesseract_DifferentCredibleNames_Conflict()
    {
        var win = new ScriptedEngine("Windows", s => s == 2.0 ? new[] { "AK-74 5.45x39 6L31 60-round magazlne" } : new[] { "AK-74 5.45x39 6L31 6O-round magazlne" });
        var tess = new ScriptedEngine("Tesseract", _ => new[] { "RPK-16 5.45x39 95-round drum magazine" });
        var o1 = await new HoverRecognizer(win, tess).RecognizeAsync(Panel, Index, default);
        Assert.Equal(MatchKind.Verified, o1.Result.Kind); // only Tesseract produced a credible full name

        var win2 = new ScriptedEngine("Windows", s => s == 2.0 ? new[] { "noise" } : new[] { "noise" });
        var tess2 = new ScriptedEngine("Tesseract", _ => new[] { "AK-74 5.45x39 6L31 60-round magazine" });
        // Windows 4x pass would run only when nothing credible: here Tesseract verified first.
        var o2 = await new HoverRecognizer(win2, tess2).RecognizeAsync(Panel, Index, default);
        Assert.Equal("55d482194bdc2d1d4e8b456b", o2.Result.Single!.Id);
        Assert.DoesNotContain(4.0, win2.Calls);

        // Now both credible and different: Windows 1.5x says 60-round 6L31, Tesseract says RPK drum.
        var win3 = new ScriptedEngine("Windows", s => s == 2.0 ? new[] { "Secure container Gamma" } : new[] { "noise" });
        var tess3 = new ScriptedEngine("Tesseract", _ => new[] { "RPK-16 5.45x39 95-round drum magazine" });
        var o3 = await new HoverRecognizer(win3, tess3).RecognizeAsync(Panel, Index, default);
        Assert.Equal(MatchKind.Conflict, o3.Result.Kind);
        Assert.Contains(o3.Result.Items, i => i.Id == "5bed625c0db834001c062946");
    }

    [Fact]
    public void LaterRead_ClarifiesEarlierAmbiguousCandidates()
    {
        // 2x read hits identical-name group {Gamma x3}; 1.5x verifies none; Tesseract can't narrow identical names,
        // so use a synthetic: aggregate of {A,B} and {A} -> A.
        var a = LiveCatalog.Regular.Items["55d482194bdc2d1d4e8b456b"];
        var b = LiveCatalog.Regular.Items["5bed625c0db834001c062946"];
        var r = HoverRecognizer.Aggregate(new[]
        {
            new ReadAttempt(new OcrRead("W", 2, new[] { "?" }, 1, 1, default), new MatchResult(MatchKind.Conflict, new[] { a, b }, "?")),
            new ReadAttempt(new OcrRead("T", 3, new[] { "?" }, 1, 1, default), new MatchResult(MatchKind.Verified, new[] { a }, "?")),
        });
        Assert.Equal(MatchKind.Verified, r!.Kind);
        Assert.Equal(a.Id, r.Single!.Id);
    }

    [Fact]
    public async Task NoCredibleRead_TriesWindowsUpTo4x_CappedByMaxDimension()
    {
        var win = new ScriptedEngine("Windows", s => s > 2.0 ? new[] { "9x19mm FMJ M882" } : new[] { "9x19rnm" }, max: 10_000);
        var o = await new HoverRecognizer(win, new ScriptedEngine("Tesseract", _ => new[] { "" })).RecognizeAsync(Panel, Index, default);
        Assert.Equal(new[] { 2.0, 1.5, 4.0 }, win.Calls);
        Assert.Equal(MatchKind.Verified, o.Result.Kind);
    }

    [Fact]
    public async Task FourXPass_SkippedWhenCapEqualsEarlierSize()
    {
        // Max dimension 600 on a 300 px panel: 2x already hits the cap, so 4x would be identical.
        var win = new ScriptedEngine("Windows", _ => new[] { "zzzz" }, max: 600);
        var o = await new HoverRecognizer(win, null).RecognizeAsync(Panel, Index, default);
        Assert.Equal(new[] { 2.0, 1.5 }, win.Calls);
        Assert.Contains(o.Notes, n => n.Contains("skipped"));
    }

    [Fact]
    public async Task MissingWindowsRecognizer_ContinuesWithTesseract()
    {
        var win = new ScriptedEngine("Windows", _ => throw new InvalidOperationException(), available: false);
        var tess = new ScriptedEngine("Tesseract", _ => new[] { "9x39mm PAB-9 gs" });
        var o = await new HoverRecognizer(win, tess).RecognizeAsync(Panel, Index, default);
        Assert.Equal(MatchKind.Verified, o.Result.Kind);
        Assert.Contains(o.Notes, n => n.StartsWith("Windows OCR unavailable"));
    }

    [Fact]
    public async Task NoEnginesAvailable_GivesUsableNoMatch()
    {
        var o = await new HoverRecognizer(null, new ScriptedEngine("Tesseract", _ => Array.Empty<string>(), available: false)).RecognizeAsync(Panel, Index, default);
        Assert.Equal(MatchKind.None, o.Result.Kind);
        Assert.Equal(2, o.Notes.Count);
    }

    [Fact]
    public async Task SuggestionOnly_WhenNoFullName()
    {
        var win = new ScriptedEngine("Windows", _ => new[] { "Molot Arms VPO-209 .366 TKM carbne" });
        var o = await new HoverRecognizer(win, null).RecognizeAsync(Panel, Index, default);
        Assert.Equal(MatchKind.PossibleStrong, o.Result.Kind);
    }

    [Fact]
    public async Task Cancellation_DiscardsLookup()
    {
        var cts = new CancellationTokenSource();
        var win = new ScriptedEngine("Windows", _ => { cts.Cancel(); return new[] { "noise" }; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HoverRecognizer(win, null).RecognizeAsync(Panel, Index, cts.Token));
    }
}
