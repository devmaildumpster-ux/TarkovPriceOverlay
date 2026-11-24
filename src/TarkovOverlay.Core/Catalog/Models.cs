using System.Text.Json.Serialization;

namespace TarkovOverlay.Core.Catalog;

public static class KnownIds
{
    public const string Roubles = "5449016a4bdc2d6f028b456f";
    public const string Dollars = "5696686a4bdc2da3298b456a";
    public const string Euros = "569668774bdc2da2298b4568";
}

/// <summary>A trader's purchase price for the item (what you get when selling to them).</summary>
public sealed record TraderSale(string TraderId, long Price, long PriceRub, string Currency);

/// <summary>A cash offer to buy the item from a trader.</summary>
public sealed record TraderBuy(string TraderId, long Price, long PriceRub, string Currency, int MinTraderLevel, string? TaskUnlockId, int? BuyLimit);

public sealed record GridSize(int Width, int Height)
{
    [JsonIgnore] public int Area => Width * Height;
}

public sealed record ContainerSpec(string Kind, int? Capacity, IReadOnlyList<GridSize> Grids);

public sealed record ContainedItem(string ItemId, double Count);

/// <summary>Normalized item record. Nullable means "unknown / not supplied" and is kept distinct from zero.</summary>
public sealed class CatalogItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ShortName { get; init; }
    public string? NormalizedName { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? BasePrice { get; init; }
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();
    public int? StackMaxSize { get; init; }
    public long? Avg24hPrice { get; init; }
    public DateTimeOffset? LastScan { get; init; }
    public long? LastLowPrice { get; init; }
    public int? LastOfferCount { get; init; }
    public long? Low24hPrice { get; init; }
    public long? High24hPrice { get; init; }
    public int? MinLevelForFlea { get; init; }
    public IReadOnlyList<TraderSale> SellToTrader { get; init; } = Array.Empty<TraderSale>();
    public IReadOnlyList<TraderBuy> BuyFromTrader { get; init; } = Array.Empty<TraderBuy>();
    public ContainerSpec? Container { get; init; }
    public int? MagazineCapacity { get; init; }
    public IReadOnlyList<ContainedItem> ContainsItems { get; init; } = Array.Empty<ContainedItem>();
    /// <summary>Tarkov Wiki page from the feed ("wikiLink"); null in caches written before 1.2.0.</summary>
    public string? WikiLink { get; init; }
    /// <summary>tarkov.dev item page from the feed ("link"); null in caches written before 1.2.0.</summary>
    public string? Link { get; init; }

    [JsonIgnore] public bool IsNoFlea => Types.Any(t => string.Equals(t, "noFlea", StringComparison.OrdinalIgnoreCase));
    [JsonIgnore] public bool IsPreset => Types.Contains("preset");
    [JsonIgnore] public bool IsAmmo => Types.Contains("ammo");
    [JsonIgnore] public bool IsAmmoPack => Types.Contains("ammoBox");
    [JsonIgnore] public int? Footprint => Width is > 0 && Height is > 0 ? Width * Height : null;

    public override string ToString() => $"{Name} [{Id}]";
}

public sealed record MarketSettings(bool Enabled, int? MinPlayerLevel, double? SellOfferFeeRate, double? SellRequirementFeeRate);

public sealed record QuestHandIn(string ObjectiveId, IReadOnlyList<string> ItemIds, int Count, bool FoundInRaid, bool Optional);

public sealed record QuestInfo(
    string Id, string Name, string? TraderId, int? MinPlayerLevel,
    IReadOnlyList<string> PrerequisiteTaskIds, IReadOnlyList<QuestHandIn> HandIns,
    bool KappaRequired);

public sealed record HideoutItemRequirement(string RequirementId, string ItemId, int Count, bool FoundInRaid);
public sealed record HideoutStationRequirement(string StationId, int Level);
public sealed record HideoutTraderRequirement(string TraderId, string RequirementType, string CompareMethod, double Value);
public sealed record HideoutSkillRequirement(string Skill, int Level);
public sealed record HideoutBonus(string Type, double? Value, string? Skill, bool Passive);

public sealed record HideoutLevel(
    int Level, IReadOnlyList<HideoutItemRequirement> Items, IReadOnlyList<HideoutStationRequirement> Stations,
    IReadOnlyList<HideoutTraderRequirement> Traders, IReadOnlyList<HideoutSkillRequirement> Skills,
    IReadOnlyList<HideoutBonus> Bonuses);

public sealed record HideoutStation(string Id, string Name, IReadOnlyList<HideoutLevel> Levels);

public sealed record RecipeItem(string ItemId, double Count, bool Tool, bool FoundInRaid);

public sealed record Craft(
    string Id, string StationId, int Level, int? DurationSeconds, string? TaskUnlockId,
    IReadOnlyList<RecipeItem> Inputs, int QuestItemInputs, RecipeItem Output, IReadOnlyList<string> GameEditions);

public sealed record Barter(
    string Id, string TraderId, int MinTraderLevel, string? TaskUnlockId,
    IReadOnlyList<RecipeItem> Inputs, RecipeItem Output, int? BuyLimit);

public enum SectionState { Missing, Fresh, Cached }

/// <summary>Per-section availability so quests, hideout and recipes degrade independently.</summary>
public sealed class SectionStatus
{
    public SectionState Items { get; set; }
    public SectionState Traders { get; set; }
    public SectionState Quests { get; set; }
    public SectionState Hideout { get; set; }
    public SectionState Crafts { get; set; }
    public SectionState Barters { get; set; }

    [JsonIgnore]
    public IEnumerable<(string Name, SectionState State)> All => new[]
    {
        ("items", Items), ("traders", Traders), ("quests", Quests), ("hideout", Hideout), ("crafts", Crafts), ("barters", Barters),
    };

    [JsonIgnore] public bool AnyMissing => All.Any(s => s.State == SectionState.Missing);
}

/// <summary>The normalized last-good catalog for one mode. This is what the cache stores.</summary>
public sealed class CatalogSnapshot
{
    public const int CurrentSchema = 1;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public required string ModeId { get; init; }
    public DateTimeOffset FetchedAt { get; set; }
    public MarketSettings? Market { get; set; }
    public Dictionary<string, CatalogItem> Items { get; set; } = new();
    public Dictionary<string, string> TraderNames { get; set; } = new();
    public Dictionary<string, string> StationNames { get; set; } = new();
    public Dictionary<string, string> TaskNames { get; set; } = new();
    public List<QuestInfo>? Quests { get; set; }
    public List<HideoutStation>? Hideout { get; set; }
    public List<Craft>? Crafts { get; set; }
    public List<Barter>? Barters { get; set; }
    public SectionStatus Sections { get; set; } = new();

    public string TraderName(string? traderId) =>
        traderId != null && TraderNames.TryGetValue(traderId, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "Trader";

    public string StationName(string? stationId) =>
        stationId != null && StationNames.TryGetValue(stationId, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "Hideout station";

    public string? TaskName(string? taskId) =>
        taskId != null && TaskNames.TryGetValue(taskId, out var n) && !string.IsNullOrWhiteSpace(n) ? n : null;

    public CatalogItem? Item(string? id) => id != null && Items.TryGetValue(id, out var i) ? i : null;
}
