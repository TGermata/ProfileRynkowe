namespace MarketProfile.Core;

/// <summary>
/// Świece D/W/M „na żywo”: ostatnia świeca z bazy uzupełniana bieżącymi profilami TPO (bez zapytań do brokera).
/// Profile tej samej daty handlowej (sesja nocna + dzienna) to doba ETH – tak jak świeca dzienna kontraktu ciągłego.
/// </summary>
public static class LiveBars
{
    /// <summary>
    /// Świeca dzienna z profili danego dnia handlowego (wszystkie sesje i części): otwarcie najwcześniejszego,
    /// maksimum / minimum wszystkich, zamknięcie = ostatnia cena najnowszego, wolumen = suma. Null – brak profili.
    /// Czas świecy: data handlowa, 00:00 UTC (jak świece dzienne w bazie).
    /// <paramref name="cashOnly"/> = true – tylko profile sesji dziennej (kasowej), bez nocnej.
    /// </summary>
    public static Bar? LiveDayBar(IEnumerable<ProfileSnapshot> profiles, DateOnly tradingDate, bool cashOnly = false)
    {
        var day = profiles
            .Where(p => !p.IsEmpty && p.TradingDate == tradingDate && (!cashOnly || SessionNames.IsDay(p.Session.Name)))
            .OrderBy(p => p.PartStart)
            .ToList();
        if (day.Count == 0)
        {
            return null;
        }

        var time = new DateTimeOffset(tradingDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return new Bar(time, day[0].Open, day.Max(p => p.High), day.Min(p => p.Low), day[^1].LastPrice,
            day.Sum(p => p.TotalVolume));
    }

    /// <summary>
    /// Świece z bazy z ostatnią świecą uzupełnioną świecą dnia na żywo. Gdy ostatnia świeca należy do tego samego
    /// okresu (dzień / tydzień / miesiąc): maksimum i minimum rozszerzone, zamknięcie = cena na żywo, otwarcie bez zmian,
    /// wolumen dzienny = większy z obu (W/M – z bazy, bo zawiera już część dzisiejszego). Brak świecy okresu
    /// (np. nowy dzień przed odświeżeniem z brokera) – dopisana nowa świeca. Starsze świece bez zmian.
    /// </summary>
    public static IReadOnlyList<Bar> MergeLive(IReadOnlyList<Bar> stored, BarInterval interval, Bar liveDay)
    {
        var key = interval.PeriodStart(liveDay.Time);
        var result = stored.ToList();
        var last = result.Count > 0 ? result[^1] : (Bar?)null;

        if (last is { } bar && bar.Time == key)
        {
            result[^1] = bar with
            {
                High = Math.Max(bar.High, liveDay.High),
                Low = Math.Min(bar.Low, liveDay.Low),
                Close = liveDay.Close,
                Volume = interval == BarInterval.Day ? Math.Max(bar.Volume, liveDay.Volume) : bar.Volume,
            };
        }
        else if (last is null || last.Value.Time < key)
        {
            result.Add(liveDay with { Time = key });
        }

        return result;
    }
}

/// <summary>
/// Bieżące świece wewnątrzdzienne (np. 30 min) z ticków na żywo – tylko z sesji kasowej (<see cref="CashSession"/>).
/// Ticki przychodzą z wątku źródła danych, odczyt (<see cref="Merge"/>) – z wątku UI; klasa jest bezpieczna wątkowo.
/// </summary>
public sealed class LiveIntervalBarBuilder(Instrument instrument, BarInterval interval)
{
    private readonly Lock _lock = new();
    private readonly SortedDictionary<DateTimeOffset, Bar> _bars = [];
    private long _version;

    /// <summary>Licznik zmian – rośnie z każdym przyjętym tickiem (do sprawdzania, czy odświeżyć widok).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Dodaje tick do świecy jego okresu; ticki spoza sesji kasowej są pomijane.</summary>
    public void Add(Tick tick)
    {
        if (!CashSession.Contains(instrument, tick.Time))
        {
            return;
        }

        var key = interval.PeriodStart(tick.Time);
        lock (_lock)
        {
            _bars[key] = _bars.TryGetValue(key, out var bar)
                ? bar with
                {
                    High = Math.Max(bar.High, tick.Price),
                    Low = Math.Min(bar.Low, tick.Price),
                    Close = tick.Price,
                    Volume = bar.Volume + tick.Volume,
                }
                : new Bar(key, tick.Price, tick.Price, tick.Price, tick.Price, tick.Volume);
        }

        Interlocked.Increment(ref _version);
    }

    /// <summary>Usuwa świece na żywo (np. po zatrzymaniu źródła danych albo zmianie instrumentu).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _bars.Clear();
        }

        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Świece z bazy uzupełnione świecami na żywo: świeca tego samego okresu jest rozszerzana (maksimum, minimum,
    /// zamknięcie z notowań na żywo; otwarcie z bazy – jest dokładne także po starcie w środku okresu),
    /// brakująca – dopisywana. Starsze świece bez zmian.
    /// </summary>
    public IReadOnlyList<Bar> Merge(IReadOnlyList<Bar> stored)
    {
        Bar[] live;
        lock (_lock)
        {
            live = [.. _bars.Values];
        }

        if (live.Length == 0)
        {
            return stored;
        }

        var result = new SortedDictionary<DateTimeOffset, Bar>();
        foreach (var bar in stored)
        {
            result[bar.Time] = bar;
        }

        foreach (var bar in live)
        {
            result[bar.Time] = result.TryGetValue(bar.Time, out var saved)
                ? saved with
                {
                    High = Math.Max(saved.High, bar.High),
                    Low = Math.Min(saved.Low, bar.Low),
                    Close = bar.Close,
                    Volume = Math.Max(saved.Volume, bar.Volume),
                }
                : bar;
        }

        return [.. result.Values];
    }
}
