namespace MarketProfile.Core;

/// <summary>
/// Źródło danych, które może utracić połączenie z brokerem (np. restart TWS / IB Gateway).
/// Aplikacja po takim zdarzeniu zatrzymuje źródło i próbuje połączyć się ponownie.
/// </summary>
public interface IConnectionLossAware
{
    /// <summary>Połączenie zostało zerwane (nie przez nas); argument – opis dla użytkownika. Wywoływane z wątku w tle.</summary>
    event EventHandler<string>? ConnectionLost;
}
