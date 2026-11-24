namespace TarkovOverlay.Core.Detection;

/// <summary>Tightly packed 32-bit BGRA pixels (the layout BitBlt/DIB sections and WPF Bgra32 use).</summary>
public sealed class RgbImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public int Stride => Width * 4;

    public RgbImage(int width, int height, byte[]? bgra = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height;
        Bgra = bgra ?? new byte[width * height * 4];
        if (Bgra.Length < width * height * 4) throw new ArgumentException("pixel buffer too small");
    }

    public (byte R, byte G, byte B) Get(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Bgra[i + 2], Bgra[i + 1], Bgra[i]);
    }

    public void Set(int x, int y, byte r, byte g, byte b)
    {
        int i = (y * Width + x) * 4;
        Bgra[i] = b; Bgra[i + 1] = g; Bgra[i + 2] = r; Bgra[i + 3] = 255;
    }

    public void Fill(PixelRect r, byte red, byte g, byte b)
    {
        for (int y = Math.Max(0, r.Y); y < Math.Min(Height, r.Bottom); y++)
            for (int x = Math.Max(0, r.X); x < Math.Min(Width, r.Right); x++) Set(x, y, red, g, b);
    }

    public RgbImage Crop(PixelRect r)
    {
        r = r.Intersect(new PixelRect(0, 0, Width, Height));
        if (r.Width <= 0 || r.Height <= 0) throw new ArgumentException("empty crop");
        var dst = new RgbImage(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(Bgra, ((r.Y + y) * Width + r.X) * 4, dst.Bgra, y * dst.Stride, r.Width * 4);
        return dst;
    }

    /// <summary>Bilinear resize (used for OCR scale passes).</summary>
    public RgbImage Resize(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var dst = new RgbImage(w, h);
        double sx = (double)Width / w, sy = (double)Height / h;
        for (int y = 0; y < h; y++)
        {
            double fy = Math.Max(0, (y + 0.5) * sy - 0.5);
            int y0 = Math.Min((int)fy, Height - 1), y1 = Math.Min(y0 + 1, Height - 1);
            double wy = fy - y0;
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Max(0, (x + 0.5) * sx - 0.5);
                int x0 = Math.Min((int)fx, Width - 1), x1 = Math.Min(x0 + 1, Width - 1);
                double wx = fx - x0;
                int di = (y * w + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double v = (1 - wy) * ((1 - wx) * Bgra[(y0 * Width + x0) * 4 + c] + wx * Bgra[(y0 * Width + x1) * 4 + c])
                             + wy * ((1 - wx) * Bgra[(y1 * Width + x0) * 4 + c] + wx * Bgra[(y1 * Width + x1) * 4 + c]);
                    dst.Bgra[di + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
                dst.Bgra[di + 3] = 255;
            }
        }
        return dst;
    }

    public RgbImage Scale(double factor) => Resize((int)Math.Round(Width * factor), (int)Math.Round(Height * factor));

    /// <summary>
    /// Tesseract preparation: grayscale, light lettering inverted to dark-on-light, contrast stretched,
    /// with a light margin so glyphs do not touch the image border.
    /// </summary>
    public byte[] ToInvertedGray(out int w, out int h, int margin = 10)
    {
        w = Width + margin * 2; h = Height + margin * 2;
        var g = new byte[w * h];
        Array.Fill(g, (byte)255);
        int min = 255, max = 0;
        var lum = new byte[Width * Height];
        for (int i = 0, p = 0; i < lum.Length; i++, p += 4)
        {
            int l = (Bgra[p + 2] * 299 + Bgra[p + 1] * 587 + Bgra[p] * 114) / 1000;
            lum[i] = (byte)l; if (l < min) min = l; if (l > max) max = l;
        }
        int range = Math.Max(1, max - min);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int l = (lum[y * Width + x] - min) * 255 / range;
                g[(y + margin) * w + x + margin] = (byte)(255 - l);
            }
        return g;
    }
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelRect Intersect(PixelRect o)
    {
        int x = Math.Max(X, o.X), y = Math.Max(Y, o.Y);
        int r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r > x && b > y ? new PixelRect(x, y, r - x, b - y) : new PixelRect(x, y, 0, 0);
    }

    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;
    public PixelRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);
    public PixelRect Inflate(int d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
    public override string ToString() => $"({X},{Y} {Width}x{Height})";
}
