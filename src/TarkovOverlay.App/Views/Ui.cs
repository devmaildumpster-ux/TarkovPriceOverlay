using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TarkovOverlay.App.Overlay;

namespace TarkovOverlay.App.Views;

/// <summary>Small factory helpers for the code-built dark UI.</summary>
internal static class Ui
{
    public static TextBlock Text(string s, double size = 12, Brush? fg = null, FontWeight? w = null, bool wrap = false) => new()
    {
        Text = s, FontSize = size, Foreground = fg ?? Theme.Text, FontWeight = w ?? FontWeights.Normal, FontFamily = Theme.Font,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button Button(string label, RoutedEventHandler onClick, string? tip = null)
    {
        var b = new Button
        {
            Content = label, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(3), Foreground = Theme.Text,
            Background = Theme.Panel, BorderBrush = Theme.CardEdge, ToolTip = tip, FontFamily = Theme.Font,
        };
        b.Click += onClick;
        return b;
    }

    public static Border Panel(UIElement child, Thickness? margin = null) => new()
    {
        Background = Theme.Panel, BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8), Margin = margin ?? new Thickness(4), Child = child,
    };

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static Style DarkListStyle()
    {
        var s = new Style(typeof(ListBox));
        s.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Window));
        s.Setters.Add(new Setter(Control.ForegroundProperty, Theme.Text));
        s.Setters.Add(new Setter(Control.BorderBrushProperty, Theme.CardEdge));
        return s;
    }
}
