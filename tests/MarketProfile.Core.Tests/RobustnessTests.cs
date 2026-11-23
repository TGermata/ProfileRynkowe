using MarketProfile.DataFeed.IBWeb;
using MarketProfile.DataFeed.Storage;
using Microsoft.Data.Sqlite;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Odporność na błędy: rejestrator przy błędzie zapisu do bazy, odtwarzanie z wyjątkiem w obsłudze ticka,
/// reguła ticka przy doczytywaniu starszej historii, kilku subskrybentów notowań IBKR Web API.
/// </summary>
public sealed class RobustnessTests : IDisposable
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero); // 10:00 w Nowym Jorku
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mp-robust-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    // ----- Rejestrator -----

    [Fact]
    public async Task Recorder_BladZapisu_TickiNieGinaIZapisujaSieZaNastepnymRazem()
    {
        var failing = true;
        var stored = new List<Tick>();
        var recorder = new MarketDataRecorder(ticks =>
        {
            if (failing)
            {
                throw new IOException("baza zablokowana");
            }

            stored.AddRange(ticks);
        }, "ES", MarketDataStore.SourceBroker);

        recorder.Record(new Tick(T0, 100m, 1));
        recorder.Record(new Tick(T0.AddSeconds(1), 101m, 2));
        Assert.Throws<IOException>(recorder.Flush);
        Assert.Equal(2, recorder.PendingTicks);
        Assert.Equal(0, recorder.RecordedTicks);

        failing = false;
        recorder.Record(new Tick(T0.AddSeconds(2), 102m, 3));
        await recorder.DisposeAsync(); // ostatni zapis przy zatrzymaniu

        Assert.Equal([100m, 101m, 102m], stored.Select(t => t.Price));
        Assert.Equal(3, recorder.RecordedTicks);
        Assert.Equal(0, recorder.PendingTicks);
    }

    [Fact]
    public async Task Recorder_StalyBladZapisu_ZglaszaRazIDisposeNieRzuca()
    {
        var failures = 0;
        var recorder = new MarketDataRecorder(_ => throw new IOException("brak miejsca"), "ES", MarketDataStore.SourceBroker);
        recorder.WriteFailed += _ => Interlocked.Increment(ref failures);

        recorder.Record(new Tick(T0, 100m, 1));
        await Task.Delay(2500); // pętla zapisu próbuje co sekundę
        await recorder.DisposeAsync();

        Assert.Equal(1, failures); // jeden komunikat na serię błędów, nie co sekundę
        Assert.Equal(1, recorder.PendingTicks);
    }

    // ----- Odtwarzanie -----

    [Fact]
    public async Task Replay_WyjatekWObsludzeTicka_ZglaszaFailedARozlaczenieNieRzuca()
    {
        var store = new MarketDataStore(_dbPath);
        store.InsertTicks("ES", Enumerable.Range(0, 10).Select(m => new Tick(T0.AddMinutes(m), 6000m + m, 1)),
            MarketDataStore.SourceBroker);

        var feed = new DatabaseReplayFeed(store, T0, T0.AddMinutes(11), speed: 3600, TimeZoneInfo.Utc);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.TickReceived += (_, _) => throw new InvalidOperationException("błąd obsługi");
        feed.Failed += (_, ex) => failed.TrySetResult(ex);

        await feed.ConnectAsync(Instrument.ES);
        await feed.StartLiveAsync();
        var finished = await Task.WhenAny(failed.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(failed.Task, finished);
        Assert.Equal("błąd obsługi", (await failed.Task).Message);
        await feed.DisposeAsync(); // nie rzuca starego wyjątku pętli
    }

    // ----- Reguła ticka -----

    private static DateTimeOffset NewYorkTime(int day, int hour, int minute)
    {
        var local = new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local));
    }

    [Fact]
    public void ProfileBuilder_StaraSwiecaPoTickach_NiePrzestawiaRegulyTicka()
    {
        var builder = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4 });
        builder.ProcessTick(new Tick(NewYorkTime(12, 9, 40), 101m, 10, TradeSide.Buy));

        // Doczytana starsza historia (dzień wcześniej, znacznie wyżej) – nie może być punktem odniesienia dla ticków na żywo.
        builder.ProcessBar(new Bar(NewYorkTime(11, 10, 0), 110m, 111m, 109m, 110m, 50));

        // Tick bez strony po tej samej cenie co poprzedni → ta sama strona (kupno), a nie sprzedaż (101 < 110).
        builder.ProcessTick(new Tick(NewYorkTime(12, 9, 41), 101m, 5));

        var today = builder.Profiles.Where(p => p.Session.Name == "Dzienna").MaxBy(p => p.SessionStart)!.GetSnapshot();
        Assert.Equal(15, today.TotalDelta);
    }

    // ----- IBKR Web API: kilku subskrybentów -----

    [Fact]
    public void IbWebStream_DrugiSubskrybentTegoSamegoInstrumentu_NieZabieraNotowanPierwszemu()
    {
        var sent = new List<string>();
        var stream = new IbWebMarketStream(text =>
        {
            lock (sent)
            {
                sent.Add(text);
            }

            return Task.CompletedTask;
        });

        var main = 0;
        var test = 0;
        using var mainSubscription = stream.Subscribe(123, _ => main++);
        var testSubscription = stream.Subscribe(123, _ => test++);
        const string message = """{"topic":"smd+123","conid":123,"31":"100.5"}""";

        stream.Dispatch(message);
        testSubscription.Dispose(); // koniec testu połączenia w ustawieniach
        stream.Dispatch(message);

        Assert.Equal(2, main);
        Assert.Equal(1, test);
        Assert.Equal(1, stream.SubscriberCount(123));
        lock (sent)
        {
            Assert.Single(sent, s => s.StartsWith("smd+123", StringComparison.Ordinal)); // jedna subskrypcja w IBKR
            Assert.DoesNotContain(sent, s => s.StartsWith("umd+123", StringComparison.Ordinal)); // główny dalej subskrybuje
        }
    }

    [Fact]
    public void IbWebStream_OstatniSubskrybentOdchodzi_RezygnacjaWIbkr()
    {
        var sent = new List<string>();
        var stream = new IbWebMarketStream(text =>
        {
            lock (sent)
            {
                sent.Add(text);
            }

            return Task.CompletedTask;
        });

        var first = stream.Subscribe(7, _ => { });
        var second = stream.Subscribe(7, _ => { });
        first.Dispose();
        second.Dispose();
        second.Dispose(); // ponowne zwolnienie – bez skutków

        Assert.Equal(0, stream.SubscriberCount(7));
        lock (sent)
        {
            Assert.Single(sent, s => s.StartsWith("umd+7", StringComparison.Ordinal));
        }
    }
}
