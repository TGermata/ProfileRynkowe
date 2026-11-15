using MarketProfile.Core;
using MarketProfile.DataFeed.Storage;
using Microsoft.Data.Sqlite;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Wierność symulacji: ceny zapisywane dokładnie tak jak od brokera, odtwarzanie bez wymyślonych ticków,
/// profil „tak jak wyglądał” do danego okresu (płynne cofanie) i kroki płynnego przewijania.
/// </summary>
public sealed class SimulationFidelityTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero); // 10:00 w Nowym Jorku
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mp-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    // ----- Zapis bez zaokrąglania -----

    [Fact]
    public void Zapis_CenyDokladnieJakOdBrokera_BezZaokraglania()
    {
        var store = new MarketDataStore(_dbPath);
        var ticks = new[]
        {
            new Tick(T0, 7611.25m, 1),
            new Tick(T0.AddSeconds(1), 7611.3m, 1),        // spoza siatki 0,25 – też bez zmian
            new Tick(T0.AddSeconds(2), 1.08765m, 1),       // 6E: tick 0,00005
            new Tick(T0.AddSeconds(3), 0.00000001m, 1),
        };
        store.InsertTicks("X", ticks, MarketDataStore.SourceBroker);
        store.InsertBars("X", [new Bar(T0, 1.08765m, 1.0877m, 1.08755m, 1.0876m, 5)], MarketDataStore.SourceBroker);

        Assert.Equal(ticks, store.ReadTicks("X", T0, T0.AddMinutes(1)).ToList());
        Assert.Equal(new Bar(T0, 1.08765m, 1.0877m, 1.08755m, 1.0876m, 5), store.ReadBars("X", T0, T0.AddMinutes(1)).Single());
    }

    [Fact]
    public void Migracja_StaraBazaZCenamiRazy10000_OdczytBezZmian()
    {
        // Baza ze starej wersji: ceny × 10 000, user_version = 0.
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE ticks (symbol TEXT NOT NULL, time INTEGER NOT NULL, price INTEGER NOT NULL, volume INTEGER NOT NULL, source TEXT NOT NULL);
                CREATE TABLE bars (symbol TEXT NOT NULL, time INTEGER NOT NULL, open INTEGER NOT NULL, high INTEGER NOT NULL,
                                   low INTEGER NOT NULL, close INTEGER NOT NULL, volume INTEGER NOT NULL, source TEXT NOT NULL,
                                   PRIMARY KEY (symbol, time));
                INSERT INTO ticks VALUES ('ES', {T0.ToUnixTimeMilliseconds()}, 76112500, 2, 'IB');
                INSERT INTO bars VALUES ('ES', {T0.ToUnixTimeMilliseconds()}, 76110000, 76120000, 76100000, 76112500, 9, 'IB');
                """;
            command.ExecuteNonQuery();
        }

        var store = new MarketDataStore(_dbPath);
        Assert.Equal(new Tick(T0, 7611.25m, 2), store.ReadTicks("ES", T0, T0.AddMinutes(1)).Single());
        Assert.Equal(new Bar(T0, 7611m, 7612m, 7610m, 7611.25m, 9), store.ReadBars("ES", T0, T0.AddMinutes(1)).Single());

        // Ponowne otwarcie nie przelicza drugi raz.
        Assert.Equal(7611.25m, new MarketDataStore(_dbPath).ReadTicks("ES", T0, T0.AddMinutes(1)).Single().Price);
    }

    // ----- Odtwarzanie: prawdziwe ticki albo całe świece, bez wymyślania -----

    [Fact]
    public void Merge_TickiPrzedSwiecaIPoNiej_PosortowanePoCzasieZdarzenia()
    {
        var ticks = new[] { new Tick(T0.AddSeconds(30), 1m, 1), new Tick(T0.AddMinutes(2).AddSeconds(5), 3m, 1) };
        var bars = new[] { new Bar(T0.AddMinutes(1), 2m, 2m, 2m, 2m, 1) };

        var items = BarAggregator.Merge(ticks, bars).ToList();

        Assert.Equal([T0.AddSeconds(30), T0.AddMinutes(2), T0.AddMinutes(2).AddSeconds(5)], items.Select(i => i.Time));
        Assert.Equal([true, false, true], items.Select(i => i.IsTick));
    }

    [Fact]
    public async Task Odtwarzanie_ZglaszaSwieceIZmianeRodzajuDanych()
    {
        var store = new MarketDataStore(_dbPath);
        store.InsertBars("ES", [new Bar(T0, 7600m, 7601m, 7599m, 7600.5m, 10), new Bar(T0.AddMinutes(1), 7601m, 7601m, 7601m, 7601m, 1)],
            MarketDataStore.SourceBroker);
        store.InsertTicks("ES", [new Tick(T0.AddMinutes(1).AddSeconds(1), 7601m, 1)], MarketDataStore.SourceBroker);

        await using var feed = new DatabaseReplayFeed(store, T0, T0.AddMinutes(2), 3600, TimeZoneInfo.Utc);
        var bars = new List<Bar>();
        var ticks = new List<Tick>();
        var kinds = new List<bool>();
        var done = new TaskCompletionSource();
        feed.BarReceived += (_, b) => bars.Add(b);
        feed.TickReceived += (_, t) => ticks.Add(t);
        feed.DataKindChanged += (_, k) => kinds.Add(k);
        feed.Completed += (_, _) => done.TrySetResult();

        await feed.ConnectAsync(Instrument.ES);
        await feed.StartLiveAsync();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(T0, Assert.Single(bars).Time);            // minuta bez ticków – cała świeca
        Assert.Equal(7601m, Assert.Single(ticks).Price);       // minuta z tickami – tylko prawdziwy tick
        Assert.Equal([false, true], kinds);                    // najpierw świece, potem ticki
        Assert.True(feed.IsReplayingTicks);
    }

    // ----- Bieżący profil z ticków, wcześniejsze ze świec -----

    [Fact]
    public void CurrentSessionStart_DziennaNocnaIPozaSesja()
    {
        var settings = new ProfileSettings(); // Nocna 18:00–9:30, Dzienna 9:30–16:00 (Nowy Jork)
        var ny = settings.TimeZone;
        DateTimeOffset At(int day, int hour, int minute) =>
            new(new DateTime(2026, 11, day, hour, minute, 0), ny.GetUtcOffset(new DateTime(2026, 11, day, hour, minute, 0)));

        Assert.Equal(At(10, 9, 30), SessionBoundaries.CurrentSessionStart(settings, At(10, 14, 0)));
        Assert.Equal(At(9, 18, 0), SessionBoundaries.CurrentSessionStart(settings, At(10, 2, 15)));   // nocna po północy
        Assert.Equal(At(10, 9, 30), SessionBoundaries.CurrentSessionStart(settings, At(10, 9, 30)));  // dokładnie otwarcie
        Assert.Null(SessionBoundaries.CurrentSessionStart(settings, At(10, 17, 0)));                // przerwa 16:00–18:00
    }

    [Fact]
    public async Task Symulacja_HistoriaDoPoczatkuProfilu_BiezacyProfilZTickowOdRazu()
    {
        var store = new MarketDataStore(_dbPath);
        var sessionStart = T0;                 // początek bieżącego profilu
        var start = T0.AddMinutes(30);         // start symulacji w środku profilu

        // Świece: jedna sprzed profilu (historia) i świece w profilu; ticki: w profilu przed startem i po starcie.
        store.InsertBars("ES",
        [
            new Bar(T0.AddMinutes(-10), 7590m, 7591m, 7589m, 7590m, 5),
            new Bar(T0.AddMinutes(5), 7600m, 7600m, 7600m, 7600m, 1),
            new Bar(T0.AddMinutes(40), 7610m, 7610m, 7610m, 7610m, 1),
        ], MarketDataStore.SourceBroker);
        store.InsertTicks("ES",
        [
            new Tick(T0.AddMinutes(5).AddSeconds(1), 7600m, 1),
            new Tick(T0.AddMinutes(40).AddSeconds(1), 7610m, 1),
        ], MarketDataStore.SourceBroker);

        await using var feed = new DatabaseReplayFeed(store, start, T0.AddHours(1), 1, TimeZoneInfo.Utc, sessionStart);
        var ticks = new List<Tick>();
        var first = new TaskCompletionSource();
        feed.TickReceived += (_, t) => { lock (ticks) { ticks.Add(t); } first.TrySetResult(); };
        feed.Pause();

        await feed.ConnectAsync(Instrument.ES);
        var history = await feed.GetHistoryAsync(TimeSpan.FromHours(1));
        Assert.Equal([T0.AddMinutes(-10)], history.Select(b => b.Time)); // historia kończy się przed początkiem profilu

        await feed.StartLiveAsync();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        // W pauzie: oddany od razu tylko tick z początku profilu (przed startem), tick po starcie czeka na odtwarzanie.
        lock (ticks)
        {
            Assert.Equal([7600m], ticks.Select(t => t.Price));
        }
    }

    // ----- Płynne cofanie: profil do okresu -----

    [Fact]
    public void CreateSnapshots_DoOkresu_TakJakPoZbudowaniuTylkoTychOkresow()
    {
        var settings = new ProfileSettings(); // sesje i strefa Nowego Jorku
        var full = new ProfileBuilder(settings);
        var partial = new ProfileBuilder(settings);

        // Ticki co 10 minut przez 3 godziny od 10:00 w Nowym Jorku (sesja dzienna).
        var ticks = Enumerable.Range(0, 18).Select(i => new Tick(T0.AddMinutes(i * 10), 7600m + i, 1)).ToList();
        foreach (var t in ticks)
        {
            full.ProcessTick(t);
        }

        var profile = Assert.Single(full.Profiles);
        var cutoffPeriod = profile.GetPeriodIndex(TimeZoneInfo.ConvertTime(T0.AddMinutes(90), settings.TimeZone).DateTime);
        foreach (var t in ticks.Where(t => profile.GetPeriodIndex(TimeZoneInfo.ConvertTime(t.Time, settings.TimeZone).DateTime) <= cutoffPeriod))
        {
            partial.ProcessTick(t);
        }

        var cut = new ProfileSplits().CreateSnapshots(profile, cutoffPeriod).Single();
        var expected = Assert.Single(partial.Profiles).GetSnapshot();

        Assert.Equal(expected.High, cut.High);
        Assert.Equal(expected.Low, cut.Low);
        Assert.Equal(expected.Poc, cut.Poc);
        Assert.Equal(expected.TotalTpo, cut.TotalTpo);
        Assert.True(cut.TotalTpo < profile.GetSnapshot().TotalTpo);
    }

    // ----- Kroki płynnego przewijania -----

    [Fact]
    public void TimeSteps_WPrzod_CoOkres_OstatniDokladnieCel()
    {
        var steps = TimeSteps.Between(T0, T0.AddMinutes(95), TimeSpan.FromMinutes(30), 40);
        Assert.Equal([T0.AddMinutes(30), T0.AddMinutes(60), T0.AddMinutes(90), T0.AddMinutes(95)], steps);
    }

    [Fact]
    public void TimeSteps_Wstecz_CoOkres()
    {
        var steps = TimeSteps.Between(T0, T0.AddMinutes(-60), TimeSpan.FromMinutes(30), 40);
        Assert.Equal([T0.AddMinutes(-30), T0.AddMinutes(-60)], steps);
    }

    [Fact]
    public void TimeSteps_DlugiOdcinek_NieWiecejNizLimitKlatek()
    {
        var steps = TimeSteps.Between(T0, T0.AddDays(5), TimeSpan.FromMinutes(30), 40);
        Assert.InRange(steps.Count, 1, 41);
        Assert.Equal(T0.AddDays(5), steps[^1]);
        Assert.Equal(steps.Order(), steps);
    }

    [Fact]
    public void TimeSteps_TenSamCzas_Pusto() => Assert.Empty(TimeSteps.Between(T0, T0, TimeSpan.FromMinutes(30), 40));
}
