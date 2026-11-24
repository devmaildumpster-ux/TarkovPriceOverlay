using TarkovOverlay.Core.Detection;

namespace TarkovOverlay.Tests.Fixtures;

/// <summary>
/// FIXTURE KIND: synthetic inventory-like pixel fixtures. Generated stash grid (gray cells, grid lines,
/// colored item icons, white item abbreviations and stack counts, some near-black empty cells) plus an
/// optional Tarkov-style tooltip: near-black interior, thin gray frame, white title text. Glyphs are
/// deterministic stroke patterns (not a real font) — enough for detection, not for OCR.
/// </summary>
public sealed class SyntheticScene
{
    public RgbImage Image { get; }
    public double Scale { get; }
    private readonly Random _rng;

    public SyntheticScene(int width, int height, double scale = 1.0, int seed = 7, bool inventory = true)
    {
        Image = new RgbImage(width, height);
        Scale = scale;
        _rng = new Random(seed);
        if (inventory) DrawInventory(); else Image.Fill(new PixelRect(0, 0, width, height), 34, 36, 35);
    }

    private int S(double v) => Math.Max(1, (int)Math.Round(v * Scale, MidpointRounding.AwayFromZero));

    private void DrawInventory()
    {
        int w = Image.Width, h = Image.Height, cell = S(64);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)(30 + _rng.Next(10));
                Image.Set(x, y, v, (byte)(v + 1), v);
            }
        for (int cy = 0; cy * cell < h; cy++)
            for (int cx = 0; cx * cell < w; cx++)
            {
                var r = new PixelRect(cx * cell + 1, cy * cell + 1, cell - 1, cell - 1);
                int kind = _rng.Next(6);
                if (kind == 0)
                {
                    // near-black empty cell with a grid frame: must not be taken for a tooltip
                    Image.Fill(r, 12, 12, 12);
                }
                else if (kind <= 3)
                {
                    // item icon + abbreviation at the top-right and a stack count bottom-right
                    var icon = new PixelRect(r.X + S(10), r.Y + S(14), r.Width - S(20), r.Height - S(26));
                    Image.Fill(icon, (byte)(60 + _rng.Next(80)), (byte)(60 + _rng.Next(80)), (byte)(50 + _rng.Next(60)));
                    DrawText(r.Right - S(3) - TextWidth("AB" + kind, 1), r.Y + S(2), "AB" + kind, 1, 230);
                    if (kind == 3) DrawText(r.Right - S(3) - TextWidth("60", 1), r.Bottom - S(10), "60", 1, 220);
                }
            }
        for (int x = 0; x < w; x += cell) for (int y = 0; y < h; y++) Image.Set(x, y, 62, 64, 62);
        for (int y = 0; y < h; y += cell) for (int x = 0; x < w; x++) Image.Set(x, y, 62, 64, 62);
    }

    /// <summary>Glyph cell is 3×7 units with a 1-unit advance gap; unit = glyphScale × DPI scale pixels.</summary>
    public int TextWidth(string text, int glyphScale) => text.Length * 4 * S(glyphScale) - S(glyphScale);
    public int TextHeight(int glyphScale) => 7 * S(glyphScale);

    public void DrawText(int x, int y, string text, int glyphScale, byte lum)
    {
        int u = S(glyphScale);
        foreach (var ch in text)
        {
            if (ch != ' ')
            {
                int code = ch * 2654435761 is var hsh ? (int)(hsh >> 7) : 0;
                for (int gy = 0; gy < 7; gy++)
                    for (int gx = 0; gx < 3; gx++)
                    {
                        bool on = gx == 0 || gy == 6 || ((code >> ((gy * 3 + gx) % 29)) & 1) == 1;
                        if (on)
                            for (int py = 0; py < u; py++)
                                for (int px = 0; px < u; px++)
                                {
                                    int X = x + gx * u + px, Y = y + gy * u + py;
                                    if (X >= 0 && Y >= 0 && X < Image.Width && Y < Image.Height) Image.Set(X, Y, lum, lum, lum);
                                }
                    }
            }
            x += 4 * u;
        }
    }

    /// <summary>Draws a tooltip whose interior's top-left is at (x, y). Returns the interior rectangle.</summary>
    public PixelRect DrawTooltip(int x, int y, params string[] lines)
    {
        int padX = S(7), padY = S(6), gap = S(4), border = Math.Max(1, (int)Math.Round(Scale));
        int textW = lines.Max(l => TextWidth(l, 2));
        int lineH = TextHeight(2);
        var interior = new PixelRect(x, y, textW + 2 * padX, lines.Length * lineH + (lines.Length - 1) * gap + 2 * padY);
        // frame (plus an anti-aliased transition at fractional DPI)
        Image.Fill(interior.Inflate(border), 118, 120, 118);
        if (Math.Abs(Scale - Math.Round(Scale)) > 0.01)
        {
            // fractional DPI: a 1 px blended ring between the frame and the interior
            Image.Fill(interior, 54, 55, 54);
            interior = interior.Inflate(-1);
        }
        Image.Fill(interior, 9, 10, 10);
        for (int i = 0; i < lines.Length; i++)
            DrawText(x + padX, y + padY + i * (lineH + gap), lines[i], 2, 236);
        return interior;
    }

    /// <summary>Uniform black columns at the left edge (monitor margin / letterbox).</summary>
    public void BlackMargin(int columns, bool left = true)
    {
        for (int y = 0; y < Image.Height; y++)
            for (int k = 0; k < columns; k++)
                Image.Set(left ? k : Image.Width - 1 - k, y, 0, 0, 0);
    }
}
