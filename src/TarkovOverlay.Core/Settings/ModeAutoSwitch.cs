using TarkovOverlay.Core.Catalog;

namespace TarkovOverlay.Core.Settings;

/// <summary>
/// 1.2.0: decides whether Tarkov's log ("Session mode: pve" / "regular" / "pvpseason") should switch the app's
/// PvP/PvE mode. The log line is written when the game connects in that mode, so a raid always runs in the logged
/// mode; switching to it can't disagree with a raid in progress.
/// </summary>
public static class ModeAutoSwitch
{
    public sealed record Decision(string? SwitchTo, string Reason);

    public static Decision Decide(bool enabled, string appModeId, string? loggedModeId)
    {
        if (!enabled) return new(null, "auto-detect is off");
        if (string.IsNullOrWhiteSpace(loggedModeId)) return new(null, "no session mode in the log yet");
        if (!GameMode.IsKnown(loggedModeId)) return new(null, $"unknown session mode '{loggedModeId}'");
        if (string.Equals(appModeId, loggedModeId, StringComparison.OrdinalIgnoreCase)) return new(null, "already " + loggedModeId);
        return new(GameMode.FromId(loggedModeId).Id, $"Tarkov log says {GameMode.FromId(loggedModeId).DisplayName}");
    }
}
