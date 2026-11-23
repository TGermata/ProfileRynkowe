using MarketProfile.Core;
using MarketProfile.DataFeed.IB;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Dodatkowe połączenia na żywo z IB dla zakładki „Szeroki rynek” (obok głównego źródła danych wykresu TPO):
///  - rynek szeroki (indeks, np. SPX) – notowania do bieżącej świecy 30 min,
///  - SPY – transakcje z notowaniami bid/ask (rzeczywista strona kupno / sprzedaż) do wskaźnika „Oddech rynku · SPY”,
///  - VOL-NYSE – wolumen spółek NYSE rosnących / spadających do wskaźnika „Oddech rynku · NYSE”.
/// Każde źródło IB (TWS) obsługuje jeden instrument i ma własny Client ID: główny + 3 (indeks), + 4 (SPY), + 5 (VOL-NYSE).
/// Instrument, który jest już instrumentem głównym, nie dostaje osobnego połączenia – jego ticki przekazuje model widoku.
/// Ticki SPY są zapisywane w bazie (gdy włączone „Zapisuj do bazy”) – po ponownym uruchomieniu wskaźnik liczy od 9:30.
/// </summary>
public sealed class BroadMarketLiveFeeds : IAsyncDisposable
{
    /// <summary>ETF, z którego liczony jest „Oddech rynku” (rzeczywisty agresor z bid/ask).</summary>
    public const string BreathSymbol = "SPY";

    /// <summary>
    /// IB „NYSE Volume Index” – cała NYSE: bid = wolumen spółek rosnących, ask = spadających (od otwarcia,
    /// względem zamknięcia poprzedniej sesji). Drugi „Oddech rynku” (NYSE). Wskaźnik rynku, nie instrument z listy.
    /// </summary>
    public static Instrument NyseVolumeIndex { get; } = new()
    {
        Symbol = "VOL-NYSE",
        Description = "NYSE Volume Index",
        Type = InstrumentType.Index,
        Exchange = "NYSE",
        TickSize = 1m,
        HasNightSession = false,
    };

    private readonly List<IMarketDataFeed> _feeds = [];
    private IMarketDataFeed? _indexFeed;
    private long _lastIndexTickUtcTicks;

    /// <summary>Czy uruchomiono osobne połączenie dla indeksu (false – brak albo błąd połączenia).</summary>
    public bool IndexFeedRunning => _indexFeed is not null;

    /// <summary>Notowania indeksu opóźnione (true), na żywo (false), nieznane (null – nic nie przyszło).</summary>
    public bool? IndexDelayed => (_indexFeed as IMarketDataKindSource)?.IsDelayedData;

    /// <summary>Czas otrzymania ostatniego notowania indeksu (null – jeszcze żadnego).</summary>
    public DateTimeOffset? LastIndexTickAt => Interlocked.Read(ref _lastIndexTickUtcTicks) is var t and > 0
        ? new DateTimeOffset(t, TimeSpan.Zero)
        : null;
    private MarketDataRecorder? _recorder;

    /// <summary>Tick rynku szerokiego (indeks) – z wątku źródła danych.</summary>
    public event Action<Tick>? IndexTick;

    /// <summary>Tick SPY (ze stroną transakcji z bid/ask) – z wątku źródła danych.</summary>
    public event Action<Tick>? BreathTick;

    /// <summary>Wolumen spółek NYSE rosnących i spadających (VOL-NYSE, bid / ask) – z wątku źródła danych.</summary>
    public event Action<decimal, decimal>? NyseVolume;

    /// <summary>Komunikat o problemie (np. brak uprawnień, zerwane połączenie) – z wątku źródła danych.</summary>
    public event Action<string>? Problem;

    /// <summary>
    /// Łączy się i uruchamia strumienie. <paramref name="index"/> / <paramref name="breath"/> = null – bez osobnego
    /// połączenia (instrument główny). Błąd jednego połączenia nie przerywa drugiego – trafia do <see cref="Problem"/>.
    /// </summary>
    public async Task StartAsync(IbConnectionSettings mainConnection, Instrument? index, Instrument? breath,
        MarketDataStore? recordTo, CancellationToken token)
    {
        if (index is not null)
        {
            _indexFeed = await StartFeedAsync(mainConnection with { ClientId = mainConnection.ClientId + 3 }, index, tick =>
            {
                Interlocked.Exchange(ref _lastIndexTickUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                IndexTick?.Invoke(tick);
            }, token).ConfigureAwait(false);
        }

        // NYSE Volume Index – wartości w polach bid / ask (pole „last” jest puste).
        await StartFeedAsync(mainConnection with { ClientId = mainConnection.ClientId + 5 }, NyseVolumeIndex, _ => { }, token,
            (bid, ask) =>
            {
                if (bid is { } up && ask is { } down)
                {
                    NyseVolume?.Invoke(up, down);
                }
            }).ConfigureAwait(false);

        if (breath is not null)
        {
            _recorder = recordTo is null ? null : new MarketDataRecorder(recordTo, breath.Symbol, MarketDataStore.SourceBroker);
            var recorder = _recorder;
            await StartFeedAsync(mainConnection with { ClientId = mainConnection.ClientId + 4 }, breath, tick =>
            {
                recorder?.Record(tick);
                BreathTick?.Invoke(tick);
            }, token).ConfigureAwait(false);
        }
    }

    /// <returns>Działające źródło albo null (błąd – zgłoszony w <see cref="Problem"/>).</returns>
    private async Task<IMarketDataFeed?> StartFeedAsync(IbConnectionSettings connection, Instrument instrument, Action<Tick> onTick,
        CancellationToken token, Action<decimal?, decimal?>? onBidAsk = null)
    {
        var feed = IbFeeds.Create(connection);
        try
        {
            await feed.ConnectAsync(instrument, token).ConfigureAwait(false);
            feed.TickReceived += (_, tick) => onTick(tick);
            if (onBidAsk is not null && feed is IBidAskSource quotes)
            {
                quotes.BidAskChanged += onBidAsk;
            }

            feed.StatusChanged += (_, message) =>
            {
                if (IsProblem(message))
                {
                    Problem?.Invoke($"{instrument.Symbol} (szeroki rynek): {message}");
                }
            };

            if (feed is IConnectionLossAware aware)
            {
                aware.ConnectionLost += (_, reason) => Problem?.Invoke($"{instrument.Symbol} (szeroki rynek): {reason}");
            }

            await feed.StartLiveAsync(token).ConfigureAwait(false);
            lock (_feeds)
            {
                _feeds.Add(feed);
            }

            return feed;
        }
        catch (Exception ex)
        {
            await feed.DisposeAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException && token.IsCancellationRequested)
            {
                throw;
            }

            Problem?.Invoke($"{instrument.Symbol} (szeroki rynek): {ex.Message}");
            return null;
        }
    }

    /// <summary>Komunikaty źródła, które są problemem (a nie rutynowym stanem połączenia).</summary>
    private static bool IsProblem(string message) =>
        !message.Contains("UWAGA – tick", StringComparison.Ordinal) && // inny tick niż w definicji – bez znaczenia dla tych połączeń
        (message.Contains("błąd", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("brak subskrypcji", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("UWAGA", StringComparison.Ordinal) ||
        message.Contains("zamknięte", StringComparison.OrdinalIgnoreCase));

    public async ValueTask DisposeAsync()
    {
        IMarketDataFeed[] feeds;
        lock (_feeds)
        {
            feeds = [.. _feeds];
            _feeds.Clear();
        }

        _indexFeed = null;

        foreach (var feed in feeds)
        {
            try
            {
                await feed.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Rozłączanie dodatkowego połączenia – błąd bez znaczenia.
            }
        }

        if (_recorder is { } recorder)
        {
            _recorder = null;
            await recorder.DisposeAsync().ConfigureAwait(false);
        }
    }
}
