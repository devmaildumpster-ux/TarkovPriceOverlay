using TarkovOverlay.Core.Progress;
using TarkovOverlay.Core.Settings;

namespace TarkovOverlay.Core.Presentation;

/// <summary>1.2.0: which card fields show and how quest/hideout needs are filtered. Default = everything shown.</summary>
public sealed record CardOptions
{
    public bool ShowFlea { get; init; } = true;
    public bool ShowFleaNet { get; init; } = true;
    public bool ShowTrader { get; init; } = true;
    public bool ShowBuyBack { get; init; } = true;
    public bool ShowPerSlot { get; init; } = true;
    public bool ShowStack { get; init; } = true;
    public bool ShowNeedFlags { get; init; } = true;
    public bool ShowNeeds { get; init; } = true;
    public bool ShowRecipes { get; init; } = true;
    public bool ShowKappaBadge { get; init; } = true;
    public bool FilterByProgress { get; init; } = true;
    public bool ShowNonFirNeeds { get; init; } = true;
    public IProgressView? Progress { get; init; }
    /// <summary>1.2.5: shown on every card while tarkov.dev is down / not updating (see PriceStatus.CardNote).</summary>
    public string? Notice { get; init; }

    public static readonly CardOptions Default = new();

    public static CardOptions From(AppSettings s, IProgressView? progress) => new()
    {
        ShowFlea = s.CardShowFlea, ShowFleaNet = s.CardShowFleaNet, ShowTrader = s.CardShowTrader, ShowBuyBack = s.CardShowBuyBack,
        ShowPerSlot = s.CardShowPerSlot, ShowStack = s.CardShowStack, ShowNeedFlags = s.CardShowNeedFlags, ShowNeeds = s.CardShowNeeds, ShowRecipes = s.CardShowRecipes,
        ShowKappaBadge = s.ShowKappaBadge, FilterByProgress = s.FilterNeedsByProgress, ShowNonFirNeeds = s.ShowNonFirNeeds,
        Progress = progress,
    };
}
