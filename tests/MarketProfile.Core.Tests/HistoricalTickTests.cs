using MarketProfile.Core;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Testy pobierania kompletnych historycznych ticków do bazy: pokrycie odcinków, cięcie na doby,
/// stronicowanie, przeskok przerw w handlu, zastępowanie ticków nagranych na żywo i limity brokera.
/// </summary>
public sealed class HistoricalTickTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mp-test-{Guid.NewGuid():N}.db");
    private readonly MarketDataStore _store;

    public HistoricalTickTests() => _store = new MarketDataStore(_dbPath);

    public void Dispose()
    {
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    // ----- Baza: zapis z zastąpieniem i pokrycie -----

    [Fact]
    public void SaveHistoricalTicks_ZastepujeTickiNaZywo_TylkoWOdcinku()
    {
        _store.InsertTicks("ES", [new Tick(T0.AddSeconds(5), 1m, 1), new Tick(T0.AddMinutes(5), 2m, 1)], MarketDataStore.SourceBroker);
        _store.InsertTicks("ES", [new Tick(T0.AddSeconds(6), 9m, 1)], MarketDataStore.SourceTest);

        _store.SaveHistoricalTicks("ES", T0, T0.AddMinutes(1),
            [new Tick(T0.AddSeconds(5), 10m, 2), new Tick(T0.AddSeconds(5), 11m, 3)], MarketDataStore.SourceBroker);

        var ticks = _store.ReadTicks("ES", T0, T0.AddHours(1)).ToList();
        Assert.Equal([10m, 11m, 9m, 2m], ticks.Select(t => t.Price)); // stary tick IB w odcinku zastąpiony; TEST i poza odcinkiem zostają
    }

    [Fact]
    public void SaveHistoricalTicks_LaczyStykajaceSieOdcinkiPokrycia()
    {
        _store.SaveHistoricalTicks("ES", T0, T0.AddMinutes(10), [], MarketDataStore.SourceBroker);
        _store.SaveHistoricalTicks("ES", T0.AddMinutes(10), T0.AddMinutes(20), [], MarketDataStore.SourceBroker);
        _store.SaveHistoricalTicks("ES", T0.AddMinutes(30), T0.AddMinutes(40), [], MarketDataStore.SourceBroker);
        _store.SaveHistoricalTicks("NQ", T0, T0.AddMinutes(5), [], MarketDataStore.SourceBroker);

        Assert.Equal(
            [new TimeRange(T0, T0.AddMinutes(20)), new TimeRange(T0.AddMinutes(30), T0.AddMinutes(40))],
            _store.GetTickCoverage("ES"));

        // Odcinek wypełniający dziurę łączy wszystko w jeden.
        _store.SaveHistoricalTicks("ES", T0.AddMinutes(15), T0.AddMinutes(35), [], MarketDataStore.SourceBroker);
        Assert.Equal([new TimeRange(T0, T0.AddMinutes(40))], _store.GetTickCoverage("ES"));
    }

    [Fact]
    public void DeleteSource_Broker_UsuwaTezPokrycie()
    {
        _store.SaveHistoricalTicks("ES", T0, T0.AddMinutes(10), [new Tick(T0, 1m, 1)], MarketDataStore.SourceBroker);
        _store.DeleteSource("ES", MarketDataStore.SourceTest);
        Assert.Single(_store.GetTickCoverage("ES"));

        _store.DeleteSource("ES", MarketDataStore.SourceBroker);
        Assert.Empty(_store.GetTickCoverage("ES"));
    }

    // ----- Symulacja nie miesza danych testowych z danymi od brokera -----

    [Fact]
    public async Task Symulacja_DaneTestoweIBrokeraWTychSamychMinutach_OdtwarzaTylkoBrokera()
    {
        // Jak w bazie użytkownika: ES od brokera ok. 7600, losowe dane testowe ok. 6000 w tym samym okresie.
        _store.InsertBars("ES", [new Bar(T0, 7600m, 7601m, 7599m, 7600.5m, 40), new Bar(T0.AddMinutes(1), 7601m, 7602m, 7600m, 7601m, 40)],
            MarketDataStore.SourceBroker);
        _store.InsertTicks("ES", [new Tick(T0.AddSeconds(5), 6000m, 1), new Tick(T0.AddMinutes(1).AddSeconds(5), 6001m, 1)],
            MarketDataStore.SourceTest);

        Assert.Equal(MarketDataStore.SourceBroker, _store.SimulationSource("ES"));

        await using var feed = new DatabaseReplayFeed(_store, T0.AddMinutes(1), T0.AddMinutes(2), 1, TimeZoneInfo.Utc);
        await feed.ConnectAsync(Instrument.ES);

        var history = await feed.GetHistoryAsync(TimeSpan.FromMinutes(1));
        Assert.All(history, b => Assert.InRange(b.Low, 7000m, 8000m));

        var replay = BarAggregator.Replay(_store, "ES", T0, T0.AddMinutes(2), _store.SimulationSource("ES")).ToList();
        Assert.NotEmpty(replay);
        Assert.All(replay, i => Assert.InRange(i.Bar!.Value.Low, 7000m, 8000m)); // tylko świece od brokera, bez ticków testowych
    }

    [Fact]
    public void SimulationSource_TylkoDaneTestowe_Testowe_BrakDanych_Null()
    {
        Assert.Null(_store.SimulationSource("ES"));

        _store.InsertTicks("ES", [new Tick(T0, 6000m, 1)], MarketDataStore.SourceTest);
        Assert.Equal(MarketDataStore.SourceTest, _store.SimulationSource("ES"));
        Assert.Null(_store.SimulationSource("NQ"));
    }

    // ----- Brakujące doby -----

    [Fact]
    public void MissingTickChunks_PomijaPokryte_TnieNaDoby_OdNajnowszej()
    {
        var from = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 10, 6, 0, 0, TimeSpan.Zero);
        var covered = new[] { new TimeRange(new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero)) };

        var chunks = HistoryGaps.MissingTickChunks(covered, from, to);

        Assert.Equal(
        [
            new TimeRange(new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), to),
            new TimeRange(new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero), new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero)),
            new TimeRange(from, new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)),
        ], chunks);
    }

    [Fact]
    public void TickDays_LiczyDobyBezBrakow()
    {
        var day = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var covered = new[]
        {
            new TimeRange(day, day.AddDays(1)),                                 // 10.11 – komplet
            new TimeRange(day.AddDays(1), day.AddDays(1).AddHours(5)),          // 11.11 – tylko do 05:00
            new TimeRange(day.AddDays(2).AddMinutes(2), day.AddDays(3)),        // 12.11 – brak 2 min (< 5 min) = komplet
        };

        Assert.Equal((2, 3), HistoryGaps.TickDays(covered, day, day.AddDays(3)));
        Assert.Equal((0, 0), HistoryGaps.TickDays(covered, day, day));
    }

    [Fact]
    public void MissingTickChunks_WszystkoPokryte_Pusto()
    {
        Assert.Empty(HistoryGaps.MissingTickChunks([new TimeRange(T0.AddDays(-3), T0)], T0.AddDays(-2), T0));
    }

    // ----- Pobieranie stronami -----

    /// <summary>Udawany broker: zwraca maks. <paramref name="pageSize"/> ticków od podanego momentu, nie przekraczając końca sesji.</summary>
    private static Func<DateTimeOffset, CancellationToken, Task<TickPage>> FakeBroker(
        IReadOnlyList<Tick> tape, IReadOnlyList<TimeRange> sessions, int pageSize, List<DateTimeOffset>? requests = null) =>
        (start, _) =>
        {
            requests?.Add(start);
            var session = sessions.FirstOrDefault(s => start >= s.From && start < s.To);
            if (session == default)
            {
                return Task.FromResult(new TickPage([], null, false));
            }

            var page = tape.Where(t => t.Time >= start && t.Time < session.To).Take(pageSize).ToList();
            if (page.Count > 0)
            {
                // Jak IB: ostatnia sekunda jest zawsze pełna.
                var lastSecond = page[^1].Time;
                page.AddRange(tape.Where(t => t.Time == lastSecond).Skip(page.Count(t => t.Time == lastSecond)));
            }

            return Task.FromResult(new TickPage(page, null, false));
        };

    private HistoricalTickDownloader Downloader(Func<DateTimeOffset, CancellationToken, Task<TickPage>> fetch) =>
        new(_store, fetch, TimeSpan.Zero, TimeSpan.Zero);

    [Fact]
    public async Task Download_DwieSesjeZPrzerwa_WszystkieTickiBezDuplikatow()
    {
        // Sesja 1: 15:00–15:10, sesja 2: 16:00–16:10; po 3 transakcje na sekundę co 10 s.
        var sessions = new[] { new TimeRange(T0, T0.AddMinutes(10)), new TimeRange(T0.AddHours(1), T0.AddHours(1).AddMinutes(10)) };
        var tape = sessions
            .SelectMany(s => Enumerable.Range(0, 60).SelectMany(i => Enumerable.Range(0, 3)
                .Select(k => new Tick(s.From.AddSeconds(i * 10), 100m + i + k * 0.25m, k + 1))))
            .ToList();

        // Świece mówią, gdzie był handel (pobierane przed tickami).
        _store.InsertBars("ES", BarAggregator.ToMinuteBars(tape).ToList(), MarketDataStore.SourceBroker);

        // Strona po 7 ticków – nie dzieli się równo na sekundy, więc broker dokłada resztę ostatniej sekundy.
        var requests = new List<DateTimeOffset>();
        var range = new TimeRange(T0, T0.AddHours(2));
        var result = await Downloader(FakeBroker(tape, sessions, 7, requests)).DownloadAsync("ES", range, MarketDataStore.SourceBroker, default);

        Assert.False(result.NoTickData);
        Assert.Equal(tape.Count, result.Saved);
        Assert.Equal(tape, _store.ReadTicks("ES", T0, T0.AddHours(2)).ToList());
        Assert.Equal([range], _store.GetTickCoverage("ES"));

        // Przerwa między sesjami przeskoczona jednym zapytaniem (do pierwszej świecy sesji 2), nie minuta po minucie.
        Assert.True(requests.Count < tape.Count / 7 + 6, $"za dużo zapytań: {requests.Count}");
        Assert.Contains(sessions[1].From, requests);
    }

    [Fact]
    public async Task Download_PonownieTenSamOdcinek_NieDublujeTickow()
    {
        var sessions = new[] { new TimeRange(T0, T0.AddMinutes(5)) };
        var tape = Enumerable.Range(0, 30).Select(i => new Tick(T0.AddSeconds(i * 10), 100m, 1)).ToList();
        var fetch = FakeBroker(tape, sessions, 4);

        await Downloader(fetch).DownloadAsync("ES", new TimeRange(T0, T0.AddMinutes(5)), MarketDataStore.SourceBroker, default);
        await Downloader(fetch).DownloadAsync("ES", new TimeRange(T0, T0.AddMinutes(5)), MarketDataStore.SourceBroker, default);

        Assert.Equal(30, _store.CountTicks("ES", T0, T0.AddHours(1)));
    }

    [Fact]
    public async Task Download_StronaPrzekraczaKoniecOdcinka_ZapisujeTylkoOdcinek()
    {
        var sessions = new[] { new TimeRange(T0, T0.AddMinutes(10)) };
        var tape = Enumerable.Range(0, 60).Select(i => new Tick(T0.AddSeconds(i * 10), 100m, 1)).ToList();

        var range = new TimeRange(T0, T0.AddMinutes(5));
        var result = await Downloader(FakeBroker(tape, sessions, 1000)).DownloadAsync("ES", range, MarketDataStore.SourceBroker, default);

        Assert.Equal(30, result.Saved);
        Assert.Equal(0, _store.CountTicks("ES", T0.AddMinutes(5), T0.AddHours(1)));
        Assert.Equal([range], _store.GetTickCoverage("ES"));
    }

    [Fact]
    public async Task Download_LimitZapytan_Ponawia()
    {
        var calls = 0;
        Task<TickPage> Fetch(DateTimeOffset start, CancellationToken _) =>
            Task.FromResult(++calls == 1
                ? new TickPage([], "limit zapytań IB (162): pacing violation", true)
                : new TickPage(start < T0.AddSeconds(1) ? [new Tick(T0, 1m, 1)] : [], null, false));

        var result = await Downloader(Fetch).DownloadAsync("ES", new TimeRange(T0, T0.AddMinutes(1)), MarketDataStore.SourceBroker, default);

        Assert.Equal(1, result.Saved);
        Assert.Equal(3, calls); // odrzucone + ponowione + pusta strona (koniec danych)
    }

    [Fact]
    public async Task Download_BladBrokera_Wyjatek()
    {
        static Task<TickPage> Fetch(DateTimeOffset _, CancellationToken __) =>
            Task.FromResult(new TickPage([], "brak uprawnień lub danych (354)", false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Downloader(Fetch).DownloadAsync("ES", new TimeRange(T0, T0.AddMinutes(1)), MarketDataStore.SourceBroker, default));
        Assert.Contains("354", ex.Message);
    }

    [Fact]
    public async Task Download_BrokerNieMaTickow_MimoSwiec_KonczySieBezOznaczaniaPokrycia()
    {
        // Świece z każdej minuty (handel był), ale broker nie zwraca żadnych ticków – za stare dane.
        _store.InsertBars("ES",
            Enumerable.Range(0, 60).Select(i => new Bar(T0.AddMinutes(i), 1m, 1m, 1m, 1m, 1)).ToList(),
            MarketDataStore.SourceBroker);
        var requests = new List<DateTimeOffset>();

        var result = await Downloader(FakeBroker([], [], 1000, requests))
            .DownloadAsync("ES", new TimeRange(T0, T0.AddHours(1)), MarketDataStore.SourceBroker, default);

        Assert.True(result.NoTickData);
        Assert.Equal(3, requests.Count); // nie odpytuje minuta po minucie
        Assert.Empty(_store.GetTickCoverage("ES"));
    }

    [Fact]
    public async Task Download_TickiZHistorii_ZastepujaNagraneNaZywo_WSymulacji()
    {
        // Na żywo nagrano tylko część transakcji (np. dane opóźnione); historia ma wszystkie.
        _store.InsertTicks("ES", [new Tick(T0.AddSeconds(20), 99m, 5)], MarketDataStore.SourceBroker);
        var tape = new[] { new Tick(T0.AddSeconds(10), 100m, 1), new Tick(T0.AddSeconds(20), 100.25m, 2), new Tick(T0.AddSeconds(30), 100.5m, 3) };

        await Downloader(FakeBroker(tape, [new TimeRange(T0, T0.AddMinutes(1))], 1000))
            .DownloadAsync("ES", new TimeRange(T0, T0.AddMinutes(1)), MarketDataStore.SourceBroker, default);

        Assert.Equal(tape, BarAggregator.Replay(_store, "ES", T0, T0.AddMinutes(1)).Select(i => i.Tick!.Value).ToList());
    }
}
