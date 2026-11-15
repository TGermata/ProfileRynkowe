using MarketProfile.Core;

namespace MarketProfile.DataFeed.Storage;

/// <summary>Strona historycznych transakcji od brokera (maks. ok. 1000 ticków od podanego momentu w przód).</summary>
/// <param name="Ticks">Transakcje posortowane po czasie (pusta = brak transakcji od tego momentu do końca sesji).</param>
/// <param name="Error">Opis błędu brokera, jeśli był.</param>
/// <param name="PacingViolation">Broker odrzucił zapytanie z powodu limitu zapytań – trzeba odczekać i ponowić.</param>
public sealed record TickPage(IReadOnlyList<Tick> Ticks, string? Error, bool PacingViolation);

/// <summary>Wynik pobierania ticków odcinka.</summary>
/// <param name="Saved">Liczba zapisanych ticków.</param>
/// <param name="NoTickData">Broker nie ma ticków z (części) tego odcinka – starszych nie warto pobierać.</param>
public sealed record TickDownloadResult(long Saved, bool NoTickData);

/// <summary>
/// Pobiera do bazy kompletne historyczne ticki (każdą transakcję z taśmy giełdy) z podanego odcinka,
/// stronami po ok. 1000 transakcji – tak, żeby symulacja odtwarzała rynek dokładnie tak, jak wyglądał.
///
///  - kolejna strona zaczyna się sekundę po ostatniej transakcji poprzedniej (broker zawsze oddaje pełną ostatnią sekundę),
///  - pusta strona = koniec sesji: przeskakujemy do pierwszej zapisanej świecy 1-min po tym momencie
///    (świece są pobierane wcześniej), a gdy jej nie ma – odcinek jest skończony,
///  - każda strona jest od razu zapisywana i oznaczana jako pokryta, więc przerwane pobieranie
///    (zamknięcie programu) jest kontynuowane od miejsca przerwania,
///  - ticki nagrane wcześniej na żywo w pobranym odcinku są zastępowane tickami z historii (dokładniejsze).
/// </summary>
public sealed class HistoricalTickDownloader
{
    /// <summary>Ile razy ponawiać zapytanie odrzucone przez limit zapytań.</summary>
    private const int MaxPacingRetries = 5;

    /// <summary>Po tylu pustych stronach z rzędu (mimo handlu według świec) uznajemy, że broker nie ma ticków.</summary>
    private const int MaxEmptyPagesWithTrading = 3;

    private readonly MarketDataStore _store;
    private readonly Func<DateTimeOffset, CancellationToken, Task<TickPage>> _fetchPage;
    private readonly TimeSpan _requestInterval;
    private readonly TimeSpan _pacingWait;

    /// <param name="store">Baza danych.</param>
    /// <param name="fetchPage">Pobranie jednej strony transakcji od podanego momentu.</param>
    /// <param name="requestInterval">Odstęp między zapytaniami (limit IB: 60 zapytań historycznych na 10 minut).</param>
    /// <param name="pacingWait">Przerwa po odrzuceniu zapytania przez limit zapytań.</param>
    public HistoricalTickDownloader(MarketDataStore store, Func<DateTimeOffset, CancellationToken, Task<TickPage>> fetchPage,
        TimeSpan requestInterval, TimeSpan pacingWait)
    {
        _store = store;
        _fetchPage = fetchPage;
        _requestInterval = requestInterval;
        _pacingWait = pacingWait;
    }

    /// <summary>Postęp: bieżący moment pobierania i liczba ticków zapisanych w tym odcinku.</summary>
    public event Action<DateTimeOffset, long>? Progress;

    /// <summary>
    /// Pobiera ticki odcinka [range.From, range.To). Jeśli kilka stron z rzędu jest pustych, choć według świec
    /// był wtedy handel, broker nie ma już ticków z tego okresu – pobieranie kończy się z
    /// <see cref="TickDownloadResult.NoTickData"/> (ten fragment nie jest oznaczany jako pokryty).
    /// </summary>
    public async Task<TickDownloadResult> DownloadAsync(string symbol, TimeRange range, string source, CancellationToken token)
    {
        var position = FloorToSecond(range.From);
        DateTimeOffset? skippedFrom = null; // początek przerwy bez transakcji, jeszcze nieoznaczonej jako pokryta
        var emptyWithTrading = 0;           // puste strony z rzędu w miejscach, gdzie według świec był handel
        long saved = 0;

        while (position < range.To)
        {
            token.ThrowIfCancellationRequested();
            var page = await FetchWithRetryAsync(position, token).ConfigureAwait(false);

            if (page.Ticks.Count == 0 && page.Error is { } error)
            {
                throw new InvalidOperationException(error);
            }

            var ticks = page.Ticks.Where(t => t.Time >= position && t.Time < range.To).ToList();
            if (ticks.Count == 0)
            {
                // Brak transakcji od tego momentu do końca sesji – przeskok do następnej minuty z handlem.
                // (Strona z samymi transakcjami po końcu odcinka = w odcinku nie było już handlu.)
                var nextBar = page.Ticks.Count > 0
                    ? null
                    : _store.NextBarTime(symbol, BarAggregator.FloorToMinute(position).AddMinutes(1), range.To);

                if (nextBar is not null && ++emptyWithTrading >= MaxEmptyPagesWithTrading)
                {
                    return new TickDownloadResult(saved, NoTickData: true);
                }

                skippedFrom ??= position;
                position = nextBar ?? range.To;
            }
            else
            {
                emptyWithTrading = 0;
                if (skippedFrom is { } skipped)
                {
                    // Przerwa bez transakcji przed tą stroną – oznaczamy jako pokrytą (ticków nagranych na żywo nie ruszamy).
                    _store.SaveHistoricalTicks(symbol, skipped, position, [], source, replaceExisting: false);
                    skippedFrom = null;
                }

                // Strona kończy się sekundę po ostatniej transakcji (albo na końcu odcinka, jeśli go przekroczyła).
                var end = page.Ticks[^1].Time >= range.To ? range.To : FloorToSecond(ticks[^1].Time).AddSeconds(1);
                _store.SaveHistoricalTicks(symbol, position, end, ticks, source);
                saved += ticks.Count;
                position = end;
            }

            Progress?.Invoke(position, saved);
            if (position < range.To)
            {
                await Task.Delay(_requestInterval, token).ConfigureAwait(false);
            }
        }

        if (skippedFrom is { } rest)
        {
            _store.SaveHistoricalTicks(symbol, rest, range.To, [], source, replaceExisting: false);
        }

        return new TickDownloadResult(saved, NoTickData: false);
    }

    private async Task<TickPage> FetchWithRetryAsync(DateTimeOffset start, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var page = await _fetchPage(start, token).ConfigureAwait(false);
            if (!page.PacingViolation || attempt >= MaxPacingRetries)
            {
                return page;
            }

            await Task.Delay(_pacingWait, token).ConfigureAwait(false);
        }
    }

    private static DateTimeOffset FloorToSecond(DateTimeOffset time)
    {
        var utc = time.UtcTicks;
        return new DateTimeOffset(utc - utc % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }
}
