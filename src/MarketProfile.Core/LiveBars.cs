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
    /// </summary>
    public static Bar? LiveDayBar(IEnumerable<ProfileSnapshot> profiles, DateOnly tradingDate)
    {
        var day = profiles
            .Where(p => !p.IsEmpty && p.TradingDate == tradingDate)
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
