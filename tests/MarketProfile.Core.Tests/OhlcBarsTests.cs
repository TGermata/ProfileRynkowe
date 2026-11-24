using MarketProfile.Core;
using MarketProfile.DataFeed.IB;
using MarketProfile.DataFeed.Storage;
using Microsoft.Data.Sqlite;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Świece dzienne / tygodniowe / miesięczne: zapis w bazie, uzupełnianie z brokera (pełna historia,
/// potem tylko ostatnie świece), ponowienie po limicie zapytań i odczyt dat z TWS.
/// </summary>
public sealed class OhlcBarsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mp-test-{Guid.NewGuid():N}.db");
    private readonly MarketDataStore _store;

    public OhlcBarsTests() => _store = new MarketDataStore(_dbPath);

    public void Dispose()
    {
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    private static Bar Day(int year, int month, int day, decimal close, long volume = 1000) =>
        new(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero), close - 1, close + 2, close - 3, close, volume);

    [Fact]
    public void Baza_ZapisujeSwieceOsobnoDlaInterwalu_IZastepujeBiezaca()
    {
        _store.SaveOhlcBars("ES", BarInterval.Day, [Day(2026, 11, 13, 6000.25m), Day(2026, 11, 16, 6010.125m)], MarketDataStore.SourceBroker);
        _store.SaveOhlcBars("ES", BarInterval.Week, [Day(2026, 11, 9, 5990m)], MarketDataStore.SourceBroker);

        // Bieżąca świeca dnia zmienia się w trakcie sesji – zapis nadpisuje ją, nie dubluje.
        _store.SaveOhlcBars("ES", BarInterval.Day, [Day(2026, 11, 16, 6020.5m, 2500)], MarketDataStore.SourceBroker);

        var days = _store.ReadOhlcBars("ES", BarInterval.Day);
        Assert.Equal(2, days.Count);
        Assert.Equal(6000.25m, days[0].Close);  // cena bez zaokrągleń
        Assert.Equal(6020.5m, days[1].Close);
        Assert.Equal(2500, days[1].Volume);

        Assert.Single(_store.ReadOhlcBars("ES", BarInterval.Week));
        Assert.Empty(_store.ReadOhlcBars("ES", BarInterval.Month));
        Assert.Empty(_store.ReadOhlcBars("NQ", BarInterval.Day));

        var range = _store.GetOhlcRange("ES", BarInterval.Day);
        Assert.Equal((Day(2026, 11, 13, 0).Time, Day(2026, 11, 16, 0).Time, 2), range);
        Assert.Null(_store.GetOhlcRange("ES", BarInterval.Month));

        // Odczyt zakresu [from, to).
        Assert.Single(_store.ReadOhlcBars("ES", BarInterval.Day, Day(2026, 11, 14, 0).Time));
    }

    [Fact]
    public void OkresZapytania_PelnaHistoria_PotemTylkoOstatnieSwiece()
    {
        var depth = OhlcHistoryDepth.Default;
        Assert.Equal("5 Y", OhlcBarDownloader.DurationFor(BarInterval.Day, null, Now, depth));
        Assert.Equal("10 Y", OhlcBarDownloader.DurationFor(BarInterval.Week, null, Now, depth));
        Assert.Equal("20 Y", OhlcBarDownloader.DurationFor(BarInterval.Month, null, Now, depth));

        // Ostatnia świeca z piątku, teraz poniedziałek – kilka dni (z zapasem, ostatnia świeca też ponownie).
        var friday = Day(2026, 11, 13, 0).Time;
        Assert.Equal("6 D", OhlcBarDownloader.DurationFor(BarInterval.Day, friday, Now, depth));
        Assert.Equal("3 W", OhlcBarDownloader.DurationFor(BarInterval.Week, Day(2026, 11, 9, 0).Time, Now, depth));
        Assert.Equal("1 M", OhlcBarDownloader.DurationFor(BarInterval.Month, Day(2026, 11, 2, 0).Time, Now, depth));
        Assert.Equal("3 M", OhlcBarDownloader.DurationFor(BarInterval.Month, Day(2026, 9, 1, 0).Time, Now, depth));

        // Dawno nieodświeżane – w latach.
        Assert.Equal("3 Y", OhlcBarDownloader.DurationFor(BarInterval.Day, Now.AddDays(-400), Now, depth));
    }

    [Fact]
    public async Task Uzupelnianie_PierwszyRazPelnaHistoria_PotemKrotkieOkno()
    {
        var requests = new List<(BarInterval Interval, string Duration)>();
        var downloader = new OhlcBarDownloader(_store, (interval, duration, _) =>
        {
            requests.Add((interval, duration));
            return Task.FromResult(new OhlcPage([Day(2026, 11, 13, 6000m), Day(2026, 11, 16, 6010m)], null, false));
        });

        var first = await downloader.UpdateAsync("ES", MarketDataStore.SourceBroker, Now, CancellationToken.None);
        Assert.Equal(["20 Y", "10 Y", "5 Y"], requests.Select(r => r.Duration));
        Assert.All(first, r => Assert.Equal(2, r.Saved));

        requests.Clear();
        await downloader.UpdateAsync("ES", MarketDataStore.SourceBroker, Now, CancellationToken.None);
        Assert.Equal(["1 M", "2 W", "3 D"], requests.Select(r => r.Duration));
        Assert.Equal(2, _store.ReadOhlcBars("ES", BarInterval.Day).Count);
    }

    [Fact]
    public async Task SeriaTylkoSesjaKasowa_OsobnoOdPelnejDoby()
    {
        _store.SaveOhlcBars("ES", BarInterval.Day, [Day(2026, 11, 16, 6010m)], MarketDataStore.SourceBroker);
        _store.SaveOhlcBars("ES", BarInterval.Day, [Day(2026, 11, 16, 6005m)], MarketDataStore.SourceBroker, cashOnly: true);

        Assert.Equal(6010m, Assert.Single(_store.ReadOhlcBars("ES", BarInterval.Day)).Close);
        Assert.Equal(6005m, Assert.Single(_store.ReadOhlcBars("ES", BarInterval.Day, cashOnly: true)).Close);
        Assert.Null(_store.GetOhlcRange("ES", BarInterval.Week, cashOnly: true));

        // Seria kasowa ma własny zakres – pierwsze uzupełnienie tygodni / miesięcy to pełna historia, dni – krótkie okno.
        var requests = new List<string>();
        var downloader = new OhlcBarDownloader(_store, (_, duration, _) =>
        {
            requests.Add(duration);
            return Task.FromResult(new OhlcPage([Day(2026, 11, 16, 6007m)], null, false));
        });
        await downloader.UpdateAsync("ES", MarketDataStore.SourceBroker, Now, CancellationToken.None, cashOnly: true);

        Assert.Equal(["20 Y", "10 Y", "3 D"], requests);
        Assert.Equal(6007m, _store.ReadOhlcBars("ES", BarInterval.Day, cashOnly: true)[0].Close);
        Assert.Equal(6010m, _store.ReadOhlcBars("ES", BarInterval.Day)[0].Close); // pełna doba bez zmian
    }

    [Fact]
    public async Task Uzupelnianie_PoLimicieZapytanPonawia_BladNieZapisujeNic()
    {
        var calls = 0;
        var downloader = new OhlcBarDownloader(_store, (_, _, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new OhlcPage([], "limit zapytań IB", true)
                : new OhlcPage([Day(2026, 11, 16, 6010m)], null, false));
        }, pacingWait: TimeSpan.Zero);

        var result = await downloader.UpdateIntervalAsync("ES", BarInterval.Day, MarketDataStore.SourceBroker, Now, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal(1, result.Saved);
        Assert.Null(result.Error);

        var failing = new OhlcBarDownloader(_store, (_, _, _) =>
            Task.FromResult(new OhlcPage([], "brak uprawnień", false)));
        var error = await failing.UpdateIntervalAsync("NQ", BarInterval.Week, MarketDataStore.SourceBroker, Now, CancellationToken.None);
        Assert.Equal("brak uprawnień", error.Error);
        Assert.Null(_store.GetOhlcRange("NQ", BarInterval.Week));
    }

    [Fact]
    public void PoczatekOkresu_PoniedzialekIPierwszyDzienMiesiaca()
    {
        Assert.Equal(Day(2026, 11, 9, 0).Time, BarInterval.Week.PeriodStart(Day(2026, 11, 13, 0).Time));  // piątek → poniedziałek
        Assert.Equal(Day(2026, 11, 16, 0).Time, BarInterval.Week.PeriodStart(Day(2026, 11, 16, 0).Time)); // poniedziałek
        Assert.Equal(Day(2026, 11, 16, 0).Time, BarInterval.Week.PeriodStart(Day(2026, 11, 15, 0).Time)); // niedziela → następny tydzień
        Assert.Equal(Day(2026, 10, 1, 0).Time, BarInterval.Month.PeriodStart(Day(2026, 10, 30, 0).Time));
        Assert.Equal(Day(2026, 11, 13, 0).Time, BarInterval.Day.PeriodStart(Day(2026, 11, 13, 0).Time.AddHours(5)));
    }

    [Fact]
    public void BiezacyTydzienIMiesiac_PodRoznymiDatami_ToJednaSwieca()
    {
        // Broker podpisuje bieżącą świecę dzisiejszą datą – kolejne dni nie mogą tworzyć nowych świec.
        _store.SaveOhlcBars("ES", BarInterval.Week, [Day(2026, 11, 13, 5990m), Day(2026, 11, 16, 6000m)], MarketDataStore.SourceBroker);
        _store.SaveOhlcBars("ES", BarInterval.Week, [Day(2026, 11, 17, 6015m)], MarketDataStore.SourceBroker);
        _store.SaveOhlcBars("ES", BarInterval.Month, [Day(2026, 10, 30, 5900m), Day(2026, 11, 16, 6000m)], MarketDataStore.SourceBroker);
        _store.SaveOhlcBars("ES", BarInterval.Month, [Day(2026, 11, 17, 6015m)], MarketDataStore.SourceBroker);

        var weeks = _store.ReadOhlcBars("ES", BarInterval.Week);
        Assert.Equal([Day(2026, 11, 9, 0).Time, Day(2026, 11, 16, 0).Time], weeks.Select(b => b.Time));
        Assert.Equal(6015m, weeks[1].Close);

        var months = _store.ReadOhlcBars("ES", BarInterval.Month);
        Assert.Equal([Day(2026, 10, 1, 0).Time, Day(2026, 11, 1, 0).Time], months.Select(b => b.Time));
        Assert.Equal(6015m, months[1].Close);
    }

    [Fact]
    public void StareZapisy_PodKoncemOkresu_PrzenoszoneNaPoczatekPrzyOtwarciuBazy()
    {
        // Pierwsza wersja zapisywała datę od brokera: piątek tygodnia, a bieżącą świecę – dzisiejszą datą.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString()))
        {
            connection.Open();
            foreach (var (time, close) in new[] { (Day(2026, 11, 13, 0).Time, 5990L), (Day(2026, 11, 16, 0).Time, 6000L), (Day(2026, 11, 17, 0).Time, 6015L) })
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO ohlc_bars VALUES ('ES', 'W', $t, 1, 1, 1, $c, 0, 'IB')";
                insert.Parameters.AddWithValue("$t", time.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$c", close * 100_000_000);
                insert.ExecuteNonQuery();
            }
        }

        var reopened = new MarketDataStore(_dbPath);
        var weeks = reopened.ReadOhlcBars("ES", BarInterval.Week);
        Assert.Equal([Day(2026, 11, 9, 0).Time, Day(2026, 11, 16, 0).Time], weeks.Select(b => b.Time));
        Assert.Equal(6015m, weeks[1].Close); // z dwóch zapisów bieżącego tygodnia zostaje najnowszy
    }

    [Fact]
    public void CzasSwiecyZTws_DataSekundyIStrefa()
    {
        Assert.True(IbBarTime.TryParse("20261113", out var date));
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), date);

        Assert.True(IbBarTime.TryParse("1794834000", out var seconds));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794834000), seconds);

        Assert.True(IbBarTime.TryParse("20261113 09:30:00 UTC", out var withTime));
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.Zero), withTime);

        Assert.False(IbBarTime.TryParse("abc", out _));
    }
}

/// <summary>Rynek szeroki (np. SPX): wyszukiwanie instrumentu i osobne świece w bazie.</summary>
public sealed class BroadMarketTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mp-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void RynekSzeroki_ZListyAlboWbudowany()
    {
        var custom = Instrument.BuiltIn.First(i => i.Symbol == "SPX") with { Description = "mój SPX" };
        Assert.Equal("mój SPX", Instrument.Find([Instrument.ES, custom], "SPX")!.Description);

        // SPX usunięty z listy użytkownika – definicja wbudowana (indeks CBOE).
        var builtIn = Instrument.Find([Instrument.ES], "spx");
        Assert.NotNull(builtIn);
        Assert.Equal(InstrumentType.Index, builtIn.Type);
        Assert.Null(Instrument.Find([Instrument.ES], "XYZ"));
    }

    [Fact]
    public void SwieceInstrumentuIRynkuSzerokiego_Osobno()
    {
        var store = new MarketDataStore(_dbPath);
        var day = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        store.SaveOhlcBars("ES", BarInterval.Day, [new Bar(day, 6800m, 6850m, 6790m, 6840m, 1_200_000)], MarketDataStore.SourceBroker);
        store.SaveOhlcBars("SPX", BarInterval.Day, [new Bar(day, 6770.12m, 6815.40m, 6760.03m, 6808.77m, 0)], MarketDataStore.SourceBroker);

        Assert.Equal(6840m, Assert.Single(store.ReadOhlcBars("ES", BarInterval.Day)).Close);
        var spx = Assert.Single(store.ReadOhlcBars("SPX", BarInterval.Day));
        Assert.Equal(6808.77m, spx.Close);
        Assert.Single(BarExtensions.CompleteOnly([spx])); // indeks bez wolumenu, ale z zakresem – pełna świeca
    }
}
