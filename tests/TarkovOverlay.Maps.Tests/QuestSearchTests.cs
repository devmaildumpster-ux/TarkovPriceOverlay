using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Quests;
using Xunit;

namespace TarkovOverlay.Maps.Tests;

/// <summary>
/// 1.3.1: "postman pat" in Quests… must find Therapist/Prapor Postman Pat even when the name uses an en-dash,
/// and "Not done" must still list locked follow-ups. The empty-list bug was a layout issue (Showing N, no rows);
/// these tests lock the filter/search side so a punctuation or status miss does not hide the quest again.
/// </summary>
public class QuestSearchTests
{
    private const string Factory = "55f2d3fd4bdc2d5f408b4567";

    private static TaskInfo Quest(string id, string name, string normalized, string trader, string? mapId = null,
        IReadOnlyList<TaskRequirement>? requires = null, IReadOnlyList<ObjectiveInfo>? objectives = null) =>
        new(id, name, normalized, null, trader, 0, "Any",
            requires ?? Array.Empty<TaskRequirement>(),
            objectives ?? Array.Empty<ObjectiveInfo>(),
            mapId, Kappa: true, Lightkeeper: false, WikiLink: null);

    private static TaskFeed PostmanFeed()
    {
        var findLetter = new ObjectiveInfo("find-letter", "findQuestItem", "Find the letter", false,
            Array.Empty<string>(), Array.Empty<ObjectiveSpot>());
        var p1 = Quest("59675ea386f77414b32bded2", "Postman Pat - Part 1", "postman-pat-part-1", "Prapor",
            Factory, objectives: new[] { findLetter });
        // live tarkov.dev / wiki sometimes use an en-dash in the title; PvE Part 2 is Therapist's hand-in
        var p2 = Quest("596760e186f7741e11214d58", "Postman Pat – Part 2", "postman-pat-part-2", "Therapist",
            requires: new[] { new TaskRequirement("59675ea386f77414b32bded2", new[] { "active" }) });
        var other = Quest("other", "Factory Newcomers", "factory-newcomers", "Prapor", Factory);
        return new TaskFeed { ModeId = "pve", Tasks = { p1, p2, other } };
    }

    [Theory]
    [InlineData("postman pat")]
    [InlineData("Postman Pat")]
    [InlineData("postman-pat")]
    [InlineData("Postman Pat – Part 1")] // en-dash typed in the box
    [InlineData("postman pat part 1")]
    public void Postman_pat_search_finds_part_1_and_part_2_among_not_done(string q)
    {
        var f = PostmanFeed();
        var p = new ModeProgress();
        var hits = QuestPins.Filter(f, p, new QuestListFilter("Not done", "All traders", q));
        if (q.Contains("part 1", StringComparison.OrdinalIgnoreCase) || q.Contains("Part 1", StringComparison.Ordinal))
        {
            Assert.Contains(hits, t => t.Name.StartsWith("Postman Pat", StringComparison.Ordinal) && t.Name.Contains("1"));
        }
        else
        {
            Assert.Equal(2, hits.Count);
            Assert.Contains(hits, t => t.Id == "59675ea386f77414b32bded2");
            Assert.Contains(hits, t => t.Id == "596760e186f7741e11214d58");
        }
        Assert.DoesNotContain(hits, t => t.Name == "Factory Newcomers");
    }

    [Fact]
    public void Not_done_includes_locked_part_2_and_drops_a_completed_part()
    {
        var f = PostmanFeed();
        var p = new ModeProgress();
        Assert.Equal(QuestAvailability.Available, QuestLogic.Availability(f.Get("59675ea386f77414b32bded2")!, f, p));
        Assert.Equal(QuestAvailability.Locked, QuestLogic.Availability(f.Get("596760e186f7741e11214d58")!, f, p));

        var notDone = QuestPins.Filter(f, p, new QuestListFilter("Not done", "All traders", "postman pat"));
        Assert.Equal(2, notDone.Count);

        p.Set("59675ea386f77414b32bded2", QuestState.Complete);
        var after = QuestPins.Filter(f, p, new QuestListFilter("Not done", "All traders", "postman pat"));
        var one = Assert.Single(after);
        Assert.Equal("596760e186f7741e11214d58", one.Id);
        Assert.Equal(QuestAvailability.Available, QuestLogic.Availability(one, f, p));
    }

    [Fact]
    public void Factory_only_still_finds_postman_pat_part_1()
    {
        var f = PostmanFeed();
        var p = new ModeProgress();
        var hits = QuestPins.Filter(f, p, new QuestListFilter("Not done", "All traders", "postman pat", new[] { Factory }));
        var one = Assert.Single(hits);
        Assert.Equal("59675ea386f77414b32bded2", one.Id);
    }

    [Fact]
    public void NormalizeSearch_treats_dashes_and_punctuation_as_spaces()
    {
        Assert.Equal("postman pat part 1", QuestPins.NormalizeSearch("Postman Pat – Part 1"));
        Assert.Equal("postman pat part 1", QuestPins.NormalizeSearch("postman-pat-part-1"));
        Assert.Equal("postman pat part 1", QuestPins.NormalizeSearch("  Postman   Pat — Part 1. "));
        Assert.True(QuestPins.MatchesSearch(PostmanFeed().Get("59675ea386f77414b32bded2")!, "postman pat"));
    }
}
