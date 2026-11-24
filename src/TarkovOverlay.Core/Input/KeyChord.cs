using System.Globalization;

namespace TarkovOverlay.Core.Input;

[Flags]
public enum ChordModifiers : uint { None = 0, Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8 }

/// <summary>
/// 1.2.0: a global shortcut such as "Ctrl+Alt+W" or "Alt+`" (Win32 modifier flags + virtual-key code). Parsing and
/// formatting are pure so the settings file and the key recorder agree. Plain keys without a modifier are refused
/// except F1–F24, so an extra shortcut can't swallow normal typing.
/// </summary>
public sealed record KeyChord(ChordModifiers Modifiers, uint Vk)
{
    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["`"] = 0xC0, ["Backtick"] = 0xC0, ["Tilde"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD,
        ["\\"] = 0xDC, [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF,
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["Pause"] = 0x13, ["ScrollLock"] = 0x91,
    };

    public bool IsFunctionKey => Vk is >= 0x70 and <= 0x87;

    /// <summary>Modifier keys themselves, Esc, Backspace and PrtScn can't be the main key.</summary>
    public static bool IsAllowedMainKey(uint vk) => vk is not (0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C or 0x1B or 0x08 or 0x2C or 0);

    /// <summary>Needs Ctrl, Alt or Win (Shift alone still types), unless it's F1–F24.</summary>
    public bool IsSafe => IsAllowedMainKey(Vk) && (IsFunctionKey || (Modifiers & (ChordModifiers.Ctrl | ChordModifiers.Alt | ChordModifiers.Win)) != 0);

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = new KeyChord(ChordModifiers.None, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        // split on '+' but allow the key itself to be '+'… not supported; '=' covers it.
        var parts = t.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length == 0)) return false;
        var mods = ChordModifiers.None;
        foreach (var p in parts[..^1])
        {
            var m = p.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ChordModifiers.Ctrl,
                "alt" => ChordModifiers.Alt,
                "shift" => ChordModifiers.Shift,
                "win" or "windows" => ChordModifiers.Win,
                _ => (ChordModifiers?)null,
            };
            if (m is null || (mods & m.Value) != 0) return false;
            mods |= m.Value;
        }
        var key = parts[^1];
        uint vk;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) vk = char.ToUpperInvariant(key[0]);
        else if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24) vk = (uint)(0x70 + f - 1);
        else if (Named.TryGetValue(key, out var n)) vk = n;
        else return false;
        var c = new KeyChord(mods, vk);
        if (!c.IsSafe) return false;
        chord = c;
        return true;
    }

    public static string KeyName(uint vk)
    {
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        foreach (var (k, v) in Named) if (v == vk && k is not ("Backtick" or "Tilde")) return k;
        return "VK" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ChordModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ChordModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ChordModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ChordModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(Vk));
        return string.Join("+", parts);
    }
}
