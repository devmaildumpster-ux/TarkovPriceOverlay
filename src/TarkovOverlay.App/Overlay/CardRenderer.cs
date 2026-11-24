using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TarkovOverlay.Core.Presentation;

namespace TarkovOverlay.App.Overlay;

/// <summary>
/// Renders a <see cref="CardModel"/> as one narrow column: name, status, right-aligned bold prices, thin
/// separators, compact sections. Base design is 1×; the whole card (text included) scales with a LayoutTransform.
/// </summary>
public static class CardRenderer
{
    public const double BaseWidth = 176;
    /// <summary>Expanded cards with more section lines than this use two columns.</summary>
    public const int TwoColumnThreshold = 16;

    private static void AddLine(StackPanel p, int kind, string text, bool first = false)
    {
        switch (kind)
        {
            case 0:
                if (!first) p.Children.Add(Sep());
                p.Children.Add(Label(text.ToUpperInvariant(), 8.5, Theme.Muted, FontWeights.SemiBold, spacing: true));
                break;
            case 1:
                p.Children.Add(Label(text, 9.5, Theme.Text, FontWeights.Normal, wrap: true, margin: new Thickness(0, 1, 0, 1)));
                break;
            default:
                p.Children.Add(Label(text, 8.5, Theme.Muted, FontWeights.Normal));
                break;
        }
    }

    public static FrameworkElement Build(CardModel m, double scale, bool interactive = false, double? width = null)
    {
        var stack = new StackPanel { Margin = new Thickness(9, 7, 9, 8) };

        if (m.Heading != null)
            stack.Children.Add(Label(m.Heading.ToUpperInvariant(), 9.5, Theme.Gold, FontWeights.Bold, spacing: true));

        // Header: consistent spot for the name, then status + cues on one line.
        stack.Children.Add(new TextBlock
        {
            Text = m.Title, Foreground = Theme.Text, FontFamily = Theme.Font, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2),
        });
        var cues = new WrapPanel();
        if (m.Status != null) cues.Children.Add(Pill(m.Status, Theme.ForStatus(m.Status)));
        if (m.StaleCue != null) cues.Children.Add(Pill(m.StaleCue, Theme.Warn));
        foreach (var b in m.Badges) cues.Children.Add(Pill(b, b == "KAPPA" ? Theme.Kappa : Theme.Gold));
        if (m.ModeCue != null) cues.Children.Add(Label(m.ModeCue, 8.5, Theme.Muted, FontWeights.Normal, margin: new Thickness(0, 1, 6, 0)));
        if (cues.Children.Count > 0) stack.Children.Add(cues);
        if (m.Notice != null) stack.Children.Add(Label("⚠ " + m.Notice, 9, Theme.Warn, FontWeights.Normal, wrap: true));

        if (m.Rows.Count > 0)
        {
            stack.Children.Add(Sep());
            foreach (var r in m.Rows) stack.Children.Add(Row(r));
        }
        if (m.Facts.Count > 0)
        {
            stack.Children.Add(Sep());
            foreach (var f in m.Facts) stack.Children.Add(Label(f, 9, Theme.Muted, FontWeights.Normal, wrap: true));
        }
        double baseWidth = width ?? BaseWidth;
        int sectionLines = m.Sections.Sum(x => 1 + x.Rows.Count);
        bool twoColumns = m.Expanded && sectionLines > TwoColumnThreshold;
        if (twoColumns && width is null) baseWidth = BaseWidth * 2 + 12;
        if (m.Sections.Count > 0)
        {
            // Flatten sections into (title | row | more) lines so a long section can continue in the second column.
            var lines = new List<(int Kind, string Text, string Section)>();
            foreach (var sec in m.Sections)
            {
                lines.Add((0, sec.Title, sec.Title));
                foreach (var row in sec.Rows) lines.Add((1, row, sec.Title));
                if (sec.MoreCount > 0)
                    lines.Add((2, interactive || m.Expanded ? $"+{sec.MoreCount} more" : $"+{sec.MoreCount} more · press the shortcut again", sec.Title));
            }
            if (!twoColumns)
            {
                foreach (var l in lines) AddLine(stack, l.Kind, l.Text);
            }
            else
            {
                stack.Children.Add(Sep());
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var c1 = new StackPanel(); var c2 = new StackPanel();
                int half = (lines.Count + 1) / 2;
                // don't end column 1 on a bare section title
                if (half > 0 && half < lines.Count && lines[half - 1].Kind == 0) half--;
                for (int i = 0; i < lines.Count; i++)
                {
                    var l = lines[i];
                    var col = i < half ? c1 : c2;
                    if (i == half && l.Kind != 0) AddLine(col, 0, l.Section + " (cont.)", first: true);
                    AddLine(col, l.Kind, l.Text, first: i == 0 || i == half);
                }
                Grid.SetColumn(c1, 0); Grid.SetColumn(c2, 2);
                grid.Children.Add(c1); grid.Children.Add(c2);
                stack.Children.Add(grid);
            }
        }
        if (m.Messages.Count > 0)
        {
            if (m.Rows.Count > 0 || m.Sections.Count > 0) stack.Children.Add(Sep());
            foreach (var line in m.Messages) stack.Children.Add(Label(line, 9.5, Theme.Muted, FontWeights.Normal, wrap: true));
        }

        var border = new Border
        {
            Background = Theme.Card, BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Child = stack, Width = baseWidth,
            SnapsToDevicePixels = true, IsHitTestVisible = interactive,
        };
        TextOptions.SetTextFormattingMode(border, TextFormattingMode.Ideal);
        if (Math.Abs(scale - 1) > 0.001) border.LayoutTransform = new ScaleTransform(scale, scale);
        return border;
    }

    private static UIElement Row(CardRow r)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition());
        var label = Label(r.Label, 9.5, Theme.Muted, FontWeights.Normal);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(label);
        // Price keeps its room: the label is the part that gets shortened.
        var value = new TextBlock
        {
            Text = r.Value, FontFamily = Theme.Numbers, FontSize = 13.5, FontWeight = FontWeights.Bold,
            Foreground = r.Tier is { } t ? Theme.ForTier(t) : Theme.Text, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6, 0, 0, 0),
        };
        Grid.SetColumn(value, 1);
        g.Children.Add(value);
        if (!string.IsNullOrEmpty(r.Detail))
        {
            var d = Label(r.Detail!, 8.5, r.Tier is { } t2 ? Theme.ForTier(t2) : Theme.Muted, FontWeights.Normal);
            d.TextTrimming = TextTrimming.CharacterEllipsis;
            d.HorizontalAlignment = HorizontalAlignment.Right;
            d.Opacity = r.Tier is null ? 0.85 : 0.9;
            Grid.SetRow(d, 1); Grid.SetColumnSpan(d, 2);
            g.Children.Add(d);
        }
        return g;
    }

    private static TextBlock Label(string text, double size, Brush fg, FontWeight weight, bool wrap = false, bool spacing = false, Thickness? margin = null) => new()
    {
        Text = text, FontSize = size, Foreground = fg, FontWeight = weight, FontFamily = Theme.Font,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = margin ?? new Thickness(0),
    };

    private static Border Pill(string text, Brush color) => new()
    {
        BorderBrush = color, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
        Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 1, 5, 1),
        Child = new TextBlock { Text = text, FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = color, FontFamily = Theme.Font },
    };

    private static Border Sep() => new() { Height = 1, Background = Theme.Separator, Margin = new Thickness(0, 5, 0, 4) };
}
