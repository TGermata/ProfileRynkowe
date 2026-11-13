using System.Diagnostics;
using MarketProfile.Core;

namespace MarketProfile.DataFeed.Storage;

/// <summary>
/// Symulacja: odtwarza zapisane dane z lokalnej bazy tak, jakby przychodziły na żywo.
///
///  - historia (<see cref="GetHistoryAsync"/>) – dane sprzed początku symulacji, ładowane od razu,
///  - odtwarzanie (<see cref="StartLiveAsync"/>) – ticki od <see cref="From"/> do <see cref="To"/>.
///
/// Sterowanie w trakcie odtwarzania (jak w odtwarzaczu):
///  - <see cref="Pause"/> / <see cref="Resume"/> – zatrzymanie i wznowienie zegara symulacji,
///  - <see cref="Speed"/> – prędkość, którą można zmieniać w dowolnym momencie,
///  - <see cref="Step"/> – natychmiastowe przesunięcie czasu (np. o jeden okres TPO), także w pauzie.
///
/// Długie przerwy bez danych (noc między sesjami, weekend) są przeskakiwane, żeby nie czekać na nic.
/// </summary>
public sealed class DatabaseReplayFeed : IMarketDataFeed
{
    /// <summary>Przerwa w danych dłuższa niż ta wartość (w czasie symulacji) jest przeskakiwana.</summary>
    private static readonly TimeSpan GapToSkip = TimeSpan.FromMinutes(10);

    /// <summary>Jak często pętla odtwarzania sprawdza zegar (w czasie rzeczywistym).</summary>
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(20);

    private readonly MarketDataStore _store;
    private readonly TimeZoneInfo _displayZone;
    private string? _symbol;
    private CancellationTokenSource? _liveCts;
    private Task? _liveTask;

    private volatile bool _paused;
    private long _speedBits;          // prędkość (double) zapisana jako bity – bezpieczny odczyt/zapis z różnych wątków
    private long _pendingStepTicks;   // zlecone przesunięcie czasu (w tickach .NET), wykonywane przez pętlę
    private long _currentTimeTicks;   // bieżący czas symulacji (UTC, w tickach .NET)

    /// <param name="store">Baza danych.</param>
    /// <param name="from">Początek odtwarzania.</param>
    /// <param name="to">Koniec odtwarzania.</param>
    /// <param name="speed">Przyspieszenie (60 = 1 s rzeczywista to 1 minuta danych).</param>
    /// <param name="displayZone">Strefa czasowa, w której pokazujemy czas odtwarzania w komunikatach.</param>
    public DatabaseReplayFeed(MarketDataStore store, DateTimeOffset from, DateTimeOffset to, double speed, TimeZoneInfo displayZone)
    {
        _store = store;
        From = from;
        To = to;
        Speed = speed;
        _displayZone = displayZone;
        _currentTimeTicks = from.UtcTicks;
    }

    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }

    /// <summary>Prędkość odtwarzania – można ją zmieniać w trakcie (działa od razu).</summary>
    public double Speed
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _speedBits));
        set => Interlocked.Exchange(ref _speedBits, BitConverter.DoubleToInt64Bits(Math.Max(0.1, value)));
    }

    /// <summary>Czy odtwarzanie jest wstrzymane.</summary>
    public bool IsPaused => _paused;

    /// <summary>Bieżący czas symulacji.</summary>
    public DateTimeOffset CurrentTime => new(Interlocked.Read(ref _currentTimeTicks), TimeSpan.Zero);

    public string Name => "Symulacja (baza danych)";

    public event EventHandler<Tick>? TickReceived;
    public event EventHandler<string>? StatusChanged;

    /// <summary>Zdarzenie z bieżącym czasem symulacji (ok. 10 razy na sekundę) – do wyświetlenia zegara.</summary>
    public event EventHandler<DateTimeOffset>? TimeChanged;

    /// <summary>Zdarzenie wywoływane po odtworzeniu wszystkich danych z zakresu.</summary>
    public event EventHandler? Completed;

    /// <summary>Wstrzymuje odtwarzanie (zegar symulacji stoi).</summary>
    public void Pause() => _paused = true;

    /// <summary>Wznawia odtwarzanie.</summary>
    public void Resume() => _paused = false;

    /// <summary>
    /// Przesuwa czas symulacji o podany odcinek od razu (działa także w pauzie) –
    /// wszystkie ticki z tego odcinka zostaną natychmiast przekazane dalej.
    /// </summary>
    public void Step(TimeSpan span) => Interlocked.Add(ref _pendingStepTicks, Math.Max(0, span.Ticks));

    public Task ConnectAsync(Instrument instrument, CancellationToken cancellationToken = default)
    {
        _symbol = instrument.Symbol;

        // Sprawdzamy, czy w bazie są dane do odtworzenia – jeśli nie, podpowiadamy dostępny zakres.
        if (_store.CountTicks(_symbol, From, To) == 0 && !_store.ReadBars(_symbol, From, To).Any())
        {
            var range = _store.GetDataRange(_symbol);
            var available = range is { } r
                ? $"Dostępne dane: {Format(r.From)} – {Format(r.To)}."
                : "Baza nie zawiera jeszcze danych tego instrumentu – wygeneruj dane testowe w ustawieniach.";
            throw new InvalidOperationException($"Brak danych {_symbol} od {Format(From)}. {available}");
        }

        StatusChanged?.Invoke(this, $"Symulacja {_symbol} od {Format(From)}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Bar>> GetHistoryAsync(TimeSpan lookback, CancellationToken cancellationToken = default)
    {
        var symbol = EnsureConnected();
        var bars = BarAggregator.LoadMinuteBars(_store, symbol, From - lookback, From);
        StatusChanged?.Invoke(this, $"Symulacja: wczytano {bars.Count} świec historii");
        return Task.FromResult(bars);
    }

    /// <summary>Starsza historia z bazy (doczytywana przy przesuwaniu wykresu w lewo).</summary>
    public Task<IReadOnlyList<Bar>> GetHistoryRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var symbol = EnsureConnected();
        return Task.FromResult(BarAggregator.LoadMinuteBars(_store, symbol, from, to));
    }

    public Task StartLiveAsync(CancellationToken cancellationToken = default)
    {
        var symbol = EnsureConnected();
        _liveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _liveCts.Token;
        _liveTask = Task.Run(() => ReplayAsync(symbol, token), token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pętla odtwarzania. Co ~20 ms przesuwa zegar symulacji o (upływ czasu rzeczywistego × prędkość)
    /// – chyba że jest pauza – oraz o zlecone kroki, a następnie wysyła wszystkie ticki do bieżącego czasu.
    /// </summary>
    private async Task ReplayAsync(string symbol, CancellationToken token)
    {
        // Jeśli w zakresie są ticki – odtwarzamy ticki; jeśli tylko świece – zamieniamy je na ticki.
        var source = _store.CountTicks(symbol, From, To) > 0
            ? _store.ReadTicks(symbol, From, To)
            : _store.ReadBars(symbol, From, To).SelectMany(BarAggregator.ToTicks);

        using var ticks = source.GetEnumerator();
        var hasTick = ticks.MoveNext();

        var simNow = From;
        var clock = Stopwatch.StartNew();
        var lastElapsed = TimeSpan.Zero;
        var lastNotify = TimeSpan.Zero;

        while (hasTick)
        {
            token.ThrowIfCancellationRequested();

            // 1. Upływ czasu: zegar symulacji idzie tylko, gdy nie ma pauzy.
            var elapsed = clock.Elapsed;
            if (!_paused)
            {
                simNow += (elapsed - lastElapsed) * Speed;
            }

            lastElapsed = elapsed;

            // 2. Krok zlecony przez użytkownika (np. +1 okres TPO). Jeśli trafia w przerwę w danych,
            //    najpierw przeskakujemy przerwę, żeby krok nie był "pusty".
            var step = Interlocked.Exchange(ref _pendingStepTicks, 0);
            if (step > 0)
            {
                if (ticks.Current.Time - simNow > GapToSkip)
                {
                    simNow = ticks.Current.Time;
                }

                simNow += TimeSpan.FromTicks(step);
            }

            // 3. Długa przerwa w danych (noc, weekend) – przeskakujemy ją od razu.
            if (!_paused && ticks.Current.Time - simNow > GapToSkip)
            {
                simNow = ticks.Current.Time;
                StatusChanged?.Invoke(this, $"Symulacja: przeskok przerwy do {Format(simNow)}");
            }

            // 4. Wysyłamy wszystkie ticki, których czas już minął.
            while (hasTick && ticks.Current.Time <= simNow)
            {
                TickReceived?.Invoke(this, ticks.Current);
                hasTick = ticks.MoveNext();
            }

            // 5. Zegar dla widoku – ok. 10 razy na sekundę.
            Interlocked.Exchange(ref _currentTimeTicks, simNow.UtcTicks);
            if (elapsed - lastNotify >= TimeSpan.FromMilliseconds(100))
            {
                lastNotify = elapsed;
                TimeChanged?.Invoke(this, simNow);
            }

            await Task.Delay(LoopInterval, token).ConfigureAwait(false);
        }

        TimeChanged?.Invoke(this, CurrentTime);
        StatusChanged?.Invoke(this, $"Symulacja zakończona – koniec danych ({Format(CurrentTime)})");
        Completed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync()
    {
        if (_liveCts is not null)
        {
            await _liveCts.CancelAsync().ConfigureAwait(false);

            try
            {
                if (_liveTask is not null)
                {
                    await _liveTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Oczekiwane przy zatrzymaniu.
            }

            _liveCts.Dispose();
            _liveCts = null;
            _liveTask = null;
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    /// <summary>Czas w strefie giełdy, np. "2026-11-10 09:30".</summary>
    private string Format(DateTimeOffset time) =>
        TimeZoneInfo.ConvertTime(time, _displayZone).ToString("yyyy-MM-dd HH:mm");

    private string EnsureConnected() =>
        _symbol ?? throw new InvalidOperationException("Najpierw wywołaj ConnectAsync.");
}
