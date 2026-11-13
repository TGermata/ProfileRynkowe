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

    /// <summary>Opis rodzaju danych dla użytkownika.</summary>
    public string DataTypeName => DataType switch
    {
        IbDataType.Live => "na żywo",
        IbDataType.DelayedFrozen => "opóźnione zamrożone",
        _ => "opóźnione",
    };
}
