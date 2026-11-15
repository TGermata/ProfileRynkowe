namespace MarketProfile.Core;

/// <summary>
/// Granice profili w czasie: momenty rozpoczęcia sesji (każda sesja to osobny profil).
/// Używane w symulacji do przeskakiwania „profil w przód” / „profil wstecz”.
/// Godziny sesji są w strefie <see cref="ProfileSettings.TimeZone"/>, wynik – dokładny moment (z obsługą zmiany czasu).
/// </summary>
public static class SessionBoundaries
{
    /// <summary>Najbliższy początek dowolnej sesji ściśle po <paramref name="after"/> (null, gdy brak sesji).</summary>
    public static DateTimeOffset? NextSessionStart(ProfileSettings settings, DateTimeOffset after)
    {
        DateTimeOffset? best = null;
        foreach (var start in Candidates(settings, after))
        {
            if (start > after && (best is null || start < best))
            {
                best = start;
            }
        }

        return best;
    }

    /// <summary>Najpóźniejszy początek dowolnej sesji nie później niż <paramref name="atOrBefore"/> (null, gdy brak sesji).</summary>
    public static DateTimeOffset? PreviousSessionStart(ProfileSettings settings, DateTimeOffset atOrBefore)
    {
        DateTimeOffset? best = null;
        foreach (var start in Candidates(settings, atOrBefore))
        {
            if (start <= atOrBefore && (best is null || start > best))
            {
                best = start;
            }
        }

        return best;
    }

    /// <summary>
    /// Początek sesji, w której jest <paramref name="moment"/> – czyli początek profilu, który jest w tej chwili rysowany
    /// (null, gdy moment wypada poza sesjami, np. w przerwie technicznej).
    /// </summary>
    public static DateTimeOffset? CurrentSessionStart(ProfileSettings settings, DateTimeOffset moment)
    {
        var zone = settings.TimeZone;
        var local = TimeZoneInfo.ConvertTime(moment, zone).DateTime;

        foreach (var session in settings.Sessions)
        {
            if (session.TryGetSessionStart(local, out var start))
            {
                return ToZoned(start, zone);
            }
        }

        return null;
    }

    /// <summary>Początki wszystkich sesji z dni od przedwczoraj do pojutrza (względem podanego momentu, czas giełdy).</summary>
    private static IEnumerable<DateTimeOffset> Candidates(ProfileSettings settings, DateTimeOffset around)
    {
        var zone = settings.TimeZone;
        var localDate = TimeZoneInfo.ConvertTime(around, zone).Date;

        for (var day = -2; day <= 2; day++)
        {
            foreach (var session in settings.Sessions)
            {
                var local = localDate.AddDays(day) + session.Start.ToTimeSpan();
                yield return ToZoned(local, zone);
            }
        }
    }

    /// <summary>Czas lokalny giełdy → moment; godzina nieistniejąca (przestawienie zegarów) jest przesuwana o godzinę.</summary>
    private static DateTimeOffset ToZoned(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
