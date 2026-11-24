using MarketProfile.Core;

namespace MarketProfile.Core.Tests;

/// <summary>Sektory S&amp;P 500: zmiana od otwarcia, progi kolorów i kolejność wg wag.</summary>
public sealed class SectorPerformanceTests
{
    [Theory]
    [InlineData("1.01", SectorChangeLevel.StrongUp)]
    [InlineData("1.00", SectorChangeLevel.Up)]
    [InlineData("0.51", SectorChangeLevel.Up)]
    [InlineData("0.50", SectorChangeLevel.SlightUp)]
    [InlineData("0", SectorChangeLevel.SlightUp)]
    [InlineData("-0.01", SectorChangeLevel.SlightDown)]
    [InlineData("-0.50", SectorChangeLevel.SlightDown)]
    [InlineData("-0.51", SectorChangeLevel.Down)]
    [InlineData("-1.00", SectorChangeLevel.Down)]
    [InlineData("-1.01", SectorChangeLevel.StrongDown)]
    public void Classify_ProgiKolorow(string change, SectorChangeLevel expected) =>
        Assert.Equal(expected, SectorPerformance.Classify(decimal.Parse(change, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void ChangePercent_OdOtwarcia_BrakDanychToNull()
    {
        Assert.Equal(2m, SectorPerformance.ChangePercent(200m, 204m));
        Assert.Equal(-0.5m, SectorPerformance.ChangePercent(200m, 199m));
        Assert.Null(SectorPerformance.ChangePercent(null, 199m));
        Assert.Null(SectorPerformance.ChangePercent(200m, null));
        Assert.Equal(SectorChangeLevel.None, SectorPerformance.Classify(null));
    }

    [Fact]
    public void OrderedByWeight_OdNajwiekszej_BrakujaceZWagaDomyslna()
    {
        var ordered = SectorPerformance.OrderedByWeight([new SectorWeight("MAT", 50m), new SectorWeight("IT", 1m)]);

        Assert.Equal(11, ordered.Count);
        Assert.Equal("MAT", ordered[0].Sector.Key);
        Assert.Equal(50m, ordered[0].Weight);
        Assert.Equal("IT", ordered[^1].Sector.Key);   // 1% – najmniejsza
        Assert.Equal(11, ordered.Select(x => x.Sector.Etf).Distinct().Count());
        Assert.Equal(100m, SectorPerformance.DefaultWeights.Sum(w => w.Weight));
    }
}
