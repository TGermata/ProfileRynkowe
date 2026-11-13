using System.Collections.Concurrent;
using System.Globalization;
using IBApi;
using MarketProfile.Core;
using Bar = MarketProfile.Core.Bar;

namespace MarketProfile.DataFeed.IB;

/// <summary>
/// Źródło danych z Interactive Brokers (przez TWS lub IB Gateway).
///
///  1. <see cref="ConnectAsync"/> – połączenie z TWS i wyszukanie kontraktu futures (ES/NQ, najbliższy miesiąc
///     albo miesiąc z ustawień).
///  2. <see cref="GetHistoryAsync"/> – świece 1-minutowe z przeszłości. Bez subskrypcji danych IB zwykle odmawia –
///     wtedy zwracamy pustą historię (profile budują się od bieżącej chwili), zamiast przerywać start.
///  3. <see cref="StartLiveAsync"/> – strumień transakcji:
///     - dane na żywo: tick-by-tick (każda transakcja osobno),
///     - dane opóźnione: zwykły strumień notowań (cena ostatniej transakcji + wielkość + czas).
///
/// Klasa korzysta z typów biblioteki TWS API – przed jej użyciem trzeba wywołać <see cref="IbApiLoader.Use"/>.
/// Aplikacja niczego nie kupuje ani nie sprzedaje – używane są wyłącznie zapytania o dane.
/// </summary>
public sealed class IbDataFeed : IMarketDataFeed
{
    // Identyfikatory zapytań.
    private const int ContractRequestId = 1;
    private const int HistoryRequestId = 2;
    private const int FirstLiveRequestId = 100;   // kolejne subskrypcje strumienia: 101, 102, ...

    // Numery pól notowań (TickType) używane przy danych opóźnionych i zwykłym strumieniu notowań.
    private const int FieldLast = 4, FieldLastSize = 5, FieldLastTimestamp = 45;
    private const int FieldDelayedLast = 68, FieldDelayedLastSize = 71, FieldDelayedLastTimestamp = 88;
    private const int FieldVolume = 8, FieldDelayedVolume = 74;   // narastający wolumen sesji

    /// <summary>Przyrosty narastającego wolumenu sesji – właściwe źródło wolumenu w zwykłym strumieniu notowań.</summary>
    private readonly CumulativeVolumeTracker _cumulativeVolume = new();

    /// <summary>Kody komunikatów informacyjnych TWS (nie są błędami), np. „połączenie z farmą danych OK”.</summary>
    private static readonly HashSet<int> InfoCodes = [2100, 2103, 2104, 2105, 2106, 2107, 2108, 2119, 2150, 2157, 2158, 10167];

    /// <summary>Kody braku uprawnień do danych (brak subskrypcji).</summary>
    private static readonly HashSet<int> NoSubscriptionCodes = [354, 10089, 10090, 10168, 10186, 10197];

    private readonly IbConnectionSettings _settings;
    private readonly Wrapper _wrapper;
    private readonly EReaderMonitorSignal _signal = new();
    private readonly EClientSocket _client;
    private Thread? _readerThread;
    private Instrument? _instrument;
    private Contract? _contract;          // kontrakt zwrócony przez TWS (nazwa, data wygaśnięcia)
    private ContractDetails? _details;    // szczegóły kontraktu (tick, strefa czasowa, godziny handlu)
    private Contract? _requestContract;   // ten sam kontrakt wskazany przez ConId – używany w zapytaniach o dane
    private bool _liveStarted;

    // Oczekiwanie na odpowiedzi TWS.
    private TaskCompletionSource<int> _connected = NewTcs<int>();
    private TaskCompletionSource<bool> _contractsDone = NewTcs<bool>();
    private TaskCompletionSource<bool> _historyDone = NewTcs<bool>();
    private readonly ConcurrentQueue<ContractDetails> _contracts = new();
    private readonly ConcurrentQueue<Bar> _historyBars = new();
    private string? _historyError;

    // Składanie transakcji ze strumienia notowań (dane opóźnione).
    private decimal? _lastPrice;
    private DateTimeOffset? _lastTradeTime;
    private (decimal Price, long Volume)? _pendingTrade;   // transakcja czekająca na swój znacznik czasu

    // Strażnik strumienia danych.
    private int _liveRequestId = FirstLiveRequestId;
    private CancellationTokenSource? _watchdogCts;
    private volatile bool _hasData;
    private DateTimeOffset _subscribedAt;
    private DateTimeOffset _lastDataAt;

    public IbDataFeed(IbConnectionSettings settings)
    {
        _settings = settings;
        _wrapper = new Wrapper(this);
        _client = new EClientSocket(_wrapper, _signal);
    }

    public string Name => "Interactive Brokers";

    /// <summary>Wybrany kontrakt, np. „ESZ6” (po połączeniu).</summary>
    public string? ContractName => _contract?.LocalSymbol;

    /// <summary>Data wygaśnięcia wybranego kontraktu (RRRRMMDD).</summary>
    public string? ContractExpiry => _contract?.LastTradeDateOrContractMonth;

    public event EventHandler<Tick>? TickReceived;
    public event EventHandler<string>? StatusChanged;

    /// <summary>Zdarzenie przy każdym otrzymanym notowaniu (także bid/ask) – używane przez test połączenia.</summary>
    public event EventHandler<decimal>? QuoteReceived;

    // =====================================================================
    // Połączenie i kontrakt
    // =====================================================================

    public async Task ConnectAsync(Instrument instrument, CancellationToken cancellationToken = default)
    {
        _instrument = instrument;
        Status($"IB: łączenie z {_settings.Host}:{_settings.Port} (Client ID {_settings.ClientId})...");

        // 1. Połączenie gniazdem z TWS i wątek odczytu wiadomości.
        _client.eConnect(_settings.Host, _settings.Port, _settings.ClientId);
        if (!_client.IsConnected())
        {
            throw new InvalidOperationException(
                $"Brak połączenia z TWS na {_settings.Host}:{_settings.Port}. Sprawdź, czy TWS/IB Gateway jest uruchomiony " +
                "i czy API jest włączone (Configure → API → Settings → Enable ActiveX and Socket Clients).");
        }

        var reader = new EReader(_client, _signal);
        reader.Start();
        _readerThread = new Thread(() =>
        {
            // Biblioteka TWS API odczytuje liczby (np. "0.25") według ustawień regionalnych wątku.
            // Przy polskich ustawieniach (przecinek dziesiętny) kończy się to błędem – dlatego ten wątek
            // używa ustawień niezależnych od języka (kropka dziesiętna). Reszta aplikacji nie jest zmieniana.
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            // Pętla odbioru: czekamy na sygnał o nowych wiadomościach i przekazujemy je do Wrapper.
            while (_client.IsConnected())
            {
                _signal.waitForSignal();
                try
                {
                    reader.processMsgs();
                }
                catch (Exception ex)
                {
                    // Błąd w bibliotece IB nie może zamknąć aplikacji – zgłaszamy go i pracujemy dalej.
                    Status($"IB błąd odczytu wiadomości: {ex.Message}");
                }
            }
        })
        { IsBackground = true, Name = "IB reader" };
        _readerThread.Start();

        // 2. TWS potwierdza gotowość wiadomością nextValidId.
        await WaitAsync(_connected.Task, TimeSpan.FromSeconds(10), "TWS nie potwierdził połączenia", cancellationToken);

        // 3. Wyszukanie kontraktu (futures / akcja / indeks).
        _client.reqContractDetails(ContractRequestId, BuildQuery(instrument));
        await WaitAsync(_contractsDone.Task, TimeSpan.FromSeconds(15), "TWS nie zwrócił kontraktu", cancellationToken);

        var details = ChooseContract(instrument, _contracts.ToList())
            ?? throw new InvalidOperationException(
                $"Nie znaleziono w IB instrumentu {instrument.Symbol} ({instrument.BrokerSymbol}, {instrument.Exchange}). " +
                "Sprawdź dane instrumentu w Ustawieniach → Instrumenty (przycisk „Sprawdź w IB”).");
        _contract = details.Contract;
        _details = details;

        // Do zapytań o dane używamy kontraktu wskazanego tylko identyfikatorem (ConId) i giełdą.
        // Sprawdzone w praktyce: z pełnym obiektem kontraktu zwróconym przez TWS (wersja API 10.45)
        // strumień notowań nie przychodzi wcale, a z ConId + giełda – działa poprawnie.
        _requestContract = new Contract { ConId = _contract.ConId, Exchange = _contract.Exchange };

        var expiry = instrument.Type == InstrumentType.Future ? $" (wygasa {_contract.LastTradeDateOrContractMonth})" : string.Empty;
        Status($"IB: połączono, kontrakt {ContractName}{expiry}, dane {_settings.DataTypeName}");

        // Kontrola wielkości ticka – inna niż w definicji instrumentu oznacza źle zaokrąglane ceny i wiersze TPO.
        if (details.MinTick > 0 && ToDecimal(details.MinTick) != instrument.TickSize)
        {
            Status($"IB: UWAGA – tick {instrument.Symbol} w IB to {ToDecimal(details.MinTick).ToString(CultureInfo.InvariantCulture)}, " +
                   $"a w ustawieniach {instrument.TickSize.ToString(CultureInfo.InvariantCulture)} – popraw w Ustawieniach → Instrumenty.");
        }
    }

    /// <summary>
    /// Zapytanie o kontrakt zależne od rodzaju instrumentu:
    ///  - futures: symbol, giełda, waluta, klasa kontraktu i (opcjonalnie) miesiąc,
    ///  - akcja/ETF: symbol, giełda (zwykle SMART) i giełda główna,
    ///  - indeks: symbol i giełda indeksu.
    /// </summary>
    private static Contract BuildQuery(Instrument instrument)
    {
        var contract = new Contract
        {
            Symbol = instrument.BrokerSymbol,
            Exchange = instrument.Exchange,
            Currency = instrument.Currency,
        };

        switch (instrument.Type)
        {
            case InstrumentType.Stock:
                contract.SecType = "STK";
                if (!string.IsNullOrWhiteSpace(instrument.PrimaryExchange))
                {
                    contract.PrimaryExch = instrument.PrimaryExchange;
                }

                break;

            case InstrumentType.Index:
                contract.SecType = "IND";
                break;

            default:
                contract.SecType = "FUT";
                if (!string.IsNullOrWhiteSpace(instrument.TradingClass))
                {
                    contract.TradingClass = instrument.TradingClass;
                }

                if (!string.IsNullOrWhiteSpace(instrument.ContractMonth))
                {
                    contract.LastTradeDateOrContractMonth = instrument.ContractMonth.Trim();
                }

                break;
        }

        return contract;
    }

    /// <summary>
    /// Wybór kontraktu z odpowiedzi TWS: dla futures najbliższy niewygasły (front month; jeśli podano miesiąc,
    /// TWS zwraca już tylko kontrakty z tego miesiąca), dla akcji i indeksów – pierwszy znaleziony.
    /// </summary>
    private static ContractDetails? ChooseContract(Instrument instrument, IReadOnlyList<ContractDetails> details)
    {
        if (instrument.Type != InstrumentType.Future)
        {
            return details.FirstOrDefault();
        }

        var today = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return details
            .Where(d => string.CompareOrdinal(d.Contract.LastTradeDateOrContractMonth ?? string.Empty, today) >= 0)
            .OrderBy(d => d.Contract.LastTradeDateOrContractMonth, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    // =====================================================================
    // Historia
    // =====================================================================

    /// <summary>Najdłuższy okres świec 1-minutowych w jednym zapytaniu (sprawdzone: 10 dni działa, miesiąc – nie).</summary>
    private const int MaxDaysPerHistoryRequest = 10;

    public Task<IReadOnlyList<Bar>> GetHistoryAsync(TimeSpan lookback, CancellationToken cancellationToken = default) =>
        LoadHistoryAsync(end: null, Math.Max(1, (int)Math.Ceiling(lookback.TotalDays)), cancellationToken);

    /// <summary>Starsza historia (doczytywana przy przesuwaniu wykresu w lewo) – partiami po 10 dni, kończąc na <paramref name="to"/>.</summary>
    public async Task<IReadOnlyList<Bar>> GetHistoryRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var days = Math.Max(1, (int)Math.Ceiling((to - from).TotalDays));
        var bars = await LoadHistoryAsync(to, days, cancellationToken).ConfigureAwait(false);
        return bars.Where(b => b.Time >= from && b.Time < to).ToList();
    }

    /// <summary>
    /// Pobiera <paramref name="totalDays"/> dni świec 1-minutowych kończących się w <paramref name="end"/> (null = teraz).
    /// </summary>
    private async Task<IReadOnlyList<Bar>> LoadHistoryAsync(DateTimeOffset? end, int totalDays, CancellationToken cancellationToken)
    {
        var contract = _requestContract ?? throw new InvalidOperationException("Najpierw wywołaj ConnectAsync.");
        var collected = new SortedDictionary<DateTimeOffset, Bar>();

        // Dłuższą historię pobieramy partiami po maks. 10 dni, cofając się w czasie:
        // pierwsza partia kończy się w "end" (lub teraz), każda kolejna – na początku poprzedniej.
        var remaining = totalDays;
        var endText = end is { } e ? e.UtcDateTime.ToString("yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;
        var part = 0;

        while (remaining > 0)
        {
            var days = Math.Min(remaining, MaxDaysPerHistoryRequest);
            part++;
            Status($"IB: pobieranie historii – część {part} ({days} dni, świece 1 min, łącznie {totalDays} dni)...");

            var chunk = await RequestHistoryChunkAsync(contract, endText, days, cancellationToken);
            if (chunk is null)
            {
                break; // błąd lub brak uprawnień – zostajemy przy tym, co już pobrano
            }

            foreach (var bar in chunk)
            {
                collected[bar.Time] = bar;
            }

            if (chunk.Count == 0)
            {
                break; // brak starszych danych
            }

            remaining -= days;
            endText = chunk.Min(b => b.Time).UtcDateTime.ToString("yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (collected.Count == 0 && _historyError is not null)
        {
            // Bez uprawnień IB może odmówić historii – nie przerywamy pracy.
            Status($"IB: brak danych historycznych ({_historyError}) – profile budowane od bieżącej chwili");
            return [];
        }

        // Dane opóźnione: ticki na żywo przychodzą z czasem sprzed ~15 min, a historia sięga „teraz”.
        // Przycinamy najnowszą historię do „teraz − opóźnienie”, żeby ticki płynnie ją kontynuowały
        // (bez tego ostatnie 15 minut byłoby liczone dwa razy – w historii i ponownie z ticków).
        var bars = collected.Values.AsEnumerable();
        if (end is null && _settings.DataType != IbDataType.Live)
        {
            var limit = DateTimeOffset.UtcNow.AddMinutes(-DelayedMinutes);
            bars = bars.Where(b => b.Time.AddMinutes(1) <= limit);
        }

        var result = bars.ToList();
        Status($"IB: pobrano {result.Count} świec historycznych");
        return result;
    }

    /// <summary>
    /// Jedno zapytanie o świece 1-minutowe z <paramref name="days"/> dni kończących się w <paramref name="end"/>
    /// (pusty tekst = teraz). Zwraca null przy błędzie.
    /// </summary>
    private async Task<List<Bar>?> RequestHistoryChunkAsync(Contract contract, string end, int days, CancellationToken cancellationToken)
    {
        _historyBars.Clear();
        _historyError = null;
        _historyDone = NewTcs<bool>();

        // formatDate = 2 → czas świecy jako liczba sekund od 1970 (UTC); useRTH = 0 → także sesja nocna.
        _client.reqHistoricalData(HistoryRequestId, contract, end, $"{days} D", "1 min", "TRADES", 0, 2, false, null);

        try
        {
            await WaitAsync(_historyDone.Task, TimeSpan.FromSeconds(90), "TWS nie zwrócił historii", cancellationToken);
        }
        catch (TimeoutException)
        {
            _client.cancelHistoricalData(HistoryRequestId);
            _historyError ??= "przekroczono czas oczekiwania";
        }

        return _historyError is null ? [.. _historyBars] : null;
    }

    // =====================================================================
    // Strumień danych
    // =====================================================================

    public Task StartLiveAsync(CancellationToken cancellationToken = default)
    {
        if (_requestContract is null)
        {
            throw new InvalidOperationException("Najpierw wywołaj ConnectAsync.");
        }

        Subscribe();
        _liveStarted = true;
        Status($"IB: dane {_settings.DataTypeName} – {ContractName}");

        // Strażnik strumienia: TWS czasem przyjmuje subskrypcję, ale nie wysyła notowań
        // (zaobserwowane zwłaszcza tuż po zamknięciu innego połączenia API). Wtedy ponawiamy subskrypcję.
        _watchdogCts = new CancellationTokenSource();
        _ = Task.Run(() => WatchdogAsync(_watchdogCts.Token));
        return Task.CompletedTask;
    }

    /// <summary>Subskrypcja strumienia danych (każda kolejna z nowym numerem zapytania).</summary>
    private void Subscribe()
    {
        var contract = _requestContract!;
        _liveRequestId++;
        _subscribedAt = DateTimeOffset.UtcNow;
        _hasData = false;

        if (UseTickByTick)
        {
            // Każda transakcja osobno – wymaga subskrypcji danych na żywo.
            _client.reqTickByTickData(_liveRequestId, contract, "AllLast", 0, false);
        }
        else
        {
            // Zwykły strumień notowań: dane na żywo (1, dla indeksów), opóźnione (3) lub opóźnione zamrożone (4).
            _client.reqMarketDataType(_settings.DataType switch
            {
                IbDataType.Live => 1,
                IbDataType.DelayedFrozen => 4,
                _ => 3,
            });
            _client.reqMktData(_liveRequestId, contract, string.Empty, false, false, null);
        }
    }

    /// <summary>
    /// Tick-by-tick (każda transakcja osobno) tylko dla danych na żywo i instrumentów z transakcjami –
    /// indeksy nie mają transakcji, więc zawsze korzystają ze zwykłego strumienia notowań.
    /// </summary>
    private bool UseTickByTick => _settings.DataType == IbDataType.Live && _instrument?.Type != InstrumentType.Index;

    /// <summary>Czy bieżący instrument to indeks (notowania bez transakcji i wolumenu).</summary>
    private bool IsIndex => _instrument?.Type == InstrumentType.Index;

    /// <summary>Anulowanie bieżącej subskrypcji strumienia danych.</summary>
    private void Unsubscribe()
    {
        if (UseTickByTick)
        {
            _client.cancelTickByTickData(_liveRequestId);
        }
        else
        {
            _client.cancelMktData(_liveRequestId);
        }
    }

    /// <summary>
    /// Co 5 s sprawdza, czy dane płyną. Brak pierwszego notowania przez 15 s od subskrypcji
    /// albo brak jakichkolwiek notowań przez 60 s → ponowna subskrypcja.
    /// </summary>
    private async Task WatchdogAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                if (!_client.IsConnected())
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                var silentFor = now - (_hasData ? _lastDataAt : _subscribedAt);
                var limit = _hasData ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(15);

                if (silentFor > limit)
                {
                    Status($"IB: brak notowań od {silentFor.TotalSeconds:0} s – ponawiam subskrypcję danych");
                    Unsubscribe();
                    Subscribe();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Zatrzymanie strumienia.
        }
    }

    public Task DisconnectAsync()
    {
        _watchdogCts?.Cancel();

        if (_client.IsConnected())
        {
            if (_liveStarted)
            {
                Unsubscribe();
            }

            _client.eDisconnect();
            _signal.issueSignal(); // budzimy wątek odczytu, żeby zakończył pętlę
        }

        _liveStarted = false;
        Status("IB: rozłączono");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    /// <summary>
    /// Wyszukanie instrumentu w IB (przycisk „Sprawdź w IB” w ustawieniach): łączy się, znajduje kontrakt
    /// i zwraca jego dane – nazwę, giełdę, walutę, tick, strefę czasową i propozycję godzin sesji. Potem rozłącza.
    /// </summary>
    public static async Task<IbInstrumentInfo> LookupAsync(IbConnectionSettings settings, Instrument instrument)
    {
        await using var feed = new IbDataFeed(settings);
        await feed.ConnectAsync(instrument);

        var details = feed._details!;
        var contract = details.Contract;
        return new IbInstrumentInfo(
            LongName: details.LongName ?? string.Empty,
            LocalSymbol: contract.LocalSymbol ?? instrument.Symbol,
            Exchange: contract.Exchange ?? instrument.Exchange,
            PrimaryExchange: contract.PrimaryExch ?? string.Empty,
            Currency: contract.Currency ?? instrument.Currency,
            TradingClass: contract.TradingClass ?? string.Empty,
            Expiry: contract.LastTradeDateOrContractMonth ?? string.Empty,
            MinTick: details.MinTick > 0 ? ToDecimal(details.MinTick) : instrument.TickSize,
            TimeZoneId: IbTradingHours.ToIanaTimeZone(details.TimeZoneId),
            Sessions: IbTradingHours.Suggest(details.LiquidHours, details.TradingHours));
    }
    /// <summary>
    /// Test połączenia: łączy się, wyszukuje kontrakt, czeka na pierwsze notowanie w wybranym rodzaju danych
    /// i rozłącza. Zwraca opis wyniku dla użytkownika.
    /// </summary>
    public static async Task<string> TestConnectionAsync(IbConnectionSettings settings, Instrument instrument, TimeSpan timeout)
    {
        await using var feed = new IbDataFeed(settings);
        var messages = new ConcurrentQueue<string>();
        feed.StatusChanged += (_, m) => messages.Enqueue(m);

        await feed.ConnectAsync(instrument);

        var quote = new TaskCompletionSource<decimal>(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.QuoteReceived += (_, price) => quote.TrySetResult(price);
        await feed.StartLiveAsync();

        var result = $"Połączono z TWS. Kontrakt {feed.ContractName} (wygasa {feed.ContractExpiry}). ";
        var finished = await Task.WhenAny(quote.Task, Task.Delay(timeout));
        if (finished == quote.Task)
        {
            return result + $"Dane {settings.DataTypeName}: OK – notowanie {quote.Task.Result.ToString("0.00", CultureInfo.InvariantCulture)}.";
        }

        var lastError = messages.LastOrDefault(m => m.Contains("błąd", StringComparison.OrdinalIgnoreCase)
                                                 || m.Contains("subskrypc", StringComparison.OrdinalIgnoreCase));
        return result + $"Dane {settings.DataTypeName}: brak notowań w ciągu {timeout.TotalSeconds:0} s." +
               (lastError is null ? " (Rynek może być zamknięty.)" : $" {lastError}");
    }

    // =====================================================================
    // Obsługa wiadomości z TWS (wywoływane z wątku odczytu)
    // =====================================================================

    private void OnNextValidId() => _connected.TrySetResult(0);

    private void OnError(int id, int code, string message)
    {
        // Kody 2100–2199 to ostrzeżenia TWS (np. 2188 „dane do bieżącej sekundy wymagają subskrypcji” –
        // mimo tego ostrzeżenia TWS i tak wysyła dane historyczne). Nie przerywamy niczego, tylko informujemy.
        if (InfoCodes.Contains(code) || code is >= 2100 and < 2200)
        {
            Status($"IB: {message}");
            return;
        }

        var text = $"IB błąd {code}: {message}";

        switch (id)
        {
            case ContractRequestId:
                _contractsDone.TrySetException(new InvalidOperationException(code == 200
                    ? $"Nie znaleziono kontraktu ({message}). Sprawdź symbol i miesiąc kontraktu w ustawieniach."
                    : text));
                return;

            case HistoryRequestId:
                _historyError = NoSubscriptionCodes.Contains(code) || code == 162 ? $"brak uprawnień: {message}" : text;
                _historyDone.TrySetResult(false);
                return;

            case >= FirstLiveRequestId when NoSubscriptionCodes.Contains(code):
                Status($"IB: brak subskrypcji danych {_settings.DataTypeName} ({code}). " +
                       "Zmień w Ustawieniach → Dane rodzaj danych na „Opóźnione”.");
                return;
        }

        // Błędy połączenia (np. 502 – brak TWS, 326 – Client ID zajęty) przerywają oczekiwanie na połączenie.
        if (code is 502 or 504 or 326 or 507)
        {
            var reason = code == 326
                ? $"Client ID {_settings.ClientId} jest już używany przez inne połączenie – zmień Client ID w ustawieniach."
                : text;
            _connected.TrySetException(new InvalidOperationException(reason));
        }

        Status(text);
    }

    private void OnHistoricalBar(IBApi.Bar bar)
    {
        if (TryParseBarTime(bar.Time, out var time))
        {
            _historyBars.Enqueue(new Bar(time, Price(bar.Open), Price(bar.High), Price(bar.Low), Price(bar.Close), Math.Max(0, (long)bar.Volume))); // indeksy: wolumen -1 → 0
        }
    }

    /// <summary>Transakcja tick-by-tick (dane na żywo).</summary>
    private void OnTickByTick(long time, double price, decimal size)
    {
        var p = Price(price);
        MarkDataReceived();
        QuoteReceived?.Invoke(this, p);
        TickReceived?.Invoke(this, new Tick(DateTimeOffset.FromUnixTimeSeconds(time), p, Math.Max(1, (long)size)));
    }

    /// <summary>Notowanie ceny ze zwykłego strumienia (dane opóźnione).</summary>
    private void OnTickPrice(int field, double price)
    {
        if (price <= 0)
        {
            return; // TWS wysyła -1, gdy brak wartości
        }

        MarkDataReceived();
        QuoteReceived?.Invoke(this, Price(price));

        if (field is FieldLast or FieldDelayedLast)
        {
            _lastPrice = Price(price);

            // Indeks nie ma transakcji ani wielkości – każdą nową wartość indeksu traktujemy jak "transakcję"
            // z zerowym wolumenem (litery TPO budują się normalnie, profil wolumenowy pozostaje pusty).
            if (IsIndex)
            {
                TickReceived?.Invoke(this, new Tick(EstimatedTradeTime(), _lastPrice.Value, 0));
            }
        }
    }

    /// <summary>
    /// Wielkość ostatniej transakcji. TWS wysyła kolejno: cenę (pole 68), wielkość (71) i dopiero potem
    /// znacznik czasu tej transakcji (88). Dlatego transakcję zapamiętujemy i wysyłamy dopiero po otrzymaniu
    /// jej znacznika czasu (patrz <see cref="OnTickString"/>).
    /// </summary>
    private void OnTickSize(int field, decimal size)
    {
        // Indeksy nie mają transakcji: IB wysyła dla nich "wielkość" i znacznik czasu ostatniego wyliczenia
        // (np. wczorajszego zamknięcia) – nie traktujemy tego jak transakcji (indeks obsługuje OnTickPrice).
        if (IsIndex)
        {
            return;
        }

        // Narastający wolumen sesji (pole 8 / 74) – kompletne źródło wolumenu. Jego przyrost przypisujemy do ostatniej
        // ceny jako "transakcję z wolumenem" (sprawdzone w praktyce: ~2 aktualizacje/s, setki kontraktów na minutę,
        // podczas gdy pole "wielkość ostatniej transakcji" daje tylko pojedyncze próbki).
        if (field is FieldVolume or FieldDelayedVolume)
        {
            var delta = _cumulativeVolume.Update(size);
            if (delta > 0 && _lastPrice is { } lastPrice)
            {
                TickReceived?.Invoke(this, new Tick(_lastTradeTime ?? EstimatedTradeTime(), lastPrice, delta));
            }

            return;
        }

        if (field is not (FieldLastSize or FieldDelayedLastSize) || _lastPrice is not { } price || size <= 0)
        {
            return;
        }

        // Poprzednia transakcja nie dostała znacznika czasu – wysyłamy ją z czasem szacowanym.
        // Gdy dostępny jest narastający wolumen sesji, transakcja niesie tylko cenę (wolumen 0) – bez podwójnego liczenia.
        FlushPendingTrade(_lastTradeTime ?? EstimatedTradeTime());
        _pendingTrade = (price, _cumulativeVolume.HasData ? 0 : Math.Max(1, (long)size));
    }

    /// <summary>Znacznik czasu ostatniej transakcji (sekundy od 1970) – zamyka oczekującą transakcję.</summary>
    private void OnTickString(int field, string value)
    {
        if (field is FieldLastTimestamp or FieldDelayedLastTimestamp &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            _lastTradeTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
            FlushPendingTrade(_lastTradeTime.Value);
        }
    }

    /// <summary>Wysyła oczekującą transakcję (jeśli jest) z podanym czasem.</summary>
    private void FlushPendingTrade(DateTimeOffset time)
    {
        if (_pendingTrade is { } trade)
        {
            _pendingTrade = null;
            TickReceived?.Invoke(this, new Tick(time, trade.Price, trade.Volume));
        }
    }

    /// <summary>Czas transakcji, gdy TWS nie podał znacznika: czas odbioru (dla danych opóźnionych − 15 min).</summary>
    private DateTimeOffset EstimatedTradeTime() =>
        DateTimeOffset.UtcNow.AddMinutes(_settings.DataType == IbDataType.Live ? 0 : -DelayedMinutes);

    /// <summary>Opóźnienie danych opóźnionych IB (minuty).</summary>
    private const int DelayedMinutes = 15;

    // =====================================================================
    // Pomocnicze
    // =====================================================================

    /// <summary>Notuje, że dane płyną (dla strażnika strumienia).</summary>
    private void MarkDataReceived()
    {
        _lastDataAt = DateTimeOffset.UtcNow;
        _hasData = true;
    }

    /// <summary>
    /// Zamiana liczby z TWS (double) na dokładną liczbę dziesiętną przez najkrótszy zapis tekstowy –
    /// bez tego 0.1 zamienia się w 0.1000000000000000055 (niedokładność liczb zmiennoprzecinkowych).
    /// </summary>
    private static decimal ToDecimal(double value) =>
        decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Cena z TWS (double) zaokrąglona do ticka instrumentu.</summary>
    private decimal Price(double value)
    {
        var tick = _instrument?.TickSize ?? 0.25m;
        return Math.Round((decimal)value / tick) * tick;
    }

    /// <summary>
    /// Czas świecy: przy formatDate=2 są to sekundy od 1970; na wszelki wypadek obsługujemy też format
    /// „yyyyMMdd HH:mm:ss Strefa/Czasowa”.
    /// </summary>
    private static bool TryParseBarTime(string text, out DateTimeOffset time)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            time = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            DateTime.TryParseExact($"{parts[0]} {parts[1]}", "yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            var zone = parts.Length >= 3 ? TryFindZone(parts[2]) : null;
            time = zone is null ? new DateTimeOffset(local, TimeSpan.Zero) : new DateTimeOffset(local, zone.GetUtcOffset(local));
            return true;
        }

        time = default;
        return false;
    }

    private static TimeZoneInfo? TryFindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TaskCompletionSource<T> NewTcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitAsync(Task task, TimeSpan timeout, string message, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{message} (przekroczono {timeout.TotalSeconds:0} s).");
        }
    }

    private void Status(string message) => StatusChanged?.Invoke(this, message);

    /// <summary>
    /// Odbiorca wiadomości z TWS. TWS API wymaga obiektu z metodami-zwrotkami (EWrapper);
    /// DefaultEWrapper ma puste implementacje – nadpisujemy tylko te, których potrzebujemy.
    /// </summary>
    private sealed class Wrapper(IbDataFeed owner) : DefaultEWrapper
    {
        public override void nextValidId(int orderId) => owner.OnNextValidId();

        public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson) =>
            owner.OnError(id, errorCode, errorMsg);

        public override void error(string str) => owner.Status($"IB: {str}");

        public override void error(Exception e) => owner.Status($"IB błąd: {e.Message}");

        public override void connectionClosed() => owner.Status("IB: połączenie z TWS zostało zamknięte");

        public override void contractDetails(int reqId, ContractDetails contractDetails) => owner._contracts.Enqueue(contractDetails);

        public override void contractDetailsEnd(int reqId) => owner._contractsDone.TrySetResult(true);

        public override void historicalData(int reqId, IBApi.Bar bar) => owner.OnHistoricalBar(bar);

        public override void historicalDataEnd(int reqId, string start, string end) => owner._historyDone.TrySetResult(true);

        public override void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size,
            TickAttribLast tickAttribLast, string exchange, string specialConditions) => owner.OnTickByTick(time, price, size);

        public override void tickPrice(int tickerId, int field, double price, TickAttrib attribs) => owner.OnTickPrice(field, price);

        public override void tickSize(int tickerId, int field, decimal size) => owner.OnTickSize(field, size);

        public override void tickString(int tickerId, int field, string value) => owner.OnTickString(field, value);
    }
}
