using System.Text;

namespace TarkovOverlay.Core.Detection;

/// <summary>Minimal binary PPM (P6) reader/writer for saved crops and test fixtures.</summary>
public static class Ppm
{
    public static RgbImage Read(Stream s)
    {
        string Token()
        {
            var sb = new StringBuilder();
            int c;
            while ((c = s.ReadByte()) != -1)
            {
                if (c == '#') { while ((c = s.ReadByte()) != -1 && c != '\n') { } continue; }
                if (char.IsWhiteSpace((char)c)) { if (sb.Length > 0) break; continue; }
                sb.Append((char)c);
            }
            return sb.ToString();
        }
        if (Token() != "P6") throw new InvalidDataException("not a binary PPM (P6)");
        int w = int.Parse(Token()), h = int.Parse(Token()), max = int.Parse(Token());
        if (w <= 0 || h <= 0 || w > 10000 || h > 10000 || max != 255) throw new InvalidDataException("unsupported PPM");
        var rgb = new byte[w * h * 3];
        int read = 0;
        while (read < rgb.Length) { int n = s.Read(rgb, read, rgb.Length - read); if (n <= 0) throw new EndOfStreamException(); read += n; }
        var img = new RgbImage(w, h);
        for (int i = 0, o = 0; i < rgb.Length; i += 3, o += 4)
        { img.Bgra[o] = rgb[i + 2]; img.Bgra[o + 1] = rgb[i + 1]; img.Bgra[o + 2] = rgb[i]; img.Bgra[o + 3] = 255; }
        return img;
    }

    public static void Write(RgbImage img, Stream s)
    {
        var header = Encoding.ASCII.GetBytes($"P6\n{img.Width} {img.Height}\n255\n");
        s.Write(header);
        var rgb = new byte[img.Width * img.Height * 3];
        for (int i = 0, o = 0; i < rgb.Length; i += 3, o += 4) { rgb[i] = img.Bgra[o + 2]; rgb[i + 1] = img.Bgra[o + 1]; rgb[i + 2] = img.Bgra[o]; }
        s.Write(rgb);
    }
}
