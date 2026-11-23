using MarketProfile.DataFeed.Storage;
using Microsoft.Data.Sqlite;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Zakładka „Szeroki rynek”: wskaźnik „Oddech rynku” (wolumen kupna / sprzedaży SPY z bid/ask), świece 30 min
/// z sesji kasowej na żywo i ich zapis w bazie.
/// </summary>
public sealed class MarketBreathTests : IDisposable
{
    private static readonly Instrument Spy = Instrument.Find(Instrument.BuiltIn, "SPY")!;
    private static readonly Instrument Spx = Instrument.Find(Instrument.BuiltIn, "SPX")!;
    private static readonly TimeZoneInfo NewYork = Spy.TimeZone;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mp-breath-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    /// <summary>Czas w Nowym Jorku (wtorek 24.11.2026, chyba że podano inny dzień).</summary>
    private static DateTimeOffset Ny(int hour, int minute, int day = 24)
    {
        var local = new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local));
    }

    // ----- Wartość ze znakiem -----

    [Fact]
    public void SignedRatio_PrzewagaKupnaDodatnia_SprzedazyUjemna_Zaokraglenie2Miejsca()
    {
        Assert.Equal(1.25m, MarketBreathCalculator.SignedRatio(125, 100));
        Assert.Equal(-1.25m, MarketBreathCalculator.SignedRatio(100, 125));
        Assert.Equal(1.00m, MarketBreathCalculator.SignedRatio(100, 100));
        Assert.Equal(1.33m, MarketBreathCalculator.SignedRatio(400, 300));   // 1,333…
        Assert.Equal(-1.67m, MarketBreathCalculator.SignedRatio(300, 500));  // 1,666…
        Assert.Null(MarketBreathCalculator.SignedRatio(0, 100));
        Assert.Null(MarketBreathCalculator.SignedRatio(100, 0));
    }

    [Fact]
    public void Oddech_LiczyTylkoZmierzonaStroneISesjeKasowa()
    {
        var breath = new MarketBreathCalculator(Spy);
        breath.Add(new Tick(Ny(9, 31), 600m, 300, TradeSide.Buy));
        breath.Add(new Tick(Ny(9, 32), 600m, 200, TradeSide.Sell));
        breath.Add(new Tick(Ny(9, 33), 600m, 999));                     // między bid a ask – bez strony
        breath.Add(new Tick(Ny(9, 15), 600m, 999, TradeSide.Buy));      // przed otwarciem
        breath.Add(new Tick(Ny(16, 0), 600m, 999, TradeSide.Sell));     // po zamknięciu

        Assert.Equal(300, breath.BuyVolume);
        Assert.Equal(200, breath.SellVolume);
        Assert.Equal(1.5m, breath.Value);
        Assert.Equal(new DateOnly(2026, 11, 24), breath.SessionDate);
    }

    [Fact]
    public void Oddech_NowaSesja_OdZera_StaraSesjaPomijana()
    {
        var breath = new MarketBreathCalculator(Spy);
        breath.Add(new Tick(Ny(10, 0, day: 23), 600m, 500, TradeSide.Buy));
        breath.Add(new Tick(Ny(10, 0, day: 23), 600m, 100, TradeSide.Sell));
        breath.Add(new Tick(Ny(9, 30), 600m, 100, TradeSide.Buy));
        breath.Add(new Tick(Ny(9, 40), 600m, 400, TradeSide.Sell));
        breath.Add(new Tick(Ny(15, 0, day: 23), 600m, 9999, TradeSide.Buy)); // spóźniony tick poprzedniej sesji

        Assert.Equal(-4m, breath.Value);
        Assert.Equal(100, breath.BuyVolume);
    }

    [Fact]
    public void Oddech_Load_ZBazy_TickiNaZywoStarszeNieLiczoneDrugiRaz()
    {
        var breath = new MarketBreathCalculator(Spy);
        breath.Load([new Tick(Ny(9, 35), 600m, 100, TradeSide.Buy), new Tick(Ny(9, 36), 600m, 100, TradeSide.Sell)], Ny(9, 40));
        breath.Add(new Tick(Ny(9, 36), 600m, 100, TradeSide.Sell));   // ten sam co w bazie – pominięty
        breath.Add(new Tick(Ny(9, 41), 600m, 100, TradeSide.Buy));    // nowy

        Assert.Equal(200, breath.BuyVolume);
        Assert.Equal(100, breath.SellVolume);
        Assert.Equal(2m, breath.Value);
    }

    [Fact]
    public void CashSession_Latest_PrzedOtwarciemIWWeekend_PoprzedniaSesja()
    {
        Assert.Equal(new DateOnly(2026, 11, 24), CashSession.Latest(Spy, Ny(12, 0)).Date);
        Assert.Equal(new DateOnly(2026, 11, 23), CashSession.Latest(Spy, Ny(8, 0)).Date);
        Assert.Equal(new DateOnly(2026, 11, 27), CashSession.Latest(Spy, Ny(12, 0, day: 29)).Date); // niedziela → piątek
        Assert.Equal(Ny(9, 30), CashSession.Latest(Spy, Ny(12, 0)).Start);
        Assert.Equal(Ny(16, 0), CashSession.Latest(Spy, Ny(12, 0)).End);
    }

    // ----- Świece 30 min na żywo -----

    [Fact]
    public void LiveBars30_TickiWPolgodzinach_PierwszaO930_PoZamknieciuPomijane()
    {
        var live = new LiveIntervalBarBuilder(Spx, BarInterval.Minutes30);
        live.Add(new Tick(Ny(9, 29), 6000m, 0));   // przed otwarciem
        live.Add(new Tick(Ny(9, 30), 6010m, 0));
        live.Add(new Tick(Ny(9, 45), 6020m, 0));
        live.Add(new Tick(Ny(9, 59), 6005m, 0));
        live.Add(new Tick(Ny(10, 0), 6007m, 0));
        live.Add(new Tick(Ny(16, 0), 6100m, 0));   // po zamknięciu

        var bars = live.Merge([]);

        Assert.Equal(2, bars.Count);
        Assert.Equal(new Bar(Ny(9, 30), 6010m, 6020m, 6005m, 6005m, 0), bars[0]);
        Assert.Equal(new Bar(Ny(10, 0), 6007m, 6007m, 6007m, 6007m, 0), bars[1]);
    }

    [Fact]
    public void LiveBars30_Merge_RozszerzaSwieceZBazy_OtwarcieZBazy()
    {
        IReadOnlyList<Bar> stored = [new Bar(Ny(9, 30), 6000m, 6015m, 5995m, 6010m, 0), new Bar(Ny(10, 0), 6010m, 6012m, 6008m, 6011m, 0)];
        var live = new LiveIntervalBarBuilder(Spx, BarInterval.Minutes30);
        live.Add(new Tick(Ny(10, 10), 6020m, 0));
        live.Add(new Tick(Ny(10, 12), 6018m, 0));
        live.Add(new Tick(Ny(10, 31), 6025m, 0));

        var bars = live.Merge(stored);

        Assert.Equal(3, bars.Count);
        Assert.Equal(stored[0], bars[0]);
        Assert.Equal(new Bar(Ny(10, 0), 6010m, 6020m, 6008m, 6018m, 0), bars[1]);
        Assert.Equal(new Bar(Ny(10, 30), 6025m, 6025m, 6025m, 6025m, 0), bars[2]);
    }

    // ----- Zapis świec 30 min -----

    [Fact]
    public void Ohlc30m_ZapisIOdczyt_CzasPoczatkuPolgodziny_OsobnoOdDziennych()
    {
        var store = new MarketDataStore(_dbPath);
        store.SaveOhlcBars("SPX", BarInterval.Minutes30,
            [new Bar(Ny(9, 30), 6000m, 6010m, 5990m, 6005m, 0), new Bar(Ny(10, 0), 6005m, 6020m, 6001m, 6019m, 0)],
            MarketDataStore.SourceBroker);
        store.SaveOhlcBars("SPX", BarInterval.Day, [new Bar(Ny(0, 0), 6000m, 6020m, 5990m, 6019m, 0)], MarketDataStore.SourceBroker);

        var bars = store.ReadOhlcBars("SPX", BarInterval.Minutes30);

        Assert.Equal([Ny(9, 30), Ny(10, 0)], bars.Select(b => b.Time));
        Assert.Single(store.ReadOhlcBars("SPX", BarInterval.Day));
    }

    [Fact]
    public async Task Ohlc30m_PierwszePobranie_PaczkamiWstecz_DoPolrocza()
    {
        var store = new MarketDataStore(_dbPath);
        var now = Ny(12, 0);
        var requests = new List<(DateTimeOffset? End, string Duration)>();
        var downloader = new OhlcBarDownloader(store, (_, _, _) => Task.FromResult(new OhlcPage([], null, false)));

        // Każde zapytanie: jedna świeca miesiąc przed końcem zapytania.
        var result = await downloader.UpdateIntradayAsync("SPX", MarketDataStore.SourceBroker, now, (end, duration, _) =>
        {
            requests.Add((end, duration));
            var to = end ?? now;
            return Task.FromResult(new OhlcPage([new Bar(to.AddMonths(-1), 1m, 1m, 1m, 1m, 0)], null, false));
        }, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(6, requests.Count);              // 6 miesięcy po miesiącu
        Assert.Null(requests[0].End);
        Assert.All(requests, r => Assert.Equal("1 M", r.Duration));
        Assert.Equal(6, store.ReadOhlcBars("SPX", BarInterval.Minutes30).Count);
    }

    [Fact]
    public async Task Ohlc30m_KolejnePobranie_TylkoOdOstatniejSwiecy()
    {
        var store = new MarketDataStore(_dbPath);
        store.SaveOhlcBars("SPX", BarInterval.Minutes30, [new Bar(Ny(15, 30, day: 20), 1m, 1m, 1m, 1m, 0)], MarketDataStore.SourceBroker);
        var requests = new List<string>();
        var downloader = new OhlcBarDownloader(store, (_, _, _) => Task.FromResult(new OhlcPage([], null, false)));

        await downloader.UpdateIntradayAsync("SPX", MarketDataStore.SourceBroker, Ny(12, 0), (end, duration, _) =>
        {
            requests.Add(duration);
            return Task.FromResult(new OhlcPage([new Bar(Ny(15, 30, day: 20), 2m, 2m, 2m, 2m, 0)], null, false));
        }, CancellationToken.None);

        Assert.Equal(["5 D"], requests); // 20.11 15:30 → 24.11 12:00: ok. 4 dni + zapas
        Assert.Equal(2m, store.ReadOhlcBars("SPX", BarInterval.Minutes30).Single().Close);
    }
}
