using System.Globalization;
using System.Text.Json;
using MarketProfile.Core;
using MarketProfile.DataFeed.IB;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.DataFeed.IBWeb;

/// <summary>
/// Źródło danych z Interactive Brokers przez IBKR Web API (bez TWS / IB Gateway), z kluczami OAuth 1.0a.
/// Udostępnia te same operacje co <c>IbDataFeed</c> (TWS API), dzięki czemu aplikacja korzysta z obu jednakowo.
///
/// Różnice względem TWS API (ograniczenia Web API):
///  - brak historii ticków – <see cref="GetTicksPageAsync"/> zwraca błąd (pobieranie ticków w tle jest pomijane),
///  - brak tick-by-tick – transakcje składane ze strumienia notowań (<see cref="IbWebTickAssembler"/>),
///  - świece D/W/M dla futures – tylko z bieżącego kontraktu (Web API nie ma kontraktu ciągłego CONTFUT),
///  - rodzaj danych (na żywo / opóźnione) wynika z uprawnień konta – IBKR wybiera go sam.
///
/// Aplikacja niczego nie kupuje ani nie sprzedaje – używane są wyłącznie zapytania o dane.
/// </summary>
public sealed class IbWebDataFeed : IMarketDataFeed, IBidAskSource, IMarketDataKindSource
{
    /// <summary>Świece 1-min w jednym zapytaniu: okres 1 dzień (IBKR zwraca maks. ok. 1000 świec).</summary>
    private const string MinuteChunkPeriod = "1d";

    private readonly IbConnectionSettings _settings;
    private IbWebSession _session;
    private Instrument? _instrument;
    private long _conid;
    private IbWebTickAssembler? _assembler;
    private IDisposable? _subscription;
    private bool _delayed;

    public IbWebDataFeed(IbConnectionSettings settings)
    {
        _settings = settings;
        _session = IbWebSession.Shared(settings.WebApiCompete);
    }

    public string Name => "Interactive Brokers (Web API)";

    /// <summary>
    /// Bieżąca wspólna sesja. Jeśli naszą zamknięto (np. zmiana kluczy w ustawieniach), bierzemy nową –
    /// inaczej każde zapytanie kończyłoby się błędem „ObjectDisposedException”.
    /// </summary>
    private IbWebSession Session
    {
        get
        {
            if (_session.IsDisposed)
            {
                _session = IbWebSession.TryGetShared(_settings.WebApiCompete) ?? throw new InvalidOperationException(
                    "Sesję IBKR Web API przejęło połączenie z innym ustawieniem „Przejmij sesję” – uruchom pobieranie ponownie.");
            }

            return _session;
        }
    }

    /// <summary>Wybrany kontrakt, np. „ESZ6” (po połączeniu).</summary>
    public string? ContractName { get; private set; }

    /// <summary>Data wygaśnięcia wybranego kontraktu (RRRRMMDD).</summary>
    public string? ContractExpiry { get; private set; }

    /// <summary>Ostrzeżenie o niezgodności ticka instrumentu w ustawieniach z tickiem kontraktu w IB (null = zgodne).</summary>
    public string? TickSizeWarning { get; private set; }

    /// <summary>Informacje o kontrakcie (dla „Sprawdź w IB”).</summary>
    private IbInstrumentInfo? _info;

    public event EventHandler<Tick>? TickReceived;
    public event EventHandler<string>? StatusChanged;

    /// <summary>Zdarzenie przy każdym notowaniu – używane przez test połączenia.</summary>
    public event EventHandler<decimal>? QuoteReceived;

    /// <summary>Nowe notowanie bid / ask (pola 84 / 86) – wywoływane z wątku strumienia.</summary>
    public event Action<decimal?, decimal?>? BidAskChanged;

    /// <summary>Opis rodzaju danych (po połączeniu – wg odpowiedzi IBKR).</summary>
    public string DataTypeName => _delayed ? "opóźnione" : "na żywo";

    /// <summary>Rodzaj notowań wg IBKR (pole 6509); null – przed pierwszym notowaniem na żywo.</summary>
    public bool? IsDelayedData => _assembler is null ? null : _delayed;

    // =====================================================================
    // Połączenie i kontrakt
    // =====================================================================

    public async Task ConnectAsync(Instrument instrument, CancellationToken cancellationToken = default)
    {
        _instrument = instrument;
        IbWebSession.StatusChanged += OnSessionStatus;
        IbWebSession.SharedReplaced += OnSharedReplaced;
        Status("IBKR Web API: łączenie z api.ibkr.com...");
        await Session.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        _conid = instrument.Type == InstrumentType.Future
            ? await FindFutureAsync(instrument, cancellationToken).ConfigureAwait(false)
            : await FindBySearchAsync(instrument, cancellationToken).ConfigureAwait(false);

        await LoadContractInfoAsync(instrument, cancellationToken).ConfigureAwait(false);
        _delayed = await DetectDelayedAsync(cancellationToken).ConfigureAwait(false);

        var expiry = instrument.Type == InstrumentType.Future ? $" (wygasa {ContractExpiry})" : string.Empty;
        Status($"IBKR Web API: połączono, kontrakt {ContractName}{expiry}, dane {DataTypeName}");

        TickSizeWarning = null;
        if (_info is { MinTick: > 0 } info && info.MinTick != instrument.TickSize)
        {
            TickSizeWarning = $"tick {instrument.Symbol} w IB to {info.MinTick.ToString(CultureInfo.InvariantCulture)}, " +
                              $"a w ustawieniach {instrument.TickSize.ToString(CultureInfo.InvariantCulture)} – wiersze TPO są źle wyliczane. " +
                              "Popraw w Ustawieniach → Instrumenty (dane w bazie zapisywane są bez zmian, tak jak od brokera).";
            Status($"IBKR Web API: UWAGA – {TickSizeWarning}");
        }
    }

    /// <summary>Futures: lista kontraktów z <c>/trsrv/futures</c>, wybór front month (albo miesiąca z ustawień) i klasy kontraktu.</summary>
    private async Task<long> FindFutureAsync(Instrument instrument, CancellationToken cancellationToken)
    {
        var symbol = instrument.BrokerSymbol;
        using var json = await Session.SendAsync(HttpMethod.Get, $"/trsrv/futures?symbols={Uri.EscapeDataString(symbol)}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var candidates = IbWebFormats.OrderCandidates(IbWebFormats.ParseFutures(json.RootElement, symbol), instrument.ContractMonth, DateTime.UtcNow);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Nie znaleziono w IBKR kontraktu futures {symbol}" +
                (string.IsNullOrWhiteSpace(instrument.ContractMonth) ? "." : $" z miesiąca {instrument.ContractMonth}.") +
                " Sprawdź dane instrumentu w Ustawieniach → Instrumenty.");
        }

        if (string.IsNullOrWhiteSpace(instrument.TradingClass))
        {
            return candidates[0].Conid;
        }

        // Kilka klas kontraktu o tym samym symbolu (np. EUR: 6E i E7) – sprawdzamy klasę w szczegółach kontraktu.
        foreach (var candidate in candidates.Take(8))
        {
            using var info = await Session.SendAsync(HttpMethod.Get, $"/iserver/contract/{candidate.Conid}/info",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (string.Equals(IbWebFormats.ReadString(info.RootElement, "trading_class"), instrument.TradingClass, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Conid;
            }
        }

        throw new InvalidOperationException($"Nie znaleziono w IBKR kontraktu {symbol} klasy {instrument.TradingClass}.");
    }

    /// <summary>Akcje, ETF-y i indeksy: <c>/iserver/secdef/search</c>.</summary>
    private async Task<long> FindBySearchAsync(Instrument instrument, CancellationToken cancellationToken)
    {
        var secType = instrument.Type == InstrumentType.Index ? "IND" : "STK";
        using var json = await Session.SendAsync(HttpMethod.Get,
            $"/iserver/secdef/search?symbol={Uri.EscapeDataString(instrument.BrokerSymbol)}&secType={secType}",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var results = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToList() : [];
        var wantedExchange = instrument.Type == InstrumentType.Index ? instrument.Exchange : instrument.PrimaryExchange;

        bool HasSecType(JsonElement item) =>
            item.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array &&
            sections.EnumerateArray().Any(s => string.Equals(IbWebFormats.ReadString(s, "secType"), secType, StringComparison.OrdinalIgnoreCase));

        bool OnExchange(JsonElement item) =>
            string.IsNullOrWhiteSpace(wantedExchange) ||
            (IbWebFormats.ReadString(item, "description") ?? string.Empty).Contains(wantedExchange, StringComparison.OrdinalIgnoreCase) ||
            (IbWebFormats.ReadString(item, "companyHeader") ?? string.Empty).Contains(wantedExchange, StringComparison.OrdinalIgnoreCase);

        var match = results.Where(r => string.Equals(IbWebFormats.ReadString(r, "symbol"), instrument.BrokerSymbol, StringComparison.OrdinalIgnoreCase))
            .Where(HasSecType)
            .OrderByDescending(OnExchange)
            .Select(r => IbWebFormats.ReadLong(r, "conid"))
            .FirstOrDefault(c => c is > 0);

        return match ?? throw new InvalidOperationException(
            $"Nie znaleziono w IBKR instrumentu {instrument.Symbol} ({instrument.BrokerSymbol}, {secType}). " +
            "Sprawdź dane instrumentu w Ustawieniach → Instrumenty.");
    }

    /// <summary>Szczegóły kontraktu i tick (<c>/iserver/contract/{conid}/info-and-rules</c>).</summary>
    private async Task LoadContractInfoAsync(Instrument instrument, CancellationToken cancellationToken)
    {
        using var json = await Session.SendAsync(HttpMethod.Get, $"/iserver/contract/{_conid}/info-and-rules?isBuy=true",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var minTick = root.TryGetProperty("rules", out var rules) ? IbWebFormats.ReadDecimal(rules, "increment") : null;

        ContractName = IbWebFormats.ReadString(root, "local_symbol") ?? instrument.Symbol;
        ContractExpiry = IbWebFormats.ReadString(root, "maturity_date") ?? IbWebFormats.ReadString(root, "expiry_full") ?? string.Empty;
        _info = new IbInstrumentInfo(
            LongName: IbWebFormats.ReadString(root, "company_name") ?? string.Empty,
            LocalSymbol: ContractName,
            Exchange: IbWebFormats.ReadString(root, "exchange") ?? instrument.Exchange,
            PrimaryExchange: instrument.PrimaryExchange,
            Currency: IbWebFormats.ReadString(root, "currency") ?? instrument.Currency,
            TradingClass: IbWebFormats.ReadString(root, "trading_class") ?? string.Empty,
            Expiry: ContractExpiry,
            MinTick: minTick is > 0 ? minTick.Value : instrument.TickSize,
            TimeZoneId: instrument.TimeZoneId,   // Web API nie podaje strefy w szczegółach kontraktu
            Sessions: null);
    }

    /// <summary>
    /// Rodzaj danych wg pola 6509 z <c>/iserver/marketdata/snapshot</c> (pierwsze zapytanie IBKR często zwraca puste
    /// pola – ponawiamy kilka razy). Nieznany → zakładamy dane na żywo.
    /// </summary>
    private async Task<bool> DetectDelayedAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var json = await Session.SendAsync(HttpMethod.Get, $"/iserver/marketdata/snapshot?conids={_conid}&fields=31,6509",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (json.RootElement.ValueKind == JsonValueKind.Array &&
                json.RootElement.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } item &&
                IbWebFormats.ReadString(item, "6509") is { Length: > 0 } availability)
            {
                return availability.Contains('D') || availability.Contains('Y');
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    // =====================================================================
    // Historia (świece 1-min)
    // =====================================================================

    public Task<IReadOnlyList<Bar>> GetHistoryAsync(TimeSpan lookback, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return LoadHistoryAsync(now - lookback, now, trimDelayed: true, cancellationToken);
    }

    public Task<IReadOnlyList<Bar>> GetHistoryRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        LoadHistoryAsync(from, to, trimDelayed: false, cancellationToken);

    /// <summary>Świece 1-min z [from, to): kolejne zapytania cofają się od <paramref name="to"/> (po ok. 1 dniu).</summary>
    private async Task<IReadOnlyList<Bar>> LoadHistoryAsync(DateTimeOffset from, DateTimeOffset to, bool trimDelayed, CancellationToken cancellationToken)
    {
        RequireConnected();
        var collected = new SortedDictionary<DateTimeOffset, Bar>();
        var end = to;
        var part = 0;
        string? error = null;

        while (end > from)
        {
            part++;
            Status($"IBKR Web API: pobieranie historii – część {part} (świece 1 min, do {end.ToLocalTime():yyyy-MM-dd HH:mm})...");
            IReadOnlyList<Bar> chunk;
            try
            {
                chunk = await RequestBarsAsync("1min", MinuteChunkPeriod, end, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
                break;
            }

            var inRange = chunk.Where(b => b.Time >= from && b.Time < end).ToList();
            foreach (var bar in inRange)
            {
                collected[bar.Time] = bar;
            }

            // Brak starszych danych (albo brak postępu) – koniec. Przerwy w handlu (weekend) przeskakujemy dzięki temu,
            // że następne zapytanie kończy się na najstarszej otrzymanej świecy.
            var oldest = chunk.Count > 0 ? chunk.Min(b => b.Time) : (DateTimeOffset?)null;
            if (oldest is not { } o || o >= end)
            {
                break;
            }

            end = o;
        }

        if (collected.Count == 0 && error is not null)
        {
            Status($"IBKR Web API: brak danych historycznych ({error}) – profile budowane od bieżącej chwili");
            return [];
        }

        var bars = collected.Values.AsEnumerable();
        if (trimDelayed)
        {
            // Jak w TWS: ticki opóźnione przychodzą z czasem sprzed ~15 min – przycinamy historię, żeby się nie dublowały.
            // Na żywo pomijamy niedokończoną bieżącą minutę (inaczej ticki z jej reszty byłyby odrzucone jako „w historii”).
            var limit = _delayed
                ? DateTimeOffset.UtcNow.AddMinutes(-IbWebTickAssembler.DelayedMinutes)
                : DateTimeOffset.UtcNow;
            bars = bars.Where(b => b.Time.AddMinutes(1) <= limit);
        }

        var result = bars.ToList();
        Status($"IBKR Web API: pobrano {result.Count} świec historycznych");
        return result;
    }

    /// <summary>Jedno zapytanie <c>/iserver/marketdata/history</c>: świece kończące się w <paramref name="end"/>.</summary>
    private async Task<IReadOnlyList<Bar>> RequestBarsAsync(string bar, string period, DateTimeOffset? end, CancellationToken cancellationToken,
        bool? outsideRth = null)
    {
        // Indeksy (np. SPX – rynek szeroki) – tylko sesja kasowa (indeks i tak jest liczony tylko w niej); pozostałe – pełna doba.
        var daily = bar is "1d" or "1w" or "1m";
        var withOutsideRth = outsideRth ?? _instrument?.Type != InstrumentType.Index;
        var query = $"/iserver/marketdata/history?conid={_conid}&period={period}&bar={bar}&outsideRth={(withOutsideRth ? "true" : "false")}";
        if (end is { } e)
        {
            query += $"&startTime={IbWebFormats.ToApiTime(e)}";
        }

        using var json = await Session.SendAsync(HttpMethod.Get, query, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            var message = IbWebFormats.ReadString(root, "error");
            return message is null ? [] : throw new InvalidOperationException(message);
        }

        var zone = _instrument?.TimeZone ?? TimeZoneInfo.Utc;
        var bars = new List<Bar>();
        foreach (var item in data.EnumerateArray())
        {
            if (IbWebFormats.ReadLong(item, "t") is not { } ms ||
                IbWebFormats.ReadDecimal(item, "o") is not { } open ||
                IbWebFormats.ReadDecimal(item, "h") is not { } high ||
                IbWebFormats.ReadDecimal(item, "l") is not { } low ||
                IbWebFormats.ReadDecimal(item, "c") is not { } close)
            {
                continue;
            }

            var volume = IbWebFormats.ReadDecimal(item, "v") is { } v ? Math.Max(0, (long)v) : 0;
            var time = daily ? IbWebFormats.TradingDate(ms, zone) : DateTimeOffset.FromUnixTimeMilliseconds(ms);
            bars.Add(new Bar(time, open, high, low, close, volume));
        }

        return bars;
    }

    // =====================================================================
    // Świece dzienne, tygodniowe i miesięczne
    // =====================================================================

    /// <summary>
    /// Świece D/W/M za okres <paramref name="duration"/> (format TWS, np. „5 Y”) wstecz od teraz.
    /// Dla futures – tylko bieżący kontrakt (Web API nie udostępnia kontraktu ciągłego ani wygasłych serii).
    /// <paramref name="cashOnly"/> = true – tylko sesja kasowa (outsideRth = false) także dla kontraktów terminowych.
    /// </summary>
    public async Task<OhlcPage> GetOhlcBarsAsync(BarInterval interval, string duration, CancellationToken cancellationToken = default,
        bool cashOnly = false)
    {
        RequireConnected();
        var bar = interval switch
        {
            BarInterval.Day => "1d",
            BarInterval.Week => "1w",
            _ => "1m",
        };

        Status($"IBKR Web API: pobieranie świec {interval.DisplayName().ToLowerInvariant()}ch ({duration})...");
        try
        {
            var bars = await RequestBarsAsync(bar, IbWebFormats.ToPeriod(duration), end: null, cancellationToken,
                outsideRth: cashOnly ? false : null).ConfigureAwait(false);
            return new OhlcPage(bars.OrderBy(b => b.Time).ToList(), null, false);
        }
        catch (IbWebPacingException ex)
        {
            return new OhlcPage([], ex.Message, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new OhlcPage([], ex.Message, false);
        }
    }

    /// <summary>
    /// Świece 30-minutowe tylko z sesji kasowej za okres <paramref name="duration"/> (format TWS, np. "1 M")
    /// kończący się w <paramref name="end"/> (null = teraz). Czas świecy = rzeczywisty początek (UTC).
    /// </summary>
    public async Task<OhlcPage> GetIntradayBarsAsync(DateTimeOffset? end, string duration, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        Status($"IBKR Web API: pobieranie świec 30 min ({duration})...");
        try
        {
            var bars = await RequestBarsAsync("30min", IbWebFormats.ToPeriod(duration), end, cancellationToken, outsideRth: false).ConfigureAwait(false);
            return new OhlcPage(bars.OrderBy(b => b.Time).ToList(), null, false);
        }
        catch (IbWebPacingException ex)
        {
            return new OhlcPage([], ex.Message, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new OhlcPage([], ex.Message, false);
        }
    }

    // =====================================================================
    // Ticki
    // =====================================================================

    /// <summary>IBKR Web API nie udostępnia historii ticków – zawsze pusta strona z opisem.</summary>
    public Task<TickPage> GetTicksPageAsync(DateTimeOffset start, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TickPage([], NoTickHistory, false));

    /// <summary>Opis braku historii ticków w Web API.</summary>
    public const string NoTickHistory = "IBKR Web API nie udostępnia historii ticków (tylko świece) – do pobierania ticków użyj TWS API";

    public Task StartLiveAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        _assembler = new IbWebTickAssembler(_instrument!.Type == InstrumentType.Index);
        _subscription?.Dispose();
        _subscription = Session.Stream.Subscribe(_conid, OnMarketData);
        Status($"IBKR Web API: dane {DataTypeName} – {ContractName}");
        return Task.CompletedTask;
    }

    /// <summary>Wspólną sesję zastąpiono nową (np. zmiana kluczy) – notowania na żywo przenosimy na nową sesję.</summary>
    private void OnSharedReplaced()
    {
        if (_subscription is not { } old || !_session.IsDisposed)
        {
            return;
        }

        try
        {
            var stream = Session.Stream;
            old.Dispose();
            _subscription = stream.Subscribe(_conid, OnMarketData);
            Status("IBKR Web API: nowa sesja – ponowna subskrypcja notowań");
        }
        catch (Exception ex)
        {
            Status($"IBKR Web API: notowania zatrzymane – {ex.Message}");
        }
    }

    private void OnMarketData(IReadOnlyDictionary<string, string> fields)
    {
        if (_assembler is not { } assembler)
        {
            return;
        }

        var wasDelayed = assembler.IsDelayed;
        var ticks = assembler.Process(fields, DateTimeOffset.UtcNow);
        if (fields.ContainsKey("84") || fields.ContainsKey("86"))
        {
            BidAskChanged?.Invoke(assembler.Bid, assembler.Ask);
        }

        foreach (var tick in ticks)
        {
            TickReceived?.Invoke(this, tick);
        }

        if (assembler.LastPrice is { } price && fields.ContainsKey("31"))
        {
            QuoteReceived?.Invoke(this, price);
        }

        if (assembler.IsDelayed is { } delayed && delayed != wasDelayed)
        {
            _delayed = delayed;
            Status($"IBKR Web API: dane {DataTypeName} – {ContractName}");
        }
    }

    public Task DisconnectAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        _assembler = null;
        IbWebSession.StatusChanged -= OnSessionStatus;
        IbWebSession.SharedReplaced -= OnSharedReplaced;
        Status("IBKR Web API: rozłączono");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    // =====================================================================
    // Ustawienia: „Sprawdź w IB” i test połączenia
    // =====================================================================

    /// <summary>Wyszukanie instrumentu (przycisk „Sprawdź w IB”).</summary>
    public static async Task<IbInstrumentInfo> LookupAsync(IbConnectionSettings settings, Instrument instrument)
    {
        await using var feed = new IbWebDataFeed(settings);
        await feed.ConnectAsync(instrument);
        return feed._info!;
    }

    /// <summary>Test połączenia: logowanie, kontrakt, pierwsze notowanie ze strumienia (albo brak w podanym czasie).</summary>
    public static async Task<string> TestConnectionAsync(IbConnectionSettings settings, Instrument instrument, TimeSpan timeout)
    {
        await using var feed = new IbWebDataFeed(settings);
        await feed.ConnectAsync(instrument);

        var quote = new TaskCompletionSource<decimal>(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.QuoteReceived += (_, price) => quote.TrySetResult(price);
        await feed.StartLiveAsync();

        var result = $"Połączono z IBKR Web API. Kontrakt {feed.ContractName}" +
                     (string.IsNullOrWhiteSpace(feed.ContractExpiry) ? ". " : $" (wygasa {feed.ContractExpiry}). ");
        var finished = await Task.WhenAny(quote.Task, Task.Delay(timeout));
        return finished == quote.Task
            ? result + $"Dane {feed.DataTypeName}: OK – notowanie {quote.Task.Result.ToString(CultureInfo.InvariantCulture)}."
            : result + $"Dane {feed.DataTypeName}: brak notowań w ciągu {timeout.TotalSeconds:0} s. (Rynek może być zamknięty.)";
    }

    // =====================================================================
    // Pomocnicze
    // =====================================================================

    private void RequireConnected()
    {
        if (_conid <= 0 || _instrument is null)
        {
            throw new InvalidOperationException("Najpierw wywołaj ConnectAsync.");
        }
    }

    // Komunikaty wspólnej sesji trafiają do dziennika raz (w IbWebSession) – tu tylko dalej do odbiorców.
    private void OnSessionStatus(object? sender, string message) => StatusChanged?.Invoke(this, message);

    private void Status(string message)
    {
        AppLog.Status($"IBWeb {_instrument?.Symbol}".TrimEnd(), message);
        StatusChanged?.Invoke(this, message);
    }
}
