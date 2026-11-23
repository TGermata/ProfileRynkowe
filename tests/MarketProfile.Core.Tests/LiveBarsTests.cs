using MarketProfile.Core;

namespace MarketProfile.Core.Tests;

/// <summary>Świece D/W/M na żywo: ostatnia świeca z bazy uzupełniana profilami TPO bieżącego dnia handlowego.</summary>
public sealed class LiveBarsTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static Tick At(int month, int day, int hour, int minute, decimal price, long volume)
    {
        var local = new DateTime(2026, month, day, hour, minute, 0);
        return new Tick(new DateTimeOffset(local, NewYork.GetUtcOffset(local)), price, volume);
    }

    private static Bar Bar(int month, int day, decimal open, decimal high, decimal low, decimal close, long volume) =>
        new(new DateTimeOffset(2026, month, day, 0, 0, 0, TimeSpan.Zero), open, high, low, close, volume);

    private static Bar Live(int month, int day, decimal open, decimal high, decimal low, decimal close, long volume) =>
        Bar(month, day, open, high, low, close, volume);

    [Fact]
    public void LiveDayBar_SesjaNocnaIDziennaToJednaSwiecaDnia()
    {
        var builder = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4 });
        // Sesja nocna (od 15.11 18:00) i dzienna 16.11 – ta sama data handlowa 16.11.
        builder.ProcessTick(At(11, 15, 18, 5, 6800m, 10));
        builder.ProcessTick(At(11, 16, 3, 0, 6790m, 20));
        builder.ProcessTick(At(11, 16, 9, 35, 6815m, 30));
        builder.ProcessTick(At(11, 16, 10, 0, 6808m, 40));
        // Poprzedni dzień handlowy – pomijany.
        builder.ProcessTick(At(11, 13, 10, 0, 6700m, 99));

        var snapshots = builder.Profiles.Select(p => p.GetSnapshot()).ToList();
        var bar = LiveBars.LiveDayBar(snapshots, new DateOnly(2026, 11, 16));

        Assert.Equal(Live(11, 16, 6800m, 6815m, 6790m, 6808m, 100), bar);
        Assert.Null(LiveBars.LiveDayBar(snapshots, new DateOnly(2026, 11, 17)));
    }

    [Fact]
    public void MergeLive_TaSamaSwieca_RozszerzaZakresIZamkniecie_StarszeBezZmian()
    {
        var older = Bar(11, 13, 6700m, 6750m, 6690m, 6740m, 900);
        var today = Bar(11, 16, 6800m, 6810m, 6795m, 6805m, 500);
        var live = Live(11, 16, 6801m, 6820m, 6799m, 6812m, 700);

        var merged = LiveBars.MergeLive([older, today], BarInterval.Day, live);

        Assert.Equal(older, merged[0]);
        Assert.Equal(Bar(11, 16, 6800m, 6820m, 6795m, 6812m, 700), merged[1]);
    }

    [Fact]
    public void MergeLive_TydzienIMiesiac_OstatniaSwiecaOkresu_WolumenZBazy()
    {
        var week = Bar(11, 16, 6800m, 6810m, 6780m, 6805m, 5000);   // poniedziałek 16.11
        var month = Bar(11, 1, 6600m, 6830m, 6550m, 6805m, 90000);
        var live = Live(11, 18, 6806m, 6840m, 6800m, 6835m, 700); // środa 18.11

        Assert.Equal(Bar(11, 16, 6800m, 6840m, 6780m, 6835m, 5000), LiveBars.MergeLive([week], BarInterval.Week, live)[^1]);
        Assert.Equal(Bar(11, 1, 6600m, 6840m, 6550m, 6835m, 90000), LiveBars.MergeLive([month], BarInterval.Month, live)[^1]);
    }

    [Fact]
    public void MergeLive_BrakSwiecyOkresu_DopisujeNowa()
    {
        var friday = Bar(11, 13, 6700m, 6750m, 6690m, 6740m, 900);
        var live = Live(11, 16, 6745m, 6760m, 6730m, 6755m, 300);

        var daily = LiveBars.MergeLive([friday], BarInterval.Day, live);
        Assert.Equal([friday, live], daily);

        // Nowy tydzień: świeca tygodniowa z poniedziałkiem jako początkiem.
        var weekly = LiveBars.MergeLive([Bar(11, 9, 6600m, 6750m, 6590m, 6740m, 4000)], BarInterval.Week, live);
        Assert.Equal(2, weekly.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), weekly[^1].Time);
    }
}
