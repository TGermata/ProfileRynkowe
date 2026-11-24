using MarketProfile.Core;
using MarketProfile.DataFeed.IB;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Dodatkowe połączenia na żywo z IB dla zakładki „Szeroki rynek” (obok głównego źródła danych wykresu TPO):
///  - rynek szeroki (indeks, np. SPX) – notowania do bieżącej świecy 30 min,
///  - SPY – transakcje z notowaniami bid/ask (rzeczywista strona kupno / sprzedaż) do wskaźnika „Oddech rynku · SPY”,
///  - VOL-NYSE – wolumen spółek NYSE rosnących / spadających do wskaźnika „Oddech rynku · NYSE”,
///  - AD-NYSE i AD-NASD – liczba spółek rosnących / spadających NYSE i NASDAQ.
/// Każde źródło IB (TWS) obsługuje jeden instrument i ma własny Client ID: główny + 3 (indeks), + 4 (SPY), + 5 (VOL-NYSE),
/// + 6 (AD-NYSE), + 7 (AD-NASD).
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
        TickSize = 10m, // min. tick w IB (inny – ostrzeżenie o niezgodnym ticku)
        HasNightSession = false,
    };

    /// <summary>
    /// IB „NYSE Advance/Decline” – liczba spółek NYSE rosnących (bid) i spadających (ask) względem zamknięcia
    /// poprzedniej sesji. Przez TWS API IB może tych pól nie podawać (sprawdzone 23–24.11.2026) – wtedy komunikat.
    /// </summary>
    public static Instrument NyseAdvanceDecline { get; } = AdvanceDeclineIndex("AD-NYSE", "NYSE Advance/Decline", "NYSE");

    /// <summary>IB „NASDAQ Advance/Decline” – jak <see cref="NyseAdvanceDecline"/> (wymaga subskrypcji danych NASDAQ).</summary>
    public static Instrument NasdaqAdvanceDecline { get; } = AdvanceDeclineIndex("AD-NASD", "NASDAQ Advance/Decline", "NASDAQ");

    private static Instrument AdvanceDeclineIndex(string symbol, string description, string exchange) => new()
    {
        Symbol = symbol,
        Description = description,
        Type = InstrumentType.Index,
        Exchange = exchange,
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

    /// <summary>
    /// Liczba spółek rosnących i spadających (symbol AD-NYSE / AD-NASD, bid / ask) – z wątku źródła danych.
    /// </summary>
    public event Action<string, decimal, decimal>? AdvanceDecline;

    /// <summary>
    /// Problem połączenia AD-NYSE / AD-NASD (symbol, komunikat) – pokazywany w kolumnie wskaźnika, nie jako błąd aplikacji
    /// (np. brak subskrypcji NASDAQ zgłaszałby się przy każdym połączeniu). Z wątku źródła danych.
    /// </summary>
    public event Action<string, string>? AdvanceDeclineProblem;

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

        // Liczba spółek rosnących / spadających NYSE i NASDAQ – też w polach bid / ask.
        var clientOffset = 6;
        foreach (var market in new[] { NyseAdvanceDecline, NasdaqAdvanceDecline })
        {
            var symbol = market.Symbol;
            await StartFeedAsync(mainConnection with { ClientId = mainConnection.ClientId + clientOffset++ }, market, _ => { }, token,
                (bid, ask) =>
                {
                    if (bid is { } advancing && ask is { } declining)
                    {
                        AdvanceDecline?.Invoke(symbol, advancing, declining);
                    }
                },
                message => AdvanceDeclineProblem?.Invoke(symbol, message)).ConfigureAwait(false);
        }

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

    /// <param name="onProblem">Odbiorca problemów tego połączenia; null – <see cref="Problem"/>.</param>
    /// <returns>Działające źródło albo null (błąd – zgłoszony w <see cref="Problem"/> / <paramref name="onProblem"/>).</returns>
    private async Task<IMarketDataFeed?> StartFeedAsync(IbConnectionSettings connection, Instrument instrument, Action<Tick> onTick,
        CancellationToken token, Action<decimal?, decimal?>? onBidAsk = null, Action<string>? onProblem = null)
    {
        var report = onProblem ?? (message => Problem?.Invoke($"{instrument.Symbol} (szeroki rynek): {message}"));
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
                    report(message);
                }
            };

            if (feed is IConnectionLossAware aware)
            {
                aware.ConnectionLost += (_, reason) => report(reason);
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

            AppLog.Error("Szeroki rynek", $"połączenie {instrument.Symbol} (Client ID {connection.ClientId}) nie powiodło się", ex);
            report(ex.Message);
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
