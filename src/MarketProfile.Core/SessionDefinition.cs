namespace MarketProfile.Core;

/// <summary>
/// Definicja sesji handlowej, dla której rysujemy osobny profil rynkowy
/// (np. sesja dzienna 09:30–16:00 i sesja nocna 18:00–09:30 czasu nowojorskiego).
/// Godziny są podawane w strefie czasowej ustawionej w <see cref="ProfileSettings.TimeZone"/>.
/// </summary>
public sealed record SessionDefinition
{
    /// <summary>Nazwa sesji wyświetlana na wykresie, np. "Dzienna" lub "Nocna".</summary>
    public required string Name { get; init; }

    /// <summary>Godzina rozpoczęcia sesji.</summary>
    public required TimeOnly Start { get; init; }

    /// <summary>Godzina zakończenia sesji (jeśli jest wcześniejsza niż start, sesja przechodzi przez północ).</summary>
    public required TimeOnly End { get; init; }

    /// <summary>Czy sesja zaczyna się jednego dnia, a kończy następnego (typowe dla sesji nocnej).</summary>
    public bool CrossesMidnight => End <= Start;

    /// <summary>
    /// Długość sesji. Odejmowanie TimeOnly "zawija się" przez północ, więc działa też dla sesji nocnej.
    /// Gdy start == koniec, traktujemy sesję jako całą dobę.
    /// </summary>
    public TimeSpan Duration => End == Start ? TimeSpan.FromDays(1) : End - Start;

    /// <summary>
    /// Sprawdza, czy podany czas lokalny (w strefie giełdy) należy do tej sesji.
    /// Jeśli tak – zwraca moment rozpoczęcia sesji, do której należy (to jest "klucz" profilu).
    /// </summary>
    /// <param name="localTime">Czas w strefie czasowej sesji.</param>
    /// <param name="sessionStart">Początek sesji zawierającej ten czas.</param>
    public bool TryGetSessionStart(DateTime localTime, out DateTime sessionStart)
    {
        var timeOfDay = TimeOnly.FromDateTime(localTime);
        var date = localTime.Date;

        if (!CrossesMidnight)
        {
            // Zwykła sesja w obrębie jednego dnia, np. 09:30–16:00.
            if (timeOfDay >= Start && timeOfDay < End)
            {
                sessionStart = date + Start.ToTimeSpan();
                return true;
            }
        }
        else
        {
            // Sesja przez północ, np. 18:00–09:30.
            if (timeOfDay >= Start)
            {
                // Wieczorna część – sesja zaczęła się dzisiaj.
                sessionStart = date + Start.ToTimeSpan();
                return true;
            }

            if (timeOfDay < End)
            {
                // Poranna część – sesja zaczęła się wczoraj wieczorem.
                sessionStart = date.AddDays(-1) + Start.ToTimeSpan();
                return true;
            }
        }

        sessionStart = default;
        return false;
    }
}
