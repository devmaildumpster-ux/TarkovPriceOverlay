using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.Maps.Data;
using TarkovOverlay.Maps.Definitions;
using TarkovOverlay.Maps.Extracts;
using TarkovOverlay.Maps.Geometry;

namespace TarkovOverlay.App.Map;

public sealed record QuestMarker(string TaskId, string TaskName, string Trader, string Objective, ObjectiveSpot Spot, Color Color, bool Optional, bool Active);
public sealed record PlayerMarker(Vec3 Position, double Yaw, DateTimeOffset At);

/// <summary>
/// Pan/zoom map surface. The base bitmap is drawn in tarkov.dev zoom-0 space; markers are drawn in screen space at a
/// constant size so they stay readable at every zoom. Drag to pan, wheel to zoom, double-click to fit.
/// </summary>
public sealed class MapView : FrameworkElement
{
    private MapImage? _image;
    private MapDefinition? _def;
    private IReadOnlyList<ExtractMarker> _extracts = Array.Empty<ExtractMarker>();
    private IReadOnlyList<QuestMarker> _quests = Array.Empty<QuestMarker>();
    private PlayerMarker? _player;
    private IReadOnlySet<string>? _listed;
    private double _scale = 1, _ox, _oy;
    private bool _userMoved;
    private Point? _dragFrom;
    private Point _dragOffset;
    private object? _hover;
    private Point _mouse;

    public bool ShowLabels { get; set; } = true;
    /// <summary>1.2.1: the base picture is satellite tiles → labels get dark pill backdrops so they read on busy terrain.</summary>
    public bool Satellite { get; set; }
    /// <summary>Self-test switch: Wine's font fallback crashes WPF text shaping, so the Linux check draws shapes only.</summary>
    internal static bool NoText;

    public static readonly Color PmcColor = Color.FromRgb(0x6F, 0xD0, 0x8C), ScavColor = Color.FromRgb(0xF2, 0xC1, 0x4E),
        SharedColor = Color.FromRgb(0x7F, 0xB8, 0xF5), CoOpColor = Color.FromRgb(0xC4, 0x8D, 0xF0), TransitColor = Color.FromRgb(0x4F, 0xD8, 0xD8),
        PlayerColor = Color.FromRgb(0xFF, 0x5A, 0x4F);

    public static Color ColorFor(ExtractKind k) => k switch
    {
        ExtractKind.Pmc => PmcColor, ExtractKind.Scav => ScavColor, ExtractKind.Shared => SharedColor, ExtractKind.CoOp => CoOpColor, _ => TransitColor,
    };

    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality); // 1.2.1: mipmapped/fant when zoomed out, smooth when zoomed in
    }

    public void SetMap(MapDefinition? def, MapImage? image, bool keepView = false)
    {
        bool sameMap = keepView && _def?.Key == def?.Key;
        _def = def; _image = image;
        if (!sameMap) { _userMoved = false; Fit(); }
        InvalidateVisual();
    }

    public void SetMarkers(IReadOnlyList<ExtractMarker> extracts, IReadOnlyList<QuestMarker> quests, IReadOnlySet<string>? listed)
    {
        _extracts = extracts; _listed = listed;
        _quests = _def == null ? quests : quests.Where(q => !CoversMap(q.Spot)).ToList();
        _hover = null;
        InvalidateVisual();
    }

    /// <summary>Zones like "kill anywhere on Factory" cover most of the map; drawing them hides everything else.</summary>
    private bool CoversMap(ObjectiveSpot s)
    {
        if (s.Outline.Count < 3 || _def == null) return false;
        var pts = s.Outline.Select(v => _def.Projection.ToMap(v)).ToList();
        double w = pts.Max(p => p.X) - pts.Min(p => p.X), h = pts.Max(p => p.Y) - pts.Min(p => p.Y);
        var b = _def.BoundsRect;
        return w * h > 0.2 * b.Width * b.Height;
    }

    public void SetPlayer(PlayerMarker? p) { _player = p; InvalidateVisual(); }

    /// <summary>Centres the player's dot if it is outside the view.</summary>
    public void RevealPlayer()
    {
        if (_player == null || _def == null) return;
        var s = ToScreen(_def.Projection.ToMap(_player.Position));
        if (s.X < 20 || s.Y < 20 || s.X > ActualWidth - 20 || s.Y > ActualHeight - 20)
        {
            _ox += ActualWidth / 2 - s.X; _oy += ActualHeight / 2 - s.Y;
            InvalidateVisual();
        }
    }

    public void Fit()
    {
        _userMoved = false;
        var r = ContentRect();
        if (r is not { } rect || ActualWidth < 10 || ActualHeight < 10) return;
        _scale = Math.Min(ActualWidth / rect.Width, ActualHeight / rect.Height) * 0.96;
        _ox = (ActualWidth - rect.Width * _scale) / 2 - rect.X * _scale;
        _oy = (ActualHeight - rect.Height * _scale) / 2 - rect.Y * _scale;
        InvalidateVisual();
    }

    /// <summary>Fits a given zoom-0 rectangle (self-test: same view for both styles).</summary>
    public void ShowWhole(MapRect rect)
    {
        _scale = Math.Min(ActualWidth / rect.Width, ActualHeight / rect.Height) * 0.96;
        _ox = (ActualWidth - rect.Width * _scale) / 2 - rect.X * _scale;
        _oy = (ActualHeight - rect.Height * _scale) / 2 - rect.Y * _scale;
        _userMoved = true;
        InvalidateVisual();
    }

    /// <summary>Centres a zoom-0 point at the given scale (screen px per zoom-0 px).</summary>
    public void CenterOn(MapPoint p, double scale)
    {
        _scale = scale;
        _ox = ActualWidth / 2 - p.X * scale;
        _oy = ActualHeight / 2 - p.Y * scale;
        _userMoved = true;
        InvalidateVisual();
    }

    private MapRect? ContentRect()
    {
        if (_image != null) return _image.Placement;
        if (_def != null) return _def.BoundsRect;
        return null;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (!_userMoved) Fit();
        else if (info.PreviousSize.Width > 0)
        {
            // keep the centre where it was
            _ox += (info.NewSize.Width - info.PreviousSize.Width) / 2;
            _oy += (info.NewSize.Height - info.PreviousSize.Height) / 2;
        }
    }

    private Point ToScreen(MapPoint p) => new(p.X * _scale + _ox, p.Y * _scale + _oy);
    private Point ToScreen(Vec3 v) => ToScreen(_def!.Projection.ToMap(v));

    // ---------------- input ----------------
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        double f = Math.Pow(1.0015, e.Delta);
        double ns = Math.Clamp(_scale * f, MinScale(), MaxScale());
        f = ns / _scale;
        _ox = p.X - (p.X - _ox) * f;
        _oy = p.Y - (p.Y - _oy) * f;
        _scale = ns;
        _userMoved = true;
        InvalidateVisual();
        e.Handled = true;
    }

    private double MinScale() => ContentRect() is { } r ? Math.Min(ActualWidth / r.Width, ActualHeight / r.Height) * 0.3 : 0.05;
    private double MaxScale() => ContentRect() is { } r ? Math.Max(ActualWidth / r.Width, ActualHeight / r.Height) * 24 : 100;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2) { Fit(); e.Handled = true; return; }
        _dragFrom = e.GetPosition(this);
        _dragOffset = new Point(_ox, _oy);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        if (_dragFrom is { } from)
        {
            _ox = _dragOffset.X + _mouse.X - from.X;
            _oy = _dragOffset.Y + _mouse.Y - from.Y;
            _userMoved = true;
            InvalidateVisual();
            return;
        }
        var h = HitTestMarker(_mouse);
        if (!Equals(h, _hover) || h != null) { _hover = h; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover != null) { _hover = null; InvalidateVisual(); }
    }

    private object? HitTestMarker(Point m)
    {
        if (_def == null) return null;
        object? best = null; double bestD = 11 * 11;
        void Try(object o, Point s)
        {
            double d = (s.X - m.X) * (s.X - m.X) + (s.Y - m.Y) * (s.Y - m.Y);
            if (d < bestD) { bestD = d; best = o; }
        }
        if (_player != null) Try(_player, ToScreen(_player.Position));
        foreach (var q in _quests) Try(q, ToScreen(q.Spot.Position));
        foreach (var x in _extracts) Try(x, ToScreen(x.Position));
        return best;
    }

    // ---------------- drawing ----------------
    private double Dip => VisualTreeHelper.GetDpi(this).PixelsPerDip;
    private static readonly Typeface Face = new(Theme.Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface FaceRegular = new(Theme.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private FormattedText Text(string s, double size, Brush b, bool bold = true, double maxWidth = 0)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? Face : FaceRegular, size, b, Dip);
        if (maxWidth > 0) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    private static SolidColorBrush B(Color c, byte a = 255) { var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }
    private static readonly Brush Halo = B(Colors.Black, 200);
    private static readonly Pen HaloPen = new Pen(Halo, 3).Frozen();

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Theme.Window, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_def == null) return;
        if (_image != null)
        {
            var p = _image.Placement;
            dc.DrawImage(_image.Bitmap, new Rect(p.X * _scale + _ox, p.Y * _scale + _oy, p.Width * _scale, p.Height * _scale));
        }
        if (ShowLabels && !NoText) DrawLabels(dc);
        foreach (var x in _extracts) DrawExtract(dc, x);
        foreach (var q in _quests) DrawQuest(dc, q);
        if (_player != null) DrawPlayer(dc, _player);
        if (!NoText) DrawAttribution(dc);
        if (_hover != null) DrawHover(dc, _hover);
    }

    private static readonly Brush PillBrush = B(Color.FromRgb(10, 12, 14), 175);
    private static readonly Pen PillEdge = new Pen(B(Colors.White, 40), 1).Frozen();

    /// <summary>Dark translucent rounded "pill" behind a label (the tarkov.dev satellite look).</summary>
    private static void Pill(DrawingContext dc, FormattedText ft, Point textTopLeft, double padX = 7, double padY = 2)
    {
        var r = new Rect(textTopLeft.X - padX, textTopLeft.Y - padY, ft.WidthIncludingTrailingWhitespace + 2 * padX, ft.Height + 2 * padY);
        dc.DrawRoundedRectangle(PillBrush, PillEdge, r, r.Height / 2, r.Height / 2);
    }

    private void DrawAttribution(DrawingContext dc)
    {
        if (_def == null || _image == null) return;
        var ft = Text(_def.Attribution + " · CC BY-NC-SA 4.0", 10, B(Colors.White, 200), bold: false);
        var p = new Point(ActualWidth - ft.Width - 10, ActualHeight - ft.Height - 5);
        Pill(dc, ft, p, 6, 1);
        dc.DrawText(ft, p);
    }

    private void DrawLabels(DrawingContext dc)
    {
        var brush = Satellite ? B(Colors.White) : B(Colors.White, 150);
        foreach (var l in _def!.Labels)
        {
            var s = ToScreen(_def.Projection.ToMap(l.X, l.Z));
            if (s.X < -200 || s.Y < -50 || s.X > ActualWidth + 200 || s.Y > ActualHeight + 50) continue;
            // tarkov.dev label sizes are tuned for its zoom levels; scale gently with zoom, clamped
            double size = Math.Clamp((l.Size > 0 ? l.Size : 100) / 100.0 * 13 * Math.Sqrt(_scale / 2.0), 9, Satellite ? 18 : 24);
            var ft = Text(l.Text, size, brush);
            bool rot = Math.Abs(l.Rotation) > 0.5;
            if (rot) dc.PushTransform(new RotateTransform(l.Rotation, s.X, s.Y));
            var tl = new Point(s.X - ft.Width / 2, s.Y - ft.Height / 2);
            if (Satellite) Pill(dc, ft, tl);
            dc.DrawText(ft, tl);
            if (rot) dc.Pop();
        }
    }

    private void DrawExtract(DrawingContext dc, ExtractMarker x)
    {
        var s = ToScreen(x.Position);
        if (s.X < -150 || s.Y < -40 || s.X > ActualWidth + 150 || s.Y > ActualHeight + 40) return;
        var c = ColorFor(x.Kind);
        bool notListed = _listed != null && x.Kind != ExtractKind.Transit && !_listed.Contains(x.Id);
        byte a = notListed ? (byte)90 : (byte)255;
        var fill = B(c, a);
        // outline polygon (extract zone) when zoomed in enough to be useful
        if (x.Outline.Count >= 3 && _scale > 2 && !notListed) // 1.2.7: no zone box for exits not on your list
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(ToScreen(x.Outline[0]), true, true);
                for (int i = 1; i < x.Outline.Count; i++) ctx.LineTo(ToScreen(x.Outline[i]), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(B(c, 40), new Pen(B(c, 150), 1), g);
        }
        if (x.Kind == ExtractKind.Transit)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(s.X, s.Y - 8), true, true);
                ctx.LineTo(new Point(s.X + 8, s.Y), true, false);
                ctx.LineTo(new Point(s.X, s.Y + 8), true, false);
                ctx.LineTo(new Point(s.X - 8, s.Y), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(fill, new Pen(Brushes.Black, 1.5), g);
        }
        else
        {
            dc.DrawEllipse(fill, new Pen(Brushes.Black, 1.5), s, 7, 7);
            if (x.Kind == ExtractKind.CoOp) dc.DrawEllipse(null, new Pen(B(Colors.White, a), 1.5), s, 3, 3);
            if (x.Notes.Count > 0) dc.DrawEllipse(B(Colors.White, a), null, new Point(s.X + 6, s.Y - 6), 2.5, 2.5); // has requirements
        }
        if (notListed)
        {
            var pen = new Pen(B(Colors.Black, 200), 2);
            dc.DrawLine(pen, new Point(s.X - 5, s.Y - 5), new Point(s.X + 5, s.Y + 5));
            dc.DrawLine(pen, new Point(s.X + 5, s.Y - 5), new Point(s.X - 5, s.Y + 5));
        }
        if (NoText) return;
        var ft = Text(x.Name, 11.5, B(c, a));
        var tp = new Point(s.X + 12, s.Y - ft.Height / 2);
        if (Satellite) Pill(dc, ft, tp, 5, 1);
        else dc.DrawGeometry(null, HaloPen, ft.BuildGeometry(tp));
        dc.DrawText(ft, tp);
    }

    private void DrawQuest(DrawingContext dc, QuestMarker q)
    {
        var s = ToScreen(q.Spot.Position);
        if (s.X < -40 || s.Y < -40 || s.X > ActualWidth + 40 || s.Y > ActualHeight + 40) return;
        if (q.Spot.Outline.Count >= 3)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(ToScreen(q.Spot.Outline[0]), true, true);
                for (int i = 1; i < q.Spot.Outline.Count; i++) ctx.LineTo(ToScreen(q.Spot.Outline[i]), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(B(q.Color, 45), new Pen(B(q.Color, 170), 1.2), g);
        }
        // square marker; hollow = one of several possible spawn spots; small = optional
        double r = q.Optional ? 4.5 : 6;
        var rect = new Rect(s.X - r, s.Y - r, 2 * r, 2 * r);
        dc.PushTransform(new RotateTransform(45, s.X, s.Y));
        if (q.Spot.PossibleSpawn) dc.DrawRectangle(B(Colors.Black, 160), new Pen(B(q.Color), 2.2), rect);
        else dc.DrawRectangle(B(q.Color), new Pen(Brushes.Black, 1.5), rect);
        dc.Pop();
        if (!q.Active) dc.DrawEllipse(null, new Pen(B(Colors.White, 120), 1), s, r + 4, r + 4);
    }

    private static readonly Pen PlayerRingOuter = new Pen(B(Colors.Black, 230), 6).Frozen();
    private static readonly Pen PlayerRingInner = new Pen(Brushes.White, 3).Frozen();

    private void DrawPlayer(DrawingContext dc, PlayerMarker p)
    {
        var s = ToScreen(p.Position);
        double heading = _def!.Projection.ScreenHeading(p.Position.X, p.Position.Z, p.Yaw);
        var view = new Rect(0, 0, ActualWidth, ActualHeight);
        if (!view.Contains(s) && ActualWidth > 40 && ActualHeight > 40)
        {
            // off-screen: arrow at the edge pointing to it
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            var dx = s.X - c.X; var dy = s.Y - c.Y;
            var k = Math.Min((ActualWidth / 2 - 18) / Math.Max(Math.Abs(dx), 1e-6), (ActualHeight / 2 - 18) / Math.Max(Math.Abs(dy), 1e-6));
            var e = new Point(c.X + dx * k, c.Y + dy * k);
            var ang = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(e.X, e.Y - 14), true, true);
                ctx.LineTo(new Point(e.X + 10, e.Y + 8), true, false);
                ctx.LineTo(new Point(e.X - 10, e.Y + 8), true, false);
            }
            g.Freeze();
            dc.PushTransform(new RotateTransform(ang, e.X, e.Y));
            dc.DrawGeometry(B(PlayerColor), new Pen(Brushes.White, 2), g);
            dc.Pop();
            return;
        }
        // big, high-contrast: black+white ring, heading arrow, filled dot
        dc.DrawEllipse(null, PlayerRingOuter, s, 15, 15);
        dc.DrawEllipse(null, PlayerRingInner, s, 15, 15);
        var arrow = new StreamGeometry();
        using (var ctx = arrow.Open())
        {
            ctx.BeginFigure(new Point(s.X, s.Y - 30), true, true);
            ctx.LineTo(new Point(s.X + 10, s.Y - 12), true, false);
            ctx.LineTo(new Point(s.X - 10, s.Y - 12), true, false);
        }
        arrow.Freeze();
        dc.PushTransform(new RotateTransform(heading, s.X, s.Y));
        dc.DrawGeometry(B(PlayerColor), new Pen(Brushes.Black, 2), arrow);
        dc.Pop();
        dc.DrawEllipse(B(PlayerColor), new Pen(Brushes.White, 2.5), s, 9, 9);
        if (NoText) return;
        var age = DateTimeOffset.Now - p.At;
        var ft = Text("You · " + MapPanel.Ago(age), 11.5, Brushes.White, bold: true);
        var tp = new Point(s.X - ft.Width / 2, s.Y + 18);
        if (Satellite) Pill(dc, ft, tp, 6, 1);
        else dc.DrawGeometry(null, HaloPen, ft.BuildGeometry(tp));
        dc.DrawText(ft, tp);
    }

    private void DrawHover(DrawingContext dc, object h)
    {
        if (NoText) return;
        string title, body;
        Color c;
        switch (h)
        {
            case ExtractMarker x:
                title = x.Name; c = ColorFor(x.Kind);
                var lines = new List<string> { x.KindLabel + (x.Secret ? " · secret exit" : "") };
                if (_listed != null && x.Kind != ExtractKind.Transit) lines.Add(_listed.Contains(x.Id) ? "On your exit list this raid" : "Not on your exit list this raid");
                lines.AddRange(x.Notes);
                body = string.Join("\n", lines);
                break;
            case QuestMarker q:
                title = q.TaskName; c = q.Color;
                body = $"{q.Trader}{(q.Active ? " · active" : " · available")}{(q.Optional ? " · optional" : "")}\n{q.Objective}" +
                       (q.Spot.PossibleSpawn ? "\n(one of several possible spots)" : "");
                break;
            case PlayerMarker p:
                title = "You"; c = PlayerColor;
                body = FormattableString.Invariant($"From your last in-raid screenshot, {MapPanel.Ago(DateTimeOffset.Now - p.At)}\nPosition {p.Position.X:0.#}, {p.Position.Y:0.#}, {p.Position.Z:0.#} · facing {p.Yaw:0}°\nPress PrtScn in raid to update.");
                break;
            default: return;
        }
        var tt = Text(title, 12.5, B(c));
        var tb = Text(body, 11.5, Theme.Text, bold: false, maxWidth: 300);
        double w = Math.Max(tt.Width, tb.Width) + 16, hgt = tt.Height + tb.Height + 14;
        double x0 = _mouse.X + 16, y0 = _mouse.Y + 12;
        if (x0 + w > ActualWidth - 4) x0 = Math.Max(4, _mouse.X - w - 12);
        if (y0 + hgt > ActualHeight - 4) y0 = Math.Max(4, ActualHeight - hgt - 4);
        dc.DrawRoundedRectangle(Theme.Card, new Pen(Theme.CardEdge, 1), new Rect(x0, y0, w, hgt), 5, 5);
        dc.DrawText(tt, new Point(x0 + 8, y0 + 6));
        dc.DrawText(tb, new Point(x0 + 8, y0 + 8 + tt.Height));
    }
}

internal static class FreezeExt
{
    public static T Frozen<T>(this T f) where T : Freezable { f.Freeze(); return f; }
}
