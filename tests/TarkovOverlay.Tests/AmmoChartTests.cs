using System.Text.Json;
using TarkovOverlay.Core.Ammo;
using TarkovOverlay.Core.Catalog;
using TarkovOverlay.Core.Presentation;
using TarkovOverlay.Tests.Fixtures;
using Xunit;

namespace TarkovOverlay.Tests;

/// <summary>1.3.0 ammo chart: caliber names, pen-vs-armor colors, feed parse, grouping/sort/filter, and the card row.</summary>
public class AmmoChartTests
{
    private static CatalogSnapshot Snap => LiveCatalog.Regular;
    private static CatalogItem M882 => Snap.Items["64b7bbb74b75259c590fa897"];

    // ---------------- names and the glance scale ----------------
    [Theory]
    [InlineData("Caliber556x45NATO", "5.56x45 NATO")]
    [InlineData("Caliber545x39", "5.45x39")]
    [InlineData("Caliber9x19PARA", "9x19")]
    [InlineData("Caliber12g", "12/70")]
    [InlineData("Caliber1143x23ACP", ".45 ACP")]
    [InlineData("Caliber762x35", ".300 Blackout")]
    [InlineData("Caliber366TKM", ".366 TKM")]
    public void Caliber_HasAReadableName(string id, string display) => Assert.Equal(display, AmmoCaliber.Display(id));

    [Fact]
    public void Caliber_Unknown_StripsThePrefix() => Assert.Equal("99x99Foo", AmmoCaliber.Display("Caliber99x99Foo"));

    [Theory]
    [InlineData(53, 4, 4)] // M995 vs class 4: usually
    [InlineData(53, 5, 3)] // M995 vs class 5: often (pen 53 ≥ 50)
    [InlineData(53, 6, 2)] // M995 vs class 6: 50/50 (53 is 7 under 60)
    [InlineData(31, 3, 3)] // M855 vs class 3: often
    [InlineData(31, 4, 2)] // M855 vs class 4: 50/50
    [InlineData(18, 1, 3)] // M882 vs class 1: often (18 is 8 over 10)
    [InlineData(18, 2, 2)] // M882 vs class 2
    [InlineData(18, 3, 1)]
    [InlineData(18, 6, 0)]
    [InlineData(null, 3, 0)]
    public void ArmorEffect_UsesTheTenTimesClassScale(int? pen, int cls, int level) =>
        Assert.Equal(level, ArmorEffect.Level(pen, cls));

    [Fact]
    public void ArmorEffect_ReturnsSixClasses()
    {
        var a = ArmorEffect.For(31);
        Assert.Equal(6, a.Length);
        Assert.Equal(4, a[0]); // class 1
        Assert.Equal(0, a[5]); // class 6
    }

    // ---------------- feed parse ----------------
    [Fact]
    public void ParseAmmo_ReadsTheLiveFixture()
    {
        var a = M882.Ammo!;
        Assert.Equal("Caliber9x19PARA", a.Caliber);
        Assert.Equal(56, a.Damage);
        Assert.Equal(18, a.Penetration);
        Assert.Equal(35, a.ArmorDamage);
        Assert.Equal(0.2, a.FragmentationChance);
        Assert.Equal(385, a.Speed);
        Assert.False(a.IsTracer);
        Assert.Equal("bullet", a.AmmoType);
        Assert.Equal(1, a.Pellets);
    }

    [Fact]
    public void ParseAmmo_FromAMinimalJsonObject()
    {
        using var doc = JsonDocument.Parse("""
            {"id":"x","name":"n","properties":{"propertiesType":"ItemPropertiesAmmo","caliber":"Caliber12g",
             "damage":24,"penetrationPower":3,"armorDamage":1,"fragmentationChance":0,"initialSpeed":320,
             "tracer":false,"ammoType":"buckshot","projectileCount":8}}
            """);
        var a = FeedParser.ParseAmmo(doc.RootElement)!;
        Assert.Equal("Caliber12g", a.Caliber);
        Assert.Equal(24, a.Damage);
        Assert.Equal(8, a.Pellets);
        Assert.Equal(192, a.TotalDamage);
        Assert.True(a.IsMultiProjectile);
    }

    [Theory]
    [InlineData("""{"properties":{"propertiesType":"ItemPropertiesMagazine","capacity":30}}""")]
    [InlineData("""{"properties":{"propertiesType":"ItemPropertiesAmmo"}}""")] // no caliber
    [InlineData("""{"name":"not ammo"}""")]
    public void ParseAmmo_IsNullWhenItIsntAmmo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Null(FeedParser.ParseAmmo(doc.RootElement));
    }

    [Fact]
    public void LiveCatalog_HasManyAmmoRounds_AndNoNonAmmoHasStats()
    {
        var ammo = Snap.Items.Values.Where(i => i.Ammo != null).ToList();
        Assert.True(ammo.Count >= 150, $"expected a full ammo set, got {ammo.Count}");
        Assert.All(ammo, i => Assert.True(i.IsAmmo));
        Assert.DoesNotContain(Snap.Items.Values, i => !i.IsAmmo && i.Ammo != null);
        Assert.Contains(ammo, i => i.Ammo!.Caliber == "Caliber556x45NATO");
        Assert.Contains(ammo, i => i.Ammo!.IsTracer);
        Assert.Contains(ammo, i => i.Ammo!.IsMultiProjectile);
    }

    // ---------------- grouping, sort, filter ----------------
    [Fact]
    public void Calibers_AreGroupedWithCounts_AndSortedByDisplayName()
    {
        var list = AmmoChart.Calibers(Snap);
        Assert.Contains(list, c => c.Id == "Caliber556x45NATO" && c.Display == "5.56x45 NATO" && c.Count >= 8);
        Assert.Contains(list, c => c.Id == "Caliber9x19PARA");
        var names = list.Select(c => c.Display).ToList();
        Assert.Equal(names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), names);
    }

    [Fact]
    public void Rows_ForOneCaliber_SortByPenetrationDescending_AndCarryThisModesPrices()
    {
        var rows = AmmoChart.Rows(Snap, "Caliber9x19PARA", sort: AmmoSort.Penetration, descending: true);
        Assert.True(rows.Count >= 6);
        Assert.All(rows, r => Assert.Equal("Caliber9x19PARA", r.Stats.Caliber));
        for (int i = 1; i < rows.Count; i++)
            Assert.True((rows[i - 1].Stats.Penetration ?? -1) >= (rows[i].Stats.Penetration ?? -1),
                $"{rows[i - 1].DisplayName} should be above {rows[i].DisplayName}");
        var m882 = rows.Single(r => r.Item.Id == M882.Id);
        Assert.Equal("18", m882.PenText);
        Assert.Equal("56", m882.DamageText);
        Assert.Equal("35%", m882.ArmorDmgText);
        Assert.Equal("20%", m882.FragText);
        Assert.Equal("385 m/s", m882.SpeedText);
        Assert.Equal("—", m882.TracerText);
        Assert.Equal(6, m882.Armor.Count);
        // prices come from this snapshot (regular / PvP in the fixture)
        if (!M882.IsNoFlea && (M882.LastLowPrice ?? M882.Avg24hPrice) is { } flea)
            Assert.Equal(flea, m882.FleaPrice);
        if (M882.BuyFromTrader.Any(b => b.PriceRub > 0))
        {
            Assert.NotNull(m882.Trader);
            Assert.Contains("LL", m882.TraderLabel);
            Assert.Contains("₽", AmmoChart.FormatTrader(m882), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Rows_SearchAndTracerFilter_AndBuckshotDamageText()
    {
        var tracers = AmmoChart.Rows(Snap, "Caliber556x45NATO", tracersOnly: true);
        Assert.NotEmpty(tracers);
        Assert.All(tracers, r => Assert.True(r.Stats.IsTracer));
        var m882 = AmmoChart.Rows(Snap, "Caliber9x19PARA", search: "M882");
        Assert.Contains(m882, r => r.Item.Id == M882.Id);
        Assert.DoesNotContain(AmmoChart.Rows(Snap, "Caliber9x19PARA", search: "this-does-not-exist"), r => true);
        var buck = AmmoChart.Rows(Snap, "Caliber12g").First(r => r.Stats.IsMultiProjectile);
        Assert.Contains('×', buck.DamageText);
        Assert.StartsWith(buck.Stats.Pellets + "×", buck.DamageText);
        var onlyTraders = AmmoChart.Rows(Snap, "Caliber9x19PARA", tradersOnly: true);
        Assert.All(onlyTraders, r => Assert.NotNull(r.Trader));
        Assert.True(onlyTraders.Count < AmmoChart.Rows(Snap, "Caliber9x19PARA").Count || onlyTraders.Count > 0);
    }

    [Fact]
    public void Rows_WithoutCaliber_ReturnsEveryRound_AndEmptyWhenTheSnapshotHasNoStats()
    {
        var all = AmmoChart.Rows(Snap, caliber: null);
        Assert.Equal(Snap.Items.Values.Count(i => i.Ammo != null), all.Count);
        var empty = new CatalogSnapshot { ModeId = "regular", FetchedAt = DateTimeOffset.UtcNow };
        Assert.Empty(AmmoChart.Calibers(empty));
        Assert.Empty(AmmoChart.Rows(empty, "Caliber9x19PARA"));
    }

    [Fact]
    public void RoundOf_UsesTheRoundsInsideAnAmmoPack()
    {
        var pack = Snap.Items.Values.First(i => i.IsAmmoPack && i.ContainsItems.Count > 0
            && Snap.Item(i.ContainsItems[0].ItemId)?.Ammo != null);
        var inner = Snap.Item(pack.ContainsItems[0].ItemId)!;
        Assert.Null(pack.Ammo);
        Assert.Equal(inner.Ammo, AmmoChart.StatsOf(pack, Snap));
        Assert.Equal(inner.Id, AmmoChart.RoundOf(pack, Snap)!.Id);
        Assert.Equal(M882, AmmoChart.RoundOf(M882, Snap));
        var rig = Snap.Items["59e7643b86f7742cbf2c109a"];
        Assert.Null(AmmoChart.StatsOf(rig, Snap));
        Assert.Null(AmmoChart.RoundOf(rig, Snap));
    }

    [Fact]
    public void SortByName_IsAlphabetical()
    {
        var rows = AmmoChart.Rows(Snap, "Caliber9x19PARA", sort: AmmoSort.Name, descending: false);
        var names = rows.Select(r => r.DisplayName).ToList();
        Assert.Equal(names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), names);
    }

    // ---------------- the card ----------------
    [Fact]
    public void Card_ForAmmo_ShowsCaliberPenDamage_AndAnArmorBar()
    {
        var card = new CardBuilder(LiveCatalog.RegularContext, null, () => LiveCatalog.FetchedAt).Full(M882, compact: true);
        var row = Assert.Single(card.Rows, r => r.ArmorClasses is { Count: 6 });
        Assert.Equal("9x19", row.Label);
        Assert.Contains("18 pen", row.Value);
        Assert.Contains("56 dmg", row.Value);
        Assert.Contains("35% armor", row.Value);
        Assert.Equal(ArmorEffect.For(18), row.ArmorClasses);
        Assert.Contains(card.Facts, f => f.StartsWith("Per-round price"));
    }

    [Fact]
    public void Card_ForAnAmmoPack_ShowsTheRoundsInside()
    {
        var pack = Snap.Items.Values.First(i => i.IsAmmoPack && i.ContainsItems.Count > 0
            && Snap.Item(i.ContainsItems[0].ItemId)?.Ammo != null);
        var inner = Snap.Item(pack.ContainsItems[0].ItemId)!;
        var card = new CardBuilder(LiveCatalog.RegularContext, null, () => LiveCatalog.FetchedAt).Full(pack, compact: true);
        var row = Assert.Single(card.Rows, r => r.ArmorClasses is { Count: 6 });
        Assert.Equal(AmmoCaliber.Display(inner.Ammo!.Caliber), row.Label);
        Assert.Equal(inner.Ammo.Penetration?.ToString() + " pen", row.Value.Split(" · ")[0]);
        Assert.Contains("rounds in this pack", row.Detail);
        Assert.Contains(card.Facts, f => f.StartsWith("Pack:"));
    }

    [Fact]
    public void Card_ForANonAmmoItem_HasNoArmorBar()
    {
        var rig = Snap.Items["59e7643b86f7742cbf2c109a"];
        var card = new CardBuilder(LiveCatalog.RegularContext, null, () => LiveCatalog.FetchedAt).Full(rig, compact: true);
        Assert.DoesNotContain(card.Rows, r => r.ArmorClasses is { Count: > 0 });
        Assert.Empty(new CardBuilder(LiveCatalog.RegularContext, null, () => LiveCatalog.FetchedAt).AmmoRows(rig));
    }

    [Fact]
    public void PossibleMatchCard_AlsoShowsTheAmmoRow()
    {
        var card = new CardBuilder(LiveCatalog.RegularContext, null, () => LiveCatalog.FetchedAt).Preview(M882, strong: true);
        Assert.Contains(card.Rows, r => r.ArmorClasses is { Count: 6 } && r.Label == "9x19");
    }
}
