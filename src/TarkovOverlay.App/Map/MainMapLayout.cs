using System.Windows;
using System.Windows.Controls;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Views;

namespace TarkovOverlay.App.Map;

/// <summary>
/// Main window body with the map. Columns: map · search/tracked · detail card. 1.1.4: the settings rows that sat
/// above the map moved to Settings…, so the map fills the whole first column (the header bar spans the window). A splitter between the first
/// column and the search column sets how wide the map is. "Pop out" moves the map into its own normal window.
/// </summary>
internal static class MainMapLayout
{
    public static UIElement Build(MapFeature f, UIElement middle, UIElement right)
    {
        var grid = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        double star = f.Settings.MapColumnStar is > 0.2 and < 8 ? f.Settings.MapColumnStar.Value : 1.25;
        var c0 = new ColumnDefinition { Width = new GridLength(star, GridUnitType.Star), MinWidth = 380 };
        var c3 = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 };
        grid.ColumnDefinitions.Add(c0);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(c3);

        // column 0: the map, top to bottom
        var mapHost = new ContentControl();
        var col0Panel = Ui.Panel(mapHost);
        Grid.SetColumn(col0Panel, 0); grid.Children.Add(col0Panel);

        // A plain thumb (not a GridSplitter) so dragging trades width between the map column and the detail column
        // while the search column in between keeps its width.
        var grip = new FrameworkElementFactory(typeof(Border));
        grip.SetValue(Border.BackgroundProperty, Theme.Window);
        var split = new System.Windows.Controls.Primitives.Thumb
        {
            Width = 6, Cursor = System.Windows.Input.Cursors.SizeWE, ToolTip = "Drag to make the map column wider or narrower",
            Template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb)) { VisualTree = grip },
        };
        Grid.SetColumn(split, 1); grid.Children.Add(split);
        split.DragDelta += (_, e) =>
        {
            double total = c0.ActualWidth + c3.ActualWidth;
            double w0 = Math.Clamp(c0.ActualWidth + e.HorizontalChange, c0.MinWidth, Math.Max(c0.MinWidth, total - c3.MinWidth));
            c0.Width = new GridLength(w0, GridUnitType.Star);
            c3.Width = new GridLength(Math.Max(1, total - w0), GridUnitType.Star);
        };
        split.DragCompleted += (_, _) =>
        {
            if (c3.ActualWidth > 0) { f.Settings.MapColumnStar = c0.ActualWidth / c3.ActualWidth; f.SaveSoon(); }
        };

        Grid.SetColumn(middle, 2); grid.Children.Add(middle);
        Grid.SetColumn(right, 3); grid.Children.Add(right);

        var poppedNote = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        poppedNote.Children.Add(Ui.Text("The map is in its own window.", 12, Theme.Muted));
        poppedNote.Children.Add(Ui.Button("Bring it back here", (_, _) => f.DockMap()));

        void Place(bool host)
        {
            mapHost.Content = null;
            if (!host) { mapHost.Content = poppedNote; return; }
            f.Panel.Margin = new Thickness(0);
            f.Panel.SetHostMode(poppedOut: false);
            mapHost.Content = f.Panel;
        }
        f.PanelHostRequested += Place;
        Place(true);
        return grid;
    }
}
