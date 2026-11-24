using TarkovOverlay.Core.Geometry;

namespace TarkovOverlay.Core.Detection;

/// <summary>A cleaned white-on-black crop of short-name lettering on the hovered inventory icon.</summary>
public sealed record IconNameBand(string Name, PixelRect Bounds, RgbImage Image);

/// <summary>Where the hovered cell is and the icon-label bands that still have visible lettering.</summary>
public sealed record IconNameLocateResult(bool Ok, PixelRect? Cell, int Pitch, IReadOnlyList<IconNameBand> Bands, string Note)
{
    public static IconNameLocateResult Fail(string note, PixelRect? cell = null, int pitch = 0) =>
        new(false, cell, pitch, Array.Empty<IconNameBand>(), note);
}

/// <summary>
/// 1.3.1: finds the inventory cell under the cursor (same grid comb as the stack-count reader) and
/// isolates the bright short-name lettering Tarkov paints on the icon — usually a strip along the top
/// or the bottom of the cell. Pure pixel code (testable on any OS). The name tooltip is masked so its
/// own title is never read as the icon label.
/// </summary>
public static class IconShortNameLocator
{
    public static IconNameLocateResult Locate(RgbImage img, int cursorX, int cursorY, PixelRect? tooltip, int nominalPitch)
    {
        var loc = StackCountLocator.Locate(img, cursorX, cursorY, tooltip, nominalPitch);
        if (loc.Cell is not { } cell || loc.Pitch <= 0)
            return IconNameLocateResult.Fail(string.IsNullOrEmpty(loc.Note) ? "inventory grid not found" : loc.Note);
        int p = loc.Pitch;
        int inset = Math.Max(2, p / 32);
        var mask = tooltip?.Inflate(Math.Max(3, p / 16));
        var bands = new List<IconNameBand>();
        TryAdd(bands, "top", new PixelRect(cell.X + inset, cell.Y + inset, cell.Width - 2 * inset, (int)(p * 0.36)), img, mask, p);
        TryAdd(bands, "bottom", new PixelRect(cell.X + inset, cell.Bottom - (int)(p * 0.44) - inset, cell.Width - 2 * inset, (int)(p * 0.44)), img, mask, p);
        if (bands.Count == 0)
            return IconNameLocateResult.Fail(mask is { } m && !m.Intersect(cell).IsEmpty
                ? "icon short name hidden by the name tooltip"
                : "no short-name lettering in the cell", cell, p);
        return new(true, cell, p, bands, string.Join(", ", bands.Select(b => b.Name)));
    }

    private static void TryAdd(List<IconNameBand> bands, string name, PixelRect area, RgbImage img, PixelRect? mask, int pitch)
    {
        area = area.Intersect(new PixelRect(0, 0, img.Width, img.Height));
        if (area.IsEmpty || area.Width < 8 || area.Height < 6) return;
        if (mask is { } m)
        {
            var hit = m.Intersect(area);
            if (!hit.IsEmpty && hit.Width * hit.Height >= 0.7 * area.Width * area.Height) return;
        }

        int n = 0;
        var bright = new bool[area.Width * area.Height];
        for (int y = 0; y < area.Height; y++)
            for (int x = 0; x < area.Width; x++)
            {
                if (mask is { } mk && mk.Contains(area.X + x, area.Y + y)) continue;
                var (r, g, b) = img.Get(area.X + x, area.Y + y);
                int mn = Math.Min(r, Math.Min(g, b)), mx = Math.Max(r, Math.Max(g, b));
                if (mn >= 165 && mx - mn <= 50)
                {
                    bright[y * area.Width + x] = true;
                    n++;
                }
            }
        // A short label is a run of glyphs, not a few specular pixels on the art.
        int minPx = Math.Max(18, (pitch * pitch) / 90);
        if (n < minPx) return;

        int pad = Math.Max(4, area.Height / 6);
        var outImg = new RgbImage(area.Width + 2 * pad, area.Height + 2 * pad);
        outImg.Fill(new PixelRect(0, 0, outImg.Width, outImg.Height), 10, 10, 10);
        for (int y = 0; y < area.Height; y++)
            for (int x = 0; x < area.Width; x++)
                if (bright[y * area.Width + x])
                    outImg.Set(x + pad, y + pad, 240, 240, 240);
        bands.Add(new IconNameBand(name, area, outImg));
    }
}
