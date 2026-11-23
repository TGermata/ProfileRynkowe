namespace MarketProfile.DataFeed.Storage;

/// <summary>Odcinek czasu [From, To) do pobrania od brokera.</summary>
public readonly record struct TimeRange(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Length => To - From;
}

/// <summary>
/// Wyznacza, jakie odcinki historii trzeba jeszcze pobrać, żeby w bazie było <c>days</c> ostatnich dni:
/// pobieramy tylko to, czego brakuje – nowsze dane (od ostatniej zapisanej świecy do teraz)
/// i starsze (od „teraz − dni” do najstarszej zapisanej świecy).
/// </summary>
public static class HistoryGaps
{
    /// <summary>Krótszych brakujących odcinków nie pobieramy (np. kilka minut od ostatniego przebiegu).</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(5);

    /// <param name="existing">Zakres świec już zapisanych w bazie (null = brak danych).</param>
    /// <param name="now">Bieżący moment.</param>
    /// <param name="days">Ile ostatnich dni ma być w bazie.</param>
    public static IReadOnlyList<TimeRange> Missing((DateTimeOffset Oldest, DateTimeOffset Newest)? existing, DateTimeOffset now, int days)
    {
        var wantedFrom = now - TimeSpan.FromDays(days);
        if (existing is not { } e)
        {
            return [new TimeRange(wantedFrom, now)];
        }

        var result = new List<TimeRange>();

        // Nowsze dane: od końca ostatniej zapisanej świecy do teraz.
        var newestEnd = e.Newest.AddMinutes(1);
        var newFrom = newestEnd > wantedFrom ? newestEnd : wantedFrom;
        if (now - newFrom >= MinGap)
        {
            result.Add(new TimeRange(newFrom, now));
        }

        // Starsze dane: od „teraz − dni” do najstarszej zapisanej świecy.
        // Margines 1 dnia: dane rzadko zaczynają się dokładnie o tej porze (noc, weekend, święto).
        if (e.Oldest - wantedFrom > TimeSpan.FromDays(1))
        {
            result.Add(new TimeRange(wantedFrom, e.Oldest));
        }

        return result;
    }

    /// <summary>
    /// Brakujące odcinki ticków: zakres [<paramref name="from"/>, <paramref name="to"/>) bez odcinków już pokrytych
    /// (<see cref="MarketDataStore.GetTickCoverage"/>), pocięty na kawałki po jednej dobie UTC –
    /// od najnowszego, żeby najpierw były gotowe ostatnie dni.
    /// </summary>
    public static IReadOnlyList<TimeRange> MissingTickChunks(IReadOnlyList<TimeRange> covered, DateTimeOffset from, DateTimeOffset to)
    {
        var missing = new List<TimeRange>();
        var position = from;

        foreach (var c in covered.OrderBy(c => c.From))
        {
            if (c.To <= position)
            {
                continue;
            }

            if (c.From >= to)
            {
                break;
            }

            if (c.From > position)
            {
                missing.Add(new TimeRange(position, c.From));
            }

            position = c.To;
        }

        if (position < to)
        {
            missing.Add(new TimeRange(position, to));
        }

        var chunks = new List<TimeRange>();
        foreach (var gap in missing)
        {
            var start = gap.From;
            while (start < gap.To)
            {
                var nextDay = new DateTimeOffset(start.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
                var end = nextDay < gap.To ? nextDay : gap.To;
                chunks.Add(new TimeRange(start, end));
                start = end;
            }
        }

        chunks.Reverse();
        return chunks;
    }

    /// <summary>
    /// Ile dób (UTC) z zakresu [<paramref name="from"/>, <paramref name="to"/>) ma już komplet ticków:
    /// doba jest kompletna, jeśli nie brakuje w niej żadnego odcinka dłuższego niż <see cref="MinGap"/>.
    /// </summary>
    public static (int Complete, int Total) TickDays(IReadOnlyList<TimeRange> covered, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            return (0, 0);
        }

        var missingDays = MissingTickChunks(covered, from, to)
            .Where(c => c.Length >= MinGap)
            .Select(c => c.From.UtcDateTime.Date)
            .ToHashSet();

        var total = 0;
        for (var day = from.UtcDateTime.Date; day < to.UtcDateTime; day = day.AddDays(1))
        {
            total++;
        }

        return (total - missingDays.Count, total);
    }
}
