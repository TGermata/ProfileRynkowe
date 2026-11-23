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

    /// <summary>Ustawienia alarmu – linia z alarmem jest poziomem alarmowym (null = zwykła linia).</summary>
    public PriceAlert? Alert { get; init; }

    /// <summary>Czy to poziom alarmowy.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAlert => Alert is not null;
}

/// <summary>Ile razy alarm ma zadziałać.</summary>
public enum AlertRepeat
{
    /// <summary>Jednokrotnie – po pierwszym przebiciu alarm się wyłącza (można go uzbroić ponownie).</summary>
    Once,

    /// <summary>Przy każdym przebiciu – po odsunięciu ceny od poziomu alarm uzbraja się ponownie.</summary>
    EveryCross,
}

/// <summary>Kierunek przebicia, na który reaguje alarm.</summary>
public enum AlertDirection
{
    /// <summary>W górę i w dół.</summary>
    Both,

    /// <summary>Tylko wybicie w górę (cena dochodzi do poziomu od dołu).</summary>
    Up,

    /// <summary>Tylko przebicie w dół (cena dochodzi do poziomu od góry).</summary>
    Down,
}

/// <summary>Ustawienia i stan alarmu dźwiękowego poziomu cenowego.</summary>
public sealed record PriceAlert
{
    /// <summary>Dźwięki systemowe Windows (nazwy zapisywane w <see cref="Sound"/>).</summary>
    public static readonly IReadOnlyList<string> SystemSounds = ["Exclamation", "Asterisk", "Hand", "Question", "Beep"];

    /// <summary>Jednokrotnie albo przy każdym przebiciu.</summary>
    public AlertRepeat Repeat { get; init; } = AlertRepeat.Once;

    /// <summary>Kierunek przebicia.</summary>
    public AlertDirection Direction { get; init; } = AlertDirection.Both;

    /// <summary>
    /// Tryb „przy każdym przebiciu”: o ile ticków cena musi odsunąć się od poziomu, żeby alarm uzbroił się ponownie
    /// (chroni przed serią dźwięków, gdy cena „pływa” wokół poziomu).
    /// </summary>
    public int RearmTicks { get; init; } = 2;

    /// <summary>Dźwięk: nazwa dźwięku systemowego (<see cref="SystemSounds"/>) albo ścieżka pliku .wav/.mp3.</summary>
    public string Sound { get; init; } = "Exclamation";

    /// <summary>Czy alarm jest włączony.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Alarm jednokrotny już zadziałał (jest nieaktywny do ponownego uzbrojenia).</summary>
    public bool IsTriggered { get; init; }

    /// <summary>Kiedy alarm zadziałał ostatnio (czas danych).</summary>
    public DateTimeOffset? LastTriggered { get; init; }

    /// <summary>Czy alarm może teraz zadziałać.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsArmed => IsEnabled && !IsTriggered;
}
