using MarketProfile.Core;
using MarketProfile.DataFeed.Storage;
using Microsoft.Data.Sqlite;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Sterowanie symulacją: czas końca (<see cref="DatabaseReplayFeed.StopAt"/>) i ostatnie dane przed momentem
/// (krok o słupek wstecz z przeskokiem przerwy w handlu).
/// </summary>
public sealed class SimulationControlTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero); // 10:00 w Nowym Jorku
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mp-test-{Guid.NewGuid():N}.db");
    private readonly MarketDataStore _store;

    public SimulationControlTests() => _store = new MarketDataStore(_dbPath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    /// <summary>Ticki co minutę w podanym zakresie minut od T0 (cena = numer minuty).</summary>
    private void InsertTicks(int fromMinute, int toMinute) =>
        _store.InsertTicks("ES", Enumerable.Range(fromMinute, toMinute - fromMinute + 1)
            .Select(m => new Tick(T0.AddMinutes(m), 6000m + m, 1)), MarketDataStore.SourceBroker);

    private static async Task WaitAsync(Task task) =>
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));

    [Fact]
    public async Task StopAt_ZatrzymujeDokladnieNaCzasieKonca_BezTickowPoNim()
    {
        InsertTicks(0, 60);
        var stop = T0.AddMinutes(20);
        await using var feed = new DatabaseReplayFeed(_store, T0, T0.AddMinutes(61), speed: 3600, TimeZoneInfo.Utc) { StopAt = stop };
        var received = new List<Tick>();
        var reached = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.TickReceived += (_, t) => { lock (received) { received.Add(t); } };
        feed.StopReached += (_, at) => reached.TrySetResult(at);

        await feed.ConnectAsync(Instrument.ES);
        await feed.StartLiveAsync();
        await WaitAsync(reached.Task);
        await Task.Delay(200); // pętla dalej działa – zegar ma stać

        Assert.Equal(stop, await reached.Task);
        Assert.True(feed.IsPaused);
        Assert.Equal(stop, feed.CurrentTime);
        lock (received)
        {
            Assert.Equal(21, received.Count); // minuty 0..20 (tick dokładnie w czasie końca też)
            Assert.All(received, t => Assert.True(t.Time <= stop));
        }

        // Ręczny krok działa także poza czasem końca.
        feed.Step(TimeSpan.FromMinutes(10));
        await Task.Delay(300);
        Assert.Equal(stop.AddMinutes(10), feed.CurrentTime);
        lock (received)
        {
            Assert.Equal(31, received.Count);
        }
    }

    [Fact]
    public async Task StopAt_WPrzerwieWDanych_PrzeskokPrzerwyZatrzymujeSieNaCzasieKonca()
    {
        InsertTicks(0, 10);
        InsertTicks(120, 130); // 2 h przerwy (np. między sesjami)
        var stop = T0.AddMinutes(60);
        await using var feed = new DatabaseReplayFeed(_store, T0, T0.AddMinutes(131), speed: 60, TimeZoneInfo.Utc) { StopAt = stop };
        var reached = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.StopReached += (_, at) => reached.TrySetResult(at);

        await feed.ConnectAsync(Instrument.ES);
        feed.SeekTo(T0.AddMinutes(10)); // od razu na koniec pierwszego odcinka – dalej samoczynny przeskok przerwy
        await feed.StartLiveAsync();
        await WaitAsync(reached.Task);

        Assert.Equal(stop, feed.CurrentTime);
        Assert.True(feed.IsPaused);
    }

    [Fact]
    public async Task BezStopAt_OdtwarzaDoKonca()
    {
        InsertTicks(0, 30);
        await using var feed = new DatabaseReplayFeed(_store, T0, T0.AddMinutes(31), speed: 3600, TimeZoneInfo.Utc);
        var count = 0;
        var stopped = false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.TickReceived += (_, _) => Interlocked.Increment(ref count);
        feed.StopReached += (_, _) => stopped = true;
        feed.Completed += (_, _) => done.TrySetResult();

        await feed.ConnectAsync(Instrument.ES);
        await feed.StartLiveAsync();
        await WaitAsync(done.Task);

        Assert.Equal(31, count);
        Assert.False(stopped);
    }

    [Fact]
    public void LastDataTimeBefore_OstatniTickLubSwiecaPrzedMomentem()
    {
        Assert.Null(_store.LastDataTimeBefore("ES", T0.AddDays(1)));

        _store.InsertTicks("ES", [new Tick(T0.AddSeconds(30), 6000m, 1)], MarketDataStore.SourceBroker);
        _store.InsertBars("ES", [new Bar(T0.AddMinutes(5), 6000m, 6001m, 5999m, 6000m, 3)], MarketDataStore.SourceBroker);

        Assert.Equal(T0.AddMinutes(5), _store.LastDataTimeBefore("ES", T0.AddHours(3)));            // świeca późniejsza od ticka
        Assert.Equal(T0.AddSeconds(30), _store.LastDataTimeBefore("ES", T0.AddMinutes(5)));         // ściśle przed
        Assert.Null(_store.LastDataTimeBefore("ES", T0.AddSeconds(30)));                            // przed początkiem danych
        Assert.Null(_store.LastDataTimeBefore("ES", T0.AddHours(3), MarketDataStore.SourceTest));   // inne źródło
    }
}
