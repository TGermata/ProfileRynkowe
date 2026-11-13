using MarketProfile.Core;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Testy obliczeń profilu na ręcznie policzonych przykładach.
/// </summary>
public class ProfileTests
{
    // Strefa czasowa giełdy – godziny w testach podajemy w czasie Nowego Jorku.
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Tworzy tick o podanym czasie nowojorskim.</summary>
    private static Tick TickAt(int day, int hour, int minute, decimal price, long volume = 1)
    {
        var local = new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Unspecified);
        var offset = NewYork.GetUtcOffset(local);
        return new Tick(new DateTimeOffset(local, offset), price, volume);
    }

    /// <summary>Tworzy listę wierszy o podanych liczbach TPO (ceny 1, 2, 3...).</summary>
    private static List<PriceLevel> Levels(params int[] counts) =>
        counts.Select((c, i) => new PriceLevel(i + 1, Enumerable.Range(0, c).ToList(), 0)).ToList();

    [Fact]
    public void SesjaNocna_PrzechodziPrzezPolnoc()
    {
        var night = new SessionDefinition { Name = "Nocna", Start = new TimeOnly(18, 0), End = new TimeOnly(9, 30) };

        // 20:00 we wtorek -> sesja zaczęła się we wtorek o 18:00.
        Assert.True(night.TryGetSessionStart(new DateTime(2026, 11, 10, 20, 0, 0), out var s1));
        Assert.Equal(new DateTime(2026, 11, 10, 18, 0, 0), s1);

        // 03:00 w środę -> ta sama sesja (wtorek 18:00).
        Assert.True(night.TryGetSessionStart(new DateTime(2026, 11, 11, 3, 0, 0), out var s2));
        Assert.Equal(s1, s2);

        // 12:00 -> poza sesją nocną.
        Assert.False(night.TryGetSessionStart(new DateTime(2026, 11, 11, 12, 0, 0), out _));

        Assert.Equal(TimeSpan.FromHours(15.5), night.Duration);
    }

    [Fact]
    public void Builder_PrzypisujeLiteryIOddzielaSesje()
    {
        var builder = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4 }); // wiersz = 1 punkt

        builder.ProcessTick(TickAt(12, 9, 30, 100m));   // A – sesja dzienna
        builder.ProcessTick(TickAt(12, 9, 45, 102.5m)); // A – zakres A: 100..102.5 -> wiersze 100,101,102
        builder.ProcessTick(TickAt(12, 10, 0, 101m));   // B
        builder.ProcessTick(TickAt(12, 3, 0, 95m));     // sesja nocna
        builder.ProcessTick(TickAt(12, 16, 30, 90m));   // poza sesjami – pomijany

        var profiles = builder.Profiles;
        Assert.Equal(2, profiles.Count);
        Assert.Equal("Nocna", profiles[0].Session.Name);   // nocna jest wcześniej chronologicznie
        Assert.Equal("Dzienna", profiles[1].Session.Name);

        var day = profiles[1].GetSnapshot();
        Assert.Equal(new[] { 100m, 101m, 102m }, day.Levels.Select(l => l.Price));
        Assert.Equal("A", day.Levels[0].Letters);
        Assert.Equal("AB", day.Levels[1].Letters);
        Assert.Equal("A", day.Levels[2].Letters);
        Assert.Equal(101m, day.Poc);
        Assert.Equal(100m, day.Open);       // początek rysowania: pierwsza cena profilu
        Assert.Equal(101m, day.LastPrice);  // koniec rysowania: ostatnia cena
        Assert.Equal(102.5m, day.High);
        Assert.Equal(100m, day.Low);
        Assert.Equal(4, day.TotalTpo);

        // Initial Balance = zakres okresów A i B.
        Assert.Equal(102.5m, day.InitialBalanceHigh);
        Assert.Equal(100m, day.InitialBalanceLow);

        // Data handlowa sesji nocnej z 11/12 listopada to 12 listopada.
        Assert.Equal(new DateOnly(2026, 11, 12), profiles[0].TradingDate);
    }

    [Fact]
    public void Poc_PrzyRemisieWybieraWierszNajblizszySrodka()
    {
        // Wiersze 1..5: maksimum 3 TPO na cenie 1 i 3 – środek to indeks 2 (cena 3).
        var levels = Levels(3, 1, 3, 1, 1);
        Assert.Equal(2, ProfileCalculator.FindPocIndex(levels));
    }

    // W poniższych przykładach środek to indeks 2, a maksimum (3 TPO) leży na indeksach 1 i 3 – równo daleko od środka.

    [Theory]
    [InlineData(PocTieBreak.MoreTpoSide, 3)] // powyżej środka 3+2=5 > poniżej 1+3=4
    [InlineData(PocTieBreak.Lower, 1)]
    [InlineData(PocTieBreak.Higher, 3)]
    public void Poc_RemisOdleglosci_WiecejTpoPowyzej(PocTieBreak tieBreak, int expected)
    {
        var levels = Levels(1, 3, 1, 3, 2);
        Assert.Equal(expected, ProfileCalculator.FindPocIndex(levels, tieBreak));
    }

    [Theory]
    [InlineData(PocTieBreak.MoreTpoSide, 1)] // poniżej środka 2+3=5 > powyżej 3+1=4
    [InlineData(PocTieBreak.Lower, 1)]
    [InlineData(PocTieBreak.Higher, 3)]
    public void Poc_RemisOdleglosci_WiecejTpoPonizej(PocTieBreak tieBreak, int expected)
    {
        var levels = Levels(2, 3, 1, 3, 1);
        Assert.Equal(expected, ProfileCalculator.FindPocIndex(levels, tieBreak));
    }

    [Fact]
    public void Poc_RemisOdleglosciIRowneStrony_WiecejTpoWybieraNizszy()
    {
        var levels = Levels(1, 3, 1, 3, 1);
        Assert.Equal(1, ProfileCalculator.FindPocIndex(levels, PocTieBreak.MoreTpoSide));
    }

    [Fact]
    public void Poc_DomyslnaRegulaRemisu_StronaZWiekszaLiczbaTpo()
    {
        Assert.Equal(PocTieBreak.MoreTpoSide, new ProfileSettings().PocTieBreak);
    }

    [Fact]
    public void ValueArea_StandardowaMetoda70Procent()
    {
        // TPO: [1,2,3,5,4,2,1] – razem 18, cel 12.6.
        // Start z POC (indeks 3, 5 TPO). Góra (4+2=6) > dół (3+2=5) -> w górę, suma 11.
        // Góra (1) < dół (5) -> w dół, suma 16 >= 12.6. VA = indeksy 1..5.
        var levels = Levels(1, 2, 3, 5, 4, 2, 1);
        var poc = ProfileCalculator.FindPocIndex(levels);
        Assert.Equal(3, poc);

        var (low, high) = ProfileCalculator.FindValueArea(levels, poc, 0.70m);
        Assert.Equal(1, low);
        Assert.Equal(5, high);
    }

    [Fact]
    public void ValueArea_RemisSum_WybieraPareBlizszaPoc()
    {
        // TPO: [2,2,1,3,9,4,4,2,2] – razem 29, cel 20.3; po odjęciu POC (indeks 4, 9 TPO) zostaje 11.3.
        // Góra (4+4=8) > dół (3+1=4) -> w górę, zostaje 3.3.
        // Góra (2+2=4) = dół (3+1=4) – remis; para dolna bliżej POC (1 wiersz vs 3) -> w dół, zostaje -0.7.
        var levels = Levels(2, 2, 1, 3, 9, 4, 4, 2, 2);
        var poc = ProfileCalculator.FindPocIndex(levels);
        Assert.Equal(4, poc);

        var (low, high) = ProfileCalculator.FindValueArea(levels, poc, 0.70m);
        Assert.Equal(2, low);
        Assert.Equal(6, high);
    }

    [Fact]
    public void ValueArea_RemisSumIOdleglosci_DolaczaObiePary()
    {
        // TPO: [1,3,3,9,3,3,1] – razem 23, cel 16.1; po odjęciu POC (indeks 3) zostaje 7.1.
        // Góra (6) = dół (6) i obie pary równo daleko od POC -> obie naraz, zostaje -4.9.
        var levels = Levels(1, 3, 3, 9, 3, 3, 1);
        var poc = ProfileCalculator.FindPocIndex(levels);
        Assert.Equal(3, poc);

        var (low, high) = ProfileCalculator.FindValueArea(levels, poc, 0.70m);
        Assert.Equal(1, low);
        Assert.Equal(5, high);
    }

    [Fact]
    public void Rozbicie_TworzyDwieCzesciZWlasnymPocIIb_APolaczenieJeScala()
    {
        var builder = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4 });
        builder.ProcessTick(TickAt(12, 9, 30, 100m)); // A
        builder.ProcessTick(TickAt(12, 10, 0, 100m)); // B
        builder.ProcessTick(TickAt(12, 10, 30, 105m)); // C
        builder.ProcessTick(TickAt(12, 11, 0, 105m)); // D
        builder.ProcessTick(TickAt(12, 11, 30, 106m)); // E

        var profile = builder.Profiles.Single();
        var splits = new ProfileSplits();

        // Rozbijamy od okresu C (indeks 2).
        Assert.True(splits.Split(profile, 2));
        var parts = splits.CreateSnapshots(profile);

        Assert.Equal(2, parts.Count);
        Assert.Equal(0, parts[0].FirstPeriod);
        Assert.Equal(100m, parts[0].Poc);
        Assert.Equal("AB", parts[0].Levels.Single().Letters);

        Assert.Equal(2, parts[1].FirstPeriod);
        Assert.Equal(105m, parts[1].Poc);
        Assert.Equal("CD", parts[1].Levels.Single(l => l.Price == 105m).Letters);
        Assert.Equal(105m, parts[1].InitialBalanceLow);  // IB części 2 = okresy C i D
        Assert.Equal(105m, parts[1].InitialBalanceHigh);
        Assert.Equal(106m, parts[1].LastPrice);
        Assert.Equal(105m, parts[1].Open);   // część 2 otwiera się ceną z okresu C
        Assert.Equal(100m, parts[0].LastPrice);

        // Liczba okresów Initial Balance z ustawień – przy 3 okresach IB części 2 obejmuje C, D i E.
        var builder3 = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4, InitialBalancePeriods = 3 });
        foreach (var (h, m, price) in new[] { (9, 30, 100m), (10, 0, 100m), (10, 30, 105m), (11, 0, 105m), (11, 30, 106m) })
        {
            builder3.ProcessTick(TickAt(12, h, m, price));
        }

        var whole = builder3.Profiles.Single().GetSnapshot();
        Assert.Equal(105m, whole.InitialBalanceHigh); // A, B, C
        Assert.Equal(100m, whole.InitialBalanceLow);

        // Połączenie przywraca jeden profil.
        Assert.True(splits.MergeWithPrevious(profile, 2));
        Assert.Single(splits.CreateSnapshots(profile));
    }

    [Fact]
    public void Swieca_OznaczaCalyZakresHighLow()
    {
        var builder = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4 });
        var t = TickAt(12, 9, 31, 0).Time;
        builder.ProcessBar(new Bar(t, 100m, 103.75m, 100m, 103m, 50));

        var snapshot = builder.Profiles.Single().GetSnapshot();
        Assert.Equal(4, snapshot.Levels.Count); // wiersze 100, 101, 102, 103
        Assert.All(snapshot.Levels, l => Assert.Equal("A", l.Letters));

        // Wolumen 50 rozłożony na 4 wiersze: po 12, a reszta (2) do wiersza zamknięcia (103).
        Assert.Equal(new long[] { 12, 12, 12, 14 }, snapshot.Levels.Select(l => l.Volume));
        Assert.Equal(50, snapshot.TotalVolume);
        Assert.Equal(103m, snapshot.VolumePoc);
    }
}
