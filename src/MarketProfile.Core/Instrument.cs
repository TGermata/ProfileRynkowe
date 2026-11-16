using System.Globalization;
using System.Text.Json.Serialization;

namespace MarketProfile.Core;

/// <summary>Rodzaj instrumentu (wpływa na sposób wyszukiwania kontraktu u brokera).</summary>
public enum InstrumentType
{
    /// <summary>Kontrakt terminowy (futures) – wybierany najbliższy niewygasły albo podany miesiąc.</summary>
    Future,

    /// <summary>Akcja lub ETF.</summary>
    Stock,

    /// <summary>Indeks (tylko notowania – bez wolumenu).</summary>
    Index,
}

/// <summary>
/// Opis instrumentu finansowego, dla którego budujemy profil: dane do wyszukania go u brokera,
/// wielkość ticka oraz własne godziny sesji dziennej/nocnej i strefa czasowa.
/// Lista instrumentów jest zapisywana w ustawieniach – użytkownik może ją zmieniać i rozszerzać.
/// </summary>
public sealed record Instrument
{
    /// <summary>
    /// Symbol w aplikacji – unikalny klucz instrumentu (np. "ES", "FDAX", "AAPL"). Pod tym symbolem
    /// zapisywane są dane w bazie oraz linie i rozbicia profili.
    /// </summary>
    public required string Symbol { get; init; }

    /// <summary>Opis wyświetlany na liście, np. "E-mini S&amp;P 500".</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Rodzaj instrumentu.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<InstrumentType>))]
    public InstrumentType Type { get; init; } = InstrumentType.Future;

    /// <summary>Symbol u brokera, jeśli inny niż <see cref="Symbol"/> (np. FDAX → "DAX", 6E → "EUR"). Puste = taki sam.</summary>
    public string IbSymbol { get; init; } = string.Empty;

    /// <summary>Klasa kontraktu u brokera (np. "FDAX", "6E") – rozróżnia kontrakty o tym samym symbolu. Puste = dowolna.</summary>
    public string TradingClass { get; init; } = string.Empty;

    /// <summary>Giełda (dla akcji zwykle "SMART" – najlepsza dostępna).</summary>
    public required string Exchange { get; init; }

    /// <summary>Giełda główna akcji (np. "NASDAQ", "ARCA") – pomaga jednoznacznie wskazać akcję.</summary>
    public string PrimaryExchange { get; init; } = string.Empty;

    /// <summary>Waluta instrumentu.</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>Minimalna zmiana ceny (tick), np. 0.25 dla ES.</summary>
    public required decimal TickSize { get; init; }

    /// <summary>Wysokość wiersza TPO w tickach dla tego instrumentu (0 = wartość wspólna z ustawień TPO).</summary>
    public int TicksPerRow { get; init; }

    /// <summary>Strefa czasowa godzin sesji (identyfikator IANA, np. "America/New_York", "Europe/Berlin").</summary>
    public string TimeZoneId { get; init; } = "America/New_York";

    /// <summary>Początek i koniec sesji dziennej (w strefie <see cref="TimeZoneId"/>).</summary>
    public TimeOnly DayStart { get; init; } = new(9, 30);
    public TimeOnly DayEnd { get; init; } = new(16, 0);

    /// <summary>Czy instrument ma sesję nocną (indeksy i wiele akcji jej nie mają).</summary>
    public bool HasNightSession { get; init; } = true;

    /// <summary>Początek i koniec sesji nocnej (może przechodzić przez północ).</summary>
    public TimeOnly NightStart { get; init; } = new(18, 0);
    public TimeOnly NightEnd { get; init; } = new(9, 30);

    /// <summary>Miesiąc kontraktu futures RRRRMM (puste = najbliższy niewygasły).</summary>
    public string ContractMonth { get; init; } = string.Empty;

    /// <summary>
    /// Czy po połączeniu z brokerem pobierać w tle historię tego instrumentu do lokalnej bazy
    /// (do późniejszych symulacji) – także wtedy, gdy nie jest on pokazany na wykresie.
    /// </summary>
    public bool DownloadData { get; init; }

    /// <summary>Przybliżona cena – używana tylko przez symulator (generator), żeby ceny wyglądały realistycznie.</summary>
    public decimal ReferencePrice { get; init; } = 100m;

    /// <summary>Symbol używany w zapytaniach do brokera.</summary>
    [JsonIgnore]
    public string BrokerSymbol => string.IsNullOrWhiteSpace(IbSymbol) ? Symbol : IbSymbol;

    /// <summary>Nazwa na liście instrumentów, np. "CL – Ropa WTI".</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Symbol : $"{Symbol} – {Description}";

    /// <summary>Strefa czasowa godzin sesji (gdy identyfikator jest nieznany – Nowy Jork).</summary>
    [JsonIgnore]
    public TimeZoneInfo TimeZone => TryFindTimeZone(TimeZoneId) ?? TryFindTimeZone("America/New_York")!;

    /// <summary>Sesje instrumentu: nocna (jeśli jest) i dzienna – dla każdej rysowany jest osobny profil.</summary>
    [JsonIgnore]
    public IReadOnlyList<SessionDefinition> Sessions => HasNightSession
        ?
        [
            new SessionDefinition { Name = "Nocna", Start = NightStart, End = NightEnd },
            new SessionDefinition { Name = "Dzienna", Start = DayStart, End = DayEnd },
        ]
        : [new SessionDefinition { Name = "Dzienna", Start = DayStart, End = DayEnd }];

    /// <summary>Liczba miejsc po przecinku wynikająca z wielkości ticka (najmniej 2), np. 0.00005 → 5.</summary>
    [JsonIgnore]
    public int PriceDecimals => PriceFormat.DecimalsFor(TickSize);

    public override string ToString() => DisplayName;

    /// <summary>Znajduje strefę czasową po identyfikatorze (IANA lub Windows); null, gdy nieznana.</summary>
    public static TimeZoneInfo? TryFindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception)
        {
            return null;
        }
    }

    // =====================================================================
    // Wbudowana lista popularnych instrumentów
    // =====================================================================

    private const string NewYork = "America/New_York";
    private const string Berlin = "Europe/Berlin";

    /// <summary>Kontrakt E-mini S&amp;P 500.</summary>
    public static Instrument ES { get; } = new()
    {
        Symbol = "ES", Description = "E-mini S&P 500", Exchange = "CME", TickSize = 0.25m, TicksPerRow = 4, ReferencePrice = 6000m,
        DownloadData = true,
    };

    /// <summary>Kontrakt E-mini Nasdaq 100.</summary>
    public static Instrument NQ { get; } = new()
    {
        Symbol = "NQ", Description = "E-mini Nasdaq 100", Exchange = "CME", TickSize = 0.25m, TicksPerRow = 8, ReferencePrice = 21000m,
        DownloadData = true,
    };

    /// <summary>
    /// Gotowa lista popularnych instrumentów (futures, akcje/ETF, indeksy). Godziny sesji to typowe godziny
    /// handlu głównego; przycisk „Sprawdź w IB” w ustawieniach potrafi je uzupełnić danymi brokera.
    /// </summary>
    public static IReadOnlyList<Instrument> BuiltIn { get; } =
    [
        // --- Futures (USA) ---
        ES,
        NQ,
        new() { Symbol = "YM", Description = "E-mini Dow Jones", Exchange = "CBOT", TickSize = 1m, TicksPerRow = 5, ReferencePrice = 44000m },
        new() { Symbol = "RTY", Description = "E-mini Russell 2000", Exchange = "CME", TickSize = 0.1m, TicksPerRow = 5, ReferencePrice = 2300m },
        new()
        {
            Symbol = "CL", Description = "Ropa WTI", Exchange = "NYMEX", TickSize = 0.01m, TicksPerRow = 5, ReferencePrice = 70m,
            DayStart = new(9, 0), DayEnd = new(14, 30), NightStart = new(18, 0), NightEnd = new(9, 0),
        },
        new()
        {
            Symbol = "GC", Description = "Złoto", Exchange = "COMEX", TickSize = 0.1m, TicksPerRow = 10, ReferencePrice = 2600m,
            DayStart = new(8, 20), DayEnd = new(13, 30), NightStart = new(18, 0), NightEnd = new(8, 20),
        },
        new()
        {
            Symbol = "6E", Description = "Euro FX", IbSymbol = "EUR", TradingClass = "6E", Exchange = "CME",
            TickSize = 0.00005m, TicksPerRow = 5, ReferencePrice = 1.08m,
            DayStart = new(8, 20), DayEnd = new(15, 0), NightStart = new(18, 0), NightEnd = new(8, 20),
        },

        // --- Futures (Europa, czas Frankfurtu) ---
        new()
        {
            Symbol = "FDAX", Description = "DAX futures", IbSymbol = "DAX", TradingClass = "FDAX", Exchange = "EUREX", Currency = "EUR",
            TickSize = 1m, TicksPerRow = 5, ReferencePrice = 24000m, TimeZoneId = Berlin,
            DayStart = new(9, 0), DayEnd = new(17, 30), NightStart = new(17, 30), NightEnd = new(9, 0),
        },
        new()
        {
            Symbol = "FESX", Description = "Euro Stoxx 50 futures", IbSymbol = "ESTX50", TradingClass = "FESX", Exchange = "EUREX", Currency = "EUR",
            TickSize = 1m, TicksPerRow = 2, ReferencePrice = 5300m, TimeZoneId = Berlin,
            DayStart = new(9, 0), DayEnd = new(17, 30), NightStart = new(17, 30), NightEnd = new(9, 0),
        },

        // --- Akcje i ETF (sesja nocna = handel po zamknięciu i przed otwarciem) ---
        new()
        {
            Symbol = "SPY", Description = "SPDR S&P 500 ETF", Type = InstrumentType.Stock, Exchange = "SMART", PrimaryExchange = "ARCA",
            TickSize = 0.01m, TicksPerRow = 10, ReferencePrice = 600m, NightStart = new(16, 0), NightEnd = new(9, 30),
        },
        new()
        {
            Symbol = "QQQ", Description = "Invesco QQQ (Nasdaq 100 ETF)", Type = InstrumentType.Stock, Exchange = "SMART", PrimaryExchange = "NASDAQ",
            TickSize = 0.01m, TicksPerRow = 10, ReferencePrice = 520m, NightStart = new(16, 0), NightEnd = new(9, 30),
        },
        new()
        {
            Symbol = "AAPL", Description = "Apple", Type = InstrumentType.Stock, Exchange = "SMART", PrimaryExchange = "NASDAQ",
            TickSize = 0.01m, TicksPerRow = 5, ReferencePrice = 230m, NightStart = new(16, 0), NightEnd = new(9, 30),
        },

        // --- Indeksy (tylko sesja główna) ---
        new()
        {
            Symbol = "SPX", Description = "S&P 500 (indeks)", Type = InstrumentType.Index, Exchange = "CBOE",
            TickSize = 0.01m, TicksPerRow = 50, ReferencePrice = 6000m, HasNightSession = false,
        },
        new()
        {
            Symbol = "NDX", Description = "Nasdaq 100 (indeks)", Type = InstrumentType.Index, Exchange = "NASDAQ",
            TickSize = 0.01m, TicksPerRow = 100, ReferencePrice = 21000m, HasNightSession = false,
        },
        new()
        {
            Symbol = "DAX", Description = "DAX (indeks)", Type = InstrumentType.Index, Exchange = "EUREX", Currency = "EUR",
            TickSize = 0.01m, TicksPerRow = 200, ReferencePrice = 24000m, TimeZoneId = Berlin,
            DayStart = new(9, 0), DayEnd = new(17, 30), HasNightSession = false,
        },
    ];

    /// <summary>Wszystkie wbudowane instrumenty (dla zgodności z wcześniejszym kodem).</summary>
    public static IReadOnlyList<Instrument> All => BuiltIn;

    /// <summary>Symbol domyślnego instrumentu „rynku szerokiego” (indeks S&amp;P 500).</summary>
    public const string DefaultBroadMarketSymbol = "SPX";

    /// <summary>
    /// Instrument o podanym symbolu: z listy użytkownika, a gdy go tam nie ma – z listy wbudowanej
    /// (np. SPX działa jako rynek szeroki także po usunięciu go z listy instrumentów). Null = nieznany symbol.
    /// </summary>
    public static Instrument? Find(IEnumerable<Instrument> instruments, string symbol) =>
        instruments.FirstOrDefault(i => string.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
        ?? BuiltIn.FirstOrDefault(i => string.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Formatowanie cen z liczbą miejsc po przecinku dopasowaną do wielkości ticka.</summary>
public static class PriceFormat
{
    /// <summary>Liczba miejsc po przecinku ticka (najmniej 2), np. 0.25 → 2, 0.00005 → 5.</summary>
    public static int DecimalsFor(decimal tickSize)
    {
        var normalized = tickSize / 1.000000000000000000000000000000000m; // usuwa końcowe zera (0.250 → 0.25)
        var decimals = (decimal.GetBits(normalized)[3] >> 16) & 0xFF;      // skala liczby = miejsca po przecinku
        return Math.Max(2, decimals);
    }

    /// <summary>Wzorzec formatu dla danej wielkości ticka, np. "0.00" albo "0.00000".</summary>
    public static string PatternFor(decimal tickSize) => "0." + new string('0', DecimalsFor(tickSize));

    /// <summary>Cena jako tekst z kropką dziesiętną i liczbą miejsc wynikającą z ticka.</summary>
    public static string Format(decimal price, decimal tickSize) =>
        price.ToString(PatternFor(tickSize), CultureInfo.InvariantCulture);
}
