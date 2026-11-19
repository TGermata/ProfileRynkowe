using System.Globalization;
using MarketProfile.App.Controls;
using MarketProfile.App.Infrastructure;
using MarketProfile.App.Settings;
using MarketProfile.Core;
using MarketProfile.DataFeed.IB;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Zakładka „Wykres D/W/M”: świece dzienne, tygodniowe i miesięczne z lokalnej bazy – bieżącego instrumentu albo
/// (przełącznikiem) „rynku szerokiego” z ustawień, domyślnie indeksu SPX.
/// Przy połączeniu z IB (tryb „Na żywo”) wykres jest na żywo: ostatnia świeca instrumentu bazowego co sekundę
/// uzupełniana bieżącymi profilami TPO, a świece z IB (instrument i rynek szeroki) odświeżane w bazie co kilka minut.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Indeks zakładki „Profil TPO”.</summary>
    public const int TpoTab = 0;

    /// <summary>Indeks zakładki „Wykres D/W/M”.</summary>
    public const int CandlesTab = 1;

    private int _selectedTab = TpoTab;
    private BarInterval _candleInterval = BarInterval.Day;
    private IReadOnlyList<Bar> _candleBars = [];
    private IReadOnlyList<Bar> _candleStored = []; // świece z bazy (bez uzupełnienia na żywo)
    private string? _candleStoredSymbol;
    private bool _candleLive;                       // ostatnia świeca uzupełniana danymi na żywo
    private DateTime _nextLiveCandleUpdate;
    private DateTime? _nextCandleIbRefresh;         // kolejne odświeżenie świec z IB (null = zaraz po połączeniu)
    private readonly Dictionary<string, string> _candleIbNotes = [];
    private string _candleInfo = string.Empty;
    private string _candleEmptyText = string.Empty;
    private bool _candleRefreshing;
    private int _candleSkipped;
    private int _candleLoadVersion;
    private CandleChartAppearance? _candleAppearance;
    private bool _candleShowsBroadMarket;

    /// <summary>Wybrana zakładka (<see cref="TpoTab"/> albo <see cref="CandlesTab"/>).</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsTpoTab));
                if (value == CandlesTab)
                {
                    _ = LoadCandlesAsync();
                }
                else if (value == VolumeTab)
                {
                    UpdateVolumeComparison(force: true);
                }
            }
        }
    }

    /// <summary>
    /// Czy zakładka profili TPO jest widoczna – wybrana w oknie głównym albo w osobnym oknie
    /// (elementy paska narzędzi tylko dla TPO).
    /// </summary>
    public bool IsTpoTab => IsTabShown(TpoTab);

    private readonly HashSet<int> _detachedTabs = [];

    /// <summary>Czy zakładka jest na ekranie: wybrana w oknie głównym albo przeniesiona do osobnego okna.</summary>
    private bool IsTabShown(int tab) => _selectedTab == tab || _detachedTabs.Contains(tab);

    /// <summary>
    /// Zakładka przeniesiona do osobnego okna (albo z powrotem) – w osobnym oknie jest widoczna zawsze,
    /// więc jej dane odświeżają się niezależnie od zakładki wybranej w oknie głównym.
    /// </summary>
    public void SetTabDetached(int tab, bool detached)
    {
        if (!(detached ? _detachedTabs.Add(tab) : _detachedTabs.Remove(tab)))
        {
            return;
        }

        OnPropertyChanged(nameof(IsTpoTab));
        if (detached && tab == CandlesTab)
        {
            _ = LoadCandlesAsync();
        }
        else if (detached && tab == VolumeTab)
        {
            UpdateVolumeComparison(force: true);
        }
    }

    /// <summary>Interwał świec na wykresie.</summary>
    public BarInterval CandleInterval
    {
        get => _candleInterval;
        set
        {
            if (SetProperty(ref _candleInterval, value))
            {
                OnPropertyChanged(nameof(IsMonthly));
                OnPropertyChanged(nameof(IsWeekly));
                OnPropertyChanged(nameof(IsDaily));
                _ = LoadCandlesAsync();
            }
        }
    }

    // Przełącznik M / W / D (RadioButtony).
    public bool IsMonthly { get => _candleInterval == BarInterval.Month; set { if (value) CandleInterval = BarInterval.Month; } }
    public bool IsWeekly { get => _candleInterval == BarInterval.Week; set { if (value) CandleInterval = BarInterval.Week; } }
    public bool IsDaily { get => _candleInterval == BarInterval.Day; set { if (value) CandleInterval = BarInterval.Day; } }

    // --- Przełącznik: instrument bazowy / rynek szeroki (np. ES / SPX) ---

    /// <summary>Rynek szeroki z ustawień (null = nieznany symbol).</summary>
    private Instrument? BroadMarket => _settings.BroadMarketInstrument;

    /// <summary>Czy pokazać przełącznik – rynek szeroki jest znany i to inny instrument niż wybrany.</summary>
    public bool HasBroadMarketSwitch => BroadMarket is { } broad && broad.Symbol != _settings.InstrumentSymbol;

    /// <summary>Instrument, którego świece są na wykresie: bazowy albo rynek szeroki.</summary>
    private Instrument CandleInstrument => _candleShowsBroadMarket && HasBroadMarketSwitch ? BroadMarket! : _settings.Instrument;

    public string BaseSymbolLabel => _settings.InstrumentSymbol;

    public string BroadSymbolLabel => BroadMarket?.Symbol ?? string.Empty;

    public string BroadSymbolToolTip => BroadMarket is { } broad
        ? $"{broad.Symbol} – {broad.Description}: rynek szeroki (zmiana w Ustawieniach → Wykres D/W/M). Świece D/W/M są zapisywane w bazie i odświeżane w tle."
        : string.Empty;

    public bool IsBaseSelected
    {
        get => !_candleShowsBroadMarket;
        set { if (value) SetCandleShowsBroadMarket(false); }
    }

    public bool IsBroadSelected
    {
        get => _candleShowsBroadMarket;
        set { if (value) SetCandleShowsBroadMarket(true); }
    }

    private void SetCandleShowsBroadMarket(bool broad)
    {
        if (_candleShowsBroadMarket == broad)
        {
            return;
        }

        _candleShowsBroadMarket = broad;
        OnPropertyChanged(nameof(IsBaseSelected));
        OnPropertyChanged(nameof(IsBroadSelected));
        OnPropertyChanged(nameof(CandleTickSize));
        _ = LoadCandlesAsync();
    }

    /// <summary>Wygląd wykresu D/W/M z ustawień (rodzaj prezentacji, kolory, wolumen, średnie kroczące).</summary>
    public CandleChartAppearance CandleAppearance => _candleAppearance ??= CandleChartAppearance.Create(_settings.CandleChart);

    // Przełącznik rodzaju prezentacji na pasku zakładki (zapisywany w ustawieniach).
    public bool IsCandlesType
    {
        get => _settings.CandleChart.Type == CandleChartType.Candles;
        set { if (value) SetCandleChartType(CandleChartType.Candles); }
    }

    public bool IsOhlcType
    {
        get => _settings.CandleChart.Type == CandleChartType.OhlcBars;
        set { if (value) SetCandleChartType(CandleChartType.OhlcBars); }
    }

    private void SetCandleChartType(CandleChartType type)
    {
        if (_settings.CandleChart.Type != type)
        {
            _ = ApplySettingsAsync(_settings with { CandleChart = _settings.CandleChart with { Type = type } });
        }
    }

    /// <summary>Zmieniono ustawienia – wygląd wykresu D/W/M budowany od nowa.</summary>
    private void OnCandleSettingsChanged()
    {
        _candleAppearance = null;
        OnPropertyChanged(nameof(CandleAppearance));
        OnPropertyChanged(nameof(IsCandlesType));
        OnPropertyChanged(nameof(IsOhlcType));
        OnPropertyChanged(nameof(CandleTickSize));
        OnPropertyChanged(nameof(HasBroadMarketSwitch));
        OnPropertyChanged(nameof(BaseSymbolLabel));
        OnPropertyChanged(nameof(BroadSymbolLabel));
        OnPropertyChanged(nameof(BroadSymbolToolTip));
        if (IsTabShown(CandlesTab))
        {
            _ = LoadCandlesAsync(); // np. zmieniono rynek szeroki
        }
    }

    /// <summary>Świece wybranego interwału (z bazy).</summary>
    public IReadOnlyList<Bar> CandleBars
    {
        get => _candleBars;
        private set => SetProperty(ref _candleBars, value);
    }

    /// <summary>Opis danych: liczba świec, zakres dat, stan odświeżania.</summary>
    public string CandleInfo
    {
        get => _candleInfo;
        private set => SetProperty(ref _candleInfo, value);
    }

    /// <summary>Tekst na pustym wykresie.</summary>
    public string CandleEmptyText
    {
        get => _candleEmptyText;
        private set => SetProperty(ref _candleEmptyText, value);
    }

    /// <summary>Wielkość ticka wyświetlanego instrumentu (format cen na wykresie).</summary>
    public decimal CandleTickSize => CandleInstrument.TickSize;

    /// <summary>Instrument zmieniony – świece innego instrumentu.</summary>
    private void OnCandleInstrumentChanged()
    {
        OnPropertyChanged(nameof(CandleTickSize));
        OnPropertyChanged(nameof(HasBroadMarketSwitch));
        OnPropertyChanged(nameof(BaseSymbolLabel));
        _nextCandleIbRefresh = null; // świece nowego instrumentu – z IB od razu (gdy połączony)
        _ = LoadDailyBarsAsync(); // średnie dzienne na wykresie TPO – nowego instrumentu
        if (IsTabShown(CandlesTab))
        {
            _ = LoadCandlesAsync();
        }
    }

    /// <summary>Pobieranie w tle skończyło przebieg – w bazie mogły pojawić się nowe świece.</summary>
    private void OnCandleBackgroundPass()
    {
        _ = LoadDailyBarsAsync(); // nowe świece dzienne → aktualne średnie na wykresie TPO
        if (IsTabShown(CandlesTab) && !_candleRefreshing)
        {
            _ = LoadCandlesAsync();
        }
    }

    /// <summary>Wczytuje świece wybranego interwału z bazy (w tle) i odświeża opis.</summary>
    private async Task LoadCandlesAsync()
    {
        var version = ++_candleLoadVersion;
        var symbol = CandleInstrument.Symbol;
        var interval = _candleInterval;
        var store = GetStore();

        var (bars, skipped) = await Task.Run(() =>
        {
            // Niepełne świece (sama cena zamknięcia, bez wolumenu – najstarsze dane kontraktu ciągłego) zostają w bazie,
            // ale nie są pokazywane ani brane do średnich.
            var all = store.ReadOhlcBars(symbol, interval);
            var complete = BarExtensions.CompleteOnly(all);
            return (complete, all.Count - complete.Count);
        });

        if (version != _candleLoadVersion)
        {
            return; // w międzyczasie zmieniono interwał lub instrument
        }

        _candleStored = bars;
        _candleStoredSymbol = symbol;
        _candleSkipped = skipped;
        UpdateLiveCandles(force: true);
    }

    /// <summary>Czy ostatnią świecę można uzupełniać danymi na żywo: połączenie z IB i wykres instrumentu bazowego.</summary>
    private bool CandlesLiveAvailable =>
        IsBrokerLive && IsRunning && CandleInstrument.Symbol == _settings.InstrumentSymbol &&
        _candleStoredSymbol == _settings.InstrumentSymbol;

    /// <summary>
    /// Z timera odświeżania: co sekundę ostatnia świeca wykresu D/W/M uzupełniana bieżącymi profilami TPO
    /// (tylko zakładka D/W/M); co kilka minut – świece z IB do bazy (niezależnie od zakładki, gdy połączony z IB).
    /// </summary>
    private void OnCandleTimer()
    {
        RefreshCandlesFromIbIfDue();
        if (IsTabShown(CandlesTab) && DateTime.Now >= _nextLiveCandleUpdate)
        {
            _nextLiveCandleUpdate = DateTime.Now.AddSeconds(1);
            UpdateLiveCandles(force: false);
        }
    }

    /// <summary>Świece do wyświetlenia: z bazy, a na żywo – z ostatnią świecą uzupełnioną profilami bieżącego dnia.</summary>
    private void UpdateLiveCandles(bool force)
    {
        var bars = _candleStored;
        var live = false;
        if (CandlesLiveAvailable && _snapshots.LastOrDefault(s => !s.IsEmpty) is { } latest &&
            LiveBars.LiveDayBar(_snapshots, latest.TradingDate) is { } liveDay)
        {
            bars = LiveBars.MergeLive(_candleStored, _candleInterval, liveDay);
            live = true;
        }

        var changed = force || live != _candleLive || bars.Count != _candleBars.Count ||
                      (bars.Count > 0 && bars[^1] != _candleBars[^1]);
        _candleLive = live;
        if (changed)
        {
            CandleBars = bars;
            UpdateCandleInfo();
        }
    }

    /// <summary>
    /// Świece D/W/M z IB do bazy – instrument bazowy i rynek szeroki (np. SPX, bez strumienia cen w aplikacji):
    /// zaraz po połączeniu, potem co <see cref="CandleChartStyle.IbRefreshMinutes"/> minut. Tylko przy połączeniu z IBKR.
    /// </summary>
    private void RefreshCandlesFromIbIfDue()
    {
        if (!IsBrokerLive || !IsRunning)
        {
            _nextCandleIbRefresh = null; // po ponownym połączeniu – od razu
            return;
        }

        if (_candleRefreshing || (_nextCandleIbRefresh is { } next && DateTime.Now < next))
        {
            return;
        }

        _nextCandleIbRefresh = DateTime.Now.AddMinutes(Math.Clamp(_settings.CandleChart.IbRefreshMinutes, 1, 60));
        _ = RefreshCandlesFromIbAsync();
    }

    private async Task RefreshCandlesFromIbAsync()
    {
        var instruments = new List<Instrument> { _settings.Instrument };
        if (HasBroadMarketSwitch)
        {
            instruments.Add(BroadMarket!);
        }

        _candleRefreshing = true;
        try
        {
            foreach (var instrument in instruments)
            {
                await RefreshCandlesAsync(instrument);
            }
        }
        finally
        {
            _candleRefreshing = false;
        }
    }

    private void UpdateCandleInfo()
    {
        var instrument = CandleInstrument;
        var bars = _candleBars;
        var contract = instrument.Type switch
        {
            InstrumentType.Future => " (kontrakt ciągły)",
            InstrumentType.Index => $" – {instrument.Description}",
            _ => string.Empty,
        };

        var text = bars.Count == 0
            ? $"{instrument.Symbol}{contract} – {_candleInterval.DisplayName().ToLowerInvariant()}: brak świec w bazie"
            : $"{instrument.Symbol}{contract} – {_candleInterval.DisplayName().ToLowerInvariant()}: " +
              $"{bars.Count.ToString("N0", CultureInfo.CurrentCulture)} świec, " +
              $"{bars[0].Time.UtcDateTime:dd.MM.yyyy} – {bars[^1].Time.UtcDateTime:dd.MM.yyyy}";

        if (_candleSkipped > 0)
        {
            text += $" (pominięto niepełnych: {_candleSkipped.ToString("N0", CultureInfo.CurrentCulture)})";
        }

        if (_candleLive)
        {
            text += "   ·   na żywo";
        }

        if (_candleIbNotes.TryGetValue(instrument.Symbol, out var note))
        {
            text += $"   ·   {note}";
        }

        CandleInfo = text;
        CandleEmptyText = _candleIbNotes.TryGetValue(instrument.Symbol, out var pending) && pending.StartsWith("pobieranie", StringComparison.Ordinal)
            ? "Pobieranie świec z IBKR..."
            : "Brak świec w bazie – zostaną pobrane z IBKR po połączeniu z TWS (tryb „Na żywo”)";
    }

    /// <summary>Pobiera świece D/W/M instrumentu z IB (własne połączenie), zapisuje w bazie i wczytuje ponownie.</summary>
    private async Task RefreshCandlesAsync(Instrument instrument)
    {
        var symbol = instrument.Symbol;
        _candleIbNotes[symbol] = "pobieranie z IBKR...";
        UpdateCandleInfo();

        try
        {
            IbApiLoader.Use(string.IsNullOrWhiteSpace(_settings.IbApiPath) ? IbApiLoader.AutoDetect() : _settings.IbApiPath);
            var connection = _settings.ToIbSettings();
            var store = GetStore();
            var results = await Task.Run(() => OhlcBarUpdater.ConnectAndUpdateAsync(instrument, connection, store, CancellationToken.None));

            _candleIbNotes[symbol] = OhlcBarUpdater.Errors(results) is { } error
                ? $"IBKR {DateTime.Now:HH:mm}: {error}"
                : $"z IBKR {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            _candleIbNotes[symbol] = $"nie udało się pobrać z IBKR: {ex.Message}";
        }

        if (symbol == _settings.InstrumentSymbol)
        {
            _ = LoadDailyBarsAsync();
        }

        if (symbol == CandleInstrument.Symbol && IsTabShown(CandlesTab))
        {
            await LoadCandlesAsync();
        }
        else
        {
            UpdateCandleInfo();
        }
    }
}
