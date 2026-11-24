using TarkovOverlay.Core.Economics;
using TarkovOverlay.Core.Indexes;
using TarkovOverlay.Core.Matching;

namespace TarkovOverlay.Core.Catalog;

/// <summary>An immutable snapshot plus every index derived from it. Built on a worker thread.</summary>
public sealed class CatalogContext
{
    public CatalogSnapshot Snapshot { get; }
    public GameMode Mode { get; }
    public ItemIndex Items { get; }
    public QuestIndex Quests { get; }
    public HideoutIndex Hideout { get; }
    public RecipeIndex Recipes { get; }
    public PriceCalculator Prices { get; }
    public RecipeCostEstimator Costs { get; }

    public CatalogContext(CatalogSnapshot snapshot, Func<DateTimeOffset>? now = null)
    {
        Snapshot = snapshot;
        Mode = GameMode.FromId(snapshot.ModeId);
        Items = new ItemIndex(snapshot.Items.Values);
        Quests = new QuestIndex(snapshot);
        Hideout = new HideoutIndex(snapshot);
        Recipes = new RecipeIndex(snapshot);
        Prices = new PriceCalculator(snapshot.Market, now);
        Costs = new RecipeCostEstimator(snapshot, Prices);
    }
}
