using System.Diagnostics;
using MarketProfile.Core;

namespace MarketProfile.DataFeed.Storage;

/// <summary>
/// Symulacja: odtwarza zapisane dane z lokalnej bazy tak, jakby przychodziły na żywo.
///
///  - historia (<see cref="GetHistoryAsync"/>) – świece 1-min sprzed początku bieżącego profilu (<see cref="TicksFrom"/>),
///    tak jak w trybie na żywo,
///  - odtwarzanie (<see cref="StartLiveAsync"/>) – ticki z bazy od <see cref="TicksFrom"/>: odcinek do <see cref="From"/>
///    oddawany od razu (bieżący profil powstaje w całości z ticków), dalej w tempie symulacji aż do <see cref="To"/>.
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
    private string? _source;   // źródło danych do odtworzenia (broker albo testowe – bez mieszania)
    private CancellationTokenSource? _liveCts;
    private Task? _liveTask;

    private volatile bool _paused;
    private long _speedBits;          // prędkość (double) zapisana jako bity – bezpieczny odczyt/zapis z różnych wątków
    private long _pendingStepTicks;   // zlecone przesunięcie czasu (w tickach .NET), wykonywane przez pętlę
    private long _pendingSeekTicks;   // zlecony przeskok do momentu (UTC, w tickach .NET); 0 = brak
    private long _currentTimeTicks;   // bieżący czas symulacji (UTC, w tickach .NET)
    private long _stopAtTicks;        // czas końca odtwarzania (UTC, w tickach .NET); 0 = bez ograniczeń

    /// <param name="store">Baza danych.</param>
    /// <param name="from">Początek odtwarzania.</param>
    /// <param name="to">Koniec odtwarzania.</param>
    /// <param name="speed">Przyspieszenie (60 = 1 s rzeczywista to 1 minuta danych).</param>
    /// <param name="displayZone">Strefa czasowa, w której pokazujemy czas odtwarzania w komunikatach.</param>
    /// <param name="ticksFrom">
    /// Początek profilu rysowanego w chwili <paramref name="from"/> (otwarcie sesji): od tego momentu profil budowany jest
    /// z ticków, a wcześniejsze dane – ze świec 1-min (historia). Null = od <paramref name="from"/>.
    /// </param>
    public DatabaseReplayFeed(MarketDataStore store, DateTimeOffset from, DateTimeOffset to, double speed, TimeZoneInfo displayZone,
        DateTimeOffset? ticksFrom = null)
    {
        TicksFrom = ticksFrom is { } t && t < from ? t : from;
        _store = store;
        From = from;
        To = to;
        Speed = speed;
        _displayZone = displayZone;
        _currentTimeTicks = from.UtcTicks;
    }

    public DateTimeOffset From { get; }

    /// <summary>Od tego momentu (początek bieżącego profilu) dane są odtwarzane z ticków; wcześniej – historia ze świec.</summary>
    public DateTimeOffset TicksFrom { get; }
    public DateTimeOffset To { get; }

    /// <summary>Prędkość odtwarzania – można ją zmieniać w trakcie (działa od razu).</summary>
    public double Speed
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _speedBits));
        set => Interlocked.Exchange(ref _speedBits, BitConverter.DoubleToInt64Bits(Math.Max(0.1, value)));
    }

    /// <summary>
    /// Czas końca symulacji (null = bez ograniczeń). Gdy odtwarzanie dojdzie do tego momentu, zegar zatrzymuje się
    /// dokładnie na nim i włącza się pauza (<see cref="StopReached"/>). Działa tylko na samoczynny upływ czasu –
    /// ręczne kroki i przeskoki mogą wyjść poza koniec. Można go zmieniać w trakcie odtwarzania.
    /// </summary>
    public DateTimeOffset? StopAt
    {
        get => Interlocked.Read(ref _stopAtTicks) is var t and > 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;
        set => Interlocked.Exchange(ref _stopAtTicks, value?.UtcTicks ?? 0);
    }

    /// <summary>Odtwarzanie doszło do <see cref="StopAt"/> i zostało wstrzymane (argument – czas końca).</summary>
    public event EventHandler<DateTimeOffset>? StopReached;

    /// <summary>Czy odtwarzanie jest wstrzymane.</summary>
    public bool IsPaused => _paused;

    /// <summary>Bieżący czas symulacji.</summary>
    public DateTimeOffset CurrentTime => new(Interlocked.Read(ref _currentTimeTicks), TimeSpan.Zero);

    public string Name => _source == MarketDataStore.SourceTest ? "Symulacja (dane testowe)" : "Symulacja (baza danych)";

    public event EventHandler<Tick>? TickReceived;
    public event EventHandler<string>? StatusChanged;

    /// <summary>Zdarzenie z bieżącym czasem symulacji (ok. 10 razy na sekundę) – do wyświetlenia zegara.</summary>
    public event EventHandler<DateTimeOffset>? TimeChanged;

    /// <summary>
    /// Świeca 1-min dla minuty, z której w bazie nie ma ticków – wysyłana w chwili zamknięcia minuty
    /// (zamiast wymyślania ticków; cała minuta trafia do profilu naraz).
    /// </summary>
    public event EventHandler<Bar>? BarReceived;

    /// <summary>
    /// Zmiana rodzaju odtwarzanych danych: true = prawdziwe ticki z giełdy, false = tylko świece 1-min
    /// (brak ticków w bazie dla tego okresu). Wywoływane przy pierwszym zdarzeniu i przy każdej zmianie.
    /// </summary>
    public event EventHandler<bool>? DataKindChanged;

    /// <summary>Czy ostatnio odtwarzane dane to prawdziwe ticki (null = jeszcze nic nie odtworzono).</summary>
    public bool? IsReplayingTicks { get; private set; }

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

    /// <summary>
    /// Przeskok do przodu dokładnie do podanego momentu (działa także w pauzie) – wszystkie ticki
    /// do tego momentu zostaną natychmiast przekazane dalej. Moment wcześniejszy niż bieżący czas jest ignorowany.
    /// W przeciwieństwie do <see cref="Step"/> nie przeskakuje przerw w danych, więc nie wyjdzie poza cel.
    /// </summary>
    public void SeekTo(DateTimeOffset target) => Interlocked.Exchange(ref _pendingSeekTicks, target.UtcTicks);

    public Task ConnectAsync(Instrument instrument, CancellationToken cancellationToken = default)
    {
        _symbol = instrument.Symbol;
        _source = _store.SimulationSource(_symbol);

        // Sprawdzamy, czy w bazie są dane do odtworzenia – jeśli nie, podpowiadamy dostępny zakres.
        if (_source is null || !_store.HasData(_symbol, From, To, _source))
        {
            var range = _source is null ? null : _store.GetDataRange(_symbol, _source);
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
        // Historia jak w trybie na żywo – świece 1-min, ale tylko do początku bieżącego profilu (ten powstaje z ticków).
        var bars = BarAggregator.LoadMinuteBars(_store, symbol, From - lookback, TicksFrom, _source);
        StatusChanged?.Invoke(this, $"Symulacja: wczytano {bars.Count} świec historii");
        return Task.FromResult(bars);
    }

    /// <summary>Starsza historia z bazy (doczytywana przy przesuwaniu wykresu w lewo).</summary>
    public Task<IReadOnlyList<Bar>> GetHistoryRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var symbol = EnsureConnected();
        return Task.FromResult(BarAggregator.LoadMinuteBars(_store, symbol, from, to, _source));
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
        // Minuty z zapisanymi tickami odtwarzamy tick po ticku, pozostałe (np. historia od brokera) – całymi świecami.
        // Od początku bieżącego profilu; zegar startuje od From, więc odcinek [TicksFrom, From] jest oddany od razu.
        using var items = BarAggregator.Replay(_store, symbol, TicksFrom, To, _source).GetEnumerator();
        var hasItem = items.MoveNext();

        var simNow = From;
        var clock = Stopwatch.StartNew();
        var lastElapsed = TimeSpan.Zero;
        var lastNotify = TimeSpan.Zero;

        while (hasItem)
        {
            token.ThrowIfCancellationRequested();

            // 1. Upływ czasu: zegar symulacji idzie tylko, gdy nie ma pauzy.
            var elapsed = clock.Elapsed;
            if (!_paused)
            {
                var before = simNow;
                simNow += (elapsed - lastElapsed) * Speed;
                simNow = ClampToStop(before, simNow);
            }

            lastElapsed = elapsed;

            // 2. Krok zlecony przez użytkownika (np. +1 okres TPO). Jeśli trafia w przerwę w danych,
            //    najpierw przeskakujemy przerwę, żeby krok nie był "pusty".
            var step = Interlocked.Exchange(ref _pendingStepTicks, 0);
            if (step > 0)
            {
                if (items.Current.Time - simNow > GapToSkip)
                {
                    simNow = items.Current.Time;
                }

                simNow += TimeSpan.FromTicks(step);
            }

            // 2a. Przeskok do konkretnego momentu (np. początek następnego profilu) – bez przeskakiwania przerw.
            var seek = Interlocked.Exchange(ref _pendingSeekTicks, 0);
            if (seek > simNow.UtcTicks)
            {
                simNow = new DateTimeOffset(seek, TimeSpan.Zero);
            }

            // 3. Długa przerwa w danych (noc, weekend) – przeskakujemy ją od razu.
            if (!_paused && items.Current.Time - simNow > GapToSkip)
            {
                simNow = ClampToStop(simNow, items.Current.Time); // czas końca w przerwie – zatrzymanie na nim
                if (!_paused)
                {
                    StatusChanged?.Invoke(this, $"Symulacja: przeskok przerwy do {Format(simNow)}");
                }
            }

            // 4. Wysyłamy wszystkie zdarzenia, których czas już minął.
            while (hasItem && items.Current.Time <= simNow)
            {
                var item = items.Current;
                if (IsReplayingTicks != item.IsTick)
                {
                    IsReplayingTicks = item.IsTick;
                    DataKindChanged?.Invoke(this, item.IsTick);
                }

                if (item.Tick is { } tick)
                {
                    TickReceived?.Invoke(this, tick);
                }
                else if (item.Bar is { } bar)
                {
                    BarReceived?.Invoke(this, bar);
                }

                hasItem = items.MoveNext();
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

    /// <summary>
    /// Samoczynny upływ czasu z <paramref name="from"/> do <paramref name="to"/>: jeśli po drodze jest czas końca
    /// (<see cref="StopAt"/>), zegar zatrzymuje się dokładnie na nim, włącza się pauza i wywoływane jest <see cref="StopReached"/>.
    /// </summary>
    private DateTimeOffset ClampToStop(DateTimeOffset from, DateTimeOffset to)
    {
        if (StopAt is not { } stop || from >= stop || to < stop)
        {
            return to;
        }

        _paused = true;
        StatusChanged?.Invoke(this, $"Symulacja wstrzymana – osiągnięto czas końca {Format(stop)}");
        StopReached?.Invoke(this, stop);
        return stop;
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
