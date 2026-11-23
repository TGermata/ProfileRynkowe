using MarketProfile.Core;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Testy danych do symulacji: odtwarzanie mieszanych danych (ticki + świece), przeskok do momentu,
/// granice profili (początki sesji) i wyznaczanie brakujących odcinków historii pobieranej w tle.
/// </summary>
public sealed class BackgroundDataTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero); // 10:00 w Nowym Jorku
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mp-test-{Guid.NewGuid():N}.db");
    private readonly MarketDataStore _store;

    public BackgroundDataTests() => _store = new MarketDataStore(_dbPath);

    public void Dispose()
    {
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    // ----- Odtwarzanie danych mieszanych -----

    [Fact]
    public void Replay_MinutyZTickamiZTickow_PozostaleCalymiSwiecami_BezWymyslonychCen()
    {
        // Świece: minuty 0, 1, 2 (historia od brokera). Ticki: tylko w minucie 1 (zapis na żywo).
        _store.InsertBars("ES",
        [
            new Bar(T0, 100m, 101m, 99m, 100.5m, 40),
            new Bar(T0.AddMinutes(1), 200m, 201m, 199m, 200.5m, 40),
            new Bar(T0.AddMinutes(2), 300m, 301m, 299m, 300.5m, 40),
        ], MarketDataStore.SourceBroker);
        _store.InsertTicks("ES", [new Tick(T0.AddMinutes(1).AddSeconds(10), 250m, 40)], MarketDataStore.SourceBroker);

        var items = BarAggregator.Replay(_store, "ES", T0, T0.AddMinutes(3)).ToList();

        // Świeca 0 (w chwili zamknięcia minuty) + prawdziwy tick z minuty 1 + świeca 2; świeca z minuty 1 pominięta.
        Assert.Equal(3, items.Count);
        Assert.Equal(new Bar(T0, 100m, 101m, 99m, 100.5m, 40), items[0].Bar);
        Assert.Equal(T0.AddMinutes(1), items[0].Time);
        Assert.Equal(new Tick(T0.AddMinutes(1).AddSeconds(10), 250m, 40), items[1].Tick);
        Assert.Equal(T0.AddMinutes(2), items[2].Bar!.Value.Time);
        Assert.Equal(T0.AddMinutes(3), items[2].Time);
        Assert.Equal(items.OrderBy(i => i.Time).Select(i => i.Time), items.Select(i => i.Time)); // posortowane

        // Żadnych wymyślonych ticków: każdy tick to tick z bazy.
        Assert.Single(items, i => i.IsTick);
    }

    [Fact]
    public void Replay_NiepelnaMinutaTickow_PelnaSwiecaBrokeraMaPierwszenstwo()
    {
        // Rejestrator zatrzymany w połowie minuty: 5 kontraktów z ticków, a pełna świeca brokera ma 40.
        var bar = new Bar(T0, 200m, 201m, 199m, 200.5m, 40);
        _store.InsertBars("ES", [bar], MarketDataStore.SourceBroker);
        _store.InsertTicks("ES", [new Tick(T0.AddSeconds(5), 200m, 2), new Tick(T0.AddSeconds(9), 200.25m, 3)], MarketDataStore.SourceBroker);

        var items = BarAggregator.Replay(_store, "ES", T0, T0.AddMinutes(1)).ToList();
        var loaded = BarAggregator.LoadMinuteBars(_store, "ES", T0, T0.AddMinutes(1));

        Assert.Equal(bar, Assert.Single(items).Bar);
        Assert.Equal(bar, Assert.Single(loaded));
    }

    [Fact]
    public void HasData_I_GetBarRange()
    {
        Assert.False(_store.HasData("ES", T0, T0.AddHours(1)));
        Assert.Null(_store.GetBarRange("ES", MarketDataStore.SourceBroker));

        _store.InsertBars("ES", [new Bar(T0, 1m, 1m, 1m, 1m, 1), new Bar(T0.AddMinutes(30), 1m, 1m, 1m, 1m, 1)], MarketDataStore.SourceBroker);
        _store.InsertBars("ES", [new Bar(T0.AddDays(-5), 1m, 1m, 1m, 1m, 1)], MarketDataStore.SourceTest);
        _store.InsertTicks("NQ", [new Tick(T0, 1m, 1)], MarketDataStore.SourceBroker);

        Assert.True(_store.HasData("ES", T0, T0.AddMinutes(1)));
        Assert.False(_store.HasData("ES", T0.AddMinutes(1), T0.AddMinutes(30)));
        Assert.True(_store.HasData("NQ", T0, T0.AddMinutes(1)));        // same ticki też się liczą

        var range = _store.GetBarRange("ES", MarketDataStore.SourceBroker);
        Assert.Equal((T0, T0.AddMinutes(30)), range);                 // dane testowe nie wchodzą do zakresu brokera
    }

    [Fact]
    public async Task SeekTo_WPauzie_WysylaTickiDoCeluINicPoNim()
    {
        _store.InsertTicks("ES",
        [
            new Tick(T0.AddMinutes(1), 1m, 1),
            new Tick(T0.AddMinutes(29), 2m, 1),
            new Tick(T0.AddMinutes(31), 3m, 1),
        ], MarketDataStore.SourceTest);

        await using var feed = new DatabaseReplayFeed(_store, T0, T0.AddHours(1), speed: 1, NewYork);
        var received = new List<Tick>();
        feed.TickReceived += (_, t) => { lock (received) { received.Add(t); } };

        await feed.ConnectAsync(Instrument.ES);
        feed.Pause();
        await feed.StartLiveAsync();

        feed.SeekTo(T0.AddMinutes(30));
        await Task.Delay(300);

        lock (received)
        {
            Assert.Equal(new[] { 1m, 2m }, received.Select(t => t.Price));
        }

        Assert.Equal(T0.AddMinutes(30), feed.CurrentTime);

        feed.SeekTo(T0.AddMinutes(10)); // wstecz – ignorowane
        await Task.Delay(200);
        Assert.Equal(T0.AddMinutes(30), feed.CurrentTime);
    }

    // ----- Granice profili -----

    private static ProfileSettings EsSettings => new() { TimeZone = NewYork, Sessions = Instrument.ES.Sessions };

    private static DateTimeOffset Ny(int year, int month, int day, int hour, int minute) =>
        new(new DateTime(year, month, day, hour, minute, 0), NewYork.GetUtcOffset(new DateTime(year, month, day, hour, minute, 0)));

    [Fact]
    public void NextSessionStart_ZDziennejDoNocnejIZNocnejDoDziennej()
    {
        Assert.Equal(Ny(2026, 11, 10, 18, 0), SessionBoundaries.NextSessionStart(EsSettings, Ny(2026, 11, 10, 10, 0)));
        Assert.Equal(Ny(2026, 11, 11, 9, 30), SessionBoundaries.NextSessionStart(EsSettings, Ny(2026, 11, 10, 23, 0)));
        // Dokładnie na granicy – następna granica, nie ta sama.
        Assert.Equal(Ny(2026, 11, 10, 18, 0), SessionBoundaries.NextSessionStart(EsSettings, Ny(2026, 11, 10, 9, 30)));
    }

    [Fact]
    public void PreviousSessionStart_PrzezPolnoc()
    {
        // 02:00 w nocy należy do sesji nocnej rozpoczętej poprzedniego dnia o 18:00.
        Assert.Equal(Ny(2026, 11, 9, 18, 0), SessionBoundaries.PreviousSessionStart(EsSettings, Ny(2026, 11, 10, 2, 0)));
        // Na samym początku sesji – ta sama granica.
        Assert.Equal(Ny(2026, 11, 10, 9, 30), SessionBoundaries.PreviousSessionStart(EsSettings, Ny(2026, 11, 10, 9, 30)));
    }

    [Fact]
    public void Granice_PrzyZmianieCzasu()
    {
        // 1 listopada 2026 – koniec czasu letniego w USA (UTC-4 → UTC-5).
        var next = SessionBoundaries.NextSessionStart(EsSettings, Ny(2026, 10, 31, 20, 0));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 14, 30, 0, TimeSpan.Zero), next); // 09:30 EST
    }

    [Fact]
    public void Granice_InstrumentBezSesjiNocnej()
    {
        var spx = Instrument.BuiltIn.Single(i => i.Symbol == "SPX");
        var settings = new ProfileSettings { TimeZone = NewYork, Sessions = spx.Sessions };
        Assert.Equal(Ny(2026, 11, 11, 9, 30), SessionBoundaries.NextSessionStart(settings, Ny(2026, 11, 10, 12, 0)));
    }

    // ----- Brakujące odcinki historii -----

    [Fact]
    public void HistoryGaps_PustaBaza_CaleNDni()
    {
        var gap = Assert.Single(HistoryGaps.Missing(null, T0, 20));
        Assert.Equal(new TimeRange(T0.AddDays(-20), T0), gap);
    }

    [Fact]
    public void HistoryGaps_TylkoBrakujaceKonceZakresu()
    {
        var existing = (T0.AddDays(-5), T0.AddDays(-1));
        var gaps = HistoryGaps.Missing(existing, T0, 20);

        Assert.Equal(2, gaps.Count);
        Assert.Contains(new TimeRange(T0.AddDays(-1).AddMinutes(1), T0), gaps); // nowsze
        Assert.Contains(new TimeRange(T0.AddDays(-20), T0.AddDays(-5)), gaps);  // starsze
    }

    [Fact]
    public void HistoryGaps_KompletneDane_NicDoPobrania()
    {
        var existing = (T0.AddDays(-20).AddHours(6), T0.AddMinutes(-2));
        Assert.Empty(HistoryGaps.Missing(existing, T0, 20));
    }
}
