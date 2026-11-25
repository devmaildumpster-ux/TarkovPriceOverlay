using System.Windows.Media;
using TarkovOverlay.Core.Economics;

namespace TarkovOverlay.App.Overlay;

/// <summary>High-contrast dark palette shared by the cursor card and the main window.</summary>
public static class Theme
{
    public static readonly SolidColorBrush Card = Brush("#111619");
    public static readonly SolidColorBrush CardEdge = Brush("#2C3539");
    public static readonly SolidColorBrush Text = Brush("#F0F3F1");
    public static readonly SolidColorBrush Muted = Brush("#AAB9BA");
    public static readonly SolidColorBrush Blue = Brush("#AAC8F0");
    public static readonly SolidColorBrush Green = Brush("#A4DFC1");
    public static readonly SolidColorBrush Gold = Brush("#E8C784");
    public static readonly SolidColorBrush Separator = Brush("#2A3337");
    public static readonly SolidColorBrush Window = Brush("#0B0F11");
    public static readonly SolidColorBrush Panel = Brush("#161D20");
    public static readonly SolidColorBrush Warn = Brush("#E8A784");
    public static readonly SolidColorBrush Kappa = Brush("#D7B4F3");
    public static readonly SolidColorBrush FlagActiveBg = Brush("#272115");
    public static readonly SolidColorBrush FlagQuestBg = Brush("#241B15");
    public static readonly SolidColorBrush FlagHideoutBg = Brush("#15202A");
    public static readonly FontFamily Font = new("Segoe UI");
    public static readonly FontFamily Numbers = new("Bahnschrift, Segoe UI");

    public static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static SolidColorBrush ForTier(ValueTier t) => t switch
    {
        ValueTier.Low => Muted, ValueTier.Decent => Blue, ValueTier.Good => Green, _ => Gold,
    };

    public static SolidColorBrush ForStatus(string? status) => status switch
    {
        "KEEP" => Gold, "FLEA IF ELIGIBLE" => Green, "TRADER" => Blue, _ => Muted,
    };

    /// <summary>1.3.0: pen vs armor class 1–6 (4 usually / 3 often / 2 50-50 / 1 rarely / 0 almost never).</summary>
    public static readonly SolidColorBrush Armor4 = Brush("#6EE7A8");
    public static readonly SolidColorBrush Armor3 = Green;
    public static readonly SolidColorBrush Armor2 = Gold;
    public static readonly SolidColorBrush Armor1 = Warn;
    public static readonly SolidColorBrush Armor0 = Brush("#6B4A4A");

    public static SolidColorBrush ForArmor(int level) => level switch
    {
        4 => Armor4, 3 => Armor3, 2 => Armor2, 1 => Armor1, _ => Armor0,
    };
}
