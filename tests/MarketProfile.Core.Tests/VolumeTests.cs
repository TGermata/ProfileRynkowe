using MarketProfile.Core;
using MarketProfile.DataFeed.IB;

namespace MarketProfile.Core.Tests;

/// <summary>Testy wolumenu: przyrosty narastającego wolumenu IB i wolumen bieżącego słupka TPO.</summary>
public class VolumeTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static Tick TickAt(int hour, int minute, decimal price, long volume)
    {
        var local = new DateTime(2026, 11, 12, hour, minute, 0, DateTimeKind.Unspecified);
        return new Tick(new DateTimeOffset(local, NewYork.GetUtcOffset(local)), price, volume);
    }

    [Fact]
    public void NarastajacyWolumen_PierwszaWartoscToPunktOdniesienia_PotemPrzyrosty()
    {
        var tracker = new CumulativeVolumeTracker();
        Assert.False(tracker.HasData);

        Assert.Equal(0, tracker.Update(1_000_000)); // punkt odniesienia – wcześniejszy wolumen jest w historii
        Assert.True(tracker.HasData);
        Assert.Equal(25, tracker.Update(1_000_025));
        Assert.Equal(0, tracker.Update(1_000_025));  // bez zmian
        Assert.Equal(5, tracker.Update(1_000_030));
    }

    [Fact]
    public void NarastajacyWolumen_SpadekToNowaSesja_UjemnePomijane()
    {
        var tracker = new CumulativeVolumeTracker();
        tracker.Update(500);
        Assert.Equal(0, tracker.Update(10));   // nowa sesja u brokera – nowy punkt odniesienia
        Assert.Equal(7, tracker.Update(17));
        Assert.Equal(0, tracker.Update(-1));   // brak danych
        Assert.Equal(3, tracker.Update(20));
    }

    [Fact]
    public void Migawka_ZawieraWolumenTylkoBiezacegoOkresu()
    {
        var builder = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4 }); // wiersz = 1 punkt
        builder.ProcessTick(TickAt(9, 35, 100m, 50));   // okres A
        builder.ProcessTick(TickAt(10, 5, 101m, 7));    // okres B
        builder.ProcessTick(TickAt(10, 10, 102m, 3));   // okres B

        var snapshot = builder.Profiles.Single(p => p.Session.Name == "Dzienna").GetSnapshot();

        Assert.Equal(1, snapshot.CurrentPeriodIndex);
        Assert.Equal(2, snapshot.CurrentPeriodVolume.Count);
        Assert.Equal(7, snapshot.CurrentPeriodVolume[101m]);
        Assert.Equal(3, snapshot.CurrentPeriodVolume[102m]);
        Assert.False(snapshot.CurrentPeriodVolume.ContainsKey(100m)); // wolumen okresu A nie należy do bieżącego słupka
        Assert.Equal(7, snapshot.MaxCurrentPeriodVolume);
        Assert.Equal(60, snapshot.TotalVolume);
    }
}
