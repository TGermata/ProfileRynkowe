using System.Globalization;
using MarketProfile.App.Infrastructure;
using MarketProfile.Core;
using MarketProfile.DataFeed.IB;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Edycja jednego instrumentu w ustawieniach (zakładka „Instrumenty”). Pola są tekstowe, żeby można było
/// swobodnie pisać – poprawność sprawdzamy przy zapisie (<see cref="TryCreate"/>).
/// </summary>
public sealed class InstrumentEditorViewModel : ObservableObject
{
    private string _symbol = string.Empty;
    private string _description = string.Empty;

    public InstrumentEditorViewModel(Instrument instrument) => Load(instrument);

    /// <summary>Symbol w aplikacji (unikalny klucz).</summary>
    public string Symbol
    {
        get => _symbol;
        set
        {
            if (SetProperty(ref _symbol, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            if (SetProperty(ref _description, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>Nazwa na liście instrumentów.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Symbol : $"{Symbol} – {Description}";

    public InstrumentType Type { get; set; }
    public string IbSymbol { get; set; } = string.Empty;
    public string TradingClass { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string PrimaryExchange { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string TickSize { get; set; } = string.Empty;
    public string TicksPerRow { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public string DayStart { get; set; } = string.Empty;
    public string DayEnd { get; set; } = string.Empty;
    public bool HasNightSession { get; set; }
    public string NightStart { get; set; } = string.Empty;
    public string NightEnd { get; set; } = string.Empty;
    public string ContractMonth { get; set; } = string.Empty;

    /// <summary>Kod produktu na stronie CME (do pobierania SETTLE); puste = nie pobieramy.</summary>
    public string CmeProductId { get; set; } = string.Empty;

    public string ReferencePrice { get; set; } = string.Empty;

    /// <summary>Czy pobierać w tle historię tego instrumentu do bazy.</summary>
    public bool DownloadData { get; set; }

    /// <summary>Wypełnia pola wartościami instrumentu.</summary>
    public void Load(Instrument i)
    {
        Symbol = i.Symbol;
        Description = i.Description;
        Type = i.Type;
        IbSymbol = i.IbSymbol;
        TradingClass = i.TradingClass;
        Exchange = i.Exchange;
        PrimaryExchange = i.PrimaryExchange;
        Currency = i.Currency;
        TickSize = i.TickSize.ToString(CultureInfo.InvariantCulture);
        TicksPerRow = i.TicksPerRow > 0 ? i.TicksPerRow.ToString(CultureInfo.InvariantCulture) : string.Empty;
        TimeZoneId = i.TimeZoneId;
        DayStart = Time(i.DayStart);
        DayEnd = Time(i.DayEnd);
        HasNightSession = i.HasNightSession;
        NightStart = Time(i.NightStart);
        NightEnd = Time(i.NightEnd);
        ContractMonth = i.ContractMonth;
        CmeProductId = i.CmeProductId > 0 ? i.CmeProductId.ToString(CultureInfo.InvariantCulture) : string.Empty;
        ReferencePrice = i.ReferencePrice.ToString(CultureInfo.InvariantCulture);
        DownloadData = i.DownloadData;
        OnPropertyChanged(string.Empty); // odśwież wszystkie pola w oknie
    }

    /// <summary>
    /// Uzupełnia pola danymi znalezionymi w IB: giełda, waluta, tick, strefa czasowa i godziny sesji.
    /// Symbol, opis (jeśli już wpisany) i wysokość wiersza zostają bez zmian.
    /// </summary>
    public void Apply(IbInstrumentInfo info)
    {
        if (string.IsNullOrWhiteSpace(Description))
        {
            Description = info.LongName.Trim();
        }

        Exchange = info.Exchange;
        if (!string.IsNullOrWhiteSpace(info.PrimaryExchange))
        {
            PrimaryExchange = info.PrimaryExchange;
        }

        Currency = info.Currency;
        TickSize = info.MinTick.ToString(CultureInfo.InvariantCulture);
        TimeZoneId = info.TimeZoneId;

        if (info.Sessions is { } s)
        {
            DayStart = Time(s.DayStart);
            DayEnd = Time(s.DayEnd);
            HasNightSession = s.HasNight;
            NightStart = Time(s.NightStart);
            NightEnd = Time(s.NightEnd);
        }

        OnPropertyChanged(string.Empty);
    }

    /// <summary>Tworzy instrument z wpisanych wartości. Zwraca opis błędu albo null, gdy wszystko jest poprawne.</summary>
    public string? TryCreate(out Instrument? instrument)
    {
        instrument = null;
        var name = string.IsNullOrWhiteSpace(Symbol) ? "(bez symbolu)" : Symbol.Trim();

        if (string.IsNullOrWhiteSpace(Symbol))
        {
            return "Instrument bez symbolu – wpisz symbol (np. CL, AAPL).";
        }

        if (string.IsNullOrWhiteSpace(Exchange))
        {
            return $"{name}: podaj giełdę (np. CME, NYMEX, SMART, EUREX).";
        }

        if (!decimal.TryParse(TickSize?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var tick) || tick <= 0)
        {
            return $"{name}: wielkość ticka musi być liczbą większą od 0 (np. 0.25).";
        }

        var ticksPerRow = 0;
        if (!string.IsNullOrWhiteSpace(TicksPerRow) && !SettingsViewModel.TryParseInt(TicksPerRow, 1, 10_000, out ticksPerRow))
        {
            return $"{name}: ticki na wiersz – liczba od 1 do 10000 albo puste (wartość wspólna).";
        }

        if (Instrument.TryFindTimeZone(TimeZoneId) is null)
        {
            return $"{name}: nieznana strefa czasowa „{TimeZoneId}” – wybierz z listy (np. America/New_York).";
        }

        if (!TryTime(DayStart, out var dayStart) || !TryTime(DayEnd, out var dayEnd))
        {
            return $"{name}: godziny sesji dziennej podaj w formacie GG:MM.";
        }

        if (!TryTime(NightStart, out var nightStart) || !TryTime(NightEnd, out var nightEnd))
        {
            return $"{name}: godziny sesji nocnej podaj w formacie GG:MM.";
        }

        var month = ContractMonth?.Trim() ?? string.Empty;
        if (month.Length > 0 && (month.Length != 6 || !month.All(char.IsDigit)))
        {
            return $"{name}: miesiąc kontraktu podaj jako RRRRMM (np. 202612) albo zostaw puste.";
        }

        var cmeProductId = 0;
        if (!string.IsNullOrWhiteSpace(CmeProductId) &&
            (!int.TryParse(CmeProductId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out cmeProductId) || cmeProductId <= 0))
        {
            return $"{name}: kod produktu CME podaj jako liczbę (np. 133 dla ES) albo zostaw puste.";
        }

        if (!decimal.TryParse(ReferencePrice?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var refPrice) || refPrice <= 0)
        {
            return $"{name}: cena odniesienia (dla generatora) musi być liczbą większą od 0.";
        }

        instrument = new Instrument
        {
            Symbol = Symbol.Trim().ToUpperInvariant(),
            Description = Description?.Trim() ?? string.Empty,
            Type = Type,
            IbSymbol = IbSymbol?.Trim() ?? string.Empty,
            TradingClass = TradingClass?.Trim() ?? string.Empty,
            Exchange = Exchange.Trim().ToUpperInvariant(),
            PrimaryExchange = PrimaryExchange?.Trim().ToUpperInvariant() ?? string.Empty,
            Currency = string.IsNullOrWhiteSpace(Currency) ? "USD" : Currency.Trim().ToUpperInvariant(),
            TickSize = tick,
            TicksPerRow = ticksPerRow,
            TimeZoneId = TimeZoneId.Trim(),
            DayStart = dayStart,
            DayEnd = dayEnd,
            HasNightSession = HasNightSession,
            NightStart = nightStart,
            NightEnd = nightEnd,
            ContractMonth = month,
            CmeProductId = cmeProductId,
            ReferencePrice = refPrice,
            DownloadData = DownloadData,
        };
        return null;
    }

    /// <summary>Nazwa pozycji listy (także dla czytników ekranu i automatyzacji interfejsu).</summary>
    public override string ToString() => DisplayName;

    private static string Time(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static bool TryTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
