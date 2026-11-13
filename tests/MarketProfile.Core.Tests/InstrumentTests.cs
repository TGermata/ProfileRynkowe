using MarketProfile.DataFeed.IB;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Testy instrumentów: gotowa lista, sesje osobne dla instrumentu, formatowanie cen
/// oraz odczyt godzin handlu podawanych przez Interactive Brokers.
/// </summary>
public class InstrumentTests
{
    [Fact]
    public void ListaWbudowana_UnikalneSymboleIZnaneStrefyCzasowe()
    {
        var symbols = Instrument.BuiltIn.Select(i => i.Symbol).ToList();
        Assert.Equal(symbols.Count, symbols.Distinct().Count());
        Assert.All(Instrument.BuiltIn, i => Assert.NotNull(Instrument.TryFindTimeZone(i.TimeZoneId)));
        Assert.All(Instrument.BuiltIn, i => Assert.True(i.TickSize > 0));

        // Wszystkie trzy rodzaje instrumentów są na liście.
        Assert.Contains(Instrument.BuiltIn, i => i.Type == InstrumentType.Future);
        Assert.Contains(Instrument.BuiltIn, i => i.Type == InstrumentType.Stock);
        Assert.Contains(Instrument.BuiltIn, i => i.Type == InstrumentType.Index);
    }

    [Fact]
    public void Indeks_BezSesjiNocnej_ProfileTylkoDzienne()
    {
        var spx = Instrument.BuiltIn.Single(i => i.Symbol == "SPX");
        var session = Assert.Single(spx.Sessions);
        Assert.Equal("Dzienna", session.Name);

        // Transakcja wieczorem nie należy do żadnej sesji – nie tworzy profilu.
        var builder = new ProfileBuilder(new ProfileSettings { TickSize = spx.TickSize, Sessions = spx.Sessions, TimeZone = spx.TimeZone });
        var newYork = spx.TimeZone;
        var evening = new DateTime(2026, 11, 12, 20, 0, 0);
        builder.ProcessTick(new Tick(new DateTimeOffset(evening, newYork.GetUtcOffset(evening)), 6000m, 0));
        Assert.Empty(builder.Profiles);
    }

    [Fact]
    public void FDAX_SesjeWCzasieFrankfurtu()
    {
        var fdax = Instrument.BuiltIn.Single(i => i.Symbol == "FDAX");
        Assert.Equal("DAX", fdax.BrokerSymbol); // w IB kontrakt FDAX ma symbol DAX
        var builder = new ProfileBuilder(new ProfileSettings { TickSize = fdax.TickSize, Sessions = fdax.Sessions, TimeZone = fdax.TimeZone });

        // 10:00 we Frankfurcie = sesja dzienna (9:00–17:30 czasu niemieckiego).
        var local = new DateTime(2026, 11, 12, 10, 0, 0);
        builder.ProcessTick(new Tick(new DateTimeOffset(local, fdax.TimeZone.GetUtcOffset(local)), 24000m, 1));
        Assert.Equal("Dzienna", builder.Profiles.Single().Session.Name);
    }

    [Theory]
    [InlineData("0.25", 2, "6012.25")]
    [InlineData("0.01", 2, "6012.25")]
    [InlineData("1", 2, "6012.25")]
    [InlineData("0.00005", 5, "6012.25000")]
    [InlineData("0.250", 2, "6012.25")] // końcowe zera nie zwiększają liczby miejsc
    public void FormatCeny_ZaleznyOdTicka(string tick, int decimals, string expected)
    {
        var tickSize = decimal.Parse(tick, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(decimals, PriceFormat.DecimalsFor(tickSize));
        Assert.Equal(expected, PriceFormat.Format(6012.25m, tickSize));
    }

    [Fact]
    public void GodzinyIB_Futures_SesjaGlownaINocna()
    {
        // ES w IB (czas Chicago): handel 17:00 → 16:00 następnego dnia, sesja główna 8:30–15:00.
        var suggestion = IbTradingHours.Suggest(
            liquidHours: "20261113:0830-20261113:1500;20261114:CLOSED",
            tradingHours: "20261112:1700-20261113:1600;20261114:CLOSED");

        Assert.NotNull(suggestion);
        Assert.Equal(new TimeOnly(8, 30), suggestion.DayStart);
        Assert.Equal(new TimeOnly(15, 0), suggestion.DayEnd);
        Assert.True(suggestion.HasNight);
        Assert.Equal(new TimeOnly(15, 0), suggestion.NightStart); // od końca sesji głównej...
        Assert.Equal(new TimeOnly(8, 30), suggestion.NightEnd);   // ...do jej początku następnego dnia
    }

    [Fact]
    public void GodzinyIB_Indeks_BezSesjiNocnej()
    {
        var suggestion = IbTradingHours.Suggest(
            liquidHours: "20261113:0930-20261113:1600",
            tradingHours: "20261113:0930-20261113:1600");

        Assert.NotNull(suggestion);
        Assert.False(suggestion.HasNight);
    }

    [Fact]
    public void GodzinyIB_StaryFormatIDniZamkniete()
    {
        var intervals = IbTradingHours.Parse("20261113:0930-1600;20261114:CLOSED;20261115:1800-0500");
        Assert.Equal(2, intervals.Count);
        Assert.Equal(new DateTime(2026, 11, 16, 5, 0, 0), intervals[1].End); // przez północ
    }

    [Theory]
    [InlineData("US/Eastern", "America/New_York")]
    [InlineData("US/Central", "America/Chicago")]
    [InlineData("MET", "Europe/Berlin")]
    [InlineData("Europe/Berlin", "Europe/Berlin")]
    public void StrefaIB_NaIana(string ib, string iana) => Assert.Equal(iana, IbTradingHours.ToIanaTimeZone(ib));
}
