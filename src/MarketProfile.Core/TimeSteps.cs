namespace MarketProfile.Core;

/// <summary>
/// Kroki płynnego przewijania czasu w symulacji: od <c>from</c> do <c>to</c> (w przód lub wstecz) co jeden okres TPO,
/// a przy długich odcinkach – co kilka okresów, tak żeby kroków nie było więcej niż <c>maxSteps</c>.
/// Ostatni krok to zawsze dokładnie <c>to</c>.
/// </summary>
public static class TimeSteps
{
    public static IReadOnlyList<DateTimeOffset> Between(DateTimeOffset from, DateTimeOffset to, TimeSpan period, int maxSteps)
    {
        if (from == to || period <= TimeSpan.Zero)
        {
            return from == to ? [] : [to];
        }

        var span = to - from;
        var count = (long)Math.Ceiling(Math.Abs(span.Ticks) / (double)period.Ticks); // liczba okresów do przejścia
        var stride = Math.Max(1, (long)Math.Ceiling(count / (double)Math.Max(1, maxSteps)));
        var direction = span > TimeSpan.Zero ? 1 : -1;

        var result = new List<DateTimeOffset>();
        for (var i = stride; i < count; i += stride)
        {
            result.Add(from + TimeSpan.FromTicks(period.Ticks * i * direction));
        }

        result.Add(to);
        return result;
    }
}
