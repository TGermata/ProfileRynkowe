using MarketProfile.DataFeed.IB;

namespace MarketProfile.Core.Tests;

/// <summary>Szacunek liczby zapytań i czasu pobierania ticków historycznych z IB (maks. 1000 ticków na zapytanie, 60 zapytań / 10 min).</summary>
public sealed class IbTickDensityTests
{
    [Fact]
    public void Szacunek_1000TickowW6Minut()
    {
        // 1000 ticków w 6 minut = 10 000 ticków/h; dzień 23 h = 230 000 ticków = 230 zapytań.
        var d = new IbTickDensity(1000, TimeSpan.FromMinutes(6));

        Assert.Equal(10_000, d.TicksPerHour, 3);
        Assert.Equal(230_000, d.TicksPerDay(23));
        Assert.Equal(230, d.RequestsPerDay(23));
        Assert.Equal(TimeSpan.FromSeconds(2300), d.DownloadTime(23, 1));     // ok. 38 min
        Assert.Equal(TimeSpan.FromSeconds(2300 * 14), d.DownloadTime(23, 14)); // ok. 9 h
    }

    [Fact]
    public void Szacunek_RzadkiInstrument_ConajmniejJednoZapytanie()
    {
        var d = new IbTickDensity(50, TimeSpan.FromHours(10));
        Assert.Equal(1, d.RequestsPerDay(6.5));
    }

    [Fact]
    public void Szacunek_ProbkaKrotszaNizSekunda_BezDzieleniaPrzezZero()
    {
        var d = new IbTickDensity(1000, TimeSpan.Zero);
        Assert.Equal(3_600_000, d.TicksPerHour, 3);
    }
}
