namespace MarketProfile.Core;

/// <summary>
/// Ustalanie strony transakcji z notowań: transakcja po cenie ask (lub wyżej) to kupno, po cenie bid (lub niżej) –
/// sprzedaż. Transakcja w środku spreadu albo bez notowań – nieznana (rozstrzyga ją <see cref="TickRule"/>).
/// </summary>
public static class TradeSideClassifier
{
    public static TradeSide FromQuote(decimal price, decimal? bid, decimal? ask)
    {
        // Notowania niepoprawne (brak, zero, odwrócone) – nie zgadujemy.
        if (bid is not { } b || ask is not { } a || b <= 0 || a <= 0 || a < b)
        {
            return TradeSide.Unknown;
        }

        return price >= a ? TradeSide.Buy
            : price <= b ? TradeSide.Sell
            : TradeSide.Unknown;
    }
}

/// <summary>
/// Reguła ticka – szacunek strony transakcji bez notowań: cena wyższa od poprzedniej transakcji to kupno, niższa –
/// sprzedaż, ta sama – strona poprzedniej transakcji. Strona zmierzona (z bid/ask) ma pierwszeństwo i staje się
/// punktem odniesienia dla kolejnych transakcji po tej samej cenie.
/// </summary>
public sealed class TickRule
{
    private decimal? _lastPrice;
    private TradeSide _lastSide;

    /// <summary>Strona transakcji: zmierzona z ticka albo oszacowana regułą ticka.</summary>
    public TradeSide Classify(Tick tick) => Classify(tick.Price, tick.Side);

    /// <inheritdoc cref="Classify(Tick)"/>
    public TradeSide Classify(decimal price, TradeSide measured)
    {
        var side = measured != TradeSide.Unknown
            ? measured
            : _lastPrice is not { } last ? TradeSide.Unknown
            : price > last ? TradeSide.Buy
            : price < last ? TradeSide.Sell
            : _lastSide;

        _lastPrice = price;
        _lastSide = side;
        return side;
    }

    /// <summary>
    /// Świeca (bez stron transakcji) – tylko aktualizuje punkt odniesienia (cenę zamknięcia), żeby pierwszy tick po
    /// historii ze świec był porównywany z właściwą ceną.
    /// </summary>
    public void Observe(decimal price)
    {
        if (_lastPrice != price)
        {
            _lastSide = TradeSide.Unknown;
        }

        _lastPrice = price;
    }
}
