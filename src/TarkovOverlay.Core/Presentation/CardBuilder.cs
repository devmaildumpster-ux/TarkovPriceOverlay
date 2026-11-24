using System.Globalization;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Economics;
using TarkovOverlay.Core.Ammo;
using TarkovOverlay.Core.Indexes;
using TarkovOverlay.Core.Matching;
using TarkovOverlay.Core.Recognition;
using TarkovOverlay.Core.Settings;

namespace TarkovOverlay.Core.Presentation;

/// <summary>Builds the cursor card and the main detail card from the same row functions so amounts always agree.</summary>
public sealed class CardBuilder
{
    private readonly CatalogContext _ctx;
    private readonly Tracker? _tracker;
    private readonly Func<DateTimeOffset> _now;
    private readonly CardOptions _o;

    public CardBuilder(CatalogContext ctx, Tracker? tracker, Func<DateTimeOffset>? now = null, CardOptions? options = null)
    {
        _ctx = ctx; _tracker = tracker; _now = now ?? (() => DateTimeOffset.UtcNow); _o = options ?? CardOptions.Default;
    }

    public CardOptions Options => _o;

    // ---------------- 1.2.0: needs filtered by the player's progress ----------------
    /// <summary>Quest needs for the item after the progress / FIR filters, plus how many were hidden and why.</summary>
    public (IReadOnlyList<QuestNeed> Shown, int DoneHidden, int NonFirHidden) VisibleQuestNeeds(string itemId)
    {
        var all = _ctx.Quests.For(itemId);
        var shown = new List<QuestNeed>();
        int done = 0, nonFir = 0;
        foreach (var n in all)
        {
            if (_o.FilterByProgress && _o.Progress?.IsQuestCompleted(Mode, n.QuestId) == true) { done++; continue; }
            if (!_o.ShowNonFirNeeds && !n.FoundInRaid) { nonFir++; continue; }
            shown.Add(n);
        }
        return (shown, done, nonFir);
    }

    public (IReadOnlyList<HideoutNeed> Shown, int BuiltHidden, int NonFirHidden) VisibleHideoutNeeds(string itemId)
    {
        var all = _ctx.Hideout.For(itemId);
        var shown = new List<HideoutNeed>();
        int built = 0, nonFir = 0;
        foreach (var n in all)
        {
            if (_o.FilterByProgress && _o.Progress is { } p && p.StationLevel(Mode, n.StationId) >= n.Level) { built++; continue; }
            if (!_o.ShowNonFirNeeds && !n.FoundInRaid) { nonFir++; continue; }
            shown.Add(n);
        }
        return (shown, built, nonFir);
    }

    /// <summary>Needed (not completed) for a Kappa-required quest — Collector and its chain in the tarkov.dev feed.</summary>
    public bool IsKappaItem(string itemId) =>
        _ctx.Quests.For(itemId).Any(n => n.KappaRequired && !(_o.FilterByProgress && _o.Progress?.IsQuestCompleted(Mode, n.QuestId) == true));

    private CatalogSnapshot Snap => _ctx.Snapshot;
    private string Mode => _ctx.Mode.Id;

    // ---------------- result → card ----------------
    public CardModel ForMatch(MatchResult m, StackReading? stack = null, bool expanded = false, long? traderOffer = null) => m.Kind switch
    {
        MatchKind.Verified => Full(m.Items[0], compact: true, stack, expanded, traderOffer),
        MatchKind.PossibleStrong => Preview(m.Items[0], strong: true, m.Alternatives, m.Repair),
        MatchKind.PossibleWeak => Preview(m.Items[0], strong: false, m.Alternatives, m.Repair),
        MatchKind.Conflict when m.Repair == "identical catalog names" => SameName(m, expanded),
        MatchKind.Conflict => Choice(m, "Reads disagree — pick in app"),
        MatchKind.Several => Choice(m, "Several possible matches"),
        _ => NoMatch(m.Text),
    };

    public static CardModel NoTooltip(string title, string hint) =>
        new() { Kind = CardKind.NoTooltip, Title = title, Messages = new[] { hint } };

    public static CardModel Message(string title, params string[] lines) =>
        new() { Kind = CardKind.Message, Title = title, Messages = lines };

    public CardModel NoMatch(string text) => new()
    {
        Kind = CardKind.NoMatch,
        Notice = _o.Notice,
        Title = "No item match",
        ModeCue = _ctx.Mode.ShortCue,
        Messages = string.IsNullOrWhiteSpace(text)
            ? new[] { "No readable title text.", "Retry, or search in the main window." }
            : new[] { $"Read: “{Trim(text, 60)}”", "Search or open a saved image in the main window." },
    };

    private CardModel Choice(MatchResult m, string heading)
    {
        bool sameName = m.Items.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        return new()
        {
            Kind = m.Kind == MatchKind.Conflict ? CardKind.Conflict : CardKind.Several,
            Notice = _o.Notice,
            Heading = heading,
            // The title names what was read; the heading says what to do (they used to repeat each other).
            Title = sameName ? m.Items[0].Name : $"{m.Items.Count} candidates",
            ModeCue = _ctx.Mode.ShortCue,
            Messages = m.Items.Take(3).Select(i => CandidateLabel(i, m.Items, sameName))
                .Append(m.Items.Count > 3 ? $"+{m.Items.Count - 3} more — pick in the main window" : "Pick the right one in the main window").ToArray(),
            CandidateIds = m.Items.Select(i => i.Id).ToArray(),
        };
    }

    // ---------------- same-name catalog entries ----------------
    /// <summary>Same-name entries whose values differ by at most this fraction are shown as one card.</summary>
    public const double SameNameMergeTolerance = 0.10;

    private static readonly System.Text.RegularExpressions.Regex DogtagRx = new(@"\bdogtag\b(?!\s+case)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex VariantSuffix = new(@"-(\d+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Dogtag BEAR/USEC, Marked dogtag … (not the Dogtag case container).</summary>
    public static bool IsDogtag(CatalogItem i) => DogtagRx.IsMatch(i.Name);

    /// <summary>0 for the base entry ("dogtag-bear"), n for tarkov.dev's "-n" duplicates ("dogtag-bear-5").</summary>
    public static int VariantIndex(CatalogItem i) =>
        i.NormalizedName is { } n && VariantSuffix.Match(n) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;

    /// <summary>Value used to compare same-name entries: best trader cash, else fresh flea average.</summary>
    public static long? ReferenceValue(PriceSummary s) =>
        s.BestTrader is { PriceRub: > 0 } t ? Math.Max(t.PriceRub, s.Flea.State == FleaState.Fresh ? s.Flea.Average ?? 0 : 0)
        : s.Flea.State == FleaState.Fresh && s.Flea.Average is > 0 ? s.Flea.Average : null;

    /// <summary>
    /// True when one card is enough: dogtags (variants are tag types whose value depends on the level, which
    /// the hover tooltip doesn't show), entries with no prices at all, or entries all priced within 10%.
    /// </summary>
    public bool ShouldMergeSameName(IReadOnlyList<CatalogItem> items)
    {
        if (items.Count < 2 || items.All(IsDogtag)) return true;
        var v = items.Select(i => ReferenceValue(_ctx.Prices.Summarize(i))).ToList();
        if (v.All(x => x is null)) return true;
        if (v.Any(x => x is null)) return false;
        long min = v.Min(x => x!.Value), max = v.Max(x => x!.Value);
        return max <= min * (1 + SameNameMergeTolerance);
    }

    private CardModel SameName(MatchResult m, bool expanded)
    {
        var items = m.Items.GroupBy(i => i.Id).Select(g => g.First()).ToList();
        if (!ShouldMergeSameName(items)) return Choice(m, "Same name — pick in app");
        return MergedSameName(items, expanded);
    }

    /// <summary>One card for several same-name catalog entries: price ranges, the base entry's value, and its usage sections.</summary>
    public CardModel MergedSameName(IReadOnlyList<CatalogItem> items, bool expanded = false)
    {
        var baseItem = items.OrderBy(VariantIndex).ThenBy(i => i.Id, StringComparer.Ordinal).First();
        var sums = items.Select(i => _ctx.Prices.Summarize(i)).ToList();
        var bs = _ctx.Prices.Summarize(baseItem);
        var full = Full(baseItem, compact: true, expanded: expanded);
        bool dog = items.All(IsDogtag);

        static string Range(IEnumerable<long> vals)
        {
            var l = vals.ToList();
            long lo = l.Min(), hi = l.Max();
            return lo == hi ? Money.Rub(lo) : $"{Money.Rub(lo)}–{Money.Rub(hi)}";
        }
        var rows = new List<CardRow>();
        var flea = sums.Where(s => s.Flea.State != FleaState.Unavailable && s.Flea.Average is > 0).Select(s => s.Flea.Average!.Value).ToList();
        var trader = sums.Where(s => s.BestTrader is { PriceRub: > 0 }).Select(s => s.BestTrader!.PriceRub).ToList();
        rows.Add(new CardRow("Flea", flea.Count > 0 ? Range(flea) : "—",
            flea.Count > 0 ? $"{flea.Count} of {items.Count} variants priced" : bs.Flea.Reason == FleaUnavailableReason.ItemRestricted ? "can't be sold on flea" : "no flea price"));
        rows.Add(new CardRow("Trader", trader.Count > 0 ? Range(trader) : "—",
            trader.Count > 0 ? $"{trader.Count} of {items.Count} variants" : "no trader buys it"));
        var bv = ReferenceValue(bs);
        rows.Add(new CardRow(dog ? "Base tag" : "Base entry", bv is { } b ? Money.Rub(b) : "—",
            bs.BestTrader is { PriceRub: > 0 } ? "trader" : bs.Flea.Average is > 0 ? "flea avg" : (baseItem.BasePrice is > 0 ? $"base price {Money.Rub(baseItem.BasePrice.Value)}" : null)));

        var facts = new List<string>
        {
            dog ? $"Value depends on level · {items.Count} tag variants (the hover tooltip doesn't show the level)"
                : flea.Count + trader.Count == 0 ? $"{items.Count} catalog entries share this name · none has a price"
                : $"{items.Count} catalog entries share this name · values within {(int)(SameNameMergeTolerance * 100)}%",
        };
        facts.AddRange(full.Facts.Where(f => !f.StartsWith("Stack count", StringComparison.Ordinal)));

        return new CardModel
        {
            Kind = CardKind.Full,
            Notice = _o.Notice,
            Title = baseItem.Name,
            ItemId = baseItem.Id,
            Status = full.Status,
            ModeCue = full.ModeCue,
            StaleCue = full.StaleCue,
            Rows = rows,
            Facts = facts,
            Sections = full.Sections,
            Badges = full.Badges,
            Expanded = expanded,
            CandidateIds = items.Select(i => i.Id).ToArray(),
        };
    }

    /// <summary>"Girl poster · #3 · flea ₽28,732": what tells same-name entries apart.</summary>
    private string CandidateLabel(CatalogItem i, IReadOnlyList<CatalogItem> all, bool sameName)
    {
        var parts = new List<string>();
        if (!sameName) parts.Add(i.Name);
        else
        {
            parts.Add($"#{VariantIndex(i) + 1}");
            if (all.Select(x => x.ShortName).Distinct().Count() > 1 && !string.IsNullOrEmpty(i.ShortName)) parts.Add(i.ShortName!);
        }
        var s = _ctx.Prices.Summarize(i);
        int before = parts.Count;
        if (s.Flea.State != FleaState.Unavailable && s.Flea.Average is > 0) parts.Add($"flea {Money.Rub(s.Flea.Average.Value)}");
        if (s.BestTrader is { PriceRub: > 0 } t) parts.Add($"trader {Money.Rub(t.PriceRub)}");
        if (parts.Count == before) parts.Add("no price");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// "Possible match" (1.2.4): always shows the best candidate's Flea and Trader values, clearly headed as a possible
    /// match, plus up to two alternatives with their prices. Strong = one glyph off / unique short name / first line
    /// of a longer name; weak = further off.
    /// </summary>
    public CardModel Preview(CatalogItem item, bool strong, IReadOnlyList<CatalogItem>? alternatives = null, string? why = null)
    {
        var s = _ctx.Prices.Summarize(item);
        var rows = new List<CardRow>();
        if (_o.ShowFlea) rows.Add(FleaRow(s));
        if (_o.ShowTrader && TraderRow(s) is { } t) rows.Add(t);
        rows.AddRange(AmmoRows(item));
        var msgs = new List<string>();
        var alts = (alternatives ?? Array.Empty<CatalogItem>()).Where(a => a.Id != item.Id).Take(2).ToList();
        foreach (var a in alts) msgs.Add("Or: " + CandidateLabel(a, alts, sameName: false));
        msgs.Add(why is { Length: > 0 } ? $"Read wasn't exact ({why}) · verify in the main window" : "Verify in the main window");
        return new CardModel
        {
            Kind = strong ? CardKind.PossibleStrong : CardKind.PossibleWeak,
            Notice = _o.Notice,
            Heading = "Possible match",
            Title = item.Name,
            ItemId = item.Id,
            ModeCue = _ctx.Mode.ShortCue,
            Rows = rows,
            Messages = msgs,
            CandidateIds = alts.Prepend(item).Select(i => i.Id).ToArray(),
        };
    }

    // ---------------- full card ----------------
    /// <param name="stack">Stack-count reading for stackables (hover lookups only); null when not attempted.</param>
    /// <param name="expanded">Cursor card listing every quest (count, FIR), then crafts/barters, then hideout; rows keep the short format.</param>
    /// <param name="traderOffer">The offer read from the trader screen's tooltip (₽), shown as the first row.</param>
    public CardModel Full(CatalogItem item, bool compact, StackReading? stack = null, bool expanded = false, long? traderOffer = null)
    {
        var s = _ctx.Prices.Summarize(item);
        bool stackable = item.StackMaxSize is > 1;
        StackValue? here = stackable && stack?.Count is { } n ? StackValues.For(s, n) : null;
        StackValue? full = stackable ? StackValues.For(s, item.StackMaxSize!.Value) : null;
        string each = here != null ? " · each" : "";

        var rows = new List<CardRow>();
        if (!_o.ShowStack) { here = null; full = null; each = ""; }
        if (traderOffer is > 0) rows.Add(new CardRow("This trader offers", Money.Rub(traderOffer.Value), "read from the trader screen"));
        if (here != null && StackRow($"This stack · {here.Count}", here, s, perUnit: true) is { } hr) rows.Add(hr);
        CardRow? fullRow = full != null && (here == null || here.Count != full.Count) ? StackRow($"Full stack · {full.Count}", full, s, perUnit: false) : null;
        if (here != null && fullRow != null) rows.Add(fullRow);
        if (_o.ShowFlea) rows.Add(Suffix(FleaRow(s), each));
        if (_o.ShowFleaNet && FleaNetRow(s) is { } net) rows.Add(Suffix(net, each));
        if (_o.ShowTrader && TraderRow(s) is { } tr) rows.Add(Suffix(tr, each));
        if (_o.ShowBuyBack && BuyBackRow(s) is { } bb) rows.Add(Suffix(bb, each));
        if (_o.ShowPerSlot && here == null && PerSlotRow(s) is { } ps) rows.Add(ps); // with a stack, the stack row carries the per-slot rating
        if (here == null && fullRow != null) rows.Add(fullRow);
        if (!compact) rows.RemoveAll(r => r.Value == "—");
        rows.AddRange(AmmoRows(item));

        var facts = Facts(item).ToList();
        if (stackable && stack != null && stack.Count == null)
            facts.Insert(0, $"Stack count not read ({stack.Note}) · prices per unit");

        var route = here?.PerSlot?.Route ?? s.PerSlot?.Route;
        var open = _tracker?.OpenFor(Mode, item.Id) ?? Array.Empty<TrackedGoal>();
        string status = open.Count > 0 ? "KEEP"
            : route == SaleRoute.FleaNet ? "FLEA IF ELIGIBLE"
            : route == SaleRoute.Trader ? "TRADER"
            : "CHECK";

        int max = compact && !expanded ? 2 : int.MaxValue;
        var sections = new List<CardSection>();
        int doneHidden = 0;
        if (_o.ShowNeeds && QuestSection(item, max, compact, ref doneHidden) is { } q) sections.Add(q);
        if (_o.ShowRecipes && RecipeSection(item, max, compact) is { } r) sections.Add(r);
        if (_o.ShowNeeds && HideoutSection(item, max, compact, ref doneHidden) is { } h) sections.Add(h);
        if (doneHidden > 0) facts.Add($"{doneHidden} quest/hideout use{(doneHidden == 1 ? "" : "s")} already done · hidden");
        var badges = new List<string>();
        if (_o.ShowKappaBadge && IsKappaItem(item.Id)) badges.Add("KAPPA");

        return new CardModel
        {
            Kind = CardKind.Full,
            Notice = _o.Notice,
            Title = item.Name,
            ItemId = item.Id,
            Status = status,
            ModeCue = _ctx.Mode.ShortCue,
            StaleCue = s.Flea.State == FleaState.Stale ? "Flea stale" : null,
            Rows = rows,
            Facts = facts,
            Sections = sections,
            Badges = badges,
            Expanded = expanded,
        };
    }

    // ---------------- rows (shared by preview and full card) ----------------
    public CardRow FleaRow(PriceSummary s)
    {
        var f = s.Flea;
        if (f.State == FleaState.Unavailable)
        {
            string why = f.Reason switch
            {
                FleaUnavailableReason.ModeDisabled => "flea disabled in this mode",
                FleaUnavailableReason.ItemRestricted => "can’t be sold on flea",
                _ => "no flea price",
            };
            return new CardRow("Flea 24h avg", "Unavailable", why);
        }
        string age = f.LastScan is { } t ? Money.Age(_now() - t) : "no scan time";
        string lvl = f.MinLevel is { } l ? $"Flea if eligible · Lv {l}+" : "Flea if eligible";
        string detail = f.State == FleaState.Stale ? $"stale · {age}" : $"{lvl} · {age}";
        return new CardRow("Flea 24h avg", Money.Rub(f.Average!.Value), detail);
    }

    public CardRow? FleaNetRow(PriceSummary s)
    {
        if (s.Flea.State == FleaState.Unavailable) return null;
        if (s.FleaNet is null) return new CardRow("Flea net", "—", "estimate unavailable");
        return new CardRow("Flea net", Money.Rub(s.FleaNet.Net), $"fee ≈ {Money.Rub(s.FleaNet.Fee)}" + (s.Flea.State == FleaState.Stale ? " · stale" : ""));
    }

    public CardRow? TraderRow(PriceSummary s)
    {
        if (s.BestTrader is not { } t) return null;
        string name = Snap.TraderName(t.TraderId);
        string detail = t.Currency == "RUB" ? name : $"{name} · {Money.Format(t.Price, t.Currency)}";
        return new CardRow("Trader", Money.Rub(t.PriceRub), detail);
    }

    public CardRow? BuyBackRow(PriceSummary s)
    {
        if (s.BuyBack is not { } b) return null;
        if (b.Approximate)
            return new CardRow("Buy back", "≈" + Money.Rub(b.PriceRub), "flea low · approx");
        var parts = new List<string> { $"{Snap.TraderName(b.TraderId)} LL{b.LoyaltyLevel ?? 1}" };
        if (b.TaskUnlockId != null) parts.Add("quest: " + (Snap.TaskName(b.TaskUnlockId) ?? "required"));
        string value = b.Currency == "RUB" ? Money.Rub(b.PriceRub) : Money.Format(b.Price, b.Currency);
        if (b.Currency != "RUB") parts.Add("≈" + Money.Rub(b.PriceRub));
        return new CardRow("Buy back", value, string.Join(" · ", parts));
    }

    private static CardRow Suffix(CardRow r, string suffix) => suffix.Length == 0 ? r : r with { Label = r.Label + suffix };

    /// <summary>Stack total row: value is the better of trader total and (fresh) flea net total, with the tier symbol.</summary>
    public static CardRow? StackRow(string label, StackValue v, PriceSummary s, bool perUnit)
    {
        var parts = new List<string>();
        if (v.TraderTotal is { } t) parts.Add($"trader {Money.Rub(t)}");
        if (v.FleaNetTotal is { } f) parts.Add($"flea net ≈{Money.Rub(f)}{(v.FleaStale ? " (stale)" : "")}");
        if (perUnit)
        {
            long? unit = v.PerSlot is { } ps0 ? (ps0.Route == SaleRoute.Trader ? s.BestTrader?.PriceRub : s.FleaNet?.Net) : s.BestTrader?.PriceRub;
            if (unit is { } u) parts.Add($"{Money.Rub(u)} each");
        }
        if (v.PerSlot is { } p)
        {
            string sym = ValueTiers.Symbol(p.Tier);
            return new CardRow(label, $"{sym} {Money.Rub(p.Amount)}", string.Join(" · ", parts), p.Tier, sym);
        }
        if (v.FleaNetTotal is { } staleOnly) return new CardRow(label, "≈" + Money.Rub(staleOnly), string.Join(" · ", parts));
        return null;
    }

    public static CardRow? PerSlotRow(PriceSummary s)
    {
        if (s.PerSlot is not { } p) return null;
        string label = p.Route == SaleRoute.FleaNet ? "Per slot · flea net" : "Per slot · trader";
        string sym = ValueTiers.Symbol(p.Tier);
        return new CardRow(label, $"{sym} {Money.Rub(p.PerSlotValue)}", p.Footprint > 1 ? $"{p.Footprint} slots · {ValueTiers.Word(p.Tier)}" : ValueTiers.Word(p.Tier), p.Tier, sym);
    }

    // ---------------- ammo (1.3.0) ----------------
    /// <summary>Caliber, pen/damage and a 6-class armor bar, for loose rounds and for ammo packs (the rounds inside).</summary>
    public IReadOnlyList<CardRow> AmmoRows(CatalogItem item)
    {
        var stats = AmmoChart.StatsOf(item, Snap);
        if (stats == null) return Array.Empty<CardRow>();
        string dmg = stats.IsMultiProjectile && stats.Damage is { } d ? $"{stats.Pellets}×{d}" : stats.Damage?.ToString() ?? "—";
        var parts = new List<string>();
        if (stats.Penetration is { } p) parts.Add($"{p} pen");
        if (stats.Damage != null) parts.Add($"{dmg} dmg");
        string value = parts.Count > 0 ? string.Join(" · ", parts) : "—";
        var detail = new List<string>();
        if (stats.ArmorDamage is { } a) detail.Add($"{a}% armor");
        if (stats.FragmentationChance is { } f) detail.Add("frag " + AmmoChart.FormatFrag(f));
        if (stats.Speed is { } sp) detail.Add($"{sp} m/s");
        if (stats.IsTracer) detail.Add(string.IsNullOrWhiteSpace(stats.TracerColor) ? "tracer" : "tracer " + stats.TracerColor);
        if (item.IsAmmoPack) detail.Insert(0, "rounds in this pack");
        return new[]
        {
            new CardRow(AmmoCaliber.Display(stats.Caliber), value, detail.Count > 0 ? string.Join(" · ", detail) : null, ArmorClasses: ArmorEffect.For(stats.Penetration)),
        };
    }

    // ---------------- facts ----------------
    public IReadOnlyList<string> Facts(CatalogItem item)
    {
        var f = new List<string>();
        if (item.Container is { } c && c.Grids.Count > 0)
        {
            int inside = c.Grids.Sum(g => g.Area);
            var pocket = c.Grids.OrderByDescending(g => g.Area).ThenByDescending(g => Math.Max(g.Width, g.Height)).First();
            string outside = item.Width is { } w && item.Height is { } h ? $"{w}×{h} outside" : "outside size unknown";
            string gain = item.Footprint is { } fp && inside - fp > 0 ? $" (+{inside - fp})" : "";
            f.Add($"Storage {inside} slots · {outside}{gain}");
            f.Add($"Largest pocket {pocket.Width}×{pocket.Height}");
        }
        if (item.IsAmmo)
            f.Add(item.StackMaxSize is > 1 ? $"Per-round price · stacks to {item.StackMaxSize}" : "Per-round price");
        if (item.IsAmmoPack && item.ContainsItems.Count > 0 && Snap.Item(item.ContainsItems[0].ItemId) is { } inner)
            f.Add($"Pack: {item.ContainsItems[0].Count:0} × {inner.Name}");
        if (item.MagazineCapacity is > 0) f.Add($"Capacity {item.MagazineCapacity} rounds");
        if (item.Footprint is null) f.Add("Footprint unknown");
        return f;
    }

    // ---------------- sections ----------------
    private CardSection? QuestSection(CatalogItem item, int max, bool compact, ref int doneHidden)
    {
        var (needs, done, _) = VisibleQuestNeeds(item.Id);
        doneHidden += done;
        if (needs.Count == 0) return null;
        var ordered = needs.OrderByDescending(n => Remaining(n.GoalKey) is > 0)
            .ThenBy(n => n.Optional).ThenBy(n => n.MinPlayerLevel ?? 0).ToList();
        var rows = ordered.Take(max).Select(n => QuestRow(n, compact)).ToList();
        int required = needs.Where(n => !n.Optional).Sum(n => n.Count);
        int fir = needs.Where(n => !n.Optional && n.FoundInRaid).Sum(n => n.Count);
        string title = required > 0 ? $"Quests · {required} needed" + (fir > 0 ? (fir == required ? " · all FIR" : $" · {fir} FIR") : " · non-FIR ok") : "Quests";
        return new CardSection(title, rows, Math.Max(0, ordered.Count - rows.Count));
    }

    private int? Remaining(string goalKey) => _tracker?.Get(goalKey)?.Remaining;

    public string QuestRow(QuestNeed n, bool compact)
    {
        var parts = new List<string> { n.QuestName };
        var rem = Remaining(n.GoalKey);
        parts.Add(rem is { } r ? $"{r} left" : $"{n.Count}×");
        if (n.FoundInRaid) parts.Add("FIR");
        if (n.KappaRequired && _o.ShowKappaBadge) parts.Add("Kappa");
        if (n.Optional) parts.Add("optional");
        if (!compact)
        {
            parts.Add(n.TraderName);
            if (n.MinPlayerLevel is > 0) parts.Add($"Lv {n.MinPlayerLevel}");
            if (n.PrerequisiteNames.Count > 0) parts.Add("after " + string.Join(", ", n.PrerequisiteNames));
            if (n.AcceptedItemIds.Count > 1) parts.Add($"any of {n.AcceptedItemIds.Count} items");
        }
        else
        {
            parts.Add(n.TraderName);
            if (n.PrerequisiteNames.Count == 1) parts.Add("after " + Trim(n.PrerequisiteNames[0], 22));
            else if (n.PrerequisiteNames.Count > 1) parts.Add($"after {n.PrerequisiteNames.Count} quests");
        }
        return string.Join(" · ", parts);
    }

    private CardSection? HideoutSection(CatalogItem item, int max, bool compact, ref int doneHidden)
    {
        var (needs, built, _) = VisibleHideoutNeeds(item.Id);
        doneHidden += built;
        if (needs.Count == 0) return null;
        var ordered = needs.OrderByDescending(n => Remaining(n.GoalKey) is > 0).ThenBy(n => n.StationName, StringComparer.Ordinal).ThenBy(n => n.Level).ToList();
        var rows = ordered.Take(max).Select(n => HideoutRow(n, compact)).ToList();
        int total = needs.Sum(n => n.Count), fir = needs.Where(n => n.FoundInRaid).Sum(n => n.Count);
        return new CardSection($"Hideout · {total} total" + (fir > 0 ? (fir == total ? " · all FIR" : $" · {fir} FIR") : ""), rows, Math.Max(0, ordered.Count - rows.Count));
    }

    public string HideoutRow(HideoutNeed n, bool compact)
    {
        var parts = new List<string> { $"{n.StationName} L{n.Level}" };
        var rem = Remaining(n.GoalKey);
        parts.Add(rem is { } r ? $"{r} left" : $"{n.Count}×");
        if (n.FoundInRaid) parts.Add("FIR");
        if (n.Bonus != null) parts.Add(n.Bonus);
        if (!compact && n.Prerequisites.Count > 0) parts.Add("needs " + string.Join(", ", n.Prerequisites));
        return string.Join(" · ", parts);
    }

    private CardSection? RecipeSection(CatalogItem item, int max, bool compact)
    {
        var craftRows = new List<string>();
        var barterRows = new List<string>();
        foreach (var c in _ctx.Recipes.CraftsUsing(item.Id)) craftRows.Add(CraftRow(c, item.Id, used: true, compact));
        foreach (var c in _ctx.Recipes.CraftsProducing(item.Id)) craftRows.Add(CraftRow(c, item.Id, used: false, compact));
        foreach (var b in _ctx.Recipes.BartersUsing(item.Id)) barterRows.Add(BarterRow(b, item.Id, used: true, compact));
        foreach (var b in _ctx.Recipes.BartersProducing(item.Id)) barterRows.Add(BarterRow(b, item.Id, used: false, compact));
        int total = craftRows.Count + barterRows.Count;
        if (total == 0) return null;
        var rows = new List<string>();
        if (max == int.MaxValue) { rows.AddRange(craftRows); rows.AddRange(barterRows); }
        else
        {
            // one row each for craft and barter when both exist
            if (craftRows.Count > 0) rows.Add(craftRows[0]);
            if (barterRows.Count > 0) rows.Add(barterRows[0]);
            foreach (var extra in craftRows.Skip(1).Concat(barterRows.Skip(1)))
            { if (rows.Count >= max) break; rows.Add(extra); }
            rows = rows.Take(max).ToList();
        }
        return new CardSection("Crafts and barters", rows, total - rows.Count);
    }

    public string CraftRow(Craft c, string itemId, bool used, bool compact)
    {
        string station = $"{Snap.StationName(c.StationId)} L{c.Level}";
        string unlock = c.TaskUnlockId != null ? " · quest: " + Trim(Snap.TaskName(c.TaskUnlockId) ?? "required", 20) : "";
        string dur = c.DurationSeconds is { } d ? " · " + Duration(d) : "";
        if (used)
        {
            var me = c.Inputs.First(i => i.ItemId == itemId);
            string role = me.Tool ? "tool for" : $"{Qty(me.Count)}× into";
            string outName = Snap.Item(c.Output.ItemId)?.Name ?? "unknown item";
            return $"Craft use: {role} {Trim(outName, compact ? 28 : 80)}{(c.Output.Count > 1 ? $" ×{Qty(c.Output.Count)}" : "")} · {station}{(compact ? "" : unlock)}";
        }
        string cost = "";
        if (!compact)
        {
            var est = _ctx.Costs.Estimate(c.Inputs, c.Output, c.QuestItemInputs);
            cost = est.Known ? $" · ≈{Money.Rub(est.PerOutputRub!.Value)}/unit" : " · cost unknown";
            cost += " · needs " + string.Join(", ", c.Inputs.Select(i => $"{Qty(i.Count)}× {Snap.Item(i.ItemId)?.Name ?? "unknown"}{(i.Tool ? " (tool)" : "")}"));
        }
        return $"Craft: {station}{(c.Output.Count > 1 ? $" · makes {Qty(c.Output.Count)}" : "")}{dur}{unlock}{cost}";
    }

    public string BarterRow(Barter b, string itemId, bool used, bool compact)
    {
        string trader = $"{Snap.TraderName(b.TraderId)} LL{b.MinTraderLevel}";
        string unlock = b.TaskUnlockId != null ? " · quest: " + Trim(Snap.TaskName(b.TaskUnlockId) ?? "required", 20) : "";
        if (used)
        {
            var me = b.Inputs.First(i => i.ItemId == itemId);
            string outName = Snap.Item(b.Output.ItemId)?.Name ?? "unknown item";
            return $"Barter use: {Qty(me.Count)}× → {Trim(outName, compact ? 28 : 80)} · {trader}{(compact ? "" : unlock)}";
        }
        string inputs = string.Join(" + ", b.Inputs.Take(compact ? 2 : 99).Select(i => $"{Qty(i.Count)}× {Trim(Snap.Item(i.ItemId)?.ShortName ?? Snap.Item(i.ItemId)?.Name ?? "?", compact ? 14 : 60)}"));
        if (compact && b.Inputs.Count > 2) inputs += $" +{b.Inputs.Count - 2}";
        string cost = "";
        if (!compact)
        {
            var est = _ctx.Costs.Estimate(b.Inputs, b.Output, 0);
            cost = est.Known ? $" · ≈{Money.Rub(est.PerOutputRub!.Value)}/unit" : " · cost unknown";
        }
        return $"Barter: {trader} · {inputs}{unlock}{cost}";
    }

    internal static string Qty(double c) => c % 1 == 0 ? ((long)c).ToString(CultureInfo.InvariantCulture) : c.ToString("0.##", CultureInfo.InvariantCulture);

    internal static string Duration(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{(t.Minutes > 0 ? $" {t.Minutes}m" : "")}" : $"{t.Minutes}m";
    }

    internal static string Trim(string s, int max) => s.Length <= max ? s : s[..Math.Max(1, max - 1)] + "…";
}
