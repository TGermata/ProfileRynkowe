namespace MarketProfile.Core;

/// <summary>Rodzaj średniej kroczącej.</summary>
public enum MovingAverageKind
{
    /// <summary>Prosta (arytmetyczna).</summary>
    Sma,

    /// <summary>Wykładnicza.</summary>
    Ema,
}

/// <summary>Wskaźniki techniczne liczone na świecach (wspólne dla wykresów, analiz i automatycznego tradingu).</summary>
public static class Indicators
{
    /// <summary>
    /// Prosta średnia krocząca (SMA) cen zamknięcia: średnia z <paramref name="period"/> ostatnich zamknięć.
    /// Wynik ma tyle elementów co świece; dla pierwszych <c>period − 1</c> świec – null (za mało danych).
    /// </summary>
    public static decimal?[] Sma(IReadOnlyList<Bar> bars, int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        var result = new decimal?[bars.Count];
        decimal sum = 0;

        for (var i = 0; i < bars.Count; i++)
        {
            sum += bars[i].Close;
            if (i >= period)
            {
                sum -= bars[i - period].Close;
            }

            if (i >= period - 1)
            {
                result[i] = sum / period;
            }
        }

        return result;
    }

    /// <summary>
    /// Wykładnicza średnia krocząca (EMA) cen zamknięcia. Start: SMA z pierwszych <paramref name="period"/> zamknięć,
    /// potem <c>EMA = EMA_poprz + k × (zamknięcie − EMA_poprz)</c>, gdzie <c>k = 2 / (period + 1)</c>
    /// (tak liczą TWS i TradingView). Dla pierwszych <c>period − 1</c> świec – null (za mało danych).
    /// </summary>
    public static decimal?[] Ema(IReadOnlyList<Bar> bars, int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        var result = new decimal?[bars.Count];
        if (bars.Count < period)
        {
            return result;
        }

        decimal sum = 0;
        for (var i = 0; i < period; i++)
        {
            sum += bars[i].Close;
        }

        var k = 2m / (period + 1);
        var ema = sum / period;
        result[period - 1] = ema;
        for (var i = period; i < bars.Count; i++)
        {
            ema += k * (bars[i].Close - ema);
            result[i] = ema;
        }

        return result;
    }

    /// <summary>
    /// O ile procent cena jest nad (+) albo pod (−) średnią: <c>(cena − średnia) / średnia × 100</c>;
    /// null – brak średniej (za mało świec) albo średnia ≤ 0.
    /// </summary>
    public static decimal? PercentFromAverage(decimal price, decimal? average) =>
        average is > 0 ? (price - average.Value) / average.Value * 100m : null;

    /// <summary>Średnia krocząca wybranego rodzaju (SMA albo EMA).</summary>
    public static decimal?[] MovingAverage(IReadOnlyList<Bar> bars, MovingAverageKind kind, int period) =>
        kind == MovingAverageKind.Ema ? Ema(bars, period) : Sma(bars, period);

    /// <summary>
    /// Wartość SMA „na dziś” ze świec dziennych: <paramref name="period"/> − 1 ostatnich zamknięć świec sprzed dnia
    /// <paramref name="today"/> (świeca dzienna ma datę dnia handlowego, czas 00:00 UTC) + bieżąca cena jako
    /// zamknięcie dzisiejszej świecy. Świece z dnia <paramref name="today"/> i późniejsze są pomijane
    /// (dzisiejsza świeca w bazie bywa nieaktualna, późniejsze – w symulacji – to „przyszłość”).
    /// Null, gdy za mało świec.
    /// </summary>
    public static decimal? SmaToday(IReadOnlyList<Bar> bars, DateOnly today, int period, decimal lastPrice)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        var limit = today.ToDateTime(TimeOnly.MinValue);
        var end = bars.Count;
        while (end > 0 && bars[end - 1].Time.UtcDateTime.Date >= limit)
        {
            end--;
        }

        if (end < period - 1)
        {
            return null;
        }

        var sum = lastPrice;
        for (var i = end - (period - 1); i < end; i++)
        {
            sum += bars[i].Close;
        }

        return sum / period;
    }
}
