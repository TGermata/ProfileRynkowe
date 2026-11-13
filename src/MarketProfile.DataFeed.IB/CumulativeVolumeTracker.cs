namespace MarketProfile.DataFeed.IB;

/// <summary>
/// Zamienia narastający wolumen sesji (pola IB: 8 VOLUME / 74 DELAYED_VOLUME – łączna liczba kontraktów od początku
/// dnia) na przyrosty między kolejnymi aktualizacjami.
///
/// Dlaczego: w zwykłym strumieniu notowań IB podaje wielkość ostatniej transakcji tylko jako próbkę (raz na kilka sekund),
/// więc sumowanie jej daje ułamek prawdziwego wolumenu. Narastający wolumen sesji jest kompletny – jego przyrost to
/// dokładnie to, co zostało zawarte od poprzedniej aktualizacji.
/// (Klasa nie używa typów biblioteki TWS API – można ją testować bez niej.)
/// </summary>
public sealed class CumulativeVolumeTracker
{
    private decimal? _last;

    /// <summary>Czy otrzymano już choć jedną wartość narastającego wolumenu (wtedy to on jest źródłem wolumenu).</summary>
    public bool HasData => _last is not null;

    /// <summary>
    /// Przyjmuje nową wartość narastającego wolumenu i zwraca przyrost od poprzedniej:
    ///  - pierwsza wartość = punkt odniesienia (przyrost 0 – wcześniejszy wolumen jest już w historii),
    ///  - spadek wartości = nowy dzień / nowa sesja u brokera → nowy punkt odniesienia (przyrost 0),
    ///  - wartości ujemne (brak danych) są pomijane.
    /// </summary>
    public long Update(decimal cumulativeVolume)
    {
        if (cumulativeVolume < 0)
        {
            return 0;
        }

        var previous = _last;
        _last = cumulativeVolume;

        if (previous is not { } p || cumulativeVolume < p)
        {
            return 0;
        }

        return (long)(cumulativeVolume - p);
    }
}
