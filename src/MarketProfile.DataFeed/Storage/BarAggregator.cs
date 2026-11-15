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
    /// <param name="source">Tylko dane z tego źródła (null = ze wszystkich) – zob. <see cref="MarketDataStore.SimulationSource"/>.</param>
    public static IReadOnlyList<Bar> LoadMinuteBars(MarketDataStore store, string symbol, DateTimeOffset from, DateTimeOffset to,
        string? source = null)
    {
        var byMinute = new SortedDictionary<DateTimeOffset, Bar>();

        foreach (var bar in store.ReadBars(symbol, from, to, source))
        {
            byMinute[bar.Time] = bar;
        }

        foreach (var bar in ToMinuteBars(store.ReadTicks(symbol, from, to, source)))
        {
            byMinute[bar.Time] = bar;
        }

        return [.. byMinute.Values];
    }

    /// <summary>
    /// Dane do odtworzenia z zakresu [from, to), czytane strumieniowo: minuty, dla których są zapisane ticki,
    /// odtwarzamy tick po ticku – dokładnie tak, jak handlowano na giełdzie; minuty bez ticków (np. historia od brokera
    /// zapisana tylko jako świece) – jako całą świecę w chwili zamknięcia minuty. Nie wymyślamy żadnych cen.
    /// Ta sama zasada co w <see cref="LoadMinuteBars"/>: ticki mają pierwszeństwo.
    /// </summary>
    public static IEnumerable<ReplayItem> Replay(MarketDataStore store, string symbol, DateTimeOffset from, DateTimeOffset to,
        string? source = null) =>
        Merge(store.ReadTicks(symbol, from, to, source), store.ReadBars(symbol, from, to, source));

    /// <summary>
    /// Scala posortowane ticki i świece w jeden strumień posortowany po czasie zdarzenia. Świeca jest pomijana,
    /// jeśli w jej minucie jest choć jeden tick; w przeciwnym razie jest oddawana w chwili zamknięcia minuty.
    /// </summary>
    public static IEnumerable<ReplayItem> Merge(IEnumerable<Tick> ticks, IEnumerable<Bar> bars)
    {
        using var t = ticks.GetEnumerator();
        using var b = bars.GetEnumerator();
        var hasTick = t.MoveNext();
        var hasBar = b.MoveNext();

        while (hasBar)
        {
            var bar = b.Current;
            var barEnd = bar.Time.AddMinutes(1);

            // Ticki sprzed minuty świecy – oddajemy po kolei.
            while (hasTick && t.Current.Time < bar.Time)
            {
                yield return ReplayItem.FromTick(t.Current);
                hasTick = t.MoveNext();
            }

            // W minucie świecy są ticki – świecę pomijamy (ticki zostaną oddane w kolejnych krokach).
            if (!(hasTick && t.Current.Time < barEnd))
            {
                yield return ReplayItem.FromBar(bar);
            }

            hasBar = b.MoveNext();
        }

        while (hasTick)
        {
            yield return ReplayItem.FromTick(t.Current);
            hasTick = t.MoveNext();
        }
    }

    /// <summary>Zaokrągla czas w dół do pełnej minuty (UTC).</summary>
    public static DateTimeOffset FloorToMinute(DateTimeOffset time)
    {
        var utc = time.UtcTicks;
        return new DateTimeOffset(utc - utc % TimeSpan.TicksPerMinute, TimeSpan.Zero);
    }
}

/// <summary>
/// Jedno zdarzenie odtwarzania symulacji: prawdziwa transakcja (tick) albo – dla minuty, z której w bazie
/// nie ma ticków – cała świeca 1-min, oddawana w chwili zamknięcia minuty.
/// </summary>
/// <param name="Time">Moment zdarzenia w czasie rynku (tick – czas transakcji, świeca – koniec minuty).</param>
public readonly record struct ReplayItem(DateTimeOffset Time, Tick? Tick, Bar? Bar)
{
    public static ReplayItem FromTick(Tick tick) => new(tick.Time, tick, null);

    public static ReplayItem FromBar(Bar bar) => new(bar.Time.AddMinutes(1), null, bar);

    /// <summary>Czy to prawdziwa transakcja (a nie świeca).</summary>
    public bool IsTick => Tick is not null;
}
