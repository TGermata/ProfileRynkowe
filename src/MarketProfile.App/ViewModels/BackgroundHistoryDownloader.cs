using MarketProfile.Core;
using MarketProfile.DataFeed.IB;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Pobieranie historii w tle: po połączeniu z brokerem pobiera do lokalnej bazy dane wszystkich instrumentów
/// z zaznaczonym „Pobieraj dane”, żeby można było później odtwarzać je w symulacji:
///  1. świece 1-minutowe z ostatnich N dni (szybko – kilka zapytań),
///  2. kompletne ticki (każda transakcja) z ostatnich M dni – symulacja odtwarza wtedy rynek dokładnie
///     tak, jak wyglądał. Ticki pobierają się powoli (limit IB), więc w jednym przebiegu każdy instrument
///     dostaje jedną dobę (od najnowszej), a przebiegi idą jeden za drugim, aż wszystko będzie pobrane,
///  3. świece dzienne, tygodniowe i miesięczne (<see cref="OhlcBarDownloader"/>) – najpierw pełna historia,
///     potem co 30 minut tylko ostatnie świece (bieżący dzień / tydzień / miesiąc),
///  4. świece D/W/M „rynku szerokiego” (np. SPX) – także gdy nie ma zaznaczonego „Pobieraj dane” (tylko D/W/M).
///
///  - działa w osobnym zadaniu – okno aplikacji nie jest blokowane,
///  - używa własnego połączenia z TWS (Client ID o 1 większy niż główne), więc nie przeszkadza danym na żywo,
///  - pobiera tylko brakujące odcinki (<see cref="HistoryGaps"/>, pokrycie ticków w bazie) – przerwane pobieranie
///    jest kontynuowane po ponownym uruchomieniu; gdy wszystko jest pobrane, sprawdza przyrost co 30 minut,
///  - błąd jednego instrumentu (np. brak uprawnień) nie przerywa pobierania pozostałych.
/// </summary>
public sealed class BackgroundHistoryDownloader
{
    /// <summary>Co ile ponawiać przebieg, gdy wszystko jest już pobrane (dociągnięcie przyrostu danych).</summary>
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(30);

    /// <summary>Przerwa między instrumentami – ograniczenie liczby zapytań o historię (limity IB).</summary>
    private static readonly TimeSpan PauseBetweenInstruments = TimeSpan.FromSeconds(3);

    /// <summary>Odstęp między zapytaniami o ticki: limit IB to 60 zapytań historycznych na 10 minut.</summary>
    private static readonly TimeSpan TickRequestInterval = TimeSpan.FromSeconds(10.5);

    /// <summary>Przerwa po odrzuceniu zapytania przez limit IB (pacing violation).</summary>
    private static readonly TimeSpan PacingWait = TimeSpan.FromMinutes(1);

    /// <summary>Najnowszych minut nie pobieramy jako ticków – broker może je jeszcze uzupełniać.</summary>
    private static readonly TimeSpan TickLag = TimeSpan.FromMinutes(2);

    private readonly IReadOnlyList<Instrument> _instruments;
    private readonly Instrument? _broadMarket;   // rynek szeroki spoza listy pobierania – tylko świece D/W/M
    private readonly IbConnectionSettings _connection;
    private readonly MarketDataStore _store;
    private readonly int _days;
    private readonly int _tickDays;

    // Instrumenty, dla których starsza historia świec została już pobrana (albo broker jej nie ma) –
    // przy kolejnych przebiegach pobieramy tylko nowe dane.
    private readonly HashSet<string> _olderDone = [];

    // Granica, przed którą broker nie ma ticków danego instrumentu (wykryta w tym uruchomieniu) – starszych nie pobieramy.
    private readonly Dictionary<string, DateTimeOffset> _tickFloor = [];

    // Kiedy ostatnio uzupełniono świece D/W/M instrumentu (w tym uruchomieniu) – odświeżane co RepeatInterval.
    private readonly Dictionary<string, DateTimeOffset> _ohlcUpdatedAt = [];

    public BackgroundHistoryDownloader(IReadOnlyList<Instrument> instruments, IbConnectionSettings mainConnection,
        MarketDataStore store, int days, int tickDays, Instrument? broadMarket = null)
    {
        _instruments = instruments;
        _broadMarket = broadMarket is { } b && instruments.All(i => i.Symbol != b.Symbol) ? b : null;
        _connection = mainConnection with { ClientId = mainConnection.ClientId + 1 };
        _store = store;
        _days = Math.Max(1, days);
        _tickDays = Math.Clamp(tickDays, 0, _days);
    }

    /// <summary>Postęp do wyświetlenia (wywoływane z wątku pobierania).</summary>
    public event EventHandler<string>? Progress;

    /// <summary>Postęp pobierania ticków bieżącej doby (wywoływane z wątku pobierania, ok. co 10 s).</summary>
    public event EventHandler<TickDownloadStatus>? TickProgress;

    /// <summary>Koniec przebiegu: czy zostały ticki do pobrania (następny przebieg zaraz) i ewentualne błędy.</summary>
    public event EventHandler<BackgroundPassSummary>? PassFinished;

    /// <summary>Odstęp do następnego przebiegu, gdy wszystko jest już pobrane.</summary>
    public static TimeSpan RepeatAfter => RepeatInterval;

    /// <summary>Pętla pobierania – działa do anulowania.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            long bars = 0, ticks = 0;
            var moreTicks = false;
            var errors = new List<string>();

            for (var i = 0; i < _instruments.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var instrument = _instruments[i];
                var prefix = $"Dane w tle: {instrument.Symbol} {i + 1}/{_instruments.Count}";

                try
                {
                    var result = await DownloadAsync(instrument, prefix, token).ConfigureAwait(false);
                    bars += result.Bars;
                    ticks += result.Ticks;
                    moreTicks |= result.MoreTicks;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{instrument.Symbol}: {ex.Message}");
                }

                await Task.Delay(PauseBetweenInstruments, token).ConfigureAwait(false);
            }

            // Rynek szeroki (np. SPX) – tylko świece D/W/M.
            if (_broadMarket is { } broad)
            {
                try
                {
                    bars += await UpdateBroadMarketAsync(broad, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{broad.Symbol}: {ex.Message}");
                }
            }

            var state = moreTicks && errors.Count == 0 ? "pobieranie ticków trwa" : "gotowe";
            var summary = $"Dane w tle: {state} ({_instruments.Count} instr., +{bars:N0} świec, +{ticks:N0} ticków, {DateTime.Now:HH:mm})";
            if (errors.Count > 0)
            {
                summary += $" – błędy: {string.Join("; ", errors)}";
            }

            Progress?.Invoke(this, summary);
            PassFinished?.Invoke(this, new BackgroundPassSummary(moreTicks && errors.Count == 0,
                errors.Count > 0 ? string.Join("; ", errors) : null));

            // Zostały ticki do pobrania – następny przebieg od razu (po błędzie odczekujemy, żeby nie ponawiać go w kółko).
            if (!moreTicks || errors.Count > 0)
            {
                await Task.Delay(RepeatInterval, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Świece D/W/M rynku szerokiego (co <see cref="RepeatInterval"/>); zwraca liczbę zapisanych świec.</summary>
    private async Task<long> UpdateBroadMarketAsync(Instrument instrument, CancellationToken token)
    {
        var symbol = instrument.Symbol;
        if (_ohlcUpdatedAt.TryGetValue(symbol, out var at) && DateTimeOffset.UtcNow - at < RepeatInterval)
        {
            return 0;
        }

        Progress?.Invoke(this, $"Dane w tle: {symbol} (rynek szeroki) – świece D/W/M...");
        string? lastFeedMessage = null;
        await using var feed = IbFeeds.Create(_connection);
        feed.StatusChanged += (_, message) => lastFeedMessage = message;
        try
        {
            await feed.ConnectAsync(instrument, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException(lastFeedMessage is { } m && !m.Contains("łączenie") ? m : ex.Message, ex);
        }

        var results = await OhlcBarUpdater.UpdateAsync(feed, symbol, _store, token).ConfigureAwait(false);
        _ohlcUpdatedAt[symbol] = DateTimeOffset.UtcNow;
        if (OhlcBarUpdater.Errors(results) is { } error)
        {
            throw new InvalidOperationException($"świece D/W/M: {error}");
        }

        return results.Sum(r => r.Saved);
    }

    /// <summary>Wynik przebiegu dla jednego instrumentu.</summary>
    private readonly record struct PassResult(long Bars, long Ticks, bool MoreTicks);

    /// <summary>
    /// Jeden przebieg dla instrumentu: brakujące świece, a potem jedna doba brakujących ticków (najnowsza).
    /// Z brokerem łączy się tylko wtedy, gdy jest coś do pobrania.
    /// </summary>
    private async Task<PassResult> DownloadAsync(Instrument instrument, string prefix, CancellationToken token)
    {
        var symbol = instrument.Symbol;
        var now = DateTimeOffset.UtcNow;
        var existing = _store.GetBarRange(symbol, MarketDataStore.SourceBroker);
        var olderDone = _olderDone.Contains(symbol);
        var barGaps = HistoryGaps.Missing(existing, now, _days)
            // Odcinek sprzed najstarszej świecy pomijamy, jeśli już raz o niego pytaliśmy (broker go nie ma).
            .Where(g => !(olderDone && existing is { } e && g.To <= e.Oldest))
            .ToList();

        var ohlcDue = !_ohlcUpdatedAt.TryGetValue(symbol, out var ohlcAt) || now - ohlcAt >= RepeatInterval;
        if (barGaps.Count == 0 && TickChunks(symbol, now).Count == 0 && !ohlcDue)
        {
            return new PassResult(0, 0, false);
        }

        Progress?.Invoke(this, $"{prefix} – łączenie...");
        string? lastFeedMessage = null;

        await using var feed = IbFeeds.Create(_connection);
        feed.StatusChanged += (_, message) => lastFeedMessage = message;

        try
        {
            await feed.ConnectAsync(instrument, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            // Np. Client ID zajęty albo nieznaleziony kontrakt – opis z TWS jest zwykle dokładniejszy.
            throw new InvalidOperationException(lastFeedMessage is { } m && !m.Contains("łączenie") ? m : ex.Message, ex);
        }

        // 1. Świece 1-min.
        long savedBars = 0;
        foreach (var gap in barGaps)
        {
            token.ThrowIfCancellationRequested();
            Progress?.Invoke(this, $"{prefix} – pobieranie świec {gap.Length.TotalDays:0.#} dni...");

            var bars = await feed.GetHistoryRangeAsync(gap.From, gap.To, token).ConfigureAwait(false);
            if (bars.Count > 0)
            {
                _store.InsertBars(symbol, bars, MarketDataStore.SourceBroker);
                savedBars += bars.Count;
            }

            Progress?.Invoke(this, $"{prefix} – zapisano {savedBars:N0} świec");
        }

        // Starsza historia: raz na uruchomienie – jeśli broker nie ma starszych danych, nie pytamy w kółko.
        _olderDone.Add(symbol);

        // 2. Świece D/W/M (krótkie zapytania – przed tickami, które mogą trwać długo).
        if (ohlcDue)
        {
            Progress?.Invoke(this, $"{prefix} – świece D/W/M...");
            var ohlc = await OhlcBarUpdater.UpdateAsync(feed, symbol, _store, token).ConfigureAwait(false);
            _ohlcUpdatedAt[symbol] = DateTimeOffset.UtcNow;
            savedBars += ohlc.Sum(r => r.Saved);
            if (OhlcBarUpdater.Errors(ohlc) is { } ohlcError)
            {
                Progress?.Invoke(this, $"{prefix} – świece D/W/M: {ohlcError}");
            }
        }

        // 3. Ticki: jedna doba (najnowsza brakująca); świece z kroku 1 pozwalają przeskakiwać przerwy w handlu.
        var tickChunks = TickChunks(symbol, DateTimeOffset.UtcNow);
        if (tickChunks.Count == 0)
        {
            return new PassResult(savedBars, 0, false);
        }

        var chunk = tickChunks[0];
        var zone = instrument.TimeZone;
        var day = TimeZoneInfo.ConvertTime(chunk.From, zone).ToString("yyyy-MM-dd");
        var left = tickChunks.Count;
        var downloader = new HistoricalTickDownloader(_store,
            (start, ct) => IbFeeds.GetTicksPageAsync(feed, start, ct), TickRequestInterval, PacingWait);

        // Postęp doby w procentach liczony po minutach handlu (świece), nie po czasie zegarowym – noc i przerwy się nie liczą.
        var dayStart = new DateTimeOffset(chunk.From.UtcDateTime.Date, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        var tradingMinutes = _store.CountBars(symbol, dayStart, dayEnd);
        void Report(DateTimeOffset position, long count)
        {
            var done = tradingMinutes > 0
                ? _store.CountBars(symbol, dayStart, position) / (double)tradingMinutes
                : (position - dayStart) / (dayEnd - dayStart);
            TickProgress?.Invoke(this, new TickDownloadStatus(symbol, DateOnly.FromDateTime(dayStart.UtcDateTime),
                Math.Clamp(done, 0, 1), position, zone, left, count));
            Progress?.Invoke(this,
                $"{prefix} – ticki {day}: do {TimeZoneInfo.ConvertTime(position, zone):HH:mm}, {count:N0} ticków (zostało dób: {left})");
        }

        downloader.Progress += Report;
        Report(chunk.From, 0);

        Progress?.Invoke(this, $"{prefix} – pobieranie ticków {day} (zostało dób: {left})...");
        var result = await downloader.DownloadAsync(symbol, chunk, MarketDataStore.SourceBroker, token).ConfigureAwait(false);

        if (result.NoTickData)
        {
            // Broker nie ma ticków z tak dawna – starszych dób w tym uruchomieniu już nie pobieramy.
            _tickFloor[symbol] = chunk.To;
            Progress?.Invoke(this, $"{prefix} – IB nie ma ticków z {day} i wcześniej (zostają świece 1-min)");
            return new PassResult(savedBars, result.Saved, false);
        }

        return new PassResult(savedBars, result.Saved, left > 1);
    }

    /// <summary>Brakujące doby ticków instrumentu (od najnowszej); pusta lista, gdy ticki są wyłączone lub pobrane.</summary>
    private IReadOnlyList<TimeRange> TickChunks(string symbol, DateTimeOffset now)
    {
        if (_tickDays == 0)
        {
            return [];
        }

        var to = BarAggregator.FloorToMinute(now - TickLag);
        var from = now - TimeSpan.FromDays(_tickDays);
        if (_tickFloor.TryGetValue(symbol, out var floor) && floor > from)
        {
            from = floor;
        }

        // Pomijamy bardzo krótkie odcinki (np. kilka minut od poprzedniego przebiegu) – dociągniemy je później.
        return HistoryGaps.MissingTickChunks(_store.GetTickCoverage(symbol), from, to)
            .Where(c => c.Length >= HistoryGaps.MinGap)
            .ToList();
    }
}

/// <summary>Postęp pobierania ticków jednej doby (UTC – doba handlowa CME zaczyna się wieczorem dnia poprzedniego).</summary>
/// <param name="Symbol">Instrument.</param>
/// <param name="Day">Pobierana doba (UTC).</param>
/// <param name="Percent">Ułamek minut handlu tej doby, które mają już ticki (0–1).</param>
/// <param name="Position">Moment, do którego pobrano ticki.</param>
/// <param name="Zone">Strefa czasowa instrumentu (do wyświetlenia godziny).</param>
/// <param name="DaysLeft">Ile dób zostało do pobrania łącznie z bieżącą.</param>
/// <param name="Ticks">Liczba ticków zapisanych w tej dobie w tym przebiegu.</param>
public sealed record TickDownloadStatus(string Symbol, DateOnly Day, double Percent, DateTimeOffset Position,
    TimeZoneInfo Zone, int DaysLeft, long Ticks);

/// <summary>Wynik przebiegu pobierania w tle.</summary>
/// <param name="MoreTicks">Zostały ticki do pobrania – następny przebieg zaczyna się od razu.</param>
/// <param name="Error">Błędy instrumentów (null = bez błędów); po błędzie następny przebieg za 30 minut.</param>
public sealed record BackgroundPassSummary(bool MoreTicks, string? Error);
