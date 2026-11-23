namespace MarketProfile.Core;

/// <summary>
/// Źródło danych podające notowania bid / ask osobno od transakcji – potrzebne dla wskaźników rynku, w których
/// te pola niosą inne wartości niż ceny (np. IB VOL-NYSE: bid = wolumen spółek rosnących, ask = spadających).
/// </summary>
public interface IBidAskSource
{
    /// <summary>Nowe notowanie bid albo ask (bieżąca para; null – jeszcze nie przyszło). Wywoływane z wątku źródła danych.</summary>
    event Action<decimal?, decimal?>? BidAskChanged;
}
