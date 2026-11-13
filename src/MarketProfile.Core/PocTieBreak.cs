namespace MarketProfile.Core;

/// <summary>
/// Reguła wyboru POC przy remisie, którego nie rozstrzyga odległość od środka profilu:
/// dwa wiersze mają tyle samo TPO i leżą równo daleko od środka (jeden poniżej, drugi powyżej).
/// </summary>
public enum PocTieBreak
{
    /// <summary>
    /// Wiersz po tej stronie środka profilu, po której jest więcej TPO.
    /// Przy równych sumach – niższa cena.
    /// </summary>
    MoreTpoSide,

    /// <summary>Niższa cena.</summary>
    Lower,

    /// <summary>Wyższa cena.</summary>
    Higher,
}
