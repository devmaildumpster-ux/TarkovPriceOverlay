using TarkovOverlay.Core;

namespace TarkovOverlay.Maps;

/// <summary>Map feature folders under %LOCALAPPDATA%\TarkovPriceOverlay (same root as the price cache).</summary>
public sealed class MapPaths
{
    public string Root { get; }
    public MapPaths(AppPaths app) : this(Path.Combine(app.Root, "maps")) { }
    public MapPaths(string root) { Root = root; }

    public string DefinitionsFile => Path.Combine(Root, "maps.json");
    public string FeedDir(string modeId) => Path.Combine(Root, "feed", Sanitize(modeId));
    public string MapFeedFile(string modeId) => Path.Combine(FeedDir(modeId), "mapdata.json");
    public string TaskFeedFile(string modeId) => Path.Combine(FeedDir(modeId), "tasks.json");
    public string SourceDir => Path.Combine(Root, "source");
    public string ImageDir => Path.Combine(Root, "images");
    public string TileDir(string mapKey, string layer) => Path.Combine(Root, "tiles", Sanitize(mapKey), Sanitize(layer));
    public string ImageFile(string mapKey, string layer, string style) => Path.Combine(ImageDir, $"{Sanitize(mapKey)}.{Sanitize(layer)}.{Sanitize(style)}.png");
    public string SettingsFile => Path.Combine(Root, "map-settings.json");
    public string ProgressFile => Path.Combine(Root, "quest-progress.json");
    public string LastPositionFile => Path.Combine(Root, "last-position.json");

    public static string Sanitize(string s)
    {
        var t = string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        return t.Length == 0 ? "_" : t;
    }
}
