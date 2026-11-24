using System.Text;
using TarkovOverlay.Core.Settings;

namespace TarkovOverlay.Core.Help;

/// <summary>One line of the controls list: the key/mouse/button, and what it does.</summary>
/// <param name="HotkeyId">Set for global shortcuts: "price", "wiki", "tarkov.dev", "search", "map style".</param>
public sealed record ControlRow(string Keys, string Action, string? HotkeyId = null);

public sealed record ControlSection(string Title, IReadOnlyList<ControlRow> Rows);

/// <summary>Live values the app knows at run time (null = describe the defaults, as in CONTROLS.txt).</summary>
/// <param name="MainShortcut">The registered price shortcut label, or null if none could be registered.</param>
public sealed record ControlsLive(string? MainShortcut, string? AutoLayerMode = null, bool? ExitListReader = null, string? ScreenshotKey = null);

/// <summary>
/// 1.2.3: the single source of truth for the "Controls &amp; hotkeys" window, CONTROLS.txt and the README list.
/// Only controls that exist in the code are listed (audited for 1.2.3).
/// </summary>
public static class ControlsHelp
{
    public const string NotSet = "(not set)";
    public const string Off = "(off)";
    public const string Title = "Controls & hotkeys";

    /// <summary>The price shortcut: the first of these that Windows lets the app register (HotkeyManager uses these labels).</summary>
    public const string MainKey1 = "` (backtick)", MainKey2 = "Ctrl+` (backtick)", MainKey3 = "Ctrl+Shift+F8";
    public static readonly IReadOnlyList<string> MainShortcutCandidates = new[] { MainKey1, MainKey2, MainKey3 };

    /// <summary>Every default global shortcut (for tests / docs).</summary>
    public static IReadOnlyList<string> DefaultHotkeys => new[]
    {
        MainKey1, MainKey2, MainKey3,
        AppSettings.DefaultSearchHotkey, AppSettings.DefaultWikiHotkey, AppSettings.DefaultDevHotkey, AppSettings.DefaultMapStyleHotkey,
    };

    /// <summary>A shortcut as shown to the user: the chord, or "(not set)" when cleared or switched off.</summary>
    public static string Show(string? chord, bool enabled) => !enabled || string.IsNullOrWhiteSpace(chord) ? NotSet : chord.Trim();

    public static string MainShortcutText(AppSettings s, ControlsLive? live) =>
        !s.HotkeyEnabled ? Off
        : live == null ? MainKey1
        : live.MainShortcut ?? "(none available: another app has all three keys)";

    private static string AutoLayerText(string? mode) => mode switch
    {
        "underground" => "Underground only",
        "off" => "Off (manual)",
        _ => "All floors & underground",
    };

    public static IReadOnlyList<ControlSection> Build(AppSettings s, ControlsLive? live = null)
    {
        var main = MainShortcutText(s, live);
        var search = Show(s.EffectiveSearchHotkey, s.SearchHotkeyEnabled);
        var wiki = Show(s.EffectiveWikiHotkey, s.WikiHotkeyEnabled);
        var dev = Show(s.EffectiveDevHotkey, s.DevHotkeyEnabled);
        var style = Show(s.EffectiveMapStyleHotkey, s.MapStyleHotkeyEnabled);
        var shotKey = live?.ScreenshotKey is { Length: > 0 } k ? k : "PrtScn";
        var exitOcr = live?.ExitListReader is { } on ? (on ? "on" : "off") : "off by default";
        var dwell = s.EffectiveAutoDwellMs;

        return new[]
        {
            new ControlSection("Price cards (in game)", new[]
            {
                new ControlRow("Rest the mouse on an item", s.AutoReadEnabled || live == null
                    ? $"Auto-read on hover{(live != null ? "" : " (on by default)")}: when Tarkov's name tooltip appears (after {dwell} ms), the price card shows by itself."
                    : "Auto-read on hover is OFF (Settings… › Overlay & Card): use the price shortcut."),
                new ControlRow(main, "Price shortcut: hover an item until its name tooltip shows, then press it for the price card next to the mouse.", "price"),
                new ControlRow(main == Off || main.StartsWith("(none", StringComparison.Ordinal) ? main : "Same key again", "With a card showing (mouse still): expand / collapse the full list of quests, crafts, barters and hideout needs.", "price"),
                new ControlRow($"{MainKey2.Replace(" (backtick)", "")} / {MainKey3}", live == null
                    ? "Used as the price shortcut instead of the backtick if another app already has it (the top bar shows which one you got)."
                    : "Fallback price shortcuts, used only if the backtick is taken by another app (the gold key above is the one in use)."),
                new ControlRow("Move the mouse", $"Hides the card (past {s.EffectiveMoveTolerancePx} px). It also hides after 7 s (15 s when expanded). The card never takes focus; clicks go through it."),
                new ControlRow(wiki, "Open the Tarkov Wiki page of the last item looked up.", "wiki"),
                new ControlRow(dev, "Open the last item looked up on tarkov.dev.", "tarkov.dev"),
            }),
            new ControlSection("Quick search bar (in game)", new[]
            {
                new ControlRow(search, "Open / close the search bar over the game.", "search"),
                new ControlRow("Type", "Filter by item name or short name."),
                new ControlRow("↑ / ↓", "Pick a result."),
                new ControlRow("Enter or double-click", "Show that item's price card."),
                new ControlRow("Esc", "Close the bar and give the keyboard back to the game."),
                new ControlRow("Click outside the bar", "Closes it."),
            }),
            new ControlSection("Map", new[]
            {
                new ControlRow(style, "Switch the map Satellite ↔ Vector from in game (never takes focus; docked or popped out).", "map style"),
                new ControlRow("Satellite / Vector button", "Same switch on the map toolbar (greyed out on maps that have only one style)."),
                new ControlRow("Mouse wheel", "Zoom in / out at the mouse."),
                new ControlRow("Left-drag", "Pan the map."),
                new ControlRow("Double-click the map", "Fit the whole map (also More ▾ › Fit whole map)."),
                new ControlRow("Hover a pin", "Show the exit / transit / quest spot name and details."),
                new ControlRow("Map box", "Pick a map. Auto-follow (tick box) switches to the raid's map by itself."),
                new ControlRow("Floor box", $"Pick a floor / underground level. Auto layer switching from screenshots: {AutoLayerText(live?.AutoLayerMode)} (Settings… › Map & Screenshots); a floor you pick stays until your next screenshot."),
                new ControlRow("Exits / Quests / Me", "Show or hide exits, your picked quests and your position dot."),
                new ControlRow("Pop out / Dock", "Open the map in its own window (remembers monitor and size), or put it back."),
                new ControlRow("F11 or Fullscreen", "In the popped-out map: borderless fullscreen on that monitor."),
                new ControlRow("Esc", "Leave fullscreen (popped-out map)."),
            }),
            new ControlSection("Screenshots & position (in raid)", new[]
            {
                new ControlRow(shotKey, "Tarkov's own Screenshot key (Tarkov Settings › Controls). Each screenshot puts your dot and facing on the map, and switches the floor. Bind it if nothing happens."),
                new ControlRow("More ▾ › Test position from latest screenshot", "Re-reads the newest screenshot and says what happened (folder, file, position)."),
                new ControlRow("O", $"Experimental exit-list reader ({exitOcr}; Settings… › Map & Screenshots): after you press O in raid it reads the exit list and fades and crosses out (⊗) the map's exits that aren't on it."),
            }),
            new ControlSection("Buttons, menus & windows", new[]
            {
                new ControlRow("? Controls", "This list (also More ▾, the tray menu and Settings…). Shows your current keys."),
                new ControlRow("Settings…", "Settings (also click the PvP/PvE badge): Overlay & Card, Map & Screenshots, Troubleshooting. Changes apply right away."),
                new ControlRow("Settings… › Record… / Clear / Default", "Change a shortcut: click Record… and press the new keys (Esc cancels), Clear to turn it off, Default to restore it."),
                new ControlRow("Refresh prices", "Download fresh prices now."),
                new ControlRow("More ▾ (map)", "Exits for auto / PMC / Scav, Show transits, Browse all quests, Area names, Fit whole map, Quest tracker…, Hideout tracker…, Test position…, Map settings…, Controls & hotkeys."),
                new ControlRow("Quests…", "Quest tracker. Double-click or Space marks a quest done / not done."),
                new ControlRow("Hideout…", "Hideout tracker: the station levels you've built (cards skip those needs)."),
                new ControlRow("Deselect all / Only my active quests", "Quick quest-pin choices under the map."),
                new ControlRow("Pop out card / Load price history / Wiki / tarkov.dev", "Buttons under the item card in the main window."),
                new ControlRow("Popped-out card", "Drag to move; double-click or Esc closes it."),
                new ControlRow("− / + / ×", "Tracked items: one handed in, one more, stop tracking."),
                new ControlRow("Update available: Download / Skip this version / ×", "Shows under the top bar when a newer version is on GitHub. Download opens the release page in your browser (nothing installs by itself); Skip hides that version; × hides it until next launch. Turn the check off in Settings… › Troubleshooting › Updates."),
            }),
            new ControlSection("Tray icon (when Minimize to tray is on)", new[]
            {
                new ControlRow("Click / double-click", "Show the main window."),
                new ControlRow("Right-click", "Menu: Open, Settings…, Controls & hotkeys, Quit."),
            }),
        };
    }

    public const string ChangeNote = "Shortcuts can be changed or cleared in Settings… (Overlay & Card: price shortcut on/off; Overlay & Card / Map & Screenshots: Record… / Clear / Default).";

    /// <summary>Plain text (CONTROLS.txt, README): default keys.</summary>
    public static string ToText(string? version = null)
    {
        version ??= typeof(ControlsHelp).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
        var sb = new StringBuilder();
        sb.Append("Tarkov Price Overlay ").Append(version).Append(" — ").Append(Title).Append(" (default keys)\n");
        sb.Append("In the app: the \"? Controls\" button at the top shows this list with YOUR current keys.\n");
        sb.Append(ChangeNote).Append('\n');
        sb.Append("Run Tarkov in Borderless or Windowed mode (cards can't draw over exclusive Fullscreen).\n");
        foreach (var sec in Build(new AppSettings()))
        {
            sb.Append('\n').Append(sec.Title.ToUpperInvariant()).Append('\n');
            foreach (var r in sec.Rows)
            {
                const int col = 30;
                var keys = r.Keys;
                var lines = Wrap(r.Action, 78);
                if (keys.Length > col - 2)
                {
                    sb.Append("  ").Append(keys).Append('\n');
                    foreach (var l in lines) sb.Append(new string(' ', col + 2)).Append(l).Append('\n');
                }
                else
                {
                    sb.Append("  ").Append(keys.PadRight(col)).Append(lines[0]).Append('\n');
                    foreach (var l in lines.Skip(1)) sb.Append(new string(' ', col + 2)).Append(l).Append('\n');
                }
            }
        }
        return sb.ToString().Replace("\n", "\r\n");
    }

    /// <summary>Markdown table rows for README.md (default keys).</summary>
    public static string ToMarkdown()
    {
        var sb = new StringBuilder();
        foreach (var sec in Build(new AppSettings()))
        {
            sb.Append("\n**").Append(sec.Title).Append("**\n\n| Control | What it does |\n|---|---|\n");
            foreach (var r in sec.Rows) sb.Append("| ").Append(Md(r.Keys)).Append(" | ").Append(r.Action.Replace("|", "\\|")).Append(" |\n");
        }
        return sb.ToString();
    }

    private static string Md(string keys) => keys.Contains('`') ? "``" + " " + keys + " " + "``" : keys.Replace("|", "\\|");

    private static List<string> Wrap(string text, int width)
    {
        var res = new List<string>();
        var line = new StringBuilder();
        foreach (var w in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + w.Length > width) { res.Add(line.ToString()); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(w);
        }
        if (line.Length > 0) res.Add(line.ToString());
        return res;
    }
}
