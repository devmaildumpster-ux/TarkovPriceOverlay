using TarkovOverlay.Core.Catalog;

namespace TarkovOverlay.Core.Indexes;

/// <summary>One quest hand-in objective accepting an item. Several accepted item IDs share one goal key.</summary>
public sealed record QuestNeed(
    string GoalKey, string QuestId, string QuestName, string TraderName, int? MinPlayerLevel,
    IReadOnlyList<string> PrerequisiteNames, string ObjectiveId, IReadOnlyList<string> AcceptedItemIds,
    int Count, bool FoundInRaid, bool Optional, bool KappaRequired);

public sealed class QuestIndex
{
    private readonly Dictionary<string, List<QuestNeed>> _byItem = new(StringComparer.Ordinal);
    public bool Available { get; }
    public int ObjectiveCount { get; }

    public QuestIndex(CatalogSnapshot snap)
    {
        if (snap.Quests == null) return;
        Available = true;
        foreach (var q in snap.Quests)
        {
            var prereqs = q.PrerequisiteTaskIds.Select(snap.TaskName).Where(n => n != null).Cast<string>().ToArray();
            foreach (var h in q.HandIns)
            {
                var need = new QuestNeed(GoalKeys.Quest(snap.ModeId, q.Id, h.ObjectiveId), q.Id, q.Name, snap.TraderName(q.TraderId),
                    q.MinPlayerLevel, prereqs, h.ObjectiveId, h.ItemIds, h.Count, h.FoundInRaid, h.Optional, q.KappaRequired);
                ObjectiveCount++;
                foreach (var id in h.ItemIds)
                {
                    if (!_byItem.TryGetValue(id, out var l)) _byItem[id] = l = new List<QuestNeed>();
                    l.Add(need);
                }
            }
        }
    }

    public IReadOnlyList<QuestNeed> For(string itemId) =>
        _byItem.TryGetValue(itemId, out var l) ? l : Array.Empty<QuestNeed>();

    /// <summary>Default keep count: required (non-optional) hand-ins only.</summary>
    public int RequiredTotal(string itemId) => For(itemId).Where(n => !n.Optional).Sum(n => n.Count);
}

public static class GoalKeys
{
    public static string Quest(string mode, string questId, string objectiveId) => $"{mode}|quest|{questId}|{objectiveId}";
    public static string Hideout(string mode, string stationId, int level, string requirementId) => $"{mode}|hideout|{stationId}|{level}|{requirementId}";
    public static string ModeOf(string goalKey) => goalKey.Split('|')[0];
}
