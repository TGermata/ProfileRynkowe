namespace MarketProfile.Core;

/// <summary>
/// Wolumen jednego okresu TPO bieżącej sesji w porównaniu z tym samym okresem poprzedniej sesji.
/// </summary>
/// <param name="Index">Numer okresu (0 = A).</param>
/// <param name="Letter">Litera okresu.</param>
/// <param name="Start">Początek okresu (czas lokalny giełdy, bieżąca sesja).</param>
/// <param name="End">Koniec okresu.</param>
/// <param name="Today">Wolumen bieżącej sesji (dla okresu w trakcie – do bieżącej minuty); null = okres jeszcze się nie zaczął.</param>
/// <param name="Previous">Wolumen tego okresu w poprzedniej sesji (dla okresu w trakcie – do tej samej minuty).</param>
/// <param name="IsCurrent">Okres w trakcie.</param>
/// <param name="Minutes">Dla okresu w trakcie – ile minut okresu obejmuje porównanie.</param>
public sealed record VolumeComparisonRow(int Index, char Letter, DateTime Start, DateTime End, long? Today, long Previous,
    bool IsCurrent, int? Minutes = null)
{
    /// <summary>Różnica: dziś − poprzednia sesja (null = okres jeszcze się nie zaczął).</summary>
    public long? Difference => Today is { } today ? today - Previous : null;

    /// <summary>Różnica w procentach poprzedniej sesji (null = brak danych lub poprzednia sesja bez wolumenu).</summary>
    public double? DifferencePercent => Difference is { } diff && Previous > 0 ? 100.0 * diff / Previous : null;
}

/// <summary>Porównanie wolumenu bieżącej sesji z poprzednią, okres po okresie.</summary>
/// <param name="Rows">Wszystkie okresy sesji (przyszłe – tylko wolumen poprzedniej sesji).</param>
/// <param name="TodayTotal">Wolumen bieżącej sesji do teraz.</param>
/// <param name="PreviousTotal">Wolumen poprzedniej sesji do tej samej chwili sesji.</param>
/// <param name="CutoffLocal">Moment, do którego liczone jest porównanie (null = cała sesja).</param>
/// <param name="PreviousSessionTotal">Wolumen całej poprzedniej sesji (odniesienie: ile zwykle przychodzi do końca sesji).</param>
/// <param name="HasPrevious">Czy jest poprzednia sesja do porównania.</param>
public sealed record VolumeComparisonResult(IReadOnlyList<VolumeComparisonRow> Rows, long TodayTotal, long PreviousTotal, DateTime? CutoffLocal,
    long PreviousSessionTotal = 0, bool HasPrevious = false)
{
    public static VolumeComparisonResult Empty { get; } = new([], 0, 0, null);

    /// <summary>Różnica sum: dziś − poprzednia sesja do tej samej chwili.</summary>
    public long TotalDifference => TodayTotal - PreviousTotal;

    /// <summary>Różnica sum w procentach (null = poprzednia sesja bez wolumenu).</summary>
    public double? TotalPercent => PreviousTotal > 0 ? 100.0 * (TodayTotal - PreviousTotal) / PreviousTotal : null;
}

/// <summary>
/// Porównanie wolumenu w okresach TPO: bieżąca sesja vs poprzednia sesja tego samego rodzaju (dzienna z dzienną,
/// nocna z nocną). Okres w trakcie porównywany jest „do tej samej minuty” – np. 12 minut litery F dziś
/// z pierwszymi 12 minutami litery F poprzedniej sesji – na podstawie wolumenu zapisywanego co minutę.
/// </summary>
public static class VolumeComparison
{
    /// <summary>
    /// Poprzednia sesja tego samego rodzaju z danymi (ostatnia przed <paramref name="today"/>) – z poprzedniego dnia
    /// handlowego. Sesje z datą handlową w sobotę lub niedzielę są pomijane: to weekendowe „ogony” (np. piątkowa sesja
    /// nocna ES trwa tylko 16:00–17:00, potem giełda jest zamknięta do niedzieli). Null = brak.
    /// </summary>
    public static SessionProfile? FindPrevious(IEnumerable<SessionProfile> profiles, SessionProfile today) =>
        profiles
            .Where(p => p.Session.Name == today.Session.Name && p.SessionStart < today.SessionStart && p.HasData &&
                        p.TradingDate.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .MaxBy(p => p.SessionStart);

    /// <summary>
    /// Porównanie okres po okresie. <paramref name="nowLocal"/> = „teraz” w czasie giełdy (czas danych);
    /// null albo po końcu sesji = cała sesja zakończona.
    /// </summary>
    public static VolumeComparisonResult Compare(SessionProfile today, SessionProfile? previous, DateTime? nowLocal)
    {
        var periodMinutes = Math.Max(1, (int)Math.Round(today.Settings.TpoPeriod.TotalMinutes));
        var sessionMinutes = (int)Math.Ceiling((today.SessionEnd - today.SessionStart).TotalMinutes);
        var periodCount = (sessionMinutes + periodMinutes - 1) / periodMinutes;

        // Ostatnia minuta sesji objęta porównaniem (włącznie); null = cała sesja.
        int? cutoff = nowLocal is { } now && now < today.SessionEnd
            ? Math.Max(-1, (int)Math.Floor((now - today.SessionStart).TotalMinutes))
            : null;
        var currentPeriod = cutoff is { } c && c >= 0 ? c / periodMinutes : (int?)null;

        var todayMinutes = today.GetMinuteVolumes();
        var previousMinutes = previous?.GetMinuteVolumes() ?? new Dictionary<int, long>();

        var rows = new List<VolumeComparisonRow>(periodCount);
        long todayTotal = 0, previousTotal = 0;
        for (var p = 0; p < periodCount; p++)
        {
            var from = p * periodMinutes;
            var to = Math.Min(from + periodMinutes, sessionMinutes) - 1;   // ostatnia minuta okresu (włącznie)
            var start = today.SessionStart.AddMinutes(from);
            var end = today.SessionStart.AddMinutes(to + 1);

            if (cutoff is { } limit && from > limit)
            {
                // Okres jeszcze się nie zaczął – tylko wolumen poprzedniej sesji (tło: co „przed nami”).
                rows.Add(new VolumeComparisonRow(p, TpoLetters.ForPeriod(p), start, end, null, Sum(previousMinutes, from, to), false));
                continue;
            }

            var last = cutoff is { } l ? Math.Min(to, l) : to;
            var todayVolume = Sum(todayMinutes, from, last);
            var previousVolume = Sum(previousMinutes, from, last);
            var isCurrent = p == currentPeriod && last < to;
            rows.Add(new VolumeComparisonRow(p, TpoLetters.ForPeriod(p), start, end, todayVolume, previousVolume,
                isCurrent, isCurrent ? last - from + 1 : null));
            todayTotal += todayVolume;
            previousTotal += previousVolume;
        }

        var cutoffLocal = cutoff is { } m ? today.SessionStart.AddMinutes(m + 1) : (DateTime?)null;
        return new VolumeComparisonResult(rows, todayTotal, previousTotal, cutoffLocal,
            previousMinutes.Values.Sum(), previous is not null);
    }

    private static long Sum(IReadOnlyDictionary<int, long> minutes, int from, int to)
    {
        long sum = 0;
        for (var m = from; m <= to; m++)
        {
            sum += minutes.GetValueOrDefault(m);
        }

        return sum;
    }
}
