using System.Windows.Media;
using System.Windows.Media.Imaging;
using TarkovOverlay.Core.Detection;

namespace TarkovOverlay.App.Services;

public static class ImageIo
{
    public const long MaxFileBytes = 40L * 1024 * 1024;
    public const int MaxPixels = 8192 * 8192;

    /// <summary>Loads PNG/JPEG/BMP/TIFF (or our .ppm fixtures) into a BGRA buffer with size limits.</summary>
    public static RgbImage Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Image not found.", path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException($"Image is larger than {MaxFileBytes / (1024 * 1024)} MB.");
        if (path.EndsWith(".ppm", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(path);
            return Ppm.Read(fs);
        }
        using var s = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels) throw new InvalidDataException("Image dimensions are too large.");
        if (frame.Format != PixelFormats.Bgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var img = new RgbImage(frame.PixelWidth, frame.PixelHeight);
        frame.CopyPixels(img.Bgra, img.Stride, 0);
        return img;
    }

    public static BitmapSource ToBitmap(RgbImage img)
    {
        var b = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.Stride);
        b.Freeze();
        return b;
    }

    public static void SavePng(RgbImage img, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(ToBitmap(img)));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
