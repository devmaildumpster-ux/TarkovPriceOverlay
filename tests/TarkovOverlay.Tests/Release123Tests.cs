using System.Text.Json;
using TarkovOverlay.Core;
using TarkovOverlay.Core.Help;
using TarkovOverlay.Core.Settings;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>1.2.3: Controls &amp; hotkeys help (window content, CONTROLS.txt, README) and the first-launch flag.</summary>
public class Release123Tests
{
    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "TarkovOverlay.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static IEnumerable<ControlRow> Rows(IReadOnlyList<ControlSection> s) => s.SelectMany(x => x.Rows);
    private static string AllText(IReadOnlyList<ControlSection> s) => string.Join("\n", Rows(s).Select(r => r.Keys + " " + r.Action));

    [Fact]
    public void Every_default_hotkey_is_in_the_help_and_every_registered_extra_shortcut_has_a_row()
    {
        var st = new AppSettings();
        var help = ControlsHelp.Build(st);
        var text = AllText(help);
        foreach (var k in ControlsHelp.DefaultHotkeys) Assert.Contains(k.Replace(" (backtick)", ""), text);
        foreach (var (name, chord) in st.ExtraHotkeys())
        {
            var row = Rows(help).Single(r => r.HotkeyId == name);
            Assert.Equal(chord, row.Keys);
        }
        Assert.Equal(5, st.ExtraHotkeys().Count());
        Assert.Equal("` (backtick)", Rows(help).First(r => r.HotkeyId == "price").Keys);
    }

    [Fact]
    public void Shows_the_current_bindings()
    {
        var st = new AppSettings { SearchHotkey = "Ctrl+Shift+S", WikiHotkey = "F9", MapStyleHotkey = "Alt+M" };
        var help = ControlsHelp.Build(st, new ControlsLive("Ctrl+` (backtick)", "underground", true, "PrtScn / F12"));
        Assert.Equal("Ctrl+Shift+S", Rows(help).Single(r => r.HotkeyId == "search").Keys);
        Assert.Equal("F9", Rows(help).Single(r => r.HotkeyId == "wiki").Keys);
        Assert.Equal("Alt+M", Rows(help).Single(r => r.HotkeyId == "map style").Keys);
        Assert.Equal("Ctrl+Alt+D", Rows(help).Single(r => r.HotkeyId == "tarkov.dev").Keys);
        Assert.Equal("Ctrl+` (backtick)", Rows(help).First(r => r.HotkeyId == "price").Keys);
        var text = AllText(help);
        Assert.Contains("PrtScn / F12", text);
        Assert.Contains("Underground only", text);
        Assert.Contains("exit-list reader (on;", text);
    }

    [Fact]
    public void Cleared_or_disabled_shortcuts_show_not_set()
    {
        var st = new AppSettings { SearchHotkey = "", DevHotkeyEnabled = false, HotkeyEnabled = false };
        var help = ControlsHelp.Build(st, new ControlsLive("` (backtick)"));
        Assert.Equal(ControlsHelp.NotSet, Rows(help).Single(r => r.HotkeyId == "search").Keys);
        Assert.Equal(ControlsHelp.NotSet, Rows(help).Single(r => r.HotkeyId == "tarkov.dev").Keys);
        Assert.Equal(ControlsHelp.Off, Rows(help).First(r => r.HotkeyId == "price").Keys);
        Assert.Equal("Ctrl+Alt+W", Rows(help).Single(r => r.HotkeyId == "wiki").Keys);
        // none of the three price keys could be registered
        var none = ControlsHelp.Build(new AppSettings(), new ControlsLive(null));
        Assert.StartsWith("(none available", Rows(none).First(r => r.HotkeyId == "price").Keys);
        Assert.Equal("(not set)", ControlsHelp.Show("  ", true));
        Assert.Equal("F5", ControlsHelp.Show(" F5 ", true));
    }

    [Fact]
    public void Help_has_the_expected_sections_and_no_empty_rows()
    {
        var help = ControlsHelp.Build(new AppSettings());
        Assert.Equal(new[] { "Price cards (in game)", "Quick search bar (in game)", "Map", "Screenshots & position (in raid)", "Buttons, menus & windows", "Tray icon (when Minimize to tray is on)" },
            help.Select(s => s.Title));
        Assert.All(Rows(help), r => { Assert.False(string.IsNullOrWhiteSpace(r.Keys)); Assert.False(string.IsNullOrWhiteSpace(r.Action)); });
        var text = AllText(help);
        foreach (var must in new[] { "Esc", "↑ / ↓", "Enter", "Mouse wheel", "Left-drag", "Double-click the map", "F11", "Floor box", "PrtScn", "O", "More ▾", "Quests…", "Hideout…", "Settings…", "Right-click" })
            Assert.Contains(must, text);
    }

    [Fact]
    public void First_launch_flag_defaults_off_and_persists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tpo123-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var store = new SettingsStore(paths);
            var s = store.Load();
            Assert.False(s.ShownControlsHelp);
            s.ShownControlsHelp = true;
            store.Save(s);
            Assert.True(new SettingsStore(paths).Load().ShownControlsHelp);
            Assert.False(JsonSerializer.Deserialize<AppSettings>("{}")!.ShownControlsHelp); // older settings files: shown once after the update
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void CONTROLS_txt_matches_the_generator_and_lists_every_default_hotkey()
    {
        var file = Path.Combine(Repo(), "CONTROLS.txt");
        var expected = ControlsHelp.ToText();
        if (Environment.GetEnvironmentVariable("TPO_UPDATE_DOCS") == "1") File.WriteAllText(file, expected, new System.Text.UTF8Encoding(true));
        var actual = File.ReadAllText(file);
        Assert.Equal(expected, actual);
        foreach (var k in ControlsHelp.DefaultHotkeys) Assert.Contains(k.Replace(" (backtick)", ""), actual);
        Assert.Contains(typeof(ControlsHelp).Assembly.GetName().Version!.ToString(3), actual.Split('\n')[0]);
    }

    [Fact]
    public void README_controls_section_matches_the_generator()
    {
        var file = Path.Combine(Repo(), "README.md");
        var text = File.ReadAllText(file);
        const string a = "<!-- controls:start -->", b = "<!-- controls:end -->";
        int i = text.IndexOf(a, StringComparison.Ordinal), j = text.IndexOf(b, StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i, "README needs the controls markers");
        var block = "\n" + ControlsHelp.ToMarkdown() + "\n";
        if (Environment.GetEnvironmentVariable("TPO_UPDATE_DOCS") == "1")
        {
            text = text[..(i + a.Length)] + block + text[j..];
            File.WriteAllText(file, text);
            j = text.IndexOf(b, StringComparison.Ordinal);
        }
        Assert.Equal(block, text[(i + a.Length)..j].Replace("\r\n", "\n"));
        foreach (var k in ControlsHelp.DefaultHotkeys) Assert.Contains(k.Replace(" (backtick)", ""), text[i..j]);
        Assert.True(i < text.IndexOf("## New in 1.2.2", StringComparison.Ordinal), "controls near the top");
    }
}
