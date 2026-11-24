using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>
/// Regression floor for the whole-catalog sweep (1.2.4). Strictness first: no class may produce a confident wrong
/// item; then coverage: every look-alike/joined/reordered class must verify, and a lost second line must still put
/// the right item first (or in the choice list) for nearly every name.
/// </summary>
internal static class SweepAssertions
{
    public static void Check(Dictionary<string, MatchSweepTests.Tally> r)
    {
        foreach (var (cls, t) in r)
            Assert.True(t.WrongConfident == 0, $"{cls}: {t.WrongConfident} confident wrong item(s)\n{string.Join("\n", t.Examples)}");

        foreach (var cls in new[] { "exact name", "0→O in numbers", "1→l in numbers", "Q→O", "trailing 1→I", "x→× between digits" })
            Assert.True(r[cls].Verified == r[cls].Total, $"{cls}: {r[cls].Verified}/{r[cls].Total} verified");
        foreach (var w in new[] { 30, 36, 42 })
        {
            var joined = r[$"wrapped@{w}"]; var swapped = r[$"wrapped@{w}, lines swapped"]; var lost = r[$"wrapped@{w}, 2nd line lost"];
            Assert.Equal(joined.Total, joined.Verified);
            Assert.Equal(swapped.Total, swapped.Verified);
            // before 1.2.4 up to 39% missed here (954/2433 at width 30)
            Assert.True(lost.Missed * 10 < lost.Total, $"wrapped@{w}, 2nd line lost: {lost.Missed}/{lost.Total} missed");
        }
        var strict = r["strict: one wrong letter"];
        Assert.Equal(0, strict.Verified);
        Assert.True(strict.Missed * 10 < strict.Total, $"strict: {strict.Missed}/{strict.Total} missed");
    }
}
