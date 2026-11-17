namespace MarketProfile.Core;

/// <summary>
/// Parametry budowy profilu rynkowego ustawiane przez użytkownika.
/// </summary>
public sealed record ProfileSettings
{
    /// <summary>Wielkość ticka instrumentu (ES/NQ: 0.25).</summary>
    public decimal TickSize { get; init; } = 0.25m;

    /// <summary>
    /// Wysokość jednego wiersza TPO wyrażona w tickach.
    /// Np. 4 ticki × 0.25 = 1 punkt na wiersz.
    /// </summary>
    public int TicksPerRow { get; init; } = 4;

    /// <summary>Długość okresu TPO – każdy okres dostaje własną literę (A, B, C...). Standardowo 30 minut.</summary>
    public TimeSpan TpoPeriod { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Procent wszystkich TPO, który tworzy obszar wartości (Value Area). Standardowo 70%.</summary>
    public decimal ValueAreaPercent { get; init; } = 0.70m;

    /// <summary>
    /// Liczba pierwszych okresów TPO tworzących Initial Balance (standardowo 2 okresy = pierwsza godzina).
    /// </summary>
    public int InitialBalancePeriods { get; init; } = 2;

    /// <summary>
    /// Reguła wyboru POC, gdy dwa wiersze mają tyle samo TPO i są równo oddalone od środka profilu.
    /// </summary>
    public PocTieBreak PocTieBreak { get; init; } = PocTieBreak.MoreTpoSide;

    /// <summary>Strefa czasowa, w której podane są godziny sesji (dla ES/NQ: Nowy Jork).</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Sesje, dla których budowane są osobne profile (domyślnie: nocna i dzienna).</summary>
    public IReadOnlyList<SessionDefinition> Sessions { get; init; } =
    [
        new SessionDefinition { Name = SessionNames.Night, Start = new TimeOnly(18, 0), End = new TimeOnly(9, 30) },
        new SessionDefinition { Name = SessionNames.Day, Start = new TimeOnly(9, 30), End = new TimeOnly(16, 0) },
    ];

    /// <summary>Wysokość wiersza w jednostkach ceny (np. 4 × 0.25 = 1.00).</summary>
    public decimal RowHeight => TickSize * TicksPerRow;

    /// <summary>
    /// Zaokrągla cenę w dół do początku wiersza TPO, do którego należy.
    /// Np. przy wierszu 1.00: cena 6001.75 → wiersz 6001.00.
    /// </summary>
    public decimal ToRowPrice(decimal price) => Math.Floor(price / RowHeight) * RowHeight;
}
