namespace MarketProfile.Core;

/// <summary>Styl linii cenowej.</summary>
public enum LineStyle
{
    /// <summary>Linia ciągła.</summary>
    Solid,

    /// <summary>Linia kreskowana.</summary>
    Dashed,

    /// <summary>Linia kropkowana.</summary>
    Dotted,
}

/// <summary>Po której stronie wykresu wyświetlany jest opis linii.</summary>
public enum LabelPosition
{
    /// <summary>Opis przy lewej krawędzi wykresu.</summary>
    Left,

    /// <summary>Opis przy prawej krawędzi wykresu (obok osi cen).</summary>
    Right,
}

/// <summary>
/// Pozioma linia cenowa narysowana przez użytkownika (np. ważne wsparcie/opór, poziom z poprzedniego dnia).
/// To są DANE – kolor zapisujemy jako tekst "#RRGGBB", żeby model nie zależał od biblioteki graficznej.
/// </summary>
public sealed record PriceLine
{
    /// <summary>Unikalny identyfikator linii.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Cena, na której leży linia.</summary>
    public decimal Price { get; init; }

    /// <summary>Kolor w formacie "#RRGGBB".</summary>
    public string Color { get; init; } = "#FFD54F";

    /// <summary>Grubość linii w pikselach.</summary>
    public double Thickness { get; init; } = 2;

    /// <summary>Styl linii (ciągła, kreskowana, kropkowana).</summary>
    public LineStyle Style { get; init; } = LineStyle.Solid;

    /// <summary>Opcjonalny opis wyświetlany przy linii.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Strona wykresu, po której wyświetlany jest opis (lewa lub prawa).</summary>
    public LabelPosition LabelPosition { get; init; } = LabelPosition.Right;

    /// <summary>Czy linia jest zablokowana – zablokowanej linii nie da się przesunąć myszą.</summary>
    public bool IsLocked { get; init; }
}
