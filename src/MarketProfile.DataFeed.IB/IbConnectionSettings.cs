namespace MarketProfile.DataFeed.IB;

/// <summary>Rodzaj danych rynkowych pobieranych z Interactive Brokers.</summary>
public enum IbDataType
{
    /// <summary>
    /// Dane na żywo – każda transakcja osobno (tick-by-tick). Wymaga wykupionej subskrypcji danych giełdy (CME).
    /// </summary>
    Live,

    /// <summary>Dane opóźnione o ok. 15 minut – bezpłatne, bez subskrypcji.</summary>
    Delayed,

    /// <summary>Opóźnione „zamrożone” – ostatnie dostępne notowanie także wtedy, gdy rynek jest zamknięty.</summary>
    DelayedFrozen,
}

/// <summary>
/// Dane instrumentu znalezionego w IB (wynik „Sprawdź w IB”).
/// (Rekord nie używa typów IBApi – można go używać przed załadowaniem biblioteki TWS API.)
/// </summary>
public sealed record IbInstrumentInfo(
    string LongName,
    string LocalSymbol,
    string Exchange,
    string PrimaryExchange,
    string Currency,
    string TradingClass,
    string Expiry,
    decimal MinTick,
    string TimeZoneId,
    IbSessionSuggestion? Sessions);

/// <summary>
/// Parametry połączenia z TWS / IB Gateway.
/// (Ta klasa nie używa typów IBApi – można jej używać, zanim biblioteka TWS API zostanie załadowana.)
/// </summary>
public sealed record IbConnectionSettings
{
    /// <summary>Adres komputera z TWS / IB Gateway.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Port API: 7497 – TWS konto demo (paper), 7496 – TWS konto rzeczywiste, 4002/4001 – IB Gateway.</summary>
    public int Port { get; init; } = 7497;

    /// <summary>Identyfikator klienta API – każde połączenie z TWS musi mieć inny.</summary>
    public int ClientId { get; init; } = 1;

    /// <summary>Rodzaj danych (na żywo / opóźnione / opóźnione zamrożone).</summary>
    public IbDataType DataType { get; init; } = IbDataType.Delayed;

    /// <summary>
    /// Połączenie przez IBKR Web API (api.ibkr.com, klucze OAuth) zamiast TWS / IB Gateway.
    /// Wtedy Host, Port, ClientId i DataType nie są używane (rodzaj danych wynika z uprawnień konta).
    /// </summary>
    public bool UseWebApi { get; init; }

    /// <summary>Web API: przejęcie sesji brokerskiej, gdy ten sam użytkownik jest zalogowany gdzie indziej (np. w TWS).</summary>
    public bool WebApiCompete { get; init; }

    /// <summary>Krótki opis połączenia do komunikatów (bez kluczy).</summary>
    public string Endpoint => UseWebApi ? "IBKR Web API (api.ibkr.com)" : $"{Host}:{Port}";

    /// <summary>Opis rodzaju danych dla użytkownika.</summary>
    public string DataTypeName => UseWebApi ? "wg uprawnień konta" : DataType switch
    {
        IbDataType.Live => "na żywo",
        IbDataType.DelayedFrozen => "opóźnione zamrożone",
        _ => "opóźnione",
    };
}

/// <summary>
/// Wynik sprawdzenia, jak daleko wstecz IB udostępnia ticki (transakcje) dla instrumentu
/// – przycisk „Sprawdź historię ticków” w ustawieniach.
/// (Rekord nie używa typów IBApi – można go używać przed załadowaniem biblioteki TWS API.)
/// </summary>
/// <param name="ContractName">Sprawdzany kontrakt, np. "ESZ6".</param>
/// <param name="MaxDaysBack">Największa sprawdzona odległość (dni wstecz), dla której przyszły ticki; null = żadna.</param>
/// <param name="OldestTick">Czas najstarszego otrzymanego ticka.</param>
/// <param name="FirstDaysWithout">Pierwsza sprawdzona odległość, dla której ticków już nie było; null = były dla wszystkich.</param>
/// <param name="LastError">Ostatni komunikat błędu IB (np. brak uprawnień), jeśli był.</param>
/// <param name="Density">Gęstość ticków z próbki sprzed ok. 1 dnia (do szacunku czasu pobierania).</param>
public sealed record IbTickProbeResult(
    string ContractName,
    int? MaxDaysBack,
    DateTimeOffset? OldestTick,
    int? FirstDaysWithout,
    string? LastError,
    IbTickDensity? Density);

/// <summary>
/// Gęstość ticków: ile ticków przyszło w jednej próbce i jaki odcinek czasu obejmowały.
/// Na tej podstawie szacujemy liczbę zapytań (maks. 1000 ticków na zapytanie) i czas pobierania
/// przy limicie IB 60 zapytań historycznych na 10 minut.
/// </summary>
public sealed record IbTickDensity(int Ticks, TimeSpan Span)
{
    /// <summary>Maksymalna liczba ticków w jednym zapytaniu reqHistoricalTicks.</summary>
    public const int TicksPerRequest = 1000;

    /// <summary>Średni odstęp między zapytaniami przy limicie 60 zapytań na 10 minut.</summary>
    public static readonly TimeSpan TimePerRequest = TimeSpan.FromSeconds(10);

    /// <summary>Ticki na godzinę (próbka krótsza niż 1 s jest liczona jako 1 s).</summary>
    public double TicksPerHour => Ticks / Math.Max(Span.TotalHours, 1.0 / 3600);

    /// <summary>Szacowana liczba ticków w dniu handlowym o podanej liczbie godzin handlu.</summary>
    public long TicksPerDay(double tradingHours) => (long)Math.Round(TicksPerHour * tradingHours);

    /// <summary>Szacowana liczba zapytań na jeden dzień handlowy.</summary>
    public long RequestsPerDay(double tradingHours) => Math.Max(1, (long)Math.Ceiling(TicksPerDay(tradingHours) / (double)TicksPerRequest));

    /// <summary>Szacowany czas pobrania podanej liczby dni handlowych (przy limicie zapytań IB).</summary>
    public TimeSpan DownloadTime(double tradingHours, int days) => TimePerRequest * (RequestsPerDay(tradingHours) * Math.Max(0, days));
}
