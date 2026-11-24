using MarketProfile.Core;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Ticki spóźnione – transakcje z przerwy w danych dociągane z historii IB po powrocie połączenia, już po nowych
/// tickach na żywo: uzupełniają profil, ale nie przestawiają ceny zamknięcia, otwarcia ani delty transakcji na żywo.
/// </summary>
public sealed class LateTicksTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static Tick At(int hour, int minute, int second, decimal price, long volume, TradeSide side = TradeSide.Unknown)
    {
        var local = new DateTime(2026, 11, 24, hour, minute, second);
        return new Tick(new DateTimeOffset(local, NewYork.GetUtcOffset(local)), price, volume, side);
    }

    private static ProfileSnapshot DaySnapshot(ProfileBuilder builder) =>
        builder.Profiles.Select(p => p.GetSnapshot()).Single(p => SessionNames.IsDay(p.Session.Name) && !p.IsEmpty);

    [Fact]
    public void SpoznionyTick_UzupelniaProfil_BezZmianyZamknieciaIOtwarcia()
    {
        var builder = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4 });
        builder.ProcessTick(At(10, 5, 0, 6800m, 10));
        builder.ProcessTick(At(10, 6, 0, 6802m, 20));   // powrót danych po przerwie – ostatnia cena na żywo

        builder.ProcessTick(At(10, 5, 30, 6810m, 5));   // z przerwy: później niż pierwszy, wcześniej niż ostatni
        builder.ProcessTick(At(10, 1, 0, 6790m, 7));    // z przerwy: przed pierwszym tickiem okresu

        var day = DaySnapshot(builder);
        Assert.Equal(6802m, day.LastPrice);
        Assert.Equal(6790m, day.Open);
        Assert.Equal(6810m, day.High);
        Assert.Equal(6790m, day.Low);
        Assert.Equal(42, day.TotalVolume);
    }

    [Fact]
    public void SpoznionyTick_NiePrzestawiaRegulyTickaDlaTransakcjiNaZywo()
    {
        var builder = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4 });
        builder.ProcessTick(At(10, 5, 0, 6800m, 10, TradeSide.Buy));
        builder.ProcessTick(At(10, 6, 0, 6801m, 10));               // reguła ticka: wyżej → kupno

        builder.ProcessTick(At(10, 5, 30, 6790m, 3));               // spóźniony, bez strony – tylko wolumen
        builder.ProcessTick(At(10, 5, 40, 6795m, 4, TradeSide.Sell)); // spóźniony ze stroną (oszacowaną przez źródło)

        builder.ProcessTick(At(10, 7, 0, 6800m, 10));               // na żywo: niżej niż 6801 → sprzedaż

        var day = DaySnapshot(builder);
        Assert.Equal(20, day.TotalBuyVolume);
        Assert.Equal(14, day.TotalSellVolume);
        Assert.Equal(37, day.TotalVolume);
    }
}
