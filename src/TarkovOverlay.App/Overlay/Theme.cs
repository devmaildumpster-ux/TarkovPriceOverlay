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
}
