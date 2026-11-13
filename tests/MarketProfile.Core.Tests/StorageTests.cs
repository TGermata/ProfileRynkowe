using MarketProfile.Core;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Testy lokalnej bazy danych, agregacji ticków w świece, rejestratora i odtwarzania z bazy.
/// Każdy test działa na osobnym, tymczasowym pliku bazy, który jest usuwany po teście.
/// </summary>
public sealed class StorageTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero); // 10:00 w Nowym Jorku
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mp-test-{Guid.NewGuid():N}.db");
    private readonly MarketDataStore _store;

    public StorageTests() => _store = new MarketDataStore(_dbPath);

    public void Dispose()
    {
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Baza_ZapisujeIOdczytujeTickiISwiece()
    {
        _store.InsertTicks("ES", [new Tick(T0, 6000.25m, 3), new Tick(T0.AddSeconds(5), 6001.75m, 2)], MarketDataStore.SourceBroker);
        _store.InsertTicks("NQ", [new Tick(T0, 21000m, 1)], MarketDataStore.SourceBroker);
        _store.InsertBars("ES", [new Bar(T0.AddMinutes(-1), 5999m, 6000m, 5998.5m, 5999.75m, 40)], MarketDataStore.SourceBroker);

        var ticks = _store.ReadTicks("ES", T0, T0.AddMinutes(1)).ToList();
        Assert.Equal(2, ticks.Count);
        Assert.Equal(6001.75m, ticks[1].Price); // cena zapisana dokładnie
        Assert.Equal(T0.AddSeconds(5), ticks[1].Time);

        var bar = Assert.Single(_store.ReadBars("ES", T0.AddMinutes(-5), T0));
        Assert.Equal(5998.5m, bar.Low);

        var range = _store.GetDataRange("ES");
        Assert.NotNull(range);
        Assert.Equal(T0.AddMinutes(-1), range.Value.From);
        Assert.Equal(2, range.Value.Ticks);
        Assert.Equal(1, range.Value.Bars);
    }

    [Fact]
    public void UsuniecieDanychTestowych_NieRuszaDanychBrokera()
    {
        _store.InsertTicks("ES", [new Tick(T0, 6000m, 1)], MarketDataStore.SourceBroker);
        _store.InsertTicks("ES", [new Tick(T0.AddSeconds(1), 6001m, 1)], MarketDataStore.SourceTest);

        _store.DeleteSource("ES", MarketDataStore.SourceTest);

        var tick = Assert.Single(_store.ReadTicks("ES", T0, T0.AddMinutes(1)));
        Assert.Equal(6000m, tick.Price);
    }

    [Fact]
    public void Agregacja_TickiWSwiece1Minutowe()
    {
        var bars = BarAggregator.ToMinuteBars(
        [
            new Tick(T0.AddSeconds(1), 100m, 1),
            new Tick(T0.AddSeconds(20), 103m, 2),
            new Tick(T0.AddSeconds(50), 99m, 3),
            new Tick(T0.AddSeconds(70), 101m, 4), // następna minuta
        ]).ToList();

        Assert.Equal(2, bars.Count);
        Assert.Equal(new Bar(T0, 100m, 103m, 99m, 99m, 6), bars[0]);
        Assert.Equal(new Bar(T0.AddMinutes(1), 101m, 101m, 101m, 101m, 4), bars[1]);
    }

    [Fact]
    public async Task Rejestrator_ZapisujeTickiPoZatrzymaniu()
    {
        await using (var recorder = new MarketDataRecorder(_store, "ES", MarketDataStore.SourceBroker))
        {
            recorder.Record(new Tick(T0, 6000m, 1));
            recorder.Record(new Tick(T0.AddSeconds(1), 6000.5m, 1));
        }

        Assert.Equal(2, _store.ReadTicks("ES", T0, T0.AddMinutes(1)).Count());
    }

    [Fact]
    public async Task Odtwarzanie_WczytujeHistorieIOdtwarzaZakresZPrzeskokiemPrzerwy()
    {
        // Historia przed początkiem odtwarzania i ticki do odtworzenia z 2-godzinną przerwą w środku.
        _store.InsertTicks("ES", [new Tick(T0.AddMinutes(-30), 5990m, 5)], MarketDataStore.SourceTest);
        _store.InsertTicks("ES",
        [
            new Tick(T0.AddSeconds(1), 6000m, 1),
            new Tick(T0.AddSeconds(30), 6001m, 1),
            new Tick(T0.AddHours(2), 6010m, 1),
        ], MarketDataStore.SourceTest);

        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        await using var feed = new DatabaseReplayFeed(_store, T0, T0.AddHours(3), speed: 600, newYork);
        var received = new List<Tick>();
        var completed = new TaskCompletionSource();
        feed.TickReceived += (_, t) => received.Add(t);
        feed.Completed += (_, _) => completed.TrySetResult();

        await feed.ConnectAsync(Instrument.ES);
        var history = await feed.GetHistoryAsync(TimeSpan.FromHours(1));
        Assert.Equal(5990m, Assert.Single(history).Close);

        await feed.StartLiveAsync();

        // Przy ×600 dwie godziny trwałyby 12 s – dzięki przeskokowi przerwy odtwarzanie kończy się od razu.
        var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(completed.Task, finished);
        Assert.Equal(new[] { 6000m, 6001m, 6010m }, received.Select(t => t.Price));
    }

    [Fact]
    public async Task Symulacja_PauzaZatrzymujeCzas_KrokPrzesuwaCzasWPauzie()
    {
        _store.InsertTicks("ES",
        [
            new Tick(T0.AddMinutes(1), 6000m, 1),
            new Tick(T0.AddMinutes(20), 6001m, 1),
            new Tick(T0.AddMinutes(40), 6002m, 1),
        ], MarketDataStore.SourceTest);

        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        await using var feed = new DatabaseReplayFeed(_store, T0, T0.AddHours(1), speed: 1, newYork);
        var received = new List<Tick>();
        feed.TickReceived += (_, t) => { lock (received) { received.Add(t); } };

        await feed.ConnectAsync(Instrument.ES);
        feed.Pause();
        await feed.StartLiveAsync();

        // W pauzie przy prędkości ×1 nic nie przychodzi.
        await Task.Delay(200);
        Assert.Empty(received);

        // Krok o 30 minut w pauzie – natychmiast przychodzą ticki z 1. i 20. minuty, ale nie z 40.
        feed.Step(TimeSpan.FromMinutes(30));
        await Task.Delay(200);
        lock (received)
        {
            Assert.Equal(new[] { 6000m, 6001m }, received.Select(t => t.Price));
        }

        Assert.Equal(T0.AddMinutes(30), feed.CurrentTime);

        // Zmiana prędkości w trakcie i wznowienie – reszta danych dochodzi szybko.
        feed.Speed = 36000;
        feed.Resume();
        await Task.Delay(500);
        lock (received)
        {
            Assert.Equal(3, received.Count);
        }
    }

    [Fact]
    public async Task Generator_StarszaHistoria_LaczySieBezSkokuCeny()
    {
        await using var feed = new MarketProfile.DataFeed.SimulatedDataFeed();
        await feed.ConnectAsync(Instrument.ES);
        var history = await feed.GetHistoryAsync(TimeSpan.FromHours(6));

        var oldestTime = history[0].Time;
        var older = await feed.GetHistoryRangeAsync(oldestTime.AddHours(-3), oldestTime);

        Assert.Equal(180, older.Count);                          // 3 godziny świec 1-minutowych
        Assert.All(older, b => Assert.True(b.Time < oldestTime));
        Assert.Equal(history[0].Open, older[^1].Close);          // łączenie bez skoku ceny

        // Kolejne doczytanie łączy się z poprzednim.
        var evenOlder = await feed.GetHistoryRangeAsync(older[0].Time.AddHours(-1), older[0].Time);
        Assert.Equal(older[0].Open, evenOlder[^1].Close);
    }

    [Fact]
    public async Task Baza_StarszaHistoria_ZwracaSwieceZZakresu()
    {
        _store.InsertTicks("ES",
        [
            new Tick(T0.AddHours(-5), 5990m, 1),
            new Tick(T0.AddHours(-2), 5995m, 1),
            new Tick(T0.AddMinutes(1), 6000m, 1),
        ], MarketDataStore.SourceTest);

        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        await using var feed = new DatabaseReplayFeed(_store, T0, T0.AddHours(1), 60, newYork);
        await feed.ConnectAsync(Instrument.ES);

        var older = await feed.GetHistoryRangeAsync(T0.AddHours(-3), T0);
        Assert.Equal(5995m, Assert.Single(older).Close);
    }

    [Fact]
    public async Task Odtwarzanie_BezDanychWZakresie_ZglaszaCzytelnyBlad()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        await using var feed = new DatabaseReplayFeed(_store, T0, T0.AddHours(1), 60, newYork);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => feed.ConnectAsync(Instrument.ES));
        Assert.Contains("Brak danych ES", error.Message);
    }
}
