using TarkovOverlay.Core.Economics;

namespace TarkovOverlay.Core.Presentation;

public enum CardKind { Full, PossibleStrong, PossibleWeak, Several, Conflict, NoMatch, NoTooltip, Message }

public sealed record CardRow(string Label, string Value, string? Detail = null, ValueTier? Tier = null, string? Symbol = null, IReadOnlyList<int>? ArmorClasses = null);

public sealed record CardSection(string Title, IReadOnlyList<string> Rows, int MoreCount);

/// <summary>1.4.0: prominent quest/hideout need callout at the top of a price card.</summary>
public enum NeedFlagKind { ActiveQuest, Quest, Hideout }

public sealed record CardFlag(NeedFlagKind Kind, string Label, string Detail, int Count, int FirCount)
{
    public string Text => string.IsNullOrEmpty(Detail) ? Label : $"{Label} · {Detail}";
}

public sealed class CardModel
{
    public CardKind Kind { get; init; }
    /// <summary>Prominent heading for non-confirmed results, e.g. "Possible match".</summary>
    public string? Heading { get; init; }
    public string Title { get; init; } = "";
    public string? Status { get; init; }
    public string? ModeCue { get; init; }
    public string? StaleCue { get; init; }
    /// <summary>1.2.5: data-source notice ("tarkov.dev down · prices from 1 h ago"); the prices still show.</summary>
    public string? Notice { get; init; }
    public string? ItemId { get; init; }
    public IReadOnlyList<CardRow> Rows { get; init; } = Array.Empty<CardRow>();
    public IReadOnlyList<string> Facts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CardSection> Sections { get; init; } = Array.Empty<CardSection>();
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
    /// <summary>1.2.0: short pills after the status, e.g. "KAPPA".</summary>
    public IReadOnlyList<string> Badges { get; init; } = Array.Empty<string>();
    /// <summary>1.4.0: top-of-card callouts when the item is needed for active/upcoming quests or unbuilt hideout upgrades.</summary>
    public IReadOnlyList<CardFlag> Flags { get; init; } = Array.Empty<CardFlag>();
    public IReadOnlyList<string> CandidateIds { get; init; } = Array.Empty<string>();
    /// <summary>True for the expanded cursor card (all quests, crafts, barters, hideout rows).</summary>
    public bool Expanded { get; init; }
    /// <summary>True when the compact card hides rows that an expanded card would show.</summary>
    public bool HasMore => Sections.Any(s => s.MoreCount > 0);
}
