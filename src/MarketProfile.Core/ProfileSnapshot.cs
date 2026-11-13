namespace MarketProfile.Core;

/// <summary>
/// Jeden wiersz (poziom cenowy) profilu.
/// </summary>
/// <param name="Price">Dolna cena wiersza (cena jest zaokrąglona do wysokości wiersza TPO).</param>
/// <param name="Periods">Numery okresów TPO (posortowane), w których cena odwiedziła ten wiersz – każdy to jedna litera.</param>
/// <param name="Volume">Łączny wolumen transakcji w tym wierszu.</param>
public sealed record PriceLevel(decimal Price, IReadOnlyList<int> Periods, long Volume)
{
    /// <summary>Liczba TPO (liter) w wierszu.</summary>
    public int TpoCount => Periods.Count;

    /// <summary>Litery TPO w wierszu, np. "ABDF".</summary>
    public string Letters => string.Concat(Periods.Select(TpoLetters.ForPeriod));
}

/// <summary>
/// Okres TPO (np. 30 minut) z przypisaną literą.
/// </summary>
/// <param name="Index">Numer okresu od początku sesji (0 = A).</param>
/// <param name="Start">Początek okresu (czas lokalny sesji).</param>
/// <param name="End">Koniec okresu (czas lokalny sesji).</param>
/// <param name="High">Najwyższa cena w okresie.</param>
/// <param name="Low">Najniższa cena w okresie.</param>
public sealed record TpoPeriod(int Index, DateTime Start, DateTime End, decimal High, decimal Low)
{
    /// <summary>Litera okresu.</summary>
    public char Letter => TpoLetters.ForPeriod(Index);
}

/// <summary>
/// Niezmienna "migawka" profilu w danym momencie – razem z wyliczonymi poziomami (POC, Value Area, IB).
/// Warstwa prezentacji rysuje WYŁĄCZNIE migawki, dzięki czemu nie ma problemów z wątkami:
/// dane mogą być w tym czasie dalej aktualizowane przez strumień ticków.
/// </summary>
public sealed record ProfileSnapshot
{
    /// <summary>Sesja (dzienna/nocna), której dotyczy profil.</summary>
    public required SessionDefinition Session { get; init; }

    /// <summary>Data handlowa profilu (dla sesji nocnej – data dnia, w którym sesja się kończy).</summary>
    public required DateOnly TradingDate { get; init; }

    /// <summary>Początek sesji (czas lokalny giełdy).</summary>
    public required DateTime SessionStart { get; init; }

    /// <summary>Koniec sesji (czas lokalny giełdy).</summary>
    public required DateTime SessionEnd { get; init; }

    /// <summary>Wysokość wiersza TPO w jednostkach ceny.</summary>
    public required decimal RowHeight { get; init; }

    /// <summary>Wielkość ticka instrumentu (np. do przyciągania linii cenowych do pełnych ticków).</summary>
    public decimal TickSize { get; init; } = 0.25m;

    /// <summary>Wiersze profilu od najniższej do najwyższej ceny (bez przerw – puste wiersze mają 0 TPO).</summary>
    public required IReadOnlyList<PriceLevel> Levels { get; init; }

    /// <summary>Okresy TPO, które już wystąpiły.</summary>
    public required IReadOnlyList<TpoPeriod> Periods { get; init; }

    /// <summary>
    /// Pierwszy okres TPO tej migawki. Dla całego profilu = 0; dla kolejnych części rozbitego profilu
    /// jest to okres, od którego profil został rozbity.
    /// </summary>
    public int FirstPeriod { get; init; }

    /// <summary>
    /// Czas rozpoczęcia tej części profilu (dla całego profilu = początek sesji).
    /// Jednoznacznie identyfikuje profil/część – używany np. do zapamiętania trybu wyświetlania.
    /// </summary>
    public DateTime PartStart { get; init; }

    /// <summary>Numer części rozbitego profilu (liczony od 1).</summary>
    public int PartNumber { get; init; } = 1;

    /// <summary>Liczba części, na które rozbito profil sesji (1 = profil nie jest rozbity).</summary>
    public int PartCount { get; init; } = 1;

    /// <summary>Najwyższa cena sesji.</summary>
    public decimal High { get; init; }

    /// <summary>Najniższa cena sesji.</summary>
    public decimal Low { get; init; }

    /// <summary>Point of Control – wiersz z największą liczbą TPO.</summary>
    public decimal Poc { get; init; }

    /// <summary>Volume Point of Control – wiersz z największym wolumenem (null, gdy brak wolumenu).</summary>
    public decimal? VolumePoc { get; init; }

    /// <summary>Największy wolumen w jednym wierszu – do skalowania histogramu profilu wolumenowego.</summary>
    public long MaxRowVolume { get; init; }

    /// <summary>Górna granica obszaru wartości (Value Area High).</summary>
    public decimal ValueAreaHigh { get; init; }

    /// <summary>Dolna granica obszaru wartości (Value Area Low).</summary>
    public decimal ValueAreaLow { get; init; }

    /// <summary>Najwyższa cena Initial Balance (pierwszych okresów sesji). Null, jeśli brak danych.</summary>
    public decimal? InitialBalanceHigh { get; init; }

    /// <summary>Najniższa cena Initial Balance. Null, jeśli brak danych.</summary>
    public decimal? InitialBalanceLow { get; init; }

    /// <summary>Cena otwarcia profilu – pierwsza cena jego pierwszego okresu (początek rysowania profilu).</summary>
    public decimal Open { get; init; }

    /// <summary>Ostatnia cena transakcji w tej sesji (koniec rysowania profilu – cena zamknięcia).</summary>
    public decimal LastPrice { get; init; }

    /// <summary>Łączna liczba TPO w profilu.</summary>
    public int TotalTpo { get; init; }

    /// <summary>Łączny wolumen sesji.</summary>
    public long TotalVolume { get; init; }

    /// <summary>Numer wersji profilu – rośnie przy każdej zmianie danych (pozwala wykryć, czy trzeba odświeżyć widok).</summary>
    public long Version { get; init; }

    /// <summary>Czy profil nie zawiera jeszcze żadnych danych.</summary>
    /// <summary>Numer ostatniego (bieżącego) okresu TPO w tej migawce (-1 = brak okresów).</summary>
    public int CurrentPeriodIndex { get; init; } = -1;

    /// <summary>Wolumen ostatniego okresu TPO według wierszy (cena wiersza → wolumen) – profil bieżącego słupka.</summary>
    public IReadOnlyDictionary<decimal, long> CurrentPeriodVolume { get; init; } = new Dictionary<decimal, long>();

    /// <summary>Największy wolumen wiersza w bieżącym okresie (0 = brak wolumenu).</summary>
    public long MaxCurrentPeriodVolume => CurrentPeriodVolume.Count > 0 ? CurrentPeriodVolume.Values.Max() : 0;

    public bool IsEmpty => Levels.Count == 0;
}
