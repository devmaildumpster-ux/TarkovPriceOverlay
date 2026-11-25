namespace TarkovOverlay.Core.Detection;

/// <summary>
/// Isolates bright or colored HUD lettering (white, green active timers, orange transits) on dark semi-transparent
/// bars so OCR does not lose top rows when bright sky or scenery sits behind/beside the exit list.
/// </summary>
public static class HudTextIsolator
{
    public static PixelRect ExitListRegion(PixelRect game) =>
        new(game.X + (int)(game.Width * 0.62), game.Y, (int)(game.Width * 0.38), (int)(game.Height * 0.45));

    public static double ExitListScaleFor(int cropHeight) => cropHeight < 700 ? 2.0 : 1.5;

    /// <summary>
    /// 1D horizontal morphological white top-hat over max(R,G,B): erases narrow glyph strokes while preserving both
    /// uniform sky and wide horizontal sky gaps between dark rows, then subtracts the opened background.
    /// </summary>
    public static RgbImage IsolateBrightHudText(RgbImage src, int radius = 6, int noiseFloor = 24)
    {
        int w = src.Width, h = src.Height;
        var v = new byte[w * h];
        for (int i = 0, p = 0; i < v.Length; i++, p += 4)
            v[i] = Math.Max(src.Bgra[p + 2], Math.Max(src.Bgra[p + 1], src.Bgra[p]));

        var er = new byte[w];
        var diff = new byte[w * h];
        int maxDiff = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                byte m = 255;
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                for (int k = x0; k <= x1; k++)
                    if (v[row + k] < m) m = v[row + k];
                er[x] = m;
            }
            for (int x = 0; x < w; x++)
            {
                byte m = 0;
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                for (int k = x0; k <= x1; k++)
                    if (er[k] > m) m = er[k];
                int d = v[row + x] - m;
                byte dv = d > noiseFloor ? (byte)d : (byte)0;
                diff[row + x] = dv;
                if (dv > maxDiff) maxDiff = dv;
            }
        }

        int denom = Math.Max(40, maxDiff);
        var bgra = new byte[w * h * 4];
        for (int i = 0, p = 0; i < diff.Length; i++, p += 4)
        {
            byte val = (byte)Math.Clamp(diff[i] * 255 / denom, 0, 255);
            bgra[p] = bgra[p + 1] = bgra[p + 2] = val;
            bgra[p + 3] = 255;
        }
        return new RgbImage(w, h, bgra);
    }
}
