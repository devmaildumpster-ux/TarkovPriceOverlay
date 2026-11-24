using System.Globalization;
using System.Text.Json;
using TarkovOverlay.Core.Ammo;

namespace TarkovOverlay.Core.Catalog;

/// <summary>
/// Converts Tarkov.dev JSON feed documents into normalized, validated records.
/// Every accessor checks the JSON value kind; anything missing or malformed becomes null rather than zero.
/// </summary>
public static class FeedParser
{
    // ---------- generic helpers ----------
    internal static JsonElement? Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined ? v : null;

    internal static string? Str(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    internal static long? Long(JsonElement e, string name)
    {
        if (Prop(e, name) is not { ValueKind: JsonValueKind.Number } v) return null;
        if (v.TryGetInt64(out var l)) return l;
        if (v.TryGetDouble(out var d) && double.IsFinite(d)) return (long)Math.Round(d, MidpointRounding.AwayFromZero);
        return null;
    }

    internal static int? Int(JsonElement e, string name)
    {
        var l = Long(e, name);
        return l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
    }

    internal static double? Dbl(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    internal static bool? Bool(JsonElement e, string name) =>
        Prop(e, name) is { } v && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

    internal static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    internal static DateTimeOffset? Time(JsonElement e, string name)
    {
        var s = Str(e, name);
        if (s == null) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
    }

    /// <summary>Returns rows of a "data.{name}" member that may be either a dictionary keyed by ID or an array.</summary>
    internal static IEnumerable<JsonElement> Rows(JsonElement container)
    {
        if (container.ValueKind == JsonValueKind.Array) return container.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object);
        if (container.ValueKind == JsonValueKind.Object) return container.EnumerateObject().Select(p => p.Value).Where(x => x.ValueKind == JsonValueKind.Object);
        return Enumerable.Empty<JsonElement>();
    }

    public static JsonElement Data(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var d) ? d : default;

    /// <summary>Reads a translation dictionary (the "data" object of an *_en document).</summary>
    public static Dictionary<string, string> Translations(JsonDocument? doc)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc == null) return map;
        var data = Data(doc);
        if (data.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in data.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString() ?? "";
        return map;
    }

    /// <summary>
    /// The feed stores names on base records as translation keys ("{id} Name"). Join them to text.
    /// When no translation exists and the value looks like a key, return null instead of showing the key.
    /// </summary>
    internal static string? Translate(string? keyOrText, IReadOnlyDictionary<string, string> tr, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(keyOrText)) return null;
        if (tr.TryGetValue(keyOrText, out var t) && !string.IsNullOrWhiteSpace(t)) return t.Trim();
        if (LooksLikeKey(keyOrText, id)) return null;
        return keyOrText.Trim();
    }

    internal static bool LooksLikeKey(string s, string? id)
    {
        if (id != null && s.StartsWith(id, StringComparison.Ordinal)) return true;
        if (s.Length >= 24 && IsHexId(s.AsSpan(0, 24))) return true;
        return s.StartsWith("hideout_area_", StringComparison.Ordinal) || s.StartsWith("hideout_", StringComparison.Ordinal);
    }

    private static bool IsHexId(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    // ---------- items ----------
    public sealed record ItemsResult(Dictionary<string, CatalogItem> Items, MarketSettings? Market, int Rejected);

    public static ItemsResult ParseItems(JsonDocument itemsDoc, JsonDocument? itemsEn)
    {
        var tr = Translations(itemsEn);
        var data = Data(itemsDoc);
        var items = new Dictionary<string, CatalogItem>(StringComparer.Ordinal);
        int rejected = 0;
        if (data.ValueKind != JsonValueKind.Object || Prop(data, "items") is not { } rows)
            throw new FormatException("items feed has no data.items member");

        foreach (var r in Rows(rows))
        {
            var id = Str(r, "id");
            if (string.IsNullOrEmpty(id)) { rejected++; continue; }
            var name = Translate(Str(r, "name"), tr, id);
            if (name == null) { rejected++; continue; } // a nameless item cannot be matched or displayed
            var item = new CatalogItem
            {
                Id = id,
                Name = name,
                ShortName = Translate(Str(r, "shortName"), tr, id),
                NormalizedName = Str(r, "normalizedName"),
                Width = Int(r, "width") is > 0 and var w ? w : null,
                Height = Int(r, "height") is > 0 and var h ? h : null,
                BasePrice = Long(r, "basePrice"),
                Types = Arr(r, "types").Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToArray(),
                StackMaxSize = Int(r, "stackMaxSize"),
                Avg24hPrice = Long(r, "avg24hPrice"),
                LastScan = Time(r, "lastScan"),
                LastLowPrice = Long(r, "lastLowPrice"),
                LastOfferCount = Int(r, "lastOfferCount"),
                Low24hPrice = Long(r, "low24hPrice"),
                High24hPrice = Long(r, "high24hPrice"),
                MinLevelForFlea = Int(r, "minLevelForFlea"),
                SellToTrader = Arr(r, "sellToTrader").Select(ParseSale).Where(x => x != null).Cast<TraderSale>().ToArray(),
                BuyFromTrader = Arr(r, "buyFromTrader").Select(ParseBuy).Where(x => x != null).Cast<TraderBuy>().ToArray(),
                Container = ParseContainer(r),
                MagazineCapacity = Prop(r, "properties") is { ValueKind: JsonValueKind.Object } p && Str(p, "propertiesType") == "ItemPropertiesMagazine" ? Int(p, "capacity") : null,
                WikiLink = Str(r, "wikiLink"),
                Link = Str(r, "link"),
                ContainsItems = Arr(r, "containsItems").Select(c => Str(c, "item") is { } cid ? new ContainedItem(cid, Dbl(c, "count") ?? 1) : null).Where(c => c != null).Cast<ContainedItem>().ToArray(),
                Ammo = ParseAmmo(r),
            };
            items[id] = item;
        }

        MarketSettings? market = null;
        if (Prop(data, "fleaMarket") is { ValueKind: JsonValueKind.Object } fm)
        {
            market = new MarketSettings(
                Bool(fm, "enabled") ?? false,
                Int(fm, "minPlayerLevel"),
                Dbl(fm, "sellOfferFeeRate"),
                Dbl(fm, "sellRequirementFeeRate"));
        }
        return new ItemsResult(items, market, rejected);
    }

    private static TraderSale? ParseSale(JsonElement o)
    {
        var t = Str(o, "trader"); var price = Long(o, "price"); var rub = Long(o, "priceRUB");
        if (t == null || price == null || rub == null) return null;
        return new TraderSale(t, price.Value, rub.Value, (Str(o, "currency") ?? "RUB").ToUpperInvariant());
    }

    private static TraderBuy? ParseBuy(JsonElement o)
    {
        var t = Str(o, "trader"); var price = Long(o, "price"); var rub = Long(o, "priceRUB");
        if (t == null || price == null || rub == null) return null;
        return new TraderBuy(t, price.Value, rub.Value, (Str(o, "currency") ?? "RUB").ToUpperInvariant(),
            Int(o, "minTraderLevel") ?? 1, Str(o, "taskUnlock"), Int(o, "buyLimit"));
    }


    /// <summary>1.3.0: ItemPropertiesAmmo (damage, penetration, caliber, …). Null when the item isn't ammo.</summary>
    internal static AmmoStats? ParseAmmo(JsonElement r)
    {
        if (Prop(r, "properties") is not { ValueKind: JsonValueKind.Object } p) return null;
        if (Str(p, "propertiesType") != "ItemPropertiesAmmo") return null;
        var caliber = Str(p, "caliber");
        if (string.IsNullOrWhiteSpace(caliber)) return null;
        int? projectiles = Int(p, "projectileCount");
        if (projectiles is <= 0) projectiles = null;
        return new AmmoStats(
            caliber,
            Int(p, "damage"),
            Int(p, "penetrationPower"),
            Int(p, "armorDamage"),
            Dbl(p, "fragmentationChance"),
            Int(p, "initialSpeed"),
            Bool(p, "tracer"),
            Str(p, "tracerColor"),
            Str(p, "ammoType"),
            projectiles);
    }

    private static ContainerSpec? ParseContainer(JsonElement r)
    {
        if (Prop(r, "properties") is not { ValueKind: JsonValueKind.Object } p) return null;
        var type = Str(p, "propertiesType");
        var grids = Arr(p, "grids").Select(g => (Int(g, "width"), Int(g, "height")))
            .Where(g => g.Item1 is > 0 && g.Item2 is > 0).Select(g => new GridSize(g.Item1!.Value, g.Item2!.Value)).ToArray();
        if (grids.Length == 0) return null;
        string kind = type switch
        {
            "ItemPropertiesChestRig" => "rig",
            "ItemPropertiesBackpack" => "backpack",
            "ItemPropertiesContainer" => "container",
            _ => "container",
        };
        return new ContainerSpec(kind, Int(p, "capacity"), grids);
    }

    // ---------- traders ----------
    /// <summary>traders_en keys look like "{traderId} Nickname".</summary>
    public static Dictionary<string, string> ParseTraderNames(JsonDocument tradersEn)
    {
        var tr = Translations(tradersEn);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in tr)
        {
            const string suffix = " Nickname";
            if (k.EndsWith(suffix, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(v))
                names[k[..^suffix.Length]] = v.Trim();
        }
        return names;
    }

    // ---------- tasks ----------
    public static (List<QuestInfo> Quests, Dictionary<string, string> Names) ParseTasks(JsonDocument tasksDoc, JsonDocument? tasksEn)
    {
        var tr = Translations(tasksEn);
        var data = Data(tasksDoc);
        var rows = Prop(data, "tasks") ?? throw new FormatException("tasks feed has no data.tasks member");
        var list = new List<QuestInfo>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in Rows(rows))
        {
            var id = Str(t, "id");
            if (id == null) continue;
            var name = Translate(Str(t, "name"), tr, id) ?? Str(t, "normalizedName") ?? "Quest";
            names[id] = name;
            var handIns = new List<QuestHandIn>();
            foreach (var o in Arr(t, "objectives"))
            {
                if (Str(o, "type") != "giveItem") continue; // findItem objectives are not counted, avoiding double-counting
                var oid = Str(o, "id");
                var ids = Arr(o, "items").Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : Str(x, "id")).Where(x => x != null).Cast<string>().Distinct().ToArray();
                var count = Int(o, "count") ?? 0;
                if (oid == null || ids.Length == 0 || count <= 0) continue;
                handIns.Add(new QuestHandIn(oid, ids, count, Bool(o, "foundInRaid") ?? false, Bool(o, "optional") ?? false));
            }
            var prereq = Arr(t, "taskRequirements").Select(x => Str(x, "task")).Where(x => x != null).Cast<string>().ToArray();
            list.Add(new QuestInfo(id, name, Str(t, "trader"), Int(t, "minPlayerLevel"), prereq, handIns, Bool(t, "kappaRequired") ?? false));
        }
        return (list, names);
    }

    // ---------- hideout ----------
    public static (List<HideoutStation> Stations, Dictionary<string, string> Names) ParseHideout(JsonDocument hideoutDoc, JsonDocument? hideoutEn)
    {
        var tr = Translations(hideoutEn);
        var data = Data(hideoutDoc);
        var rows = Prop(data, "hideoutStations") ?? data;
        var stations = new List<HideoutStation>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in Rows(rows))
        {
            var id = Str(s, "id");
            if (id == null) continue;
            var name = Translate(Str(s, "name"), tr, id) ?? Pretty(Str(s, "normalizedName")) ?? "Hideout station";
            names[id] = name;
            var levels = new List<HideoutLevel>();
            foreach (var l in Arr(s, "levels"))
            {
                var lv = Int(l, "level");
                if (lv == null) continue;
                var items = Arr(l, "itemRequirements").Select(ir =>
                {
                    var item = Str(ir, "item") ?? (Prop(ir, "item") is { } io ? Str(io, "id") : null);
                    var count = Int(ir, "count") ?? Int(ir, "quantity");
                    if (item == null || count is not > 0) return null;
                    var fir = Prop(ir, "attributes") is { } a && Bool(a, "foundInRaid") == true;
                    return new HideoutItemRequirement(Str(ir, "id") ?? $"{id}-{lv}-{item}", item, count.Value, fir);
                }).Where(x => x != null).Cast<HideoutItemRequirement>().ToArray();
                var st = Arr(l, "stationLevelRequirements").Select(x => Str(x, "station") is { } sid && Int(x, "level") is { } sl ? new HideoutStationRequirement(sid, sl) : null)
                    .Where(x => x != null).Cast<HideoutStationRequirement>().ToArray();
                var trq = Arr(l, "traderRequirements").Select(x => Str(x, "trader") is { } tid ? new HideoutTraderRequirement(tid, Str(x, "requirementType") ?? "level", Str(x, "compareMethod") ?? ">=", Dbl(x, "value") ?? 0) : null)
                    .Where(x => x != null).Cast<HideoutTraderRequirement>().ToArray();
                var sk = Arr(l, "skillRequirements").Select(x => (Str(x, "skill") ?? Str(x, "name")) is { } n && Int(x, "level") is { } sl ? new HideoutSkillRequirement(n, sl) : null)
                    .Where(x => x != null).Cast<HideoutSkillRequirement>().ToArray();
                var bonuses = Arr(l, "bonuses").Select(x => Str(x, "type") is { } bt ? new HideoutBonus(bt, Dbl(x, "value"), Str(x, "skill"), Bool(x, "passive") ?? true) : null)
                    .Where(x => x != null).Cast<HideoutBonus>().ToArray();
                levels.Add(new HideoutLevel(lv.Value, items, st, trq, sk, bonuses));
            }
            stations.Add(new HideoutStation(id, name, levels.OrderBy(x => x.Level).ToArray()));
        }
        return (stations, names);
    }

    private static string? Pretty(string? normalized) =>
        string.IsNullOrWhiteSpace(normalized) ? null : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.Replace('-', ' '));

    // ---------- crafts & barters ----------
    private static RecipeItem? Recipe(JsonElement x)
    {
        var item = Str(x, "item") ?? (Prop(x, "item") is { } io ? Str(io, "id") : null);
        var count = Dbl(x, "count") ?? Dbl(x, "quantity");
        if (item == null || count is not > 0) return null;
        var attrs = Prop(x, "attributes");
        bool tool = attrs is { } a1 && Bool(a1, "tool") == true;
        bool fir = attrs is { } a2 && Bool(a2, "foundInRaid") == true;
        return new RecipeItem(item, count.Value, tool, fir);
    }

    public static List<Craft> ParseCrafts(JsonDocument doc)
    {
        var data = Data(doc);
        var rows = data.ValueKind == JsonValueKind.Array ? data : Prop(data, "crafts") ?? throw new FormatException("crafts feed has no rows");
        var list = new List<Craft>();
        foreach (var c in Rows(rows))
        {
            var id = Str(c, "id"); var station = Str(c, "station");
            var output = Prop(c, "productItem") is { } p ? Recipe(p) : null;
            if (id == null || station == null || output == null) continue;
            list.Add(new Craft(id, station, Int(c, "level") ?? 1, Int(c, "duration"), Str(c, "taskUnlock"),
                Arr(c, "requiredItems").Select(Recipe).Where(x => x != null).Cast<RecipeItem>().ToArray(),
                Arr(c, "requiredQuestItems").Count(), output,
                Arr(c, "gameEditions").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()));
        }
        return list;
    }

    public static List<Barter> ParseBarters(JsonDocument doc)
    {
        var data = Data(doc);
        var rows = data.ValueKind == JsonValueKind.Array ? data : Prop(data, "barters") ?? throw new FormatException("barters feed has no rows");
        var list = new List<Barter>();
        foreach (var b in Rows(rows))
        {
            var id = Str(b, "id"); var trader = Str(b, "trader");
            var output = Prop(b, "offeredItem") is { } p ? Recipe(p) : null;
            if (id == null || trader == null || output == null) continue;
            list.Add(new Barter(id, trader, Int(b, "minTraderLevel") ?? 1, Str(b, "taskUnlock"),
                Arr(b, "requiredItems").Select(Recipe).Where(x => x != null).Cast<RecipeItem>().ToArray(), output, Int(b, "buyLimit")));
        }
        return list;
    }

    // ---------- price history ----------
    public sealed record PricePoint(DateTimeOffset Time, long? Average, long? Min);

    public static List<PricePoint> ParsePriceHistory(JsonDocument doc)
    {
        var data = Data(doc);
        var list = new List<PricePoint>();
        if (data.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in data.EnumerateArray())
        {
            var ts = Long(p, "timestamp");
            if (ts == null) continue;
            list.Add(new PricePoint(DateTimeOffset.FromUnixTimeMilliseconds(ts.Value), Long(p, "price"), Long(p, "priceMin")));
        }
        return list.OrderBy(x => x.Time).ToList();
    }
}
