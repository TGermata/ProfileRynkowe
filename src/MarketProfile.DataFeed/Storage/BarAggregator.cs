using MarketProfile.Core;

namespace MarketProfile.DataFeed.Storage;

/// <summary>
/// Zamiana ticków na świece 1-minutowe. Przy ładowaniu historii z bazy przetwarzamy świece zamiast
/// milionów pojedynczych ticków – profil TPO wychodzi taki sam, a ładowanie jest wielokrotnie szybsze.
/// </summary>
public static class BarAggregator
{
    /// <summary>Grupuje ticki (posortowane po czasie) w świece 1-minutowe.</summary>
    public static IEnumerable<Bar> ToMinuteBars(IEnumerable<Tick> ticks)
    {
        Bar? current = null;

        foreach (var tick in ticks)
        {
            var minute = FloorToMinute(tick.Time);

            if (current is { } bar && bar.Time == minute)
            {
                // Ta sama minuta – rozszerzamy świecę.
                current = bar with
                {
                    High = Math.Max(bar.High, tick.Price),
                    Low = Math.Min(bar.Low, tick.Price),
                    Close = tick.Price,
                    Volume = bar.Volume + tick.Volume,
                };
            }
            else
            {
                // Nowa minuta – oddajemy poprzednią świecę i zaczynamy następną.
                if (current is { } finished)
                {
                    yield return finished;
                }

                current = new Bar(minute, tick.Price, tick.Price, tick.Price, tick.Price, tick.Volume);
            }
        }

        if (current is { } last)
        {
            yield return last;
        }
    }

    /// <summary>
    /// Świece z bazy z zakresu [from, to): świece zbudowane z ticków mają pierwszeństwo,
    /// a zapisane świece uzupełniają minuty, dla których nie ma ticków.
    /// </summary>
    public static IReadOnlyList<Bar> LoadMinuteBars(MarketDataStore store, string symbol, DateTimeOffset from, DateTimeOffset to)
    {
        var byMinute = new SortedDictionary<DateTimeOffset, Bar>();

        foreach (var bar in store.ReadBars(symbol, from, to))
        {
            byMinute[bar.Time] = bar;
        }

        foreach (var bar in ToMinuteBars(store.ReadTicks(symbol, from, to)))
        {
            byMinute[bar.Time] = bar;
        }

        return [.. byMinute.Values];
    }

    /// <summary>
    /// Zamienia świecę na 4 ticki (open, low/high, high/low, close) rozłożone w ciągu minuty –
    /// używane przy odtwarzaniu zakresu, dla którego w bazie są tylko świece.
    /// </summary>
    public static IEnumerable<Tick> ToTicks(Bar bar)
    {
        var volume = Math.Max(1, bar.Volume / 4);
        var rising = bar.Close >= bar.Open;

        yield return new Tick(bar.Time, bar.Open, volume);
        yield return new Tick(bar.Time.AddSeconds(15), rising ? bar.Low : bar.High, volume);
        yield return new Tick(bar.Time.AddSeconds(30), rising ? bar.High : bar.Low, volume);
        yield return new Tick(bar.Time.AddSeconds(45), bar.Close, Math.Max(1, bar.Volume - 3 * volume));
    }

    /// <summary>Zaokrągla czas w dół do pełnej minuty (UTC).</summary>
    public static DateTimeOffset FloorToMinute(DateTimeOffset time)
    {
        var utc = time.UtcTicks;
        return new DateTimeOffset(utc - utc % TimeSpan.TicksPerMinute, TimeSpan.Zero);
    }
}
