using TarkovOverlay.Core;
using TarkovOverlay.Core.Input;
using TarkovOverlay.Core.Settings;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>1.2.1: the Satellite ↔ Vector map shortcut.</summary>
public class Release121Tests
{
    [Fact]
    public void Map_style_hotkey_default_is_safe_and_unique()
    {
        var s = new AppSettings();
        Assert.Equal("Ctrl+Alt+M", s.EffectiveMapStyleHotkey);
        Assert.True(s.MapStyleHotkeyEnabled);
        Assert.True(KeyChord.TryParse(s.EffectiveMapStyleHotkey, out var c) && c.IsSafe);
        Assert.Empty(s.HotkeyClashes("map style"));
        Assert.All(new[] { "wiki", "tarkov.dev", "search", "map style" }, n => Assert.Empty(s.HotkeyClashes(n)));
        // not the main price-card key either (backtick / Ctrl+backtick / Ctrl+Shift+F8)
        foreach (var main in new[] { "`", "Ctrl+`", "Ctrl+Shift+F8" })
            if (KeyChord.TryParse(main, out var m)) Assert.NotEqual(m, c);
    }

    [Fact]
    public void Map_style_hotkey_persists_clears_and_reports_clashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "tpo-" + Guid.NewGuid());
        var paths = new AppPaths(root);
        try
        {
            Directory.CreateDirectory(root);
            // a 1.2.0 file: no map-style key → default on after the update
            File.WriteAllText(paths.SettingsFile, """{"SchemaVersion":1,"ModeId":"pve","WikiHotkey":"F8"}""");
            var s = new SettingsStore(paths).Load();
            Assert.Equal("Ctrl+Alt+M", s.EffectiveMapStyleHotkey);
            Assert.True(s.MapStyleHotkeyEnabled);

            s.MapStyleHotkey = "Ctrl+Shift+F9";
            new SettingsStore(paths).Save(s);
            Assert.Equal("Ctrl+Shift+F9", new SettingsStore(paths).Load().EffectiveMapStyleHotkey);

            s.MapStyleHotkey = ""; // cleared stays cleared
            new SettingsStore(paths).Save(s);
            var r = new SettingsStore(paths).Load();
            Assert.Equal("", r.EffectiveMapStyleHotkey);
            Assert.DoesNotContain(r.ExtraHotkeys(), h => h.Name == "map style");

            s.MapStyleHotkey = "not a key";
            new SettingsStore(paths).Save(s);
            Assert.Equal("Ctrl+Alt+M", new SettingsStore(paths).Load().EffectiveMapStyleHotkey); // unparsable → default

            s.MapStyleHotkey = "ctrl + alt + w"; // same as the Wiki default
            s.WikiHotkey = null;
            Assert.Equal(new[] { "wiki" }, s.HotkeyClashes("map style"));
            s.MapStyleHotkeyEnabled = false;
            Assert.Empty(s.HotkeyClashes("wiki"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
