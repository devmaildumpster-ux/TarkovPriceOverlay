namespace TarkovOverlay.Core.Ammo;

/// <summary>
/// 1.3.0: ballistic stats from tarkov.dev (<c>ItemPropertiesAmmo</c>). Nulls mean the feed didn't send that field
/// (kept distinct from zero). <see cref="ArmorDamage"/> is already a percent (37 = 37%);
/// <see cref="FragmentationChance"/> is a 0–1 fraction.
/// </summary>
public sealed record AmmoStats(
    string Caliber,
    int? Damage,
    int? Penetration,
    int? ArmorDamage,
    double? FragmentationChance,
    int? Speed,
    bool? Tracer,
    string? TracerColor,
    string? AmmoType,
    int? ProjectileCount)
{
    public int Pellets => ProjectileCount is > 1 ? ProjectileCount.Value : 1;
    public bool IsMultiProjectile => Pellets > 1;
    public int? TotalDamage => Damage is { } d ? d * Pellets : null;
    public bool IsTracer => Tracer == true;
}

/// <summary>
/// How well a round's penetration compares to armor classes 1–6. This is the widely used community scale
/// (compare penetration to 10 × class), not a simulation of in-raid rolls: it is for telling rounds apart at a glance.
/// 4 = usually pens, 3 = often, 2 = 50/50, 1 = rarely, 0 = almost never.
/// </summary>
public static class ArmorEffect
{
    public const int Classes = 6, MaxLevel = 4;

    public static int Level(int? penetration, int armorClass)
    {
        if (penetration is not { } p || armorClass is < 1 or > Classes) return 0;
        int delta = p - armorClass * 10;
        if (delta >= 10) return 4;
        if (delta >= 0) return 3;
        if (delta >= -10) return 2;
        if (delta >= -20) return 1;
        return 0;
    }

    /// <summary>Six levels, class 1 through 6.</summary>
    public static int[] For(int? penetration)
    {
        var a = new int[Classes];
        for (int c = 1; c <= Classes; c++) a[c - 1] = Level(penetration, c);
        return a;
    }

    public static string Word(int level) => level switch
    {
        4 => "usually pens",
        3 => "often pens",
        2 => "50/50",
        1 => "rarely pens",
        _ => "almost never",
    };
}

/// <summary>Pretty names for tarkov.dev caliber ids ("Caliber556x45NATO" → "5.56x45 NATO").</summary>
public static class AmmoCaliber
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Caliber12g"] = "12/70",
        ["Caliber20g"] = "20/70",
        ["Caliber20x1mm"] = "20x1mm",
        ["Caliber23x75"] = "23x75",
        ["Caliber26x75"] = "26x75",
        ["Caliber40x46"] = "40x46",
        ["Caliber40mmRU"] = "40mm RU",
        ["Caliber46x30"] = "4.6x30",
        ["Caliber57x28"] = "5.7x28",
        ["Caliber545x39"] = "5.45x39",
        ["Caliber556x45NATO"] = "5.56x45 NATO",
        ["Caliber58x42"] = "5.8x42",
        ["Caliber68x51"] = "6.8x51",
        ["Caliber762x25TT"] = "7.62x25 TT",
        ["Caliber762x35"] = ".300 Blackout",
        ["Caliber762x39"] = "7.62x39",
        ["Caliber762x51"] = "7.62x51",
        ["Caliber762x54R"] = "7.62x54R",
        ["Caliber784x49"] = "7.84x49",
        ["Caliber86x70"] = ".338 Lapua",
        ["Caliber9x18PM"] = "9x18 PM",
        ["Caliber9x19PARA"] = "9x19",
        ["Caliber9x21"] = "9x21",
        ["Caliber9x33R"] = ".357 Magnum",
        ["Caliber9x39"] = "9x39",
        ["Caliber93x64"] = "9.3x64",
        ["Caliber1143x23ACP"] = ".45 ACP",
        ["Caliber127x33"] = ".50 AE",
        ["Caliber127x55"] = "12.7x55",
        ["Caliber127x99"] = "12.7x99",
        ["Caliber366TKM"] = ".366 TKM",
    };

    public static string Display(string? caliber)
    {
        if (string.IsNullOrWhiteSpace(caliber)) return "Unknown";
        if (Names.TryGetValue(caliber, out var n)) return n;
        var s = caliber.StartsWith("Caliber", StringComparison.OrdinalIgnoreCase) ? caliber["Caliber".Length..] : caliber;
        return s.Length > 0 ? s : caliber;
    }
}
