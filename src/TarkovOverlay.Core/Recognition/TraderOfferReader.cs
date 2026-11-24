using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Text;

namespace TarkovOverlay.Core.Recognition;

/// <summary>
/// Reads the trader screen's offer ("₽ 35 118", gold second line) for an already-identified item. It never
/// affects identification. A value is returned only when it parses cleanly and every engine that produced
/// a number agrees; otherwise the card simply has no offer row.
/// </summary>
public sealed class TraderOfferReader
{
    private readonly IReadOnlyList<IOcrEngine> _engines;
    public TraderOfferReader(params IOcrEngine?[] engines) => _engines = engines.Where(e => e != null).Select(e => e!).ToList();

    /// <param name="nameReadLines">Lines of the OCR read that identified the item (a neutral price line may be among them).</param>
    /// <param name="secondary">Crop of the coloured lines under the name, if the detector found any.</param>
    public async Task<(long? Offer, string Note)> ReadAsync(IReadOnlyList<string>? nameReadLines, RgbImage? secondary, CancellationToken ct)
    {
        if (nameReadLines != null)
            foreach (var l in NameNormalizer.SecondaryLines(nameReadLines))
                if (NameNormalizer.ParseRublePrice(l) is { } v) return (v, $"offer from name read \"{l}\"");
        if (secondary == null || secondary.Width < 8 || secondary.Height < 6) return (null, "no secondary line");
        var values = new List<long>(); var notes = new List<string>();
        foreach (var e in _engines.Where(e => e.IsAvailable))
        {
            OcrRead r;
            try { r = await e.RecognizeAsync(secondary, 3.0, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { notes.Add($"{e.Name}: error {ex.Message}"); continue; }
            var line = string.Join(" ", r.Lines).Trim();
            var v = NameNormalizer.ParseRublePrice(line);
            notes.Add($"{e.Name}: \"{line}\"{(v is { } n ? $" → {n}" : "")}");
            if (v is { } val) values.Add(val);
        }
        if (values.Count == 0) return (null, "no price · " + string.Join(" | ", notes));
        if (values.Distinct().Count() > 1) return (null, "offer reads disagree · " + string.Join(" | ", notes));
        return (values[0], string.Join(" | ", notes));
    }
}
