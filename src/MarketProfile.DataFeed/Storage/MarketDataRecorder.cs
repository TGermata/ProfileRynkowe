using System.Collections.Concurrent;
using MarketProfile.Core;

namespace MarketProfile.DataFeed.Storage;

/// <summary>
/// Rejestrator danych: zapisuje do lokalnej bazy wszystko, co przychodzi od brokera
/// (ticki na żywo i świece historyczne), żeby można było później odtworzyć te dane w symulatorze.
///
/// Ticki są buforowane w pamięci i zapisywane paczkami raz na sekundę w jednej transakcji –
/// zapis każdego ticka osobno byłby zbyt wolny przy szybkim rynku.
/// </summary>
public sealed class MarketDataRecorder : IAsyncDisposable
{
    private readonly MarketDataStore _store;
    private readonly string _symbol;
    private readonly string _source;
    private readonly ConcurrentQueue<Tick> _buffer = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushLoop;
    private long _recorded;

    public MarketDataRecorder(MarketDataStore store, string symbol, string source)
    {
        _store = store;
        _symbol = symbol;
        _source = source;
        _flushLoop = Task.Run(FlushLoopAsync);
    }

    /// <summary>Liczba zapisanych ticków.</summary>
    public long RecordedTicks => Interlocked.Read(ref _recorded);

    /// <summary>Dodaje tick do zapisu (można wywoływać z dowolnego wątku).</summary>
    public void Record(Tick tick) => _buffer.Enqueue(tick);

    /// <summary>Zapisuje świece historyczne (np. pobrane od brokera przy starcie).</summary>
    public void RecordBars(IEnumerable<Bar> bars) => _store.InsertBars(_symbol, bars, _source);

    /// <summary>Zapisuje zbuforowane ticki do bazy.</summary>
    public void Flush()
    {
        var batch = new List<Tick>();
        while (_buffer.TryDequeue(out var tick))
        {
            batch.Add(tick);
        }

        if (batch.Count > 0)
        {
            _store.InsertTicks(_symbol, batch, _source);
            Interlocked.Add(ref _recorded, batch.Count);
        }
    }

    private async Task FlushLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                Flush();
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
        await _cts.CancelAsync().ConfigureAwait(false);
        await _flushLoop.ConfigureAwait(false);
        Flush();
        _cts.Dispose();
    }
}
