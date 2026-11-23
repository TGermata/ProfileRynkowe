using System.Collections.Concurrent;
using MarketProfile.Core;

namespace MarketProfile.DataFeed.Storage;

/// <summary>
/// Rejestrator danych: zapisuje do lokalnej bazy wszystko, co przychodzi od brokera
/// (ticki na żywo i świece historyczne), żeby można było później odtworzyć te dane w symulatorze.
///
/// Ticki są buforowane w pamięci i zapisywane paczkami raz na sekundę w jednej transakcji –
/// zapis każdego ticka osobno byłby zbyt wolny przy szybkim rynku.
///
/// Błąd zapisu (np. zablokowana baza, brak miejsca) nie gubi ticków i nie zatrzymuje rejestratora:
/// nieudana paczka czeka w pamięci i jest zapisywana ponownie przy następnej próbie.
/// </summary>
public sealed class MarketDataRecorder : IAsyncDisposable
{
    private readonly Action<IReadOnlyList<Tick>> _insertTicks;
    private readonly MarketDataStore? _store;
    private readonly string _symbol;
    private readonly string _source;
    private readonly ConcurrentQueue<Tick> _buffer = new();
    private readonly List<Tick> _pending = [];
    private readonly Lock _flushLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushLoop;
    private long _recorded;
    private bool _failing;

    public MarketDataRecorder(MarketDataStore store, string symbol, string source)
        : this(ticks => store.InsertTicks(symbol, ticks, source), symbol, source)
    {
        _store = store;
    }

    /// <summary>Konstruktor z własną funkcją zapisu ticków (używany w testach).</summary>
    public MarketDataRecorder(Action<IReadOnlyList<Tick>> insertTicks, string symbol, string source)
    {
        _insertTicks = insertTicks;
        _symbol = symbol;
        _source = source;
        _flushLoop = Task.Run(FlushLoopAsync);
    }

    /// <summary>
    /// Błąd zapisu do bazy (zgłaszany raz na serię nieudanych prób; wywoływany z wątku w tle).
    /// Ticki nie giną – zostaną zapisane przy następnej udanej próbie.
    /// </summary>
    public event Action<Exception>? WriteFailed;

    /// <summary>Zapis działa ponownie po wcześniejszym błędzie (wywoływany z wątku w tle).</summary>
    public event Action? WriteRecovered;

    /// <summary>Liczba zapisanych ticków.</summary>
    public long RecordedTicks => Interlocked.Read(ref _recorded);

    /// <summary>Liczba ticków czekających na zapis (bufor + paczki po nieudanym zapisie).</summary>
    public int PendingTicks
    {
        get
        {
            lock (_flushLock)
            {
                return _pending.Count + _buffer.Count;
            }
        }
    }

    /// <summary>Dodaje tick do zapisu (można wywoływać z dowolnego wątku).</summary>
    public void Record(Tick tick) => _buffer.Enqueue(tick);

    /// <summary>Zapisuje świece historyczne (np. pobrane od brokera przy starcie).</summary>
    public void RecordBars(IEnumerable<Bar> bars)
    {
        if (_store is null)
        {
            throw new InvalidOperationException("Rejestrator bez bazy danych nie zapisuje świec.");
        }

        _store.InsertBars(_symbol, bars, _source);
    }

    /// <summary>
    /// Zapisuje zbuforowane ticki do bazy. Przy błędzie ticki zostają w pamięci, a wyjątek jest rzucany dalej.
    /// </summary>
    public void Flush()
    {
        lock (_flushLock)
        {
            while (_buffer.TryDequeue(out var tick))
            {
                _pending.Add(tick);
            }

            if (_pending.Count == 0)
            {
                return;
            }

            _insertTicks(_pending);
            Interlocked.Add(ref _recorded, _pending.Count);
            _pending.Clear();
        }
    }

    /// <summary>Zapis z obsługą błędu: zgłasza błąd raz na serię i powrót do działania.</summary>
    private void TryFlush()
    {
        try
        {
            Flush();
            if (_failing)
            {
                _failing = false;
                WriteRecovered?.Invoke();
            }
        }
        catch (Exception ex)
        {
            if (!_failing)
            {
                _failing = true;
                WriteFailed?.Invoke(ex);
            }
        }
    }

    private async Task FlushLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                TryFlush();
            }
        }
        catch (OperationCanceledException)
        {
            // Zatrzymanie rejestratora.
        }
    }

    /// <summary>Zatrzymuje rejestrator i zapisuje ostatnie ticki z bufora.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            await _flushLoop.ConfigureAwait(false);
        }
        finally
        {
            TryFlush();
            _cts.Dispose();
        }
    }
}
