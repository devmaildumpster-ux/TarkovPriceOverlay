namespace TarkovOverlay.Core.Catalog;

/// <summary>Tarkov.dev game modes. The ID is used verbatim in every mode-dependent URL.</summary>
public sealed record GameMode(string Id, string DisplayName, string ShortCue)
{
    public static readonly GameMode Regular = new("regular", "Regular PvP", "PvP");
    public static readonly GameMode Pve = new("pve", "PvE", "PvE");
    public static readonly GameMode Seasonal = new("pvp-season", "Seasonal PvP", "Season");

    public static IReadOnlyList<GameMode> All { get; } = new[] { Regular, Pve, Seasonal };

    public static GameMode FromId(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Regular;

    public static bool IsKnown(string? id) => All.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => DisplayName;
}
