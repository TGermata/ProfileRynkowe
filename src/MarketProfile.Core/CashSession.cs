namespace MarketProfile.Core;

/// <summary>
/// Sesja kasowa (dzienna) instrumentu – np. 9:30–16:00 czasu nowojorskiego dla SPX i SPY – w dni robocze.
/// Używana przez wykres „Szeroki rynek” (świece tylko z sesji kasowej) i wskaźnik „Oddech rynku”.
/// </summary>
public static class CashSession
{
    /// <summary>Czy moment należy do sesji kasowej (dzień roboczy, godzina w [DayStart, DayEnd)).</summary>
    public static bool Contains(Instrument instrument, DateTimeOffset time) => DateOf(instrument, time) is not null;

    /// <summary>Data sesji kasowej, do której należy moment (null – poza sesją).</summary>
    public static DateOnly? DateOf(Instrument instrument, DateTimeOffset time)
    {
        var local = TimeZoneInfo.ConvertTime(time, instrument.TimeZone).DateTime;
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return null;
        }

        var timeOfDay = TimeOnly.FromDateTime(local);
        return timeOfDay >= instrument.DayStart && timeOfDay < instrument.DayEnd ? DateOnly.FromDateTime(local) : null;
    }

    /// <summary>
    /// Ostatnia sesja kasowa, która już się zaczęła (trwająca albo zakończona): przed otwarciem i w weekend – poprzednia.
    /// Święta giełdowe nie są znane – taka sesja po prostu nie ma danych.
    /// </summary>
    public static (DateOnly Date, DateTimeOffset Start, DateTimeOffset End) Latest(Instrument instrument, DateTimeOffset now)
    {
        var zone = instrument.TimeZone;
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var i = 0; i < 10; i++, date = date.AddDays(-1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            var start = At(date, instrument.DayStart, zone);
            if (start <= now)
            {
                return (date, start, At(date, instrument.DayEnd, zone));
            }
        }

        throw new InvalidOperationException("Nie znaleziono sesji kasowej.");
    }

    private static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
