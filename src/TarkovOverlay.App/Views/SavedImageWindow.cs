using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.App.Services;
using TarkovOverlay.Core.Detection;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Presentation;

namespace TarkovOverlay.App.Views;

/// <summary>
/// Saved-image lookup: open a screenshot, drag a box around the item title (or the whole tooltip), and read it
/// locally with the same detector + hybrid OCR. Nothing is uploaded; nothing is written unless exported.
/// </summary>
public sealed class SavedImageWindow : Window
{
    private readonly AppServices _s;
    private readonly Action<MatchResult> _toMain;
    private readonly Canvas _canvas = new();
    private readonly Image _image = new() { Stretch = Stretch.None };
    private readonly Rectangle _sel = new() { Stroke = Brushes.Gold, StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(40, 255, 215, 0)), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Ui.Text("Open an image, then drag a box around the item name.", 11.5, Theme.Muted, wrap: true);
    private readonly StackPanel _result = new();
    private RgbImage? _img;
    private Point _start;
    private bool _dragging;

    public SavedImageWindow(AppServices services, Action<MatchResult> toMain)
    {
        _s = services; _toMain = toMain;
        Title = "Saved image lookup"; Width = 1100; Height = 740; Background = Theme.Window;
        var dock = new DockPanel { Margin = new Thickness(6) };
        var top = Ui.Row(Ui.Button("Open image…", (_, _) => Open()), Ui.Button("Read selection", async (_, _) => await ReadSelection()), Ui.Button("Read whole image", async (_, _) => await ReadSelection(whole: true)));
        var header = new StackPanel(); header.Children.Add(top); header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        var side = new ScrollViewer { Width = 300, Content = _result, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(side, Dock.Right); dock.Children.Add(side);
        _canvas.Children.Add(_image); _canvas.Children.Add(_sel);
        _canvas.Background = Brushes.Black; _canvas.Cursor = Cursors.Cross;
        _canvas.MouseLeftButtonDown += (_, e) => { if (_img == null) return; _start = Clamp(e.GetPosition(_canvas)); _dragging = true; _canvas.CaptureMouse(); UpdateSel(_start); _sel.Visibility = Visibility.Visible; };
        _canvas.MouseMove += (_, e) => { if (_dragging) UpdateSel(Clamp(e.GetPosition(_canvas))); };
        _canvas.MouseLeftButtonUp += (_, _) => { _dragging = false; _canvas.ReleaseMouseCapture(); };
        dock.Children.Add(new ScrollViewer { Content = _canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;
    }

    private Point Clamp(Point p) => _img == null ? p : new Point(Math.Clamp(p.X, 0, _img.Width), Math.Clamp(p.Y, 0, _img.Height));

    private void UpdateSel(Point p)
    {
        Canvas.SetLeft(_sel, Math.Min(p.X, _start.X)); Canvas.SetTop(_sel, Math.Min(p.Y, _start.Y));
        _sel.Width = Math.Abs(p.X - _start.X); _sel.Height = Math.Abs(p.Y - _start.Y);
    }

    private void Open()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.ppm|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _img = ImageIo.Load(dlg.FileName);
            var bmp = ImageIo.ToBitmap(_img);
            _image.Source = bmp; _image.Width = _img.Width; _image.Height = _img.Height;
            _canvas.Width = _img.Width; _canvas.Height = _img.Height;
            _sel.Visibility = Visibility.Collapsed;
            _status.Text = $"{System.IO.Path.GetFileName(dlg.FileName)} · {_img.Width}×{_img.Height}. Drag around the item name or the whole tooltip, then Read selection.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            _status.Text = "Could not open image: " + ex.Message;
        }
    }

    private async Task ReadSelection(bool whole = false)
    {
        var ctx = _s.Catalog.Context;
        if (_img == null) { _status.Text = "Open an image first."; return; }
        if (ctx == null) { _status.Text = "Catalog not loaded yet."; return; }
        PixelRect r;
        if (whole || _sel.Visibility != Visibility.Visible || _sel.Width < 8 || _sel.Height < 6) r = new PixelRect(0, 0, _img.Width, _img.Height);
        else r = new PixelRect((int)Canvas.GetLeft(_sel), (int)Canvas.GetTop(_sel), (int)Math.Ceiling(_sel.Width), (int)Math.Ceiling(_sel.Height));
        var crop = _img.Crop(r);
        _status.Text = "Reading…";
        var img = _img;
        var (panel, det) = await Task.Run(() =>
        {
            // If the selection contains a Tarkov tooltip, read only its interior; otherwise read the selection as-is.
            var d = TooltipDetector.Detect(crop, crop.Width / 2, crop.Height / 2, 1.0, wideRetry: true);
            return (d.Status == DetectionStatus.Found ? crop.Crop(d.Best!.TitlePanel) : crop, d);
        });
        if (panel.Width * panel.Height > 4_000_000) { _status.Text = "Selection too large to OCR — drag a box around the item name."; return; }
        var outcome = await Task.Run(() => _s.Recognizer.RecognizeAsync(panel, ctx.Items, CancellationToken.None, det.Status == DetectionStatus.Found ? det.Best!.TitleLines : 0));
        var sb = new StringBuilder();
        sb.AppendLine(det.Status == DetectionStatus.Found ? "Tooltip panel found in selection." : "No tooltip panel found; read the selection as-is.");
        DiagnosticsFormatter.AppendOutcome(sb, outcome);
        _status.Text = sb.ToString();
        _result.Children.Clear();
        _result.Children.Add(CardRenderer.Build(_s.Cards(ctx).ForMatch(outcome.Result), 1.4, interactive: true));
        if (outcome.Result.Items.Count > 0) _toMain(outcome.Result);
    }
}
