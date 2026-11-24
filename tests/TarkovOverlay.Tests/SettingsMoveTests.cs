using TarkovOverlay.Core;
using TarkovOverlay.Core.Geometry;
using TarkovOverlay.Core.Settings;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>1.1.4: options moved into Settings… keep their saved values; the shortcut on/off is now persisted.</summary>
public class SettingsMoveTests
{
    [Fact]
    public void MovedSettings_RoundTrip_AndOlderFilesKeepDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "tpo-" + Guid.NewGuid());
        var paths = new AppPaths(root);
        try
        {
            var s = new SettingsStore(paths).Load();
            Assert.True(s.HotkeyEnabled);
            s.HotkeyEnabled = false; s.ModeId = "pve"; s.TooltipScale = 3; s.DismissOnMove = false; s.MoveTolerancePx = 15;
            s.AlwaysShowFullList = true; s.AutoReadEnabled = false; s.AutoDwellMs = 750; s.AutoMaxTooltipWaitMs = 1200;
            new SettingsStore(paths).Save(s);
            var r = new SettingsStore(paths).Load();
            Assert.False(r.HotkeyEnabled);
            Assert.Equal("pve", r.ModeId);
            Assert.Equal(3, r.EffectiveTooltipScale);
            Assert.False(r.DismissOnMove);
            Assert.Equal(15, r.EffectiveMoveTolerancePx);
            Assert.True(r.AlwaysShowFullList);
            Assert.False(r.AutoReadEnabled);
            Assert.Equal(750, r.EffectiveAutoDwellMs);
            Assert.Equal(1200, r.EffectiveAutoMaxTooltipWaitMs);
            // a 1.1.3 file has no HotkeyEnabled: the shortcut stays on after the update
            File.WriteAllText(paths.SettingsFile, """{"SchemaVersion":1,"ModeId":"regular","DismissOnMove":true,"MoveTolerancePx":6}""");
            Assert.True(new SettingsStore(paths).Load().HotkeyEnabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(6, 1.0, 6)]
    [InlineData(6, 1.5, 9)]
    [InlineData(0, 1.0, 2)]   // never stricter than the old fixed 2 px
    [InlineData(2, 1.0, 2)]
    [InlineData(40, 1.25, 50)]
    public void LookupTolerance_FollowsTheCardTolerance(int tolAt100, double dpi, int expected) =>
        Assert.Equal(expected, DismissPolicy.LookupTolerancePx(tolAt100, dpi));

    [Fact]
    public void HandJitterDuringASlowRead_NoLongerDiscardsTheCard()
    {
        // 1.1.3: a 3-4 px drift while OCR fell through to Tesseract (e.g. "ComTac IT") threw the result away
        int tol = DismissPolicy.LookupTolerancePx(AppSettings.DefaultMoveTolerancePx, 1.0);
        Assert.False(CardPlacement.HasMoved(1445, 311, 1448, 314, tol));
        Assert.True(CardPlacement.HasMoved(1445, 311, 1452, 311, tol));   // a real move still cancels
        Assert.True(CardPlacement.HasMoved(1445, 311, 1448, 314));        // the old 2 px rule
    }
}
