namespace MarketProfile.Core;

/// <summary>Źródło danych, które wie, czy notowania są na żywo, czy opóźnione (np. brak subskrypcji – ok. 15 min).</summary>
public interface IMarketDataKindSource
{
    /// <summary>true – notowania opóźnione, false – na żywo, null – jeszcze nie wiadomo (brak notowań).</summary>
    bool? IsDelayedData { get; }
}
