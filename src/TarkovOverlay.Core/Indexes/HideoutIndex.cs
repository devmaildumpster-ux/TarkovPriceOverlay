using System.Globalization;
using TarkovOverlay.Core.Catalog;

namespace TarkovOverlay.Core.Indexes;

public sealed record HideoutNeed(
    string GoalKey, string StationId, string StationName, int Level, string ItemId, int Count, bool FoundInRaid,
    IReadOnlyList<string> Prerequisites, string? Bonus);

public sealed class HideoutIndex
{
    private readonly Dictionary<string, List<HideoutNeed>> _byItem = new(StringComparer.Ordinal);
    public bool Available { get; }

    public HideoutIndex(CatalogSnapshot snap)
    {
        if (snap.Hideout == null) return;
        Available = true;
        foreach (var st in snap.Hideout)
            foreach (var lv in st.Levels)
            {
                var prereqs = new List<string>();
                prereqs.AddRange(lv.Stations.Select(s => $"{snap.StationName(s.StationId)} level {s.Level}"));
                prereqs.AddRange(lv.Traders.Select(t => t.RequirementType == "level"
                    ? $"{snap.TraderName(t.TraderId)} LL{t.Value.ToString("0", CultureInfo.InvariantCulture)}"
                    : $"{snap.TraderName(t.TraderId)} {t.RequirementType} {t.CompareMethod} {t.Value.ToString("0.##", CultureInfo.InvariantCulture)}"));
                prereqs.AddRange(lv.Skills.Select(s => $"{SplitCamel(s.Skill)} skill {s.Level}"));
                var bonus = lv.Bonuses.Select(DescribeBonus).FirstOrDefault(b => b != null);
                foreach (var req in lv.Items)
                {
                    var need = new HideoutNeed(GoalKeys.Hideout(snap.ModeId, st.Id, lv.Level, req.RequirementId), st.Id, st.Name, lv.Level,
                        req.ItemId, req.Count, req.FoundInRaid, prereqs, bonus);
                    if (!_byItem.TryGetValue(req.ItemId, out var l)) _byItem[req.ItemId] = l = new List<HideoutNeed>();
                    l.Add(need);
                }
            }
    }

    public IReadOnlyList<HideoutNeed> For(string itemId) => _byItem.TryGetValue(itemId, out var l) ? l : Array.Empty<HideoutNeed>();
    public int Total(string itemId) => For(itemId).Sum(n => n.Count);

    /// <summary>Only clearly-labelled bonuses from known feed types; unknown types are not guessed at.</summary>
    public static string? DescribeBonus(HideoutBonus b)
    {
        var v = b.Value;
        string Pct(double x) => (x * (Math.Abs(x) <= 1 ? 100 : 1)).ToString("+0.#;-0.#", CultureInfo.InvariantCulture) + "%";
        return b.Type switch
        {
            "StashSize" when v.HasValue => "Stash size upgrade",
            "AdditionalSlots" when v.HasValue => $"+{v.Value.ToString("0", CultureInfo.InvariantCulture)} production slots",
            "ScavCooldownTimer" when v.HasValue => $"Scav cooldown {Pct(v.Value)}",
            "FuelConsumption" when v.HasValue => $"Fuel use {Pct(v.Value)}",
            "ExperienceRate" when v.HasValue => $"XP rate {Pct(v.Value)}",
            "InsuranceReturnTime" when v.HasValue => $"Insurance return {Pct(v.Value)}",
            "RagfairCommission" when v.HasValue => $"Flea fee {Pct(v.Value)}",
            "QuestMoneyReward" when v.HasValue => $"Quest money {Pct(v.Value)}",
            "SkillGroupLevelingBoost" when v.HasValue && b.Skill != null => $"{b.Skill} skills {Pct(v.Value)}",
            "UnlockWeaponModification" => "Unlocks weapon modding",
            "UnlockWeaponRepair" => "Unlocks weapon repair",
            "UnlockArmorRepair" => "Unlocks armor repair",
            _ => null,
        };
    }

    internal static string SplitCamel(string s) =>
        string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(s[i - 1]) ? " " + c : c.ToString()));
}
