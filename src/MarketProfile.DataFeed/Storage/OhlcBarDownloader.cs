using MarketProfile.Core;

namespace MarketProfile.DataFeed.Storage;

/// <summary>Odpowiedź brokera na zapytanie o świece D/W/M.</summary>
/// <param name="Bars">Świece posortowane po dacie (pusta lista = brak danych).</param>
/// <param name="Error">Opis błędu brokera, jeśli był.</param>
/// <param name="PacingViolation">Broker odrzucił zapytanie z powodu limitu zapytań – trzeba odczekać i ponowić.</param>
public sealed record OhlcPage(IReadOnlyList<Bar> Bars, string? Error, bool PacingViolation);

/// <summary>Wynik uzupełniania jednego interwału.</summary>
/// <param name="Interval">Interwał.</param>
/// <param name="Requested">Okres zapytania wysłany do brokera (np. "5 Y", "3 D"); null = nie pytano.</param>
/// <param name="Saved">Liczba zapisanych (nowych lub zaktualizowanych) świec.</param>
/// <param name="Error">Błąd brokera (null = bez błędu).</param>
public sealed record OhlcDownloadResult(BarInterval Interval, string? Requested, int Saved, string? Error);

/// <summary>Ile historii trzymać w bazie dla każdego interwału (w latach).</summary>
public sealed record OhlcHistoryDepth(int DayYears = 5, int WeekYears = 10, int MonthYears = 20)
{
    public static OhlcHistoryDepth Default { get; } = new();

    public int YearsFor(BarInterval interval) => interval switch
    {
        BarInterval.Day => DayYears,
        BarInterval.Week => WeekYears,
        _ => MonthYears,
    };
}

/// <summary>
/// Uzupełnia w bazie świece dzienne, tygodniowe i miesięczne instrumentu:
///  - interwał bez danych – pełna historia (<see cref="OhlcHistoryDepth"/>),
///  - interwał z danymi – tylko okres od ostatniej zapisanej świecy (z zapasem), a ostatnia świeca jest nadpisywana,
///    więc bieżący, niedomknięty dzień / tydzień / miesiąc jest zawsze aktualny,
///  - zapytanie odrzucone przez limit zapytań brokera jest ponawiane po przerwie.
/// Pobieranie jest wstrzykiwane (<c>fetch</c>), więc logikę można testować bez brokera.
/// </summary>
public sealed class OhlcBarDownloader
{
    private const int MaxPacingRetries = 3;

    private readonly MarketDataStore _store;
    private readonly Func<BarInterval, string, CancellationToken, Task<OhlcPage>> _fetch;
    private readonly OhlcHistoryDepth _depth;
    private readonly TimeSpan _pacingWait;

    /// <param name="store">Baza danych.</param>
    /// <param name="fetch">Pobranie świec interwału za podany okres wstecz od teraz (format IB, np. "5 Y", "10 D").</param>
    /// <param name="depth">Głębokość historii (null = domyślna).</param>
    /// <param name="pacingWait">Przerwa po odrzuceniu zapytania przez limit zapytań (null = 1 min).</param>
    public OhlcBarDownloader(MarketDataStore store, Func<BarInterval, string, CancellationToken, Task<OhlcPage>> fetch,
        OhlcHistoryDepth? depth = null, TimeSpan? pacingWait = null)
    {
        _store = store;
        _fetch = fetch;
        _depth = depth ?? OhlcHistoryDepth.Default;
        _pacingWait = pacingWait ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Uzupełnia wszystkie interwały instrumentu (od miesięcznego do dziennego). <paramref name="cashOnly"/> – seria
    /// „tylko sesja kasowa” w bazie (fetch musi wtedy pytać brokera o świece z samej sesji kasowej).
    /// </summary>
    public async Task<IReadOnlyList<OhlcDownloadResult>> UpdateAsync(string symbol, string source, DateTimeOffset now, CancellationToken token,
        bool cashOnly = false)
    {
        var results = new List<OhlcDownloadResult>();
        foreach (var interval in BarIntervals.All)
        {
            results.Add(await UpdateIntervalAsync(symbol, interval, source, now, token, cashOnly).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Uzupełnia jeden interwał.</summary>
    public async Task<OhlcDownloadResult> UpdateIntervalAsync(
        string symbol, BarInterval interval, string source, DateTimeOffset now, CancellationToken token, bool cashOnly = false)
    {
        var range = _store.GetOhlcRange(symbol, interval, cashOnly);
        var duration = DurationFor(interval, range?.Newest, now, _depth);

        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var page = await _fetch(interval, duration, token).ConfigureAwait(false);
            if (page.PacingViolation && attempt < MaxPacingRetries)
            {
                await Task.Delay(_pacingWait, token).ConfigureAwait(false);
                continue;
            }

            if (page.Bars.Count == 0)
            {
                return new OhlcDownloadResult(interval, duration, 0, page.Error ?? (page.PacingViolation ? "limit zapytań" : null));
            }

            var saved = _store.SaveOhlcBars(symbol, interval, page.Bars, source, cashOnly);
            return new OhlcDownloadResult(interval, duration, saved, page.Error);
        }
    }

    /// <summary>Ile miesięcy świec 30 min pobrać przy pierwszym pobraniu.</summary>
    public const int IntradayHistoryMonths = 6;

    /// <summary>Najwięcej zapytań o świece 30 min w jednym uzupełnieniu (IB: najwyżej ~1 miesiąc na zapytanie).</summary>
    private const int MaxIntradayRequests = IntradayHistoryMonths + 2;

    /// <summary>
    /// Uzupełnia świece 30 min (tylko sesja kasowa): bez danych – <see cref="IntradayHistoryMonths"/> miesięcy wstecz,
    /// z danymi – od ostatniej zapisanej świecy (ona też jest pobierana ponownie, bo mogła być niedomknięta).
    /// Zapytania idą od teraz wstecz paczkami (najwyżej ok. miesiąc każda), aż do brakującego początku.
    /// </summary>
    /// <param name="fetch">Pobranie świec 30 min za okres (format IB) kończący się w podanej chwili (null = teraz).</param>
    public async Task<OhlcDownloadResult> UpdateIntradayAsync(string symbol, string source, DateTimeOffset now,
        Func<DateTimeOffset?, string, CancellationToken, Task<OhlcPage>> fetch, CancellationToken token)
    {
        const BarInterval interval = BarInterval.Minutes30;
        var newest = _store.GetOhlcRange(symbol, interval)?.Newest;
        var target = newest ?? now.AddMonths(-IntradayHistoryMonths);
        DateTimeOffset? end = null;
        var saved = 0;
        string? error = null;
        string? firstDuration = null;

        for (var request = 0; request < MaxIntradayRequests; request++)
        {
            var from = end ?? now;
            var days = (int)Math.Ceiling((from - target).TotalDays) + 1;
            var duration = days <= 30 ? $"{Math.Max(1, days)} D" : "1 M";
            firstDuration ??= duration;

            OhlcPage page;
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                page = await fetch(end, duration, token).ConfigureAwait(false);
                if (page.PacingViolation && attempt < MaxPacingRetries)
                {
                    await Task.Delay(_pacingWait, token).ConfigureAwait(false);
                    continue;
                }

                break;
            }

            error ??= page.Error ?? (page.PacingViolation && page.Bars.Count == 0 ? "limit zapytań" : null);
            if (page.Bars.Count == 0)
            {
                break;
            }

            saved += _store.SaveOhlcBars(symbol, interval, page.Bars, source);
            var oldest = page.Bars.Min(b => b.Time);
            if (oldest <= target || (end is { } previous && oldest >= previous))
            {
                break; // pobrano do brakującego początku (albo brak postępu)
            }

            end = oldest;
        }

        return new OhlcDownloadResult(interval, firstDuration, saved, error);
    }

    /// <summary>
    /// Okres zapytania (format IB): bez danych – pełna głębokość; z danymi – od ostatniej zapisanej świecy
    /// do teraz z zapasem jednego okresu (ostatnia świeca też jest pobierana ponownie). Powyżej roku – w latach.
    /// </summary>
    public static string DurationFor(BarInterval interval, DateTimeOffset? newest, DateTimeOffset now, OhlcHistoryDepth depth)
    {
        if (newest is not { } last)
        {
            return $"{depth.YearsFor(interval)} Y";
        }

        var days = Math.Max(0, (now - last).TotalDays);
        var years = (int)Math.Ceiling(days / 365.0);

        switch (interval)
        {
            case BarInterval.Day:
                var d = (int)Math.Ceiling(days) + 2;
                return d <= 365 ? $"{d} D" : $"{years + 1} Y";

            case BarInterval.Week:
                var w = (int)Math.Ceiling(days / 7) + 1;
                return w <= 52 ? $"{w} W" : $"{years + 1} Y";

            default:
                var m = (now.Year - last.Year) * 12 + now.Month - last.Month + 1;
                return m <= 12 ? $"{m} M" : $"{years + 1} Y";
        }
    }
}
