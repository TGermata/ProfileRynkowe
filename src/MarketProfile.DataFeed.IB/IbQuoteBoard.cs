using System.Collections.Concurrent;
using System.Globalization;
using IBApi;
using MarketProfile.Core;

namespace MarketProfile.DataFeed.IB;

/// <summary>Bieżące notowanie jednego instrumentu tablicy (null – pole jeszcze nie przyszło).</summary>
public sealed record BoardQuote(string Symbol, decimal? Last, decimal? Open, decimal? PreviousClose, bool Delayed,
    DateTimeOffset? UpdatedAt, string? Problem);

/// <summary>
/// Tablica notowań: jedno połączenie z TWS (jeden Client ID) i zwykły strumień notowań (reqMktData) dla wielu akcji / ETF-ów
/// naraz – np. 11 ETF-ów sektorowych S&amp;P 500. Z każdego instrumentu zapamiętywana jest ostatnia cena, otwarcie sesji
/// i poprzednie zamknięcie (także w wersji opóźnionej). Odczyt (<see cref="Snapshot"/>) – z dowolnego wątku.
/// Przed użyciem trzeba wywołać <see cref="IbApiLoader.Use"/>.
/// </summary>
public sealed class IbQuoteBoard : IAsyncDisposable
{
    private const int FirstContractRequestId = 1_000;
    private const int FirstMarketDataRequestId = 2_000;

    // Pola notowań (TickType): na żywo i opóźnione.
    private const int FieldLast = 4, FieldClose = 9, FieldOpen = 14;
    private const int FieldDelayedLast = 68, FieldDelayedClose = 75, FieldDelayedOpen = 76;

    private static readonly HashSet<int> NoSubscriptionCodes = [354, 10089, 10090, 10168, 10186, 10197];

    private readonly IbConnectionSettings _settings;
    private readonly IReadOnlyList<string> _symbols;
    private readonly EReaderMonitorSignal _signal = new();
    private readonly EClientSocket _client;
    private readonly ConcurrentDictionary<string, BoardQuote> _quotes = new();
    private readonly ConcurrentDictionary<int, string> _requestSymbols = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Contract?>> _contractRequests = new();
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _disconnecting;
    private long _version;

    private readonly string? _primaryExchange;

    /// <param name="symbols">Symbole akcji / ETF-ów (USD, SMART), np. XLK, XLF.</param>
    /// <param name="primaryExchange">
    /// Giełda główna (np. ARCA dla ETF-ów SPDR) – ten sam symbol bywa notowany w kilku miejscach; bez niej IB może
    /// zwrócić najpierw inny papier (i notowania nie przychodzą).
    /// </param>
    public IbQuoteBoard(IbConnectionSettings settings, IReadOnlyList<string> symbols, string? primaryExchange = null)
    {
        _settings = settings;
        _symbols = symbols;
        _primaryExchange = primaryExchange;
        _client = new EClientSocket(new Wrapper(this), _signal);
        foreach (var symbol in symbols)
        {
            _quotes[symbol] = new BoardQuote(symbol, null, null, null, false, null, null);
        }
    }

    /// <summary>Licznik zmian – rośnie z każdym notowaniem (do sprawdzania, czy odświeżyć widok).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Komunikat stanu (łączenie, błąd, brak subskrypcji) – z wątku czytnika albo wywołującego.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Kategoria wpisów dziennika: „IB#&lt;Client ID&gt; Sektory”.</summary>
    private string LogCategory => $"IB#{_settings.ClientId} Sektory";

    /// <summary>Bieżące notowania wszystkich instrumentów.</summary>
    public IReadOnlyDictionary<string, BoardQuote> Snapshot() => new Dictionary<string, BoardQuote>(_quotes);

    /// <summary>Łączy się z TWS, wyszukuje kontrakty i zamawia notowania. Błąd połączenia – wyjątek.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Status($"IB: łączenie z {_settings.Host}:{_settings.Port} (Client ID {_settings.ClientId}) – notowania {string.Join(", ", _symbols)}...");
        await Task.Run(() => _client.eConnect(_settings.Host, _settings.Port, _settings.ClientId), cancellationToken).ConfigureAwait(false);
        if (!_client.IsConnected())
        {
            throw new InvalidOperationException($"Brak połączenia z TWS na {_settings.Host}:{_settings.Port}.");
        }

        var reader = new EReader(_client, _signal);
        reader.Start();
        new Thread(() =>
        {
            // Biblioteka TWS API czyta liczby wg ustawień regionalnych wątku – przy polskich kończy się to błędem.
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            while (_client.IsConnected())
            {
                _signal.waitForSignal();
                try
                {
                    reader.processMsgs();
                }
                catch (Exception ex)
                {
                    Status($"IB błąd odczytu wiadomości: {ex.Message}");
                }
            }
        })
        { IsBackground = true, Name = "IB quote board" }.Start();

        await _connected.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        _client.reqMarketDataType(_settings.DataType switch
        {
            IbDataType.Live => 1,
            IbDataType.DelayedFrozen => 4,
            _ => 3,
        });

        // Kontrakty po kolei (ConId), potem notowania – z ConId + SMART strumień przychodzi niezawodnie.
        var requestId = FirstContractRequestId;
        var started = 0;
        foreach (var symbol in _symbols)
        {
            var id = requestId++;
            var pending = new TaskCompletionSource<Contract?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _contractRequests[id] = pending;
            _requestSymbols[id] = symbol;
            var query = new Contract { Symbol = symbol, SecType = "STK", Exchange = "SMART", Currency = "USD" };
            if (_primaryExchange is not null)
            {
                query.PrimaryExch = _primaryExchange;
            }

            _client.reqContractDetails(id, query);

            Contract? contract;
            try
            {
                contract = await pending.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                contract = null;
            }

            if (contract is null)
            {
                SetProblem(symbol, "nie znaleziono kontraktu w IB");
                continue;
            }

            AppLog.Info(LogCategory, $"{symbol}: kontrakt ConId {contract.ConId}, giełda główna {contract.PrimaryExch}");
            _contracts[symbol] = contract;
            RequestMarketData(symbol, new Contract { ConId = contract.ConId, Exchange = "SMART" });
            started++;
        }

        Status($"IB: notowania {started} z {_symbols.Count} instrumentów ({_settings.DataTypeName})");

        // Zapas: przez SMART IB czasem nie przysyła żadnych notowań (sprawdzone 24.11.2026 – XLP; ten sam ConId na ARCA
        // działa). Po 10 s bez żadnego pola – ponowne zamówienie na giełdzie głównej.
        _ = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ContinueWith(_ =>
        {
            foreach (var (symbol, contract) in _contracts)
            {
                if (_disconnecting || _receivedFields.ContainsKey(symbol) || string.IsNullOrEmpty(contract.PrimaryExch))
                {
                    continue;
                }

                AppLog.Warning(LogCategory, $"{symbol}: brak notowań przez SMART – zamawiam na giełdzie głównej {contract.PrimaryExch}");
                RequestMarketData(symbol, new Contract { ConId = contract.ConId, Exchange = contract.PrimaryExch });
            }
        }, TaskScheduler.Default);

        // Diagnostyka: po 20 s – instrumenty bez ostatniej ceny albo otwarcia (z listą pól, które przyszły).
        _ = Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ContinueWith(_ =>
        {
            foreach (var quote in _quotes.Values.Where(q => q.Problem is null && (q.Last is null || q.Open is null)))
            {
                var fields = _receivedFields.TryGetValue(quote.Symbol, out var set) ? string.Join(", ", set.Keys.Order()) : "żadne";
                AppLog.Warning(LogCategory, $"{quote.Symbol}: po 20 s brak {(quote.Last is null ? "ostatniej ceny " : string.Empty)}" +
                                            $"{(quote.Open is null ? "otwarcia " : string.Empty)}(otrzymane pola: {fields})");
            }
        }, TaskScheduler.Default);
    }

    private readonly ConcurrentDictionary<string, Contract> _contracts = new();
    private int _nextMarketDataId = FirstMarketDataRequestId;

    /// <summary>Zamawia strumień notowań (każde zamówienie z nowym numerem).</summary>
    private void RequestMarketData(string symbol, Contract contract)
    {
        var dataId = Interlocked.Increment(ref _nextMarketDataId);
        _requestSymbols[dataId] = symbol;
        _client.reqMktData(dataId, contract, string.Empty, false, false, null);
    }

    // Pola notowań, które przyszły dla symbolu (diagnostyka).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _receivedFields = new();

    private void OnTickPrice(int requestId, int field, double price)
    {
        if (!_requestSymbols.TryGetValue(requestId, out var symbol))
        {
            return;
        }

        _receivedFields.GetOrAdd(symbol, _ => new ConcurrentDictionary<int, byte>())[field] = 0;
        if (price <= 0)
        {
            return;
        }

        var value = decimal.Parse(price.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        var delayed = field >= 66;
        _quotes.AddOrUpdate(symbol,
            _ => Apply(new BoardQuote(symbol, null, null, null, delayed, null, null), field, value, delayed),
            (_, quote) => Apply(quote, field, value, delayed));
        Interlocked.Increment(ref _version);
    }

    private static BoardQuote Apply(BoardQuote quote, int field, decimal value, bool delayed)
    {
        var now = DateTimeOffset.UtcNow;
        return field switch
        {
            FieldLast or FieldDelayedLast => quote with { Last = value, Delayed = delayed, UpdatedAt = now, Problem = null },
            FieldOpen or FieldDelayedOpen => quote with { Open = value, Delayed = delayed, UpdatedAt = now, Problem = null },
            FieldClose or FieldDelayedClose => quote with { PreviousClose = value, UpdatedAt = now },
            _ => quote,
        };
    }

    private void OnError(int id, int code, string message)
    {
        if (code is >= 2100 and < 2200 or 10167)
        {
            return; // komunikaty informacyjne (farmy danych, „wyświetlane dane opóźnione”)
        }

        if (_contractRequests.TryRemove(id, out var pending))
        {
            pending.TrySetResult(null);
        }

        if (_requestSymbols.TryGetValue(id, out var symbol))
        {
            var problem = NoSubscriptionCodes.Contains(code) ? $"brak subskrypcji danych ({code})" : $"błąd {code}: {message}";
            SetProblem(symbol, problem);
            AppLog.Warning(LogCategory, $"{symbol}: IB {problem}");
            return;
        }

        Status($"IB błąd {code}: {message}");
    }

    private void SetProblem(string symbol, string problem)
    {
        _quotes.AddOrUpdate(symbol,
            _ => new BoardQuote(symbol, null, null, null, false, null, problem),
            (_, quote) => quote with { Problem = problem });
        Interlocked.Increment(ref _version);
    }

    private void OnConnectionClosed()
    {
        if (!_disconnecting)
        {
            Status("IB: połączenie z TWS zostało zamknięte");
        }
    }

    private void Status(string message)
    {
        AppLog.Status(LogCategory, message);
        StatusChanged?.Invoke(message);
    }

    public ValueTask DisposeAsync()
    {
        _disconnecting = true;
        try
        {
            if (_client.IsConnected())
            {
                foreach (var id in _requestSymbols.Keys.Where(k => k >= FirstMarketDataRequestId))
                {
                    _client.cancelMktData(id);
                }

                _client.eDisconnect();
                _signal.issueSignal();
            }
        }
        catch (Exception)
        {
            // Rozłączanie – błąd bez znaczenia.
        }

        AppLog.Info(LogCategory, "IB: rozłączono");
        return ValueTask.CompletedTask;
    }

    /// <summary>Odbiór wiadomości TWS (DefaultEWrapper ma puste implementacje – nadpisujemy potrzebne).</summary>
    private sealed class Wrapper(IbQuoteBoard owner) : DefaultEWrapper
    {
        public override void nextValidId(int orderId) => owner._connected.TrySetResult(true);

        public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson) =>
            owner.OnError(id, errorCode, errorMsg);

        public override void error(string str) => owner.Status($"IB: {str}");

        public override void error(Exception e) => owner.Status($"IB błąd: {e.Message}");

        public override void connectionClosed() => owner.OnConnectionClosed();

        public override void contractDetails(int reqId, ContractDetails contractDetails)
        {
            if (owner._contractRequests.TryRemove(reqId, out var pending))
            {
                pending.TrySetResult(contractDetails.Contract);
            }
        }

        public override void contractDetailsEnd(int reqId)
        {
            if (owner._contractRequests.TryRemove(reqId, out var pending))
            {
                pending.TrySetResult(null); // brak kontraktu
            }
        }

        public override void tickPrice(int tickerId, int field, double price, TickAttrib attribs) =>
            owner.OnTickPrice(tickerId, field, price);
    }
}
