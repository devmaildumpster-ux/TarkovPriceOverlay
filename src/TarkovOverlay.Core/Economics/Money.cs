using System.Globalization;

namespace TarkovOverlay.Core.Economics;

public static class Money
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Format(long amount, string currency = "RUB") => currency.ToUpperInvariant() switch
    {
        "USD" => "$" + amount.ToString("N0", Inv),
        "EUR" => "€" + amount.ToString("N0", Inv),
        _ => "₽" + amount.ToString("N0", Inv),
    };

    public static string Rub(long amount) => Format(amount, "RUB");

    /// <summary>"$24 (≈₽3,963)" for foreign currency, plain roubles otherwise.</summary>
    public static string WithRub(long price, string currency, long priceRub) =>
        currency.Equals("RUB", StringComparison.OrdinalIgnoreCase) ? Rub(priceRub) : $"{Format(price, currency)} (≈{Rub(priceRub)})";

    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero) return "in the future";
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalHours < 48) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} d ago";
    }
}
