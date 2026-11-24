using System.Globalization;
using System.Text.RegularExpressions;
using TarkovOverlay.Maps.Geometry;

namespace TarkovOverlay.Maps.Position;

/// <summary>
/// EFT names in-raid screenshots with the player's position and rotation quaternion, e.g.
/// "2024-01-01[12-00]_123.45, 6.78, -90.12_0.00000, 0.70711, 0.00000, 0.70711_12.34 (0).png".
/// (Menu screenshots have no coordinates and are ignored.)
/// </summary>
public static class ScreenshotName
{
    private const string Num = @"-?\d+(?:[.,]\d+)?(?:[eE][-+]?\d+)?";
    private const string Sep = @"\s*,\s*";
    // Tolerant: optional "_" after the date and between position and rotation (both have been seen missing),
    // any spaces around the commas, optional in-game time and "(n)" counter, png/jpg/bmp.
    private static readonly Regex Rx = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})\s*\[(?<h>\d{2})-(?<m>\d{2})(?:-\d{2})?\]\s*_?\s*" +
        $@"(?<x>{Num}){Sep}(?<y>{Num}){Sep}(?<z>{Num})\s*_?\s*" +
        $@"(?<qx>{Num}){Sep}(?<qy>{Num}){Sep}(?<qz>{Num}){Sep}(?<qw>{Num})" +
        @"(?:\s*_[^ (]*)?(?:\s*\(\d+\))?\.(?:png|jpg|jpeg|bmp)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DatePrefix = new(@"^\d{4}-\d{2}-\d{2}\s*\[\d{2}-\d{2}", RegexOptions.Compiled);

    /// <summary>Why a file name gave no position (for the log and the "Test position" report).</summary>
    public static string Explain(string fileName)
    {
        var name = fileName[(fileName.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        if (TryParse(name) != null) return "ok";
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp")) return $"not an image ({ext})";
        if (!DatePrefix.IsMatch(name)) return "not a Tarkov screenshot name";
        if (name.Count(c => c == ',') < 5) return "no position in the name (taken in the menu/hideout, or not in a raid)";
        return "unrecognised position format";
    }

    public sealed record Parsed(Vec3 Position, double Qx, double Qy, double Qz, double Qw, double YawDegrees, DateTime? TakenLocal);

    public static Parsed? TryParse(string fileName)
    {
        var name = fileName[(fileName.LastIndexOfAny(new[] { '\\', '/' }) + 1)..]; // Windows or Unix separators
        var m = Rx.Match(name);
        if (!m.Success) return null;
        double D(string g) => double.Parse(m.Groups[g].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        double x = D("x"), y = D("y"), z = D("z"), qx = D("qx"), qy = D("qy"), qz = D("qz"), qw = D("qw");
        if (new[] { x, y, z, qx, qy, qz, qw }.Any(v => !double.IsFinite(v))) return null;
        DateTime? taken = DateTime.TryParseExact($"{m.Groups["date"].Value} {m.Groups["h"].Value}:{m.Groups["m"].Value}", "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : null;
        return new Parsed(new Vec3(x, y, z), qx, qy, qz, qw, Rotation.YawFromQuaternion(qx, qy, qz, qw), taken);
    }
}
