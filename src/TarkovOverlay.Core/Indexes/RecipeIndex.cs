using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Economics;

namespace TarkovOverlay.Core.Indexes;

/// <summary>Index by recipe output ("obtain") and reverse index by ingredient ("used in"), for crafts and barters.</summary>
public sealed class RecipeIndex
{
    private readonly Dictionary<string, List<Craft>> _craftByOutput = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Craft>> _craftByInput = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Barter>> _barterByOutput = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Barter>> _barterByInput = new(StringComparer.Ordinal);
    public bool CraftsAvailable { get; }
    public bool BartersAvailable { get; }

    public RecipeIndex(CatalogSnapshot snap)
    {
        if (snap.Crafts != null)
        {
            CraftsAvailable = true;
            foreach (var c in snap.Crafts)
            {
                Add(_craftByOutput, c.Output.ItemId, c);
                foreach (var i in c.Inputs.Select(x => x.ItemId).Distinct()) Add(_craftByInput, i, c);
            }
        }
        if (snap.Barters != null)
        {
            BartersAvailable = true;
            foreach (var b in snap.Barters)
            {
                Add(_barterByOutput, b.Output.ItemId, b);
                foreach (var i in b.Inputs.Select(x => x.ItemId).Distinct()) Add(_barterByInput, i, b);
            }
        }
    }

    private static void Add<T>(Dictionary<string, List<T>> d, string k, T v)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = new List<T>();
        l.Add(v);
    }

    public IReadOnlyList<Craft> CraftsProducing(string id) => _craftByOutput.TryGetValue(id, out var l) ? l : Array.Empty<Craft>();
    public IReadOnlyList<Craft> CraftsUsing(string id) => _craftByInput.TryGetValue(id, out var l) ? l : Array.Empty<Craft>();
    public IReadOnlyList<Barter> BartersProducing(string id) => _barterByOutput.TryGetValue(id, out var l) ? l : Array.Empty<Barter>();
    public IReadOnlyList<Barter> BartersUsing(string id) => _barterByInput.TryGetValue(id, out var l) ? l : Array.Empty<Barter>();
}

public sealed record IngredientCost(string ItemId, string Name, double Count, long? UnitRub, string Source, bool Tool, bool FoundInRaid);

public sealed record RecipeCost(
    IReadOnlyList<IngredientCost> Consumed, IReadOnlyList<IngredientCost> Tools, int QuestItemInputs,
    long? TotalRub, long? PerOutputRub, double OutputCount, IReadOnlyList<string> Notes)
{
    public bool Known => PerOutputRub.HasValue;
}

/// <summary>
/// Ingredient cost per output: Σ(consumed unit price × qty) / output qty. Unit price is the cheaper of a fresh flea
/// 24h average or an unconditional LL1 cash trader quote; roubles at face value. Excludes fuel, time, flea fees,
/// and tool wear. Unknown ingredients make the total unknown.
/// </summary>
public sealed class RecipeCostEstimator
{
    private readonly CatalogSnapshot _snap;
    private readonly PriceCalculator _calc;

    public RecipeCostEstimator(CatalogSnapshot snap, PriceCalculator calc) { _snap = snap; _calc = calc; }

    public (long? Unit, string Source) UnitPrice(string itemId)
    {
        if (itemId == KnownIds.Roubles) return (1, "face value");
        var item = _snap.Item(itemId);
        if (item == null) return (null, "unknown item");
        long? best = null; string src = "no usable quote";
        var flea = _calc.Flea(item);
        if (flea.State == FleaState.Fresh && flea.Average is > 0) { best = flea.Average; src = "flea avg"; }
        var ll1 = item.BuyFromTrader.Where(b => b.MinTraderLevel <= 1 && b.TaskUnlockId == null && b.PriceRub > 0)
            .OrderBy(b => b.PriceRub).FirstOrDefault();
        if (ll1 != null && (best == null || ll1.PriceRub < best)) { best = ll1.PriceRub; src = $"{_snap.TraderName(ll1.TraderId)} LL1"; }
        return (best, src);
    }

    public RecipeCost Estimate(IReadOnlyList<RecipeItem> inputs, RecipeItem output, int questItemInputs)
    {
        var consumed = new List<IngredientCost>();
        var tools = new List<IngredientCost>();
        var notes = new List<string>();
        bool unknown = false;
        double total = 0;
        foreach (var i in inputs)
        {
            var (unit, src) = UnitPrice(i.ItemId);
            var ic = new IngredientCost(i.ItemId, _snap.Item(i.ItemId)?.Name ?? "Unknown item", i.Count, unit, src, i.Tool, i.FoundInRaid);
            if (i.Tool) { tools.Add(ic); continue; }
            consumed.Add(ic);
            if (i.FoundInRaid) notes.Add($"{ic.Name}: Found in Raid required");
            if (unit is null) { unknown = true; notes.Add($"{ic.Name}: {src}"); }
            else total += unit.Value * i.Count;
        }
        if (questItemInputs > 0) { unknown = true; notes.Add($"{questItemInputs} quest-only item(s) required (not purchasable)"); }
        long? totalRub = unknown ? null : Rounding.HalfAwayFromZero(total);
        long? per = unknown || output.Count <= 0 ? null : Rounding.HalfAwayFromZero(total / output.Count);
        return new RecipeCost(consumed, tools, questItemInputs, totalRub, per, output.Count, notes);
    }
}
