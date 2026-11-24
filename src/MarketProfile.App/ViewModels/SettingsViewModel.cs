using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using MarketProfile.App.Infrastructure;
using MarketProfile.App.Settings;
using MarketProfile.Core;
using MarketProfile.DataFeed.IB;
using MarketProfile.DataFeed.IBWeb;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Model widoku okna ustawień. Pola są tekstowe, żeby użytkownik mógł swobodnie pisać,
/// a poprawność sprawdzamy dopiero przy zapisie (<see cref="TryCreateSettings"/>).
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _original;
    private readonly MarketDataStore _store;
    private string _error = string.Empty;
    private string _databaseInfo = string.Empty;
    private bool _isGenerating;

    public SettingsViewModel(AppSettings settings, MarketDataStore store)
    {
        _original = settings;
        _store = store;

        // Instrumenty – każdy edytowany osobno; zaznaczony jest instrument wybrany w oknie głównym.
        foreach (var instrument in settings.Instruments)
        {
            InstrumentEditors.Add(new InstrumentEditorViewModel(instrument));
        }

        _selectedInstrumentEditor = InstrumentEditors.FirstOrDefault(e => e.Symbol == settings.InstrumentSymbol)
                                    ?? InstrumentEditors.FirstOrDefault();

        // Interactive Brokers. Pustą ścieżkę od razu uzupełniamy znalezioną automatycznie – użytkownik widzi, skąd biblioteka.
        _liveSource = settings.LiveSource;
        IbWebApiCompete = settings.IbWebApiCompete;
        RefreshWebApiKeysStatus();
        _ibApiPath = string.IsNullOrWhiteSpace(settings.IbApiPath) ? IbApiLoader.AutoDetect() ?? string.Empty : settings.IbApiPath;
        IbHost = settings.IbHost;
        IbPort = ToText(settings.IbPort);
        IbClientId = ToText(settings.IbClientId);
        IbDataType = settings.IbDataType;
        ValidateIbApiPath();

        // Baza danych.
        RecordToDatabase = settings.RecordToDatabase;
        _logDirectory = settings.EffectiveLogDirectory;
        ShowDownloadProgress = settings.ShowDownloadProgress;
        ControlSize = settings.ControlSize;
        TempoGauge = settings.TempoGauge;
        TempoGaugePosition = settings.TempoGaugePosition;
        TempoInStatusBar = settings.TempoInStatusBar;
        TestDataDays = ToText(settings.TestDataDays);
        BackgroundHistoryDays = ToText(settings.BackgroundHistoryDays);
        BackgroundTickDays = ToText(settings.BackgroundTickDays);
        RefreshDatabaseInfo();

        // Sesje i TPO.
        TicksPerRow = ToText(settings.TicksPerRow);
        TpoMinutes = ToText(settings.TpoMinutes);
        ValueAreaPercent = ToText(settings.ValueAreaPercent);
        PocTieBreak = settings.PocTieBreak;
        HistoryDays = ToText(settings.HistoryDays);
        SimulatorSpeed = settings.SimulatorSpeed.ToString(CultureInfo.InvariantCulture);

        // Profil wolumenowy.
        var volume = settings.VolumeStyle;
        VolumeGap = ToText(volume.Gap);
        VolumeWidth = ToText(volume.Width);
        VolumeMirrored = volume.Mirrored;
        VolumeBarColor = volume.BarColor;
        VolumeVpocColor = volume.VpocColor;
        VolumeDeltaBuyColor = volume.DeltaBuyColor;
        VolumeDeltaSellColor = volume.DeltaSellColor;
        VolumeOpacity = ToText(volume.Opacity);
        VolumeScale = volume.Scale;
        VolumeMode = settings.VolumeProfile;
        VolumeDisplayDelta = settings.VolumeDisplay == VolumeDisplayKind.Delta;
        LoadPeriodVolume(settings.PeriodVolume);
        LoadVolumeLabels(settings.VolumeLabels);
        VolumeCapPercent = ToText(volume.CapPercent);

        // Kolory sesji.
        DayColors = new ColorSchemeViewModel(settings.DayColors);
        NightColors = new ColorSchemeViewModel(settings.NightColors);

        // Kreska połowy okresu w trybie Split.
        MidlineEnabled = settings.SplitMidline.Enabled;
        MidlineColor = settings.SplitMidline.Color;
        MidlineThickness = ToText(settings.SplitMidline.Thickness);
        MidlineOpacity = ToText(settings.SplitMidline.Opacity);

        // Linia HalfBack (połowa wysokości profilu).
        LoadHalfBack(settings.HalfBack);
        LoadHighlight(settings.SelectedProfileHighlight);

        // Zegar bieżącego okresu TPO.
        LoadTimer(settings.PeriodTimer);

        // Elementy profilu TPO.
        ShowInitialBalance = settings.ShowInitialBalance;
        ShowPoc = settings.ShowPoc;
        InitialBalancePeriods = ToText(settings.InitialBalancePeriods);
        InventorySignificantPercent = ToText(settings.InventorySignificantPercent);
        LoadArrows(settings.Arrows);

        // Wykres D/W/M.
        LoadCandleChart(settings.CandleChart);
        LoadTpoAverages(settings.TpoAverages);
        // Szeroki rynek: tylko indeksy (świece wyłącznie z sesji kasowej).
        BroadMarketSymbol = settings.BroadMarketSymbol;
        BroadMarketOptions = settings.Instruments.Concat(Instrument.BuiltIn)
            .Where(i => i.Type == InstrumentType.Index)
            .Select(i => i.Symbol)
            .Prepend(Instrument.DefaultBroadMarketSymbol)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LoadBroadMarketChart(settings.BroadMarketChart);

        // Sektory S&P 500: wagi (kolejność jak na pasku – od największej) i data zestawienia.
        ShowSectorStrip = settings.ShowSectorStrip;
        SectorWeightsDate = settings.SectorWeightsDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var (sector, weight) in SectorPerformance.OrderedByWeight(settings.SectorWeights))
        {
            SectorWeightEditors.Add(new SectorWeightEditor(sector, weight.ToString("0.##", CultureInfo.CurrentCulture)));
        }
    }

    // --- Sektory S&P 500 (pasek na dole zakładki „Szeroki rynek”) ---

    /// <summary>Czy pokazywać pasek sektorów na dole zakładki „Szeroki rynek”.</summary>
    public bool ShowSectorStrip { get; set; }

    /// <summary>Data zestawienia S&amp;P, z którego pochodzą wagi (RRRR-MM-DD).</summary>
    public string SectorWeightsDate { get; set; } = string.Empty;

    /// <summary>Wagi 11 sektorów – edytowalna tabela.</summary>
    public ObservableCollection<SectorWeightEditor> SectorWeightEditors { get; } = [];

    /// <summary>Strona S&amp;P 500 na spglobal.com (zakładka z wagami sektorów).</summary>
    public const string SectorWeightsUrl = "https://www.spglobal.com/spdji/en/indices/equity/sp-500/#data";

    /// <summary>Symbol „rynku szerokiego” (indeks, np. SPX) – zakładka „Szeroki rynek”, przełącznik na wykresie D/W/M.</summary>
    public string BroadMarketSymbol { get; set; } = string.Empty;

    /// <summary>Symbole do wyboru jako rynek szeroki – indeksy (z listy instrumentów i wbudowane).</summary>
    public IReadOnlyList<string> BroadMarketOptions { get; } = [];

    // --- Szeroki rynek: wykres dzienny (kolory świec, średnie SMA/EMA) ---
    public string BroadUpColor { get; set; } = string.Empty;
    public string BroadDownColor { get; set; } = string.Empty;

    /// <summary>„Oddech rynku”: wskaźnik graficzny (pasek) obok liczby.</summary>
    public bool ShowBreathGauge { get; set; }

    /// <summary>Średnie kroczące wykresu „Szeroki rynek” – edytowalna lista.</summary>
    public ObservableCollection<BroadMovingAverageEditorViewModel> BroadMovingAverages { get; } = [];

    /// <summary>Rodzaje średnich do wyboru.</summary>
    public IReadOnlyList<KeyValuePair<MovingAverageKind, string>> MovingAverageKinds { get; } =
    [
        new(MovingAverageKind.Ema, "EMA"),
        new(MovingAverageKind.Sma, "SMA"),
    ];

    private void LoadBroadMarketChart(BroadMarketChartStyle style)
    {
        BroadUpColor = style.UpColor;
        BroadDownColor = style.DownColor;
        ShowBreathGauge = style.ShowBreathGauge;
        BroadMovingAverages.Clear();
        foreach (var average in style.MovingAverages)
        {
            BroadMovingAverages.Add(new BroadMovingAverageEditorViewModel(average));
        }
    }

    /// <summary>Nowa średnia na wykresie „Szeroki rynek” (EMA, kolejny kolor z palety).</summary>
    public void AddBroadMovingAverage()
    {
        string[] palette = ["#7FD4FF", "#FFFFFF", "#F0C030", "#E040FB", "#FF8C2E", "#30C070"];
        var period = BroadMovingAverages.Count == 0 ? 8 : BroadMovingAverages.Max(m => int.TryParse(m.Period, out var p) ? p : 0) * 2;
        BroadMovingAverages.Add(new BroadMovingAverageEditorViewModel(new BroadMovingAverageStyle
        {
            Period = Math.Clamp(period, 2, 500),
            Color = palette[BroadMovingAverages.Count % palette.Length],
        }));
    }

    public void RemoveBroadMovingAverage(BroadMovingAverageEditorViewModel average) => BroadMovingAverages.Remove(average);

    /// <summary>Przywraca domyślny wygląd wykresu „Szeroki rynek”: EMA 8 i EMA 21 (zapis dopiero po kliknięciu „Zapisz”).</summary>
    public void ResetBroadMarketChart()
    {
        LoadBroadMarketChart(new BroadMarketChartStyle());
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Wygląd wykresu „Szeroki rynek” z formularza; null + komunikat błędu, gdy coś jest niepoprawne.</summary>
    private BroadMarketChartStyle? TryCreateBroadMarketChart(out string? error)
    {
        error = null;
        if (!ColorUtil.TryParse(BroadUpColor, out _) || !ColorUtil.TryParse(BroadDownColor, out _))
        {
            error = "Szeroki rynek: kolory świec podaj w formacie #RRGGBB.";
            return null;
        }

        var averages = new List<BroadMovingAverageStyle>();
        for (var i = 0; i < BroadMovingAverages.Count; i++)
        {
            if (BroadMovingAverages[i].TryCreate(out var average) is { } averageError)
            {
                error = $"Szeroki rynek – średnia nr {i + 1}: {averageError}";
                return null;
            }

            averages.Add(average!);
        }

        return new BroadMarketChartStyle
        {
            RightMarginPercent = _original.BroadMarketChart.RightMarginPercent, // ustawiane na wykresie, nie w tym oknie
            ShowBreathGauge = ShowBreathGauge,
            UpColor = BroadUpColor.Trim().ToUpperInvariant(),
            DownColor = BroadDownColor.Trim().ToUpperInvariant(),
            MovingAverages = averages,
        };
    }

    // --- Wykres D/W/M: rodzaj prezentacji, kolory, wolumen, średnie kroczące ---
    public bool CandleTypeCandles { get; set; }
    public bool CandleTypeOhlc { get; set; }
    public string CandleUpColor { get; set; } = string.Empty;
    public string CandleDownColor { get; set; } = string.Empty;
    public bool CandleShowVolume { get; set; }
    public string CandleVolumeOpacity { get; set; } = string.Empty;
    public string CandleIbRefreshMinutes { get; set; } = string.Empty;

    /// <summary>Średnie kroczące (SMA) wykresu D/W/M – edytowalna lista.</summary>
    public ObservableCollection<MovingAverageEditorViewModel> MovingAverages { get; } = [];

    private void LoadCandleChart(CandleChartStyle style)
    {
        CandleTypeCandles = style.Type == CandleChartType.Candles;
        CandleTypeOhlc = style.Type == CandleChartType.OhlcBars;
        CandleUpColor = style.UpColor;
        CandleDownColor = style.DownColor;
        CandleShowVolume = style.ShowVolume;
        CandleVolumeOpacity = ToText(style.VolumeOpacity);
        CandleIbRefreshMinutes = ToText(style.IbRefreshMinutes);
        MovingAverages.Clear();
        foreach (var average in style.MovingAverages)
        {
            MovingAverages.Add(new MovingAverageEditorViewModel(average));
        }
    }

    // --- Średnie dzienne (wartości „na dziś”) jako linie na wykresie TPO ---
    public bool TpoAveragesEnabled { get; set; }

    private bool _tpoAveragesIncludeNight;

    /// <summary>Średnie na TPO z całej doby (z sesją nocną); false – tylko z sesji kasowej.</summary>
    public bool TpoAveragesIncludeNight
    {
        get => _tpoAveragesIncludeNight;
        set
        {
            if (SetProperty(ref _tpoAveragesIncludeNight, value))
            {
                OnPropertyChanged(nameof(TpoAveragesCashOnly)); // ten sam przełącznik w dwóch zakładkach
            }
        }
    }

    /// <summary>Średnie na TPO tylko z sesji kasowej (para przycisków z <see cref="TpoAveragesIncludeNight"/>).</summary>
    public bool TpoAveragesCashOnly
    {
        get => !_tpoAveragesIncludeNight;
        set => TpoAveragesIncludeNight = !value;
    }
    public bool TpoAveragesLabelLeft { get; set; }
    public bool TpoAveragesLabelRight { get; set; }
    public bool TpoAveragesShowValue { get; set; }
    public bool TpoAveragesAxisLabel { get; set; }
    public string TpoAveragesOpacity { get; set; } = string.Empty;
    public string TpoAveragesFontSize { get; set; } = string.Empty;
    public bool TpoAveragesBlink { get; set; }
    public string TpoAveragesBlinkTicks { get; set; } = string.Empty;
    public string TpoAveragesBlinkMs { get; set; } = string.Empty;

    private void LoadTpoAverages(TpoAveragesStyle style)
    {
        TpoAveragesEnabled = style.Enabled;
        TpoAveragesIncludeNight = style.IncludeNightSession;
        TpoAveragesLabelLeft = style.LabelPosition == LabelPosition.Left;
        TpoAveragesLabelRight = style.LabelPosition == LabelPosition.Right;
        TpoAveragesShowValue = style.ShowValueInLabel;
        TpoAveragesAxisLabel = style.ShowAxisLabel;
        TpoAveragesOpacity = ToText(style.Opacity);
        TpoAveragesFontSize = ToText(style.FontSize);
        TpoAveragesBlink = style.BlinkEnabled;
        TpoAveragesBlinkTicks = ToText(style.BlinkDistanceTicks);
        TpoAveragesBlinkMs = ToText(style.BlinkIntervalMs);
    }

    /// <summary>Ustawienia średnich na wykresie TPO z formularza; null + komunikat błędu, gdy coś jest niepoprawne.</summary>
    private TpoAveragesStyle? TryCreateTpoAverages(out string? error)
    {
        error = null;
        if (!TryParseInt(TpoAveragesOpacity, 10, 100, out var opacity))
        {
            error = "Średnie na wykresie TPO: krycie podaj w procentach od 10 do 100.";
            return null;
        }

        if (!TryParseInt(TpoAveragesFontSize, 7, 24, out var fontSize))
        {
            error = "Średnie na wykresie TPO: wielkość czcionki podaj od 7 do 24.";
            return null;
        }

        if (!TryParseInt(TpoAveragesBlinkTicks, 1, 400, out var blinkTicks))
        {
            error = "Średnie na wykresie TPO: odległość migania podaj w tickach od 1 do 400.";
            return null;
        }

        if (!TryParseInt(TpoAveragesBlinkMs, 150, 2000, out var blinkMs))
        {
            error = "Średnie na wykresie TPO: tempo migania podaj w milisekundach od 150 do 2000.";
            return null;
        }

        return new TpoAveragesStyle
        {
            Enabled = TpoAveragesEnabled,
            IncludeNightSession = TpoAveragesIncludeNight,
            LabelPosition = TpoAveragesLabelRight ? LabelPosition.Right : LabelPosition.Left,
            ShowValueInLabel = TpoAveragesShowValue,
            ShowAxisLabel = TpoAveragesAxisLabel,
            Opacity = opacity,
            FontSize = fontSize,
            BlinkEnabled = TpoAveragesBlink,
            BlinkDistanceTicks = blinkTicks,
            BlinkIntervalMs = blinkMs,
        };
    }

    /// <summary>Dodaje nową średnią (kolejny okres i kolor z palety).</summary>
    public void AddMovingAverage()
    {
        string[] palette = ["#F0C030", "#4FA3F7", "#E040FB", "#FF8C2E", "#2EC4E6", "#FFFFFF"];
        var period = MovingAverages.Count == 0 ? 20 : MovingAverages.Max(m => int.TryParse(m.Period, out var p) ? p : 0) * 2;
        MovingAverages.Add(new MovingAverageEditorViewModel(new MovingAverageStyle
        {
            Period = Math.Clamp(period, 2, 1000),
            Color = palette[MovingAverages.Count % palette.Length],
            Thickness = 1,
        }));
    }

    public void RemoveMovingAverage(MovingAverageEditorViewModel average) => MovingAverages.Remove(average);

    /// <summary>Przywraca domyślny wygląd wykresu D/W/M (zapis dopiero po kliknięciu „Zapisz”).</summary>
    public void ResetCandleChart()
    {
        LoadCandleChart(new CandleChartStyle());
        LoadTpoAverages(new TpoAveragesStyle());
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Wygląd wykresu D/W/M z formularza; null + komunikat błędu, gdy coś jest niepoprawne.</summary>
    private CandleChartStyle? TryCreateCandleChart(out string? error)
    {
        error = null;
        if (!ColorUtil.TryParse(CandleUpColor, out _) || !ColorUtil.TryParse(CandleDownColor, out _))
        {
            error = "Wykres D/W/M: kolory świec podaj w formacie #RRGGBB.";
            return null;
        }

        if (!TryParseInt(CandleVolumeOpacity, 5, 100, out var volumeOpacity))
        {
            error = "Wykres D/W/M: krycie wolumenu podaj w procentach od 5 do 100.";
            return null;
        }

        if (!TryParseInt(CandleIbRefreshMinutes, 1, 60, out var ibRefreshMinutes))
        {
            error = "Wykres D/W/M: odświeżanie z IBKR podaj w minutach od 1 do 60.";
            return null;
        }

        var averages = new List<MovingAverageStyle>();
        for (var i = 0; i < MovingAverages.Count; i++)
        {
            if (MovingAverages[i].TryCreate(out var average) is { } averageError)
            {
                error = $"Wykres D/W/M – średnia nr {i + 1}: {averageError}";
                return null;
            }

            averages.Add(average!);
        }

        return new CandleChartStyle
        {
            RightMarginPercent = _original.CandleChart.RightMarginPercent, // ustawiane na wykresie, nie w tym oknie
            CashSessionOnly = _original.CandleChart.CashSessionOnly,       // przełącznik na pasku wykresu
            Type = CandleTypeOhlc ? CandleChartType.OhlcBars : CandleChartType.Candles,
            UpColor = CandleUpColor.Trim().ToUpperInvariant(),
            DownColor = CandleDownColor.Trim().ToUpperInvariant(),
            ShowVolume = CandleShowVolume,
            VolumeOpacity = volumeOpacity,
            IbRefreshMinutes = ibRefreshMinutes,
            MovingAverages = averages,
        };
    }

    /// <summary>Liczba okresów TPO tworzących Initial Balance.</summary>
    public string InitialBalancePeriods { get; set; } = string.Empty;

    /// <summary>Karta „Otwarcie”: inwentarz istotny od tylu % zakresu poprzedniego dnia.</summary>
    public string InventorySignificantPercent { get; set; } = string.Empty;

    // --- Strzałki otwarcia i zamknięcia profilu ---
    public bool ArrowShowOpen { get; set; }
    public bool ArrowShowClose { get; set; }
    public string ArrowOpenColor { get; set; } = string.Empty;
    public string ArrowCloseColor { get; set; } = string.Empty;
    public string ArrowThickness { get; set; } = string.Empty;
    public string ArrowHeadSize { get; set; } = string.Empty;
    public string ArrowLength { get; set; } = string.Empty;

    private void LoadArrows(ProfileArrowsStyle arrows)
    {
        ArrowShowOpen = arrows.ShowOpen;
        ArrowShowClose = arrows.ShowClose;
        ArrowOpenColor = arrows.OpenColor;
        ArrowCloseColor = arrows.CloseColor;
        ArrowThickness = ToText(arrows.Thickness);
        ArrowHeadSize = ToText(arrows.HeadSize);
        ArrowLength = ToText(arrows.Length);
    }

    // --- Elementy profilu TPO ---

    /// <summary>Czy pokazywać pasek Initial Balance.</summary>
    public bool ShowInitialBalance { get; set; }

    /// <summary>Czy pokazywać POC.</summary>
    public bool ShowPoc { get; set; }

    // --- Zegar bieżącego okresu TPO ---
    public bool TimerEnabled { get; set; }
    public TimerPosition TimerPosition { get; set; }
    public string TimerFontSize { get; set; } = string.Empty;
    public FontWeightKind TimerFontWeight { get; set; }
    public string TimerColor { get; set; } = string.Empty;

    /// <summary>Położenia zegara do wyboru.</summary>
    public IReadOnlyList<KeyValuePair<TimerPosition, string>> TimerPositions { get; } =
    [
        new(TimerPosition.TopLeft, "Lewy górny róg"),
        new(TimerPosition.TopCenter, "Góra – środek"),
        new(TimerPosition.TopRight, "Prawy górny róg"),
    ];

    /// <summary>Grubości czcionki do wyboru.</summary>
    public IReadOnlyList<KeyValuePair<FontWeightKind, string>> FontWeights { get; } =
    [
        new(FontWeightKind.Normal, "Normalna"),
        new(FontWeightKind.SemiBold, "Półgruba"),
        new(FontWeightKind.Bold, "Gruba"),
    ];

    private void LoadTimer(PeriodTimerStyle timer)
    {
        TimerEnabled = timer.Enabled;
        TimerPosition = timer.Position;
        TimerFontSize = ToText(timer.FontSize);
        TimerFontWeight = timer.FontWeight;
        TimerColor = timer.Color;
    }

    // --- Linia HalfBack (połowa wysokości profilu) ---
    public bool HalfBackEnabled { get; set; }
    public LineStyle HalfBackLineStyle { get; set; }
    public string HalfBackColor { get; set; } = string.Empty;
    public string HalfBackThickness { get; set; } = string.Empty;
    public string HalfBackOpacity { get; set; } = string.Empty;

    /// <summary>Style linii do wyboru.</summary>
    /// <summary>Linia albo pasek – rodzaj średniej na wykresie TPO.</summary>
    public IReadOnlyList<KeyValuePair<TpoAverageShape, string>> TpoShapes { get; } =
    [
        new(TpoAverageShape.Line, "Linia"),
        new(TpoAverageShape.Band, "Pasek"),
    ];

    public IReadOnlyList<KeyValuePair<LineStyle, string>> LineStyles { get; } =
    [
        new(LineStyle.Solid, "Ciągła"),
        new(LineStyle.Dashed, "Kreskowana"),
        new(LineStyle.Dotted, "Kropkowana"),
    ];

    // --- Podświetlenie profilu wybranego w tabeli parametrów ---
    public string HighlightColor { get; set; } = string.Empty;
    public string HighlightOpacity { get; set; } = string.Empty;

    private void LoadHighlight(ProfileHighlightStyle highlight)
    {
        HighlightColor = highlight.Color;
        HighlightOpacity = ToText(highlight.Opacity);
    }

    private void LoadHalfBack(HalfBackStyle halfBack)
    {
        HalfBackEnabled = halfBack.Enabled;
        HalfBackLineStyle = halfBack.Style;
        HalfBackColor = halfBack.Color;
        HalfBackThickness = ToText(halfBack.Thickness);
        HalfBackOpacity = ToText(halfBack.Opacity);
    }

    // --- Kreska połowy okresu (tryb Split) ---
    public bool MidlineEnabled { get; set; }
    public string MidlineColor { get; set; }
    public string MidlineThickness { get; set; }
    public string MidlineOpacity { get; set; }

    // --- Sesje i TPO ---
    public string TicksPerRow { get; set; }
    public string TpoMinutes { get; set; }
    public string ValueAreaPercent { get; set; }

    /// <summary>Reguła wyboru POC przy remisie odległości od środka profilu.</summary>
    public PocTieBreak PocTieBreak { get; set; }

    /// <summary>Reguły remisu POC do wyboru.</summary>
    public IReadOnlyList<KeyValuePair<PocTieBreak, string>> PocTieBreaks { get; } =
    [
        new(PocTieBreak.MoreTpoSide, "Strona z większą liczbą TPO"),
        new(PocTieBreak.Lower, "Niższa cena"),
        new(PocTieBreak.Higher, "Wyższa cena"),
    ];

    public string HistoryDays { get; set; }
    public string SimulatorSpeed { get; set; }

    // --- Profil wolumenowy: dla których profili TPO ---

    /// <summary>Dla których profili TPO rysować profil wolumenowy (to samo co „Wolumen” na pasku narzędzi).</summary>
    public VolumeProfileMode VolumeMode { get; set; }

    /// <summary>Opcje wyboru profili, dla których rysowany jest profil wolumenowy.</summary>
    public IReadOnlyList<KeyValuePair<VolumeProfileMode, string>> VolumeModes { get; } =
    [
        new(VolumeProfileMode.None, "Brak (wyłączony)"),
        new(VolumeProfileMode.Current, "Ostatni profil TPO"),
        new(VolumeProfileMode.All, "Wszystkie profile TPO"),
    ];

    // --- Instrumenty ---

    private InstrumentEditorViewModel? _selectedInstrumentEditor;
    private string _instrumentLookupResult = string.Empty;
    private bool _isLookingUp;

    /// <summary>Lista instrumentów (każdy z własnym edytorem).</summary>
    public ObservableCollection<InstrumentEditorViewModel> InstrumentEditors { get; } = [];

    /// <summary>Instrument zaznaczony na liście (edytowany po prawej stronie).</summary>
    public InstrumentEditorViewModel? SelectedInstrumentEditor
    {
        get => _selectedInstrumentEditor;
        set
        {
            if (SetProperty(ref _selectedInstrumentEditor, value))
            {
                InstrumentLookupResult = string.Empty;
            }
        }
    }

    /// <summary>Wynik „Sprawdź w IB” / komunikaty listy instrumentów.</summary>
    public string InstrumentLookupResult
    {
        get => _instrumentLookupResult;
        private set => SetProperty(ref _instrumentLookupResult, value);
    }

    /// <summary>Czy można uruchomić „Sprawdź w IB” (nie trwa już inne wyszukiwanie).</summary>
    public bool CanLookupInstrument => !_isLookingUp;

    /// <summary>Rodzaje instrumentów do wyboru.</summary>
    public IReadOnlyList<KeyValuePair<InstrumentType, string>> InstrumentTypes { get; } =
    [
        new(InstrumentType.Future, "Futures (kontrakt terminowy)"),
        new(InstrumentType.Stock, "Akcja / ETF"),
        new(InstrumentType.Index, "Indeks"),
    ];

    /// <summary>Popularne strefy czasowe (można też wpisać własną, np. "Asia/Singapore").</summary>
    public IReadOnlyList<string> CommonTimeZones { get; } =
    [
        "America/New_York", "America/Chicago", "America/Los_Angeles", "Europe/London", "Europe/Berlin",
        "Europe/Paris", "Europe/Warsaw", "Europe/Zurich", "Asia/Tokyo", "Asia/Hong_Kong", "Asia/Singapore", "Australia/Sydney",
    ];

    /// <summary>Dodaje nowy instrument (do uzupełnienia – najwygodniej przyciskiem „Sprawdź w IB”).</summary>
    public void AddInstrument()
    {
        var number = 1;
        while (InstrumentEditors.Any(e => e.Symbol == $"NOWY{number}"))
        {
            number++;
        }

        var editor = new InstrumentEditorViewModel(new Instrument
        {
            Symbol = $"NOWY{number}",
            Type = InstrumentType.Stock,
            Exchange = "SMART",
            TickSize = 0.01m,
            NightStart = new TimeOnly(16, 0),
            NightEnd = new TimeOnly(9, 30),
        });
        InstrumentEditors.Add(editor);
        SelectedInstrumentEditor = editor;
        InstrumentLookupResult = "Wpisz symbol (np. MSFT, CL), wybierz rodzaj i giełdę, a potem kliknij „Sprawdź w IBKR”.";
    }

    /// <summary>Usuwa zaznaczony instrument (musi zostać co najmniej jeden).</summary>
    public void RemoveSelectedInstrument()
    {
        if (SelectedInstrumentEditor is not { } editor)
        {
            return;
        }

        if (InstrumentEditors.Count <= 1)
        {
            InstrumentLookupResult = "Na liście musi zostać co najmniej jeden instrument.";
            return;
        }

        var index = InstrumentEditors.IndexOf(editor);
        InstrumentEditors.Remove(editor);
        SelectedInstrumentEditor = InstrumentEditors[Math.Min(index, InstrumentEditors.Count - 1)];
        InstrumentLookupResult = $"Usunięto {editor.Symbol} (zmiana zostanie zapisana po kliknięciu „Zapisz”). Dane w bazie i linie zostają.";
    }

    /// <summary>Dodaje brakujące instrumenty z gotowej listy (istniejących nie zmienia).</summary>
    public void RestoreBuiltInInstruments()
    {
        var added = 0;
        foreach (var builtIn in Instrument.BuiltIn.Where(b => InstrumentEditors.All(e => e.Symbol != b.Symbol)))
        {
            InstrumentEditors.Add(new InstrumentEditorViewModel(builtIn));
            added++;
        }

        InstrumentLookupResult = added == 0
            ? "Wszystkie instrumenty z gotowej listy są już na liście."
            : $"Dodano {added} instrumentów z gotowej listy.";
    }

    /// <summary>
    /// „Sprawdź w IB”: wyszukuje zaznaczony instrument w Interactive Brokers i uzupełnia giełdę, walutę, tick,
    /// strefę czasową i godziny sesji. Używa Client ID + 101, żeby nie kolidować z połączeniem aplikacji.
    /// </summary>
    public async Task LookupSelectedInstrumentAsync()
    {
        if (SelectedInstrumentEditor is not { } editor)
        {
            return;
        }

        if (!TryCreateIbSettings(out var ibSettings, out var ibError))
        {
            InstrumentLookupResult = ibError;
            return;
        }

        if (!ibSettings.UseWebApi && !IbApiValid)
        {
            InstrumentLookupResult = $"Interactive Brokers: {IbApiStatus}";
            return;
        }

        if (editor.TryCreate(out var instrument) is { } error)
        {
            InstrumentLookupResult = error;
            return;
        }

        _isLookingUp = true;
        OnPropertyChanged(nameof(CanLookupInstrument));
        InstrumentLookupResult = $"Wyszukiwanie {instrument!.BrokerSymbol} w IBKR...";

        try
        {
            UseTwsLibraryIfNeeded(ibSettings);
            var info = await IbFeeds.LookupAsync(ibSettings with { ClientId = ibSettings.ClientId + 101 }, instrument);
            editor.Apply(info);

            var expiry = string.IsNullOrWhiteSpace(info.Expiry) ? string.Empty : $", wygasa {info.Expiry}";
            var sessions = info.Sessions is { } s
                ? $"Sesja główna wg IBKR: {s.DayStart:HH\\:mm}–{s.DayEnd:HH\\:mm}" + (s.HasNight ? $", poza nią: {s.NightStart:HH\\:mm}–{s.NightEnd:HH\\:mm}" : ", bez handlu poza nią")
                : "IBKR nie podał godzin handlu.";
            InstrumentLookupResult =
                $"Znaleziono: {info.LocalSymbol} – {info.LongName} ({info.Exchange}, {info.Currency}, tick {info.MinTick.ToString(CultureInfo.InvariantCulture)}{expiry}). " +
                $"Strefa czasowa: {info.TimeZoneId}. {sessions}. Sprawdź godziny sesji i kliknij „Zapisz”.";
        }
        catch (Exception ex)
        {
            InstrumentLookupResult = $"Nie znaleziono w IBKR: {ex.Message}";
        }
        finally
        {
            _isLookingUp = false;
            OnPropertyChanged(nameof(CanLookupInstrument));
        }
    }

    // --- Interactive Brokers ---

    private string _ibApiPath;
    private string _ibApiStatus = string.Empty;
    private bool _ibApiValid;
    private string _ibTestResult = string.Empty;
    private bool _isTestingIb;

    private LiveSourceKind _liveSource;

    /// <summary>Tryb „Na żywo” korzysta z Interactive Brokers przez TWS / IB Gateway.</summary>
    public bool UseInteractiveBrokers
    {
        get => _liveSource == LiveSourceKind.InteractiveBrokers;
        set => SetLiveSource(value, LiveSourceKind.InteractiveBrokers);
    }

    /// <summary>Tryb „Na żywo” korzysta z Interactive Brokers przez IBKR Web API (bez TWS).</summary>
    public bool UseIbWebApi
    {
        get => _liveSource == LiveSourceKind.InteractiveBrokersWebApi;
        set => SetLiveSource(value, LiveSourceKind.InteractiveBrokersWebApi);
    }

    /// <summary>Tryb „Na żywo” korzysta z generatora losowych cen.</summary>
    public bool UseGeneratorLive
    {
        get => _liveSource == LiveSourceKind.Generator;
        set => SetLiveSource(value, LiveSourceKind.Generator);
    }

    /// <summary>Przyciski opcji: zaznaczenie ustawia źródło, odznaczenie (przez inny przycisk) niczego nie zmienia.</summary>
    private void SetLiveSource(bool selected, LiveSourceKind kind)
    {
        if (!selected || _liveSource == kind)
        {
            return;
        }

        _liveSource = kind;
        OnPropertyChanged(nameof(UseInteractiveBrokers));
        OnPropertyChanged(nameof(UseIbWebApi));
        OnPropertyChanged(nameof(UseGeneratorLive));
    }

    /// <summary>Dla TWS API wskazuje bibliotekę ze ścieżki z okna (Web API jej nie potrzebuje).</summary>
    private void UseTwsLibraryIfNeeded(IbConnectionSettings settings)
    {
        if (!settings.UseWebApi)
        {
            IbApiLoader.Use(IbApiPath);
        }
    }

    // --- IBKR Web API (klucze OAuth) ---
    //
    // BEZPIECZEŃSTWO: klucze nie są właściwościami wiązanymi z oknem ani częścią AppSettings. Wartości z pól hasła
    // i zawartość plików PEM trafiają od razu do zaszyfrowanego magazynu (DPAPI, %AppData%\MarketProfile) i nie są
    // nigdzie wyświetlane ani zapisywane jawnie.

    private readonly IbWebCredentialStore _webKeys = new();
    private readonly Dictionary<WebApiKeyFile, string> _webKeyFiles = [];
    private string _webApiKeysStatus = string.Empty;
    private string _webApiKeyFilesText = string.Empty;

    /// <summary>Web API: przejmij sesję, gdy ten sam użytkownik jest zalogowany w TWS (TWS zostanie rozłączony).</summary>
    public bool IbWebApiCompete { get; set; }

    /// <summary>Stan zapisanych kluczy (bez ich wartości).</summary>
    public string WebApiKeysStatus
    {
        get => _webApiKeysStatus;
        private set => SetProperty(ref _webApiKeysStatus, value);
    }

    /// <summary>Wybrane pliki kluczy (tylko nazwy plików) oczekujące na „Zapisz klucze”.</summary>
    public string WebApiKeyFilesText
    {
        get => _webApiKeyFilesText;
        private set => SetProperty(ref _webApiKeyFilesText, value);
    }

    /// <summary>Wybór pliku klucza (zawartość jest czytana dopiero przy „Zapisz klucze”).</summary>
    public void SetWebApiKeyFile(WebApiKeyFile kind, string path)
    {
        _webKeyFiles[kind] = path;
        var names = _webKeyFiles.OrderBy(f => f.Key).Select(f => $"{WebApiKeyFileName(f.Key)}: {Path.GetFileName(f.Value)}");
        var warning = _webKeyFiles.Values.Any(IsInsideGitRepository)
            ? "\n⚠ Co najmniej jeden plik leży w folderze repozytorium Git – po zapisaniu kluczy przenieś go stamtąd (nigdy nie dodawaj go do Gita)."
            : string.Empty;
        WebApiKeyFilesText = string.Join(" · ", names) + warning;
    }

    private static string WebApiKeyFileName(WebApiKeyFile kind) => kind switch
    {
        WebApiKeyFile.Signature => "klucz podpisu",
        WebApiKeyFile.Encryption => "klucz szyfrowania",
        _ => "dhparam",
    };

    /// <summary>
    /// Zapisuje klucze (zaszyfrowane). Puste pole / niewybrany plik = zostaje wartość zapisana wcześniej.
    /// Przed zapisem sprawdza, czy klucze są poprawne i pasują do siebie (odszyfrowanie access token secret, plik DH).
    /// </summary>
    public bool SaveWebApiKeys(string consumerKey, string accessToken, string accessTokenSecret)
    {
        try
        {
            var existing = LoadWebApiKeysOrEmpty();
            string Pem(WebApiKeyFile kind, string old) => _webKeyFiles.TryGetValue(kind, out var path) ? File.ReadAllText(path) : old;

            var credentials = new IbWebCredentials
            {
                ConsumerKey = string.IsNullOrWhiteSpace(consumerKey) ? existing.ConsumerKey : consumerKey.Trim(),
                AccessToken = string.IsNullOrWhiteSpace(accessToken) ? existing.AccessToken : accessToken.Trim(),
                AccessTokenSecret = string.IsNullOrWhiteSpace(accessTokenSecret) ? existing.AccessTokenSecret : accessTokenSecret.Trim(),
                SignatureKeyPem = Pem(WebApiKeyFile.Signature, existing.SignatureKeyPem),
                EncryptionKeyPem = Pem(WebApiKeyFile.Encryption, existing.EncryptionKeyPem),
                DhParamPem = Pem(WebApiKeyFile.DhParam, existing.DhParamPem),
            };

            if (!credentials.IsComplete)
            {
                WebApiKeysStatus = $"Nie zapisano – brakuje: {string.Join(", ", credentials.Missing())}.";
                return false;
            }

            // Sprawdzenie bez łączenia z IBKR: klucz szyfrowania odszyfrowuje secret, klucz podpisu podpisuje, plik DH się wczytuje.
            _ = IbWebOAuth.DecryptPrepend(credentials.EncryptionKeyPem, credentials.AccessTokenSecret);
            _ = IbWebOAuth.SignRsa(credentials.SignatureKeyPem, "test");
            _ = IbWebOAuth.ReadDhParameters(credentials.DhParamPem);

            _webKeys.Save(credentials);
            _webKeyFiles.Clear();
            WebApiKeyFilesText = string.Empty;
            _ = IbWebSession.ResetSharedAsync(); // nowa sesja z nowymi kluczami
            RefreshWebApiKeysStatus();
            return true;
        }
        catch (Exception ex)
        {
            // Komunikat wyjątku nie zawiera wartości kluczy (błędy kryptografii / odczytu pliku).
            WebApiKeysStatus = $"Nie zapisano – klucze są niepoprawne albo nie pasują do siebie ({ex.GetType().Name}: {ex.Message}).";
            return false;
        }
    }

    /// <summary>Usuwa zapisane klucze Web API.</summary>
    public void DeleteWebApiKeys()
    {
        _webKeys.Delete();
        _ = IbWebSession.ResetSharedAsync();
        RefreshWebApiKeysStatus();
    }

    private IbWebCredentials LoadWebApiKeysOrEmpty()
    {
        try
        {
            return _webKeys.Load() ?? new IbWebCredentials();
        }
        catch (Exception)
        {
            return new IbWebCredentials(); // uszkodzony plik / inny użytkownik Windows – zapis nadpisze
        }
    }

    private bool HasCompleteWebApiKeys() => LoadWebApiKeysOrEmpty().IsComplete;

    private void RefreshWebApiKeysStatus()
    {
        try
        {
            WebApiKeysStatus = _webKeys.Load() is { } keys
                ? keys.IsComplete
                    ? $"✓ Klucze zapisane (zaszyfrowane w Windows, poza repozytorium), consumer key {IbWebCredentials.Mask(keys.ConsumerKey)}."
                    : $"Klucze niekompletne – brakuje: {string.Join(", ", keys.Missing())}."
                : "Brak zapisanych kluczy – wpisz je i wczytaj pliki, potem „Zapisz klucze”.";
        }
        catch (Exception ex)
        {
            WebApiKeysStatus = $"Nie udało się odczytać zapisanych kluczy: {ex.Message}";
        }
    }

    /// <summary>Czy plik leży w folderze repozytorium Git (jest nad nim folder .git).</summary>
    private static bool IsInsideGitRepository(string path)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(path)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ścieżka do biblioteki TWS API (plik CSharpAPI.dll albo folder) – sprawdzana na bieżąco.</summary>
    public string IbApiPath
    {
        get => _ibApiPath;
        set
        {
            if (SetProperty(ref _ibApiPath, value))
            {
                ValidateIbApiPath();
            }
        }
    }

    /// <summary>Wynik sprawdzenia ścieżki biblioteki (np. „Znaleziono CSharpAPI.dll (wersja 10.45.1.0)”).</summary>
    public string IbApiStatus
    {
        get => _ibApiStatus;
        private set => SetProperty(ref _ibApiStatus, value);
    }

    /// <summary>Czy ścieżka wskazuje poprawną bibliotekę (kolor komunikatu w oknie).</summary>
    public bool IbApiValid
    {
        get => _ibApiValid;
        private set => SetProperty(ref _ibApiValid, value);
    }

    public string IbHost { get; set; }
    public string IbPort { get; set; }
    public string IbClientId { get; set; }
    public IbDataType IbDataType { get; set; }

    /// <summary>Rodzaje danych z IB do wyboru.</summary>
    public IReadOnlyList<KeyValuePair<IbDataType, string>> IbDataTypes { get; } =
    [
        new(IbDataType.Live, "Na żywo (wymaga subskrypcji danych)"),
        new(IbDataType.Delayed, "Opóźnione ok. 15 min (bez subskrypcji)"),
        new(IbDataType.DelayedFrozen, "Opóźnione zamrożone (także gdy rynek zamknięty)"),
    ];

    /// <summary>Wynik testu połączenia.</summary>
    public string IbTestResult
    {
        get => _ibTestResult;
        private set => SetProperty(ref _ibTestResult, value);
    }

    /// <summary>Czy można uruchomić test połączenia (nie trwa już inny test).</summary>
    public bool CanTestIb => !_isTestingIb;

    /// <summary>
    /// „Sprawdź historię ticków”: pyta TWS o transakcje z coraz dalszej przeszłości (1 … 365 dni) dla instrumentu
    /// wybranego na wykresie i pokazuje, jak daleko wstecz są ticki oraz ile trwałoby ich pobranie.
    /// Używa Client ID + 102 (+1 – pobieranie w tle, +100 – test połączenia, +101 – „Sprawdź w IB”).
    /// </summary>
    public async Task ProbeTickHistoryAsync()
    {
        if (!TryCreateIbSettings(out var ibSettings, out var error))
        {
            IbTestResult = error;
            return;
        }

        var instrument = _original.Instrument;
        _isTestingIb = true;
        OnPropertyChanged(nameof(CanTestIb));
        IbTestResult = $"Łączenie z {ibSettings.Endpoint} – sprawdzanie historii ticków {instrument.Symbol} (ok. 1–3 min)...";

        _probeCts?.Cancel();
        var cts = _probeCts = new CancellationTokenSource();
        try
        {
            UseTwsLibraryIfNeeded(ibSettings);
            var progress = new Progress<string>(message => IbTestResult = $"{instrument.Symbol}: {message}");
            var result = await IbFeeds.ProbeTickHistoryAsync(ibSettings with { ClientId = ibSettings.ClientId + 102 }, instrument, progress, cts.Token);
            IbTestResult = DescribeTickProbe(result, instrument, _original.BackgroundHistoryDays);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            IbTestResult = "Sprawdzanie historii ticków przerwane";
        }
        catch (Exception ex)
        {
            IbTestResult = $"Błąd: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts))
            {
                _probeCts = null;
            }

            cts.Dispose();
            _isTestingIb = false;
            OnPropertyChanged(nameof(CanTestIb));
        }
    }

    private CancellationTokenSource? _probeCts;

    /// <summary>Przerywa trwające długie operacje (np. sprawdzanie historii ticków) – przy zamykaniu okna ustawień.</summary>
    public void CancelPendingOperations() => _probeCts?.Cancel();

    /// <summary>Opis wyniku sprawdzenia historii ticków dla użytkownika.</summary>
    private static string DescribeTickProbe(IbTickProbeResult r, Instrument instrument, int backgroundDays)
    {
        if (r.MaxDaysBack is not { } maxDays)
        {
            return $"{r.ContractName}: IBKR nie zwrócił żadnych ticków historycznych." +
                   (r.LastError is { } e ? $" {e}" : " (Możliwy brak uprawnień do danych tej giełdy.)");
        }

        var text = $"{r.ContractName}: ticki dostępne co najmniej {maxDays} dni wstecz" +
                   (r.OldestTick is { } oldest ? $" (najstarszy otrzymany: {oldest.ToLocalTime():yyyy-MM-dd HH:mm})" : string.Empty) +
                   (r.FirstDaysWithout is { } without ? $"; {without} dni wstecz – już brak." : " (sprawdzono do 365 dni).");

        if (r.Density is { } d)
        {
            // Godziny handlu w dobie = suma długości sesji instrumentu; dni kalendarzowe → handlowe (5 z 7).
            var hours = instrument.Sessions.Sum(s => s.Duration.TotalHours);
            var tradingDays = Math.Max(1, (int)Math.Round(backgroundDays * 5 / 7.0));
            text += $"\nGęstość (próbka sprzed ok. 1 dnia): ok. {d.TicksPerDay(hours):N0} ticków na dzień handlowy " +
                    $"= ok. {d.RequestsPerDay(hours):N0} zapytań po 1000 ticków. Przy limicie IBKR (60 zapytań / 10 min) " +
                    $"pobranie 1 dnia trwa ok. {FormatDuration(d.DownloadTime(hours, 1))}, " +
                    $"{backgroundDays} dni (ok. {tradingDays} handlowych) – ok. {FormatDuration(d.DownloadTime(hours, tradingDays))}.";
        }

        return text;
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0.#} h" : $"{Math.Max(1, t.TotalMinutes):0} min";

    /// <summary>Wyszukuje bibliotekę automatycznie i wpisuje znalezioną ścieżkę.</summary>
    public void AutoDetectIbApi()
    {
        var found = IbApiLoader.AutoDetect();
        if (found is null)
        {
            IbApiStatus = "Nie znaleziono biblioteki TWS API automatycznie – wskaż plik CSharpAPI.dll przyciskiem „Przeglądaj…”.";
            IbApiValid = false;
            return;
        }

        IbApiPath = found;
        OnPropertyChanged(nameof(IbApiPath));
    }

    /// <summary>
    /// Test połączenia z TWS: ładuje bibliotekę z podanej ścieżki, łączy się, wyszukuje kontrakt,
    /// czeka na notowanie w wybranym rodzaju danych i rozłącza. Używa Client ID + 100,
    /// żeby nie kolidować z połączeniem, które aplikacja może mieć otwarte w tej chwili.
    /// </summary>
    public async Task TestIbConnectionAsync()
    {
        if (!TryCreateIbSettings(out var ibSettings, out var error))
        {
            IbTestResult = error;
            return;
        }

        _isTestingIb = true;
        OnPropertyChanged(nameof(CanTestIb));
        IbTestResult = $"Łączenie z {ibSettings.Endpoint}...";

        try
        {
            UseTwsLibraryIfNeeded(ibSettings);
            IbTestResult = await IbFeeds.TestConnectionAsync(ibSettings with { ClientId = ibSettings.ClientId + 100 }, _original.Instrument);
        }
        catch (Exception ex)
        {
            IbTestResult = $"Błąd: {ex.Message}";
        }
        finally
        {
            _isTestingIb = false;
            OnPropertyChanged(nameof(CanTestIb));
        }
    }

    private void ValidateIbApiPath()
    {
        IbApiValid = IbApiLoader.TryValidate(_ibApiPath, out var dll, out var message);
        IbApiStatus = IbApiValid && dll is not null && !string.Equals(dll, _ibApiPath?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? $"{message}\n{dll}"
            : message;
    }

    /// <summary>Sprawdza pola połączenia z IB i tworzy z nich ustawienia.</summary>
    private bool TryCreateIbSettings(out IbConnectionSettings settings, out string error)
    {
        settings = new IbConnectionSettings();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(IbHost))
        {
            error = "Interactive Brokers: podaj adres TWS (np. 127.0.0.1).";
            return false;
        }

        if (!TryParseInt(IbPort, 1, 65535, out var port))
        {
            error = "Interactive Brokers: port podaj jako liczbę 1–65535 (TWS demo: 7497).";
            return false;
        }

        if (!TryParseInt(IbClientId, 0, 999_999, out var clientId))
        {
            error = "Interactive Brokers: Client ID podaj jako liczbę 0–999999.";
            return false;
        }

        settings = new IbConnectionSettings
        {
            Host = IbHost.Trim(),
            Port = port,
            ClientId = clientId,
            DataType = IbDataType,
            UseWebApi = UseIbWebApi,
            WebApiCompete = IbWebApiCompete,
        };
        return true;
    }

    // --- Baza danych ---
    public bool RecordToDatabase { get; set; }

    // --- Dziennik zdarzeń ---
    private string _logDirectory = string.Empty;

    /// <summary>Folder dziennika zdarzeń i błędów (domyślnie %AppData%\MarketProfile\logs).</summary>
    public string LogDirectory
    {
        get => _logDirectory;
        set => SetProperty(ref _logDirectory, value);
    }

    /// <summary>Domyślny folder dziennika (podpowiedź i przycisk „Domyślny”).</summary>
    public string DefaultLogDirectory => AppSettings.DefaultLogDirectory;

    /// <summary>Przywraca domyślny folder dziennika.</summary>
    public void ResetLogDirectory() => LogDirectory = AppSettings.DefaultLogDirectory;

    /// <summary>Czy pokazywać postęp pobierania w tle w polu informacji okna głównego.</summary>
    public bool ShowDownloadProgress { get; set; }

    /// <summary>Rozmiar kontrolek pasków okna głównego.</summary>
    public ControlSize ControlSize { get; set; }

    /// <summary>Rozmiary kontrolek do wyboru (wartość – opis).</summary>
    public IReadOnlyList<KeyValuePair<ControlSize, string>> ControlSizes { get; } =
    [
        new(ControlSize.Small, "Małe"),
        new(ControlSize.Normal, "Normalne"),
        new(ControlSize.Large, "Duże"),
    ];

    // --- Tempo budowania TPO ---
    public bool TempoGauge { get; set; }
    public TempoGaugePosition TempoGaugePosition { get; set; }
    public bool TempoInStatusBar { get; set; }

    /// <summary>Położenia wskaźnika tempa do wyboru.</summary>
    public IReadOnlyList<KeyValuePair<TempoGaugePosition, string>> TempoGaugePositions { get; } =
    [
        new(TempoGaugePosition.UnderTimer, "Pod zegarem TPO"),
        new(TempoGaugePosition.TopLeft, "Lewy górny róg"),
        new(TempoGaugePosition.TopCenter, "Góra – środek"),
        new(TempoGaugePosition.TopRight, "Prawy górny róg"),
        new(TempoGaugePosition.BottomLeft, "Lewy dolny róg"),
        new(TempoGaugePosition.BottomRight, "Prawy dolny róg"),
    ];
    public string TestDataDays { get; set; }

    /// <summary>Ile dni historii pobierać w tle dla instrumentów z zaznaczonym „Pobieraj dane”.</summary>
    public string BackgroundHistoryDays { get; set; }

    /// <summary>Ile ostatnich dni kompletnych ticków pobierać w tle (0 = tylko świece).</summary>
    public string BackgroundTickDays { get; set; }

    /// <summary>Informacja o danych instrumentu zapisanych w bazie.</summary>
    public string DatabaseInfo
    {
        get => _databaseInfo;
        private set => SetProperty(ref _databaseInfo, value);
    }

    /// <summary>Czy trwa generowanie danych testowych (blokuje przycisk).</summary>
    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (SetProperty(ref _isGenerating, value))
            {
                OnPropertyChanged(nameof(CanGenerate));
            }
        }
    }

    public bool CanGenerate => !IsGenerating;

    /// <summary>Ścieżka pliku bazy (do wyświetlenia).</summary>
    public string DatabasePath => _store.FilePath;

    /// <summary>
    /// Generuje dane testowe (ticki z generatora cen) dla ostatnich N dni i zapisuje je w bazie.
    /// Poprzednie dane testowe tego instrumentu są usuwane; dane brokera pozostają.
    /// </summary>
    public async Task GenerateTestDataAsync()
    {
        if (!TryParseInt(TestDataDays, 1, 60, out var days))
        {
            Error = "Dane testowe: podaj liczbę dni od 1 do 60.";
            return;
        }

        Error = string.Empty;
        IsGenerating = true;
        DatabaseInfo = $"Generowanie danych testowych ({days} dni)...";

        try
        {
            var instrument = _original.Instrument;
            var count = await Task.Run(() => TestDataGenerator.Generate(_store, instrument, days));
            RefreshDatabaseInfo();
            DatabaseInfo += $"\nWygenerowano {count:N0} ticków testowych.";
        }
        catch (Exception ex)
        {
            Error = $"Błąd generowania danych: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    /// <summary>Odświeża informację o zakresie danych instrumentu w bazie.</summary>
    private void RefreshDatabaseInfo()
    {
        var symbol = _original.InstrumentSymbol;
        if (_store.GetDataRange(symbol) is not { } range)
        {
            DatabaseInfo = $"Brak danych {symbol} w bazie.";
            return;
        }

        var zone = _original.ExchangeTimeZone;
        DatabaseInfo = $"{symbol}: dane od {TimeZoneInfo.ConvertTime(range.From, zone):yyyy-MM-dd HH:mm} " +
                       $"do {TimeZoneInfo.ConvertTime(range.To, zone):yyyy-MM-dd HH:mm} " +
                       $"({range.Ticks:N0} ticków, {range.Bars:N0} świec)";
    }

    // --- Profil wolumenowy ---

    /// <summary>Skale profilu wolumenowego do wyboru.</summary>
    public IReadOnlyList<KeyValuePair<VolumeScaleMode, string>> VolumeScales { get; } =
    [
        new(VolumeScaleMode.Linear, "Liniowa"),
        new(VolumeScaleMode.SquareRoot, "Pierwiastkowa"),
        new(VolumeScaleMode.Logarithmic, "Logarytmiczna"),
    ];

    public VolumeScaleMode VolumeScale { get; set; }
    public string VolumeCapPercent { get; set; }
    public string VolumeGap { get; set; }
    public string VolumeWidth { get; set; }
    public bool VolumeMirrored { get; set; }

    /// <summary>Zwykły kierunek słupków (przeciwieństwo <see cref="VolumeMirrored"/> – dla przycisków opcji).</summary>
    public bool VolumeNormal
    {
        get => !VolumeMirrored;
        set => VolumeMirrored = !value;
    }

    public string VolumeBarColor { get; set; }
    public string VolumeVpocColor { get; set; }
    public string VolumeDeltaBuyColor { get; set; } = "";
    public string VolumeDeltaSellColor { get; set; } = "";

    /// <summary>Profil różnicowy (delta) zamiast tradycyjnego – to samo co w menu „Wyświetlanie”.</summary>
    public bool VolumeDisplayDelta { get; set; }

    public bool VolumeDisplayTotal
    {
        get => !VolumeDisplayDelta;
        set => VolumeDisplayDelta = !value;
    }
    public string VolumeOpacity { get; set; }

    // --- Profil wolumenowy bieżącego słupka TPO ---
    public bool PeriodVolumeEnabled { get; set; }
    public string PeriodVolumeGapFromTpo { get; set; } = "";
    public string PeriodVolumeGapToSession { get; set; } = "";
    public string PeriodVolumeWidth { get; set; } = "";
    public string PeriodVolumeColor { get; set; } = "";
    public string PeriodVolumeOpacity { get; set; } = "";
    public PeriodVolumeScaleMode PeriodVolumeScaleMode { get; set; }
    public string PeriodVolumeReferencePeriods { get; set; } = "";

    /// <summary>Skale profilu wolumenowego bieżącego słupka do wyboru.</summary>
    public IReadOnlyList<KeyValuePair<PeriodVolumeScaleMode, string>> PeriodVolumeScaleModes { get; } =
    [
        new(PeriodVolumeScaleMode.Reference, "Względem poprzednich słupków"),
        new(PeriodVolumeScaleMode.OwnMaximum, "Własne maksimum słupka"),
    ];

    /// <summary>Wczytuje ustawienia profilu wolumenowego bieżącego słupka do pól formularza.</summary>
    private void LoadPeriodVolume(PeriodVolumeStyle style)
    {
        PeriodVolumeEnabled = style.Enabled;
        PeriodVolumeGapFromTpo = ToText(style.GapFromTpo);
        PeriodVolumeGapToSession = ToText(style.GapToSessionVolume);
        PeriodVolumeWidth = ToText(style.Width);
        PeriodVolumeColor = style.BarColor;
        PeriodVolumeOpacity = ToText(style.Opacity);
        PeriodVolumeScaleMode = style.ScaleMode;
        PeriodVolumeReferencePeriods = ToText(style.ReferencePeriods);
    }

    // --- Liczby wolumenu przy słupkach ---
    public bool VolumeLabelsOnSession { get; set; }
    public bool VolumeLabelsOnPeriod { get; set; }
    public VolumeLabelPosition VolumeLabelPosition { get; set; }
    public string VolumeLabelColor { get; set; } = "";
    public string VolumeLabelOpacity { get; set; } = "";
    public string VolumeLabelFontSize { get; set; } = "";
    public FontWeightKind VolumeLabelFontWeight { get; set; }

    /// <summary>Położenia liczby wolumenu do wyboru.</summary>
    public IReadOnlyList<KeyValuePair<VolumeLabelPosition, string>> VolumeLabelPositions { get; } =
    [
        new(VolumeLabelPosition.OnBar, "Na słupku"),
        new(VolumeLabelPosition.Right, "Z prawej strony słupka"),
        new(VolumeLabelPosition.Left, "Z lewej strony słupka"),
    ];

    /// <summary>Wczytuje ustawienia liczb wolumenu do pól formularza.</summary>
    private void LoadVolumeLabels(VolumeLabelStyle style)
    {
        VolumeLabelsOnSession = style.ShowOnSessionVolume;
        VolumeLabelsOnPeriod = style.ShowOnPeriodVolume;
        VolumeLabelPosition = style.Position;
        VolumeLabelColor = style.Color;
        VolumeLabelOpacity = ToText(style.Opacity);
        VolumeLabelFontSize = ToText(style.FontSize);
        VolumeLabelFontWeight = style.FontWeight;
    }

    // --- Kolory sesji ---
    public ColorSchemeViewModel DayColors { get; }
    public ColorSchemeViewModel NightColors { get; }

    /// <summary>Opis błędu walidacji wyświetlany w oknie.</summary>
    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>
    /// Sprawdza wpisane wartości. Zwraca nowe ustawienia albo null (wtedy w <see cref="Error"/> jest opis problemu).
    /// </summary>
    public AppSettings? TryCreateSettings()
    {
        if (!TryParseInt(TicksPerRow, 1, 1000, out var ticksPerRow))
        {
            return Fail("Ticki na wiersz: podaj liczbę od 1 do 1000.");
        }

        if (!TryParseInt(TpoMinutes, 1, 240, out var tpoMinutes))
        {
            return Fail("Okres TPO: podaj liczbę minut od 1 do 240.");
        }

        if (!TryParseInt(ValueAreaPercent, 1, 100, out var vaPercent))
        {
            return Fail("Value Area: podaj procent od 1 do 100.");
        }

        if (!TryParseInt(HistoryDays, 1, 30, out var historyDays))
        {
            return Fail("Historia: podaj liczbę dni od 1 do 30.");
        }

        if (!double.TryParse(SimulatorSpeed?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
            || speed < 1 || speed > 3600)
        {
            return Fail("Przyspieszenie symulatora: podaj liczbę od 1 do 3600.");
        }

        // Profil wolumenowy.
        if (!TryParseInt(VolumeGap, 0, 200, out var volumeGap))
        {
            return Fail("Profil wolumenowy – odległość od TPO: podaj liczbę pikseli od 0 do 200.");
        }

        if (!TryParseInt(VolumeWidth, 10, 400, out var volumeWidth))
        {
            return Fail("Profil wolumenowy – długość: podaj liczbę pikseli od 10 do 400.");
        }

        if (!TryParseInt(VolumeOpacity, 5, 100, out var volumeOpacity))
        {
            return Fail("Profil wolumenowy – krycie: podaj procent od 5 do 100.");
        }

        if (!TryParseInt(VolumeCapPercent, 5, 100, out var volumeCap))
        {
            return Fail("Profil wolumenowy – limit: podaj procent od 5 do 100 (100 = bez limitu).");
        }

        if (!TryParseInt(PeriodVolumeGapFromTpo, 0, 200, out var pvGapFromTpo) ||
            !TryParseInt(PeriodVolumeGapToSession, 0, 200, out var pvGapToSession))
        {
            return Fail("Wolumen bieżącego słupka – odstępy: podaj liczbę pikseli od 0 do 200.");
        }

        if (!TryParseInt(PeriodVolumeWidth, 5, 400, out var pvWidth))
        {
            return Fail("Wolumen bieżącego słupka – długość: podaj liczbę pikseli od 5 do 400.");
        }

        if (!TryParseInt(PeriodVolumeOpacity, 5, 100, out var pvOpacity))
        {
            return Fail("Wolumen bieżącego słupka – krycie: podaj procent od 5 do 100.");
        }

        if (!TryParseInt(PeriodVolumeReferencePeriods, 1, 200, out var pvReferencePeriods))
        {
            return Fail("Wolumen bieżącego słupka – liczba słupków do odniesienia: podaj liczbę od 1 do 200.");
        }

        if (!TryParseInt(VolumeLabelFontSize, 5, 40, out var labelFontSize))
        {
            return Fail("Liczby wolumenu – wielkość czcionki: podaj liczbę od 5 do 40.");
        }

        if (!TryParseInt(VolumeLabelOpacity, 5, 100, out var labelOpacity))
        {
            return Fail("Liczby wolumenu – krycie: podaj procent od 5 do 100.");
        }

        if (!ColorUtil.TryParse(VolumeLabelColor, out _))
        {
            return Fail("Liczby wolumenu – kolor: podaj kolor w postaci #RRGGBB.");
        }

        if (!TryCreateIbSettings(out var ibSettings, out var ibError))
        {
            return Fail(ibError);
        }

        if (UseInteractiveBrokers && !IbApiValid)
        {
            return Fail($"Interactive Brokers: {IbApiStatus}");
        }

        if (UseIbWebApi && !HasCompleteWebApiKeys())
        {
            return Fail("IBKR Web API: najpierw zapisz klucze OAuth (Dane i broker → Źródło i połączenie → „Zapisz klucze”).");
        }

        if (!TryParseInt(TestDataDays, 1, 60, out var testDays))
        {
            return Fail("Dane testowe: podaj liczbę dni od 1 do 60.");
        }

        if (!TryParseInt(BackgroundHistoryDays, 1, 365, out var backgroundDays))
        {
            return Fail("Dane w tle: podaj liczbę dni historii od 1 do 365.");
        }

        if (!TryParseInt(BackgroundTickDays, 0, backgroundDays, out var tickDays))
        {
            return Fail($"Dane w tle: podaj liczbę dni ticków od 0 do {backgroundDays} (nie więcej niż dni historii świec).");
        }

        if (!ColorUtil.TryParse(VolumeBarColor, out _) || !ColorUtil.TryParse(VolumeVpocColor, out _) ||
            !ColorUtil.TryParse(VolumeDeltaBuyColor, out _) || !ColorUtil.TryParse(VolumeDeltaSellColor, out _))
        {
            return Fail("Profil wolumenowy: kolory podaj w formacie #RRGGBB.");
        }

        if (!ColorUtil.TryParse(MidlineColor, out _))
        {
            return Fail("Kreska połowy okresu: kolor podaj w formacie #RRGGBB.");
        }

        if (!TryParseInt(MidlineThickness, 1, 10, out var midlineThickness))
        {
            return Fail("Kreska połowy okresu: grubość podaj w pikselach od 1 do 10.");
        }

        if (!TryParseInt(MidlineOpacity, 5, 100, out var midlineOpacity))
        {
            return Fail("Kreska połowy okresu: krycie podaj w procentach od 5 do 100.");
        }

        if (!ColorUtil.TryParse(HalfBackColor, out _))
        {
            return Fail("HalfBack: kolor podaj w formacie #RRGGBB.");
        }

        if (!TryParseInt(HalfBackThickness, 1, 10, out var halfBackThickness))
        {
            return Fail("HalfBack: grubość podaj w pikselach od 1 do 10.");
        }

        if (!ColorUtil.TryParse(HighlightColor, out _))
        {
            return Fail("Podświetlenie wybranego profilu: kolor podaj w formacie #RRGGBB.");
        }

        if (!TryParseInt(HighlightOpacity, 5, 100, out var highlightOpacity))
        {
            return Fail("Podświetlenie wybranego profilu: krycie podaj w procentach od 5 do 100.");
        }

        if (!TryParseInt(HalfBackOpacity, 5, 100, out var halfBackOpacity))
        {
            return Fail("HalfBack: krycie podaj w procentach od 5 do 100.");
        }

        if (!TryParseInt(InitialBalancePeriods, 1, 20, out var ibPeriods))
        {
            return Fail("Initial Balance: podaj liczbę okresów TPO od 1 do 20.");
        }

        if (!TryParseInt(InventorySignificantPercent, 1, 100, out var inventoryPercent))
        {
            return Fail("Inwentarz: próg istotności podaj w procentach zakresu poprzedniego dnia, od 1 do 100.");
        }

        if (!ColorUtil.TryParse(ArrowOpenColor, out _) || !ColorUtil.TryParse(ArrowCloseColor, out _))
        {
            return Fail("Strzałki: kolory podaj w formacie #RRGGBB.");
        }

        if (!TryParseInt(ArrowThickness, 1, 10, out var arrowThickness))
        {
            return Fail("Strzałki: grubość podaj w pikselach od 1 do 10.");
        }

        if (!TryParseInt(ArrowHeadSize, 2, 30, out var arrowHead))
        {
            return Fail("Strzałki: wielkość grotu podaj w pikselach od 2 do 30.");
        }

        if (!TryParseInt(ArrowLength, 4, 80, out var arrowLength))
        {
            return Fail("Strzałki: długość podaj w pikselach od 4 do 80.");
        }

        if (!TryParseInt(TimerFontSize, 6, 72, out var timerFontSize))
        {
            return Fail("Zegar i tempo TPO: wielkość czcionki podaj od 6 do 72.");
        }

        if (!ColorUtil.TryParse(TimerColor, out _))
        {
            return Fail("Zegar i tempo TPO: kolor podaj w formacie #RRGGBB.");
        }

        if (TryCreateCandleChart(out var candleError) is not { } candleChart)
        {
            return Fail(candleError!);
        }

        if (TryCreateBroadMarketChart(out var broadChartError) is not { } broadMarketChart)
        {
            return Fail(broadChartError!);
        }

        if (TryCreateTpoAverages(out var tpoAveragesError) is not { } tpoAverages)
        {
            return Fail(tpoAveragesError!);
        }

        // Kolory sesji.
        if (DayColors.TryCreate(out var dayColors) is { } dayError)
        {
            return Fail($"Kolory sesji dziennej: {dayError}");
        }

        if (NightColors.TryCreate(out var nightColors) is { } nightError)
        {
            return Fail($"Kolory sesji nocnej: {nightError}");
        }

        // Instrumenty: każdy musi być poprawny, a symbole – unikalne.
        var instruments = new List<Instrument>();
        foreach (var editor in InstrumentEditors)
        {
            if (editor.TryCreate(out var instrument) is { } instrumentError)
            {
                SelectedInstrumentEditor = editor;
                return Fail($"Instrumenty: {instrumentError}");
            }

            instruments.Add(instrument!);
        }

        if (instruments.Count == 0)
        {
            return Fail("Instrumenty: lista nie może być pusta.");
        }

        if (instruments.GroupBy(i => i.Symbol).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            return Fail($"Instrumenty: symbol {duplicate.Key} występuje więcej niż raz.");
        }

        var broadSymbol = BroadMarketSymbol?.Trim() ?? string.Empty;
        if (Instrument.Find(instruments, broadSymbol) is not { } broadInstrument)
        {
            return Fail($"Szeroki rynek: nieznany instrument „{broadSymbol}” – wybierz indeks z listy (np. SPX).");
        }

        if (broadInstrument.Type != InstrumentType.Index)
        {
            return Fail($"Szeroki rynek: {broadInstrument.Symbol} nie jest indeksem – wybierz indeks (np. SPX), " +
                        "bo wykres pokazuje świece tylko z sesji kasowej.");
        }

        var logDirectory = string.IsNullOrWhiteSpace(LogDirectory) ? AppSettings.DefaultLogDirectory : LogDirectory.Trim();
        if (!Path.IsPathFullyQualified(logDirectory) || logDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return Fail($"Dziennik: „{logDirectory}” nie jest pełną ścieżką folderu (np. C:\\Logi\\MarketProfile).");
        }

        // Sektory: każda waga 0–100 %, data zestawienia S&P.
        var sectorWeights = new List<SectorWeight>();
        foreach (var editor in SectorWeightEditors)
        {
            var text = editor.WeightText?.Trim().Replace(',', '.') ?? string.Empty;
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var weight) || weight is < 0 or > 100)
            {
                return Fail($"Sektory: waga „{editor.Name}” musi być liczbą od 0 do 100 (%).");
            }

            sectorWeights.Add(new SectorWeight(editor.Key, weight));
        }

        if (!DateOnly.TryParseExact(SectorWeightsDate?.Trim(), ["yyyy-MM-dd", "dd.MM.yyyy"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var weightsDate))
        {
            return Fail("Sektory: data wag w formacie RRRR-MM-DD (np. 2026-10-31).");
        }

        // Wybrany instrument zostaje, chyba że został usunięty – wtedy pierwszy z listy.
        var selectedSymbol = instruments.Any(i => i.Symbol == _original.InstrumentSymbol)
            ? _original.InstrumentSymbol
            : instruments[0].Symbol;

        Error = string.Empty;
        return _original with
        {
            Instruments = instruments,
            InstrumentSymbol = selectedSymbol,            TicksPerRow = ticksPerRow,
            TpoMinutes = tpoMinutes,
            ValueAreaPercent = vaPercent,
            PocTieBreak = PocTieBreak,
            HistoryDays = historyDays,
            SimulatorSpeed = speed,
            RecordToDatabase = RecordToDatabase,
            LogDirectory = logDirectory,
            ShowDownloadProgress = ShowDownloadProgress,
            ControlSize = ControlSize,
            TempoGauge = TempoGauge,
            TempoGaugePosition = TempoGaugePosition,
            TempoInStatusBar = TempoInStatusBar,
            LiveSource = _liveSource,
            IbWebApiCompete = IbWebApiCompete,
            IbApiPath = IbApiPath?.Trim() ?? string.Empty,
            IbHost = ibSettings.Host,
            IbPort = ibSettings.Port,
            IbClientId = ibSettings.ClientId,
            IbDataType = ibSettings.DataType,
            TestDataDays = testDays,
            BackgroundHistoryDays = backgroundDays,
            BackgroundTickDays = tickDays,
            VolumeProfile = VolumeMode,
            VolumeDisplay = VolumeDisplayDelta ? VolumeDisplayKind.Delta : VolumeDisplayKind.Total,
            VolumeLabels = new VolumeLabelStyle
            {
                ShowOnSessionVolume = VolumeLabelsOnSession,
                ShowOnPeriodVolume = VolumeLabelsOnPeriod,
                Position = VolumeLabelPosition,
                Color = VolumeLabelColor.Trim().ToUpperInvariant(),
                Opacity = labelOpacity,
                FontSize = labelFontSize,
                FontWeight = VolumeLabelFontWeight,
            },
            PeriodVolume = new PeriodVolumeStyle
            {
                Enabled = PeriodVolumeEnabled,
                GapFromTpo = pvGapFromTpo,
                GapToSessionVolume = pvGapToSession,
                Width = pvWidth,
                BarColor = PeriodVolumeColor.Trim().ToUpperInvariant(),
                Opacity = pvOpacity,
                ScaleMode = PeriodVolumeScaleMode,
                ReferencePeriods = pvReferencePeriods,
            },
            VolumeStyle = new VolumeProfileStyle
            {
                Scale = VolumeScale,
                CapPercent = volumeCap,
                Gap = volumeGap,
                Width = volumeWidth,
                Mirrored = VolumeMirrored,
                BarColor = VolumeBarColor.Trim().ToUpperInvariant(),
                VpocColor = VolumeVpocColor.Trim().ToUpperInvariant(),
                DeltaBuyColor = VolumeDeltaBuyColor.Trim().ToUpperInvariant(),
                DeltaSellColor = VolumeDeltaSellColor.Trim().ToUpperInvariant(),
                Opacity = volumeOpacity,
            },
            DayColors = dayColors!,
            NightColors = nightColors!,
            SplitMidline = new SplitMidlineStyle
            {
                Enabled = MidlineEnabled,
                Color = MidlineColor.Trim().ToUpperInvariant(),
                Thickness = midlineThickness,
                Opacity = midlineOpacity,
            },
            SelectedProfileHighlight = new ProfileHighlightStyle
            {
                Color = HighlightColor.Trim().ToUpperInvariant(),
                Opacity = highlightOpacity,
            },
            HalfBack = new HalfBackStyle
            {
                Enabled = HalfBackEnabled,
                Style = HalfBackLineStyle,
                Color = HalfBackColor.Trim().ToUpperInvariant(),
                Thickness = halfBackThickness,
                Opacity = halfBackOpacity,
            },
            ShowInitialBalance = ShowInitialBalance,
            ShowPoc = ShowPoc,
            InitialBalancePeriods = ibPeriods,
            InventorySignificantPercent = inventoryPercent,
            Arrows = new ProfileArrowsStyle
            {
                ShowOpen = ArrowShowOpen,
                ShowClose = ArrowShowClose,
                OpenColor = ArrowOpenColor.Trim().ToUpperInvariant(),
                CloseColor = ArrowCloseColor.Trim().ToUpperInvariant(),
                Thickness = arrowThickness,
                HeadSize = arrowHead,
                Length = arrowLength,
            },
            CandleChart = candleChart,
            BroadMarketChart = broadMarketChart,
            TpoAverages = tpoAverages,
            BroadMarketSymbol = broadInstrument.Symbol,
            ShowSectorStrip = ShowSectorStrip,
            SectorWeights = sectorWeights,
            SectorWeightsDate = weightsDate,
            PeriodTimer = new PeriodTimerStyle
            {
                Enabled = TimerEnabled,
                Position = TimerPosition,
                FontSize = timerFontSize,
                FontWeight = TimerFontWeight,
                Color = TimerColor.Trim().ToUpperInvariant(),
            },
        };
    }

    /// <summary>Przywraca domyślne kolory obu sesji i domyślny wygląd profilu wolumenowego.</summary>
    public void ResetAppearance()
    {
        var defaults = new VolumeProfileStyle();
        VolumeGap = ToText(defaults.Gap);
        VolumeWidth = ToText(defaults.Width);
        VolumeMirrored = defaults.Mirrored;
        VolumeBarColor = defaults.BarColor;
        VolumeVpocColor = defaults.VpocColor;
        VolumeDeltaBuyColor = defaults.DeltaBuyColor;
        VolumeDeltaSellColor = defaults.DeltaSellColor;
        VolumeOpacity = ToText(defaults.Opacity);
        VolumeScale = defaults.Scale;
        VolumeCapPercent = ToText(defaults.CapPercent);
        LoadPeriodVolume(new PeriodVolumeStyle());
        LoadVolumeLabels(new VolumeLabelStyle());
        var midline = new SplitMidlineStyle();
        MidlineEnabled = midline.Enabled;
        MidlineColor = midline.Color;
        MidlineThickness = ToText(midline.Thickness);
        MidlineOpacity = ToText(midline.Opacity);
        LoadHalfBack(new HalfBackStyle());
        LoadHighlight(new ProfileHighlightStyle());
        LoadTimer(new PeriodTimerStyle());
        LoadArrows(new ProfileArrowsStyle());
        DayColors.Load(SessionColorScheme.DefaultDay);
        NightColors.Load(SessionColorScheme.DefaultNight);

        // Odświeżamy wszystkie pola w oknie.
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Ustawia komunikat błędu i zwraca null (brak poprawnych ustawień).</summary>
    private AppSettings? Fail(string message)
    {
        Error = message;
        return null;
    }

    private static string ToText(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal static bool TryParseInt(string? text, int min, int max, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
        && value >= min && value <= max;
}

/// <summary>
/// Pola edycji schematu kolorów jednej sesji (dziennej lub nocnej).
/// </summary>
public sealed class ColorSchemeViewModel : ObservableObject
{
    public ColorSchemeViewModel(SessionColorScheme scheme) => Load(scheme);

    /// <summary>Litery w kolorach "tęczy" (każdy okres inny kolor) – w przeciwnym razie jeden kolor.</summary>
    public bool RainbowLetters { get; set; }

    /// <summary>Litery w jednym kolorze (przeciwieństwo <see cref="RainbowLetters"/> – dla przycisków opcji).</summary>
    public bool SingleColorLetters
    {
        get => !RainbowLetters;
        set => RainbowLetters = !value;
    }

    public string LetterColor { get; set; } = string.Empty;
    public string PocColor { get; set; } = string.Empty;
    public string ValueAreaColor { get; set; } = string.Empty;
    public string ValueAreaOpacity { get; set; } = string.Empty;
    public string IbColor { get; set; } = string.Empty;

    /// <summary>Wypełnia pola wartościami schematu.</summary>
    public void Load(SessionColorScheme scheme)
    {
        RainbowLetters = scheme.LetterMode == LetterColorMode.Rainbow;
        LetterColor = scheme.LetterColor;
        PocColor = scheme.PocColor;
        ValueAreaColor = scheme.ValueAreaColor;
        ValueAreaOpacity = scheme.ValueAreaOpacity.ToString(CultureInfo.InvariantCulture);
        IbColor = scheme.IbColor;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Tworzy schemat z wpisanych wartości. Zwraca opis błędu albo null, gdy wszystko jest poprawne.</summary>
    public string? TryCreate(out SessionColorScheme? scheme)
    {
        scheme = null;

        foreach (var (name, value) in new[]
                 {
                     ("kolor liter", LetterColor), ("kolor POC", PocColor),
                     ("kolor Value Area", ValueAreaColor), ("kolor IB", IbColor),
                 })
        {
            if (!ColorUtil.TryParse(value, out _))
            {
                return $"{name} podaj w formacie #RRGGBB.";
            }
        }

        if (!SettingsViewModel.TryParseInt(ValueAreaOpacity, 0, 100, out var vaOpacity))
        {
            return "krycie Value Area podaj w procentach od 0 do 100.";
        }

        scheme = new SessionColorScheme
        {
            LetterMode = RainbowLetters ? LetterColorMode.Rainbow : LetterColorMode.Single,
            LetterColor = LetterColor.Trim().ToUpperInvariant(),
            PocColor = PocColor.Trim().ToUpperInvariant(),
            ValueAreaColor = ValueAreaColor.Trim().ToUpperInvariant(),
            ValueAreaOpacity = vaOpacity,
            IbColor = IbColor.Trim().ToUpperInvariant(),
        };
        return null;
    }
}

/// <summary>Edycja jednej średniej kroczącej (SMA/EMA) wykresu „Szeroki rynek”.</summary>
public sealed class BroadMovingAverageEditorViewModel(BroadMovingAverageStyle style) : ObservableObject
{
    public bool Enabled { get; set; } = style.Enabled;
    public MovingAverageKind Kind { get; set; } = style.Kind;
    public string Period { get; set; } = style.Period.ToString(CultureInfo.InvariantCulture);
    public string Color { get; set; } = style.Color;
    public string Thickness { get; set; } = style.Thickness.ToString(CultureInfo.InvariantCulture);
    public LineStyle LineStyle { get; set; } = style.LineStyle;

    /// <summary>Średnia z formularza; zwraca opis błędu albo null.</summary>
    public string? TryCreate(out BroadMovingAverageStyle? result)
    {
        result = null;
        if (!SettingsViewModel.TryParseInt(Period, 1, 500, out var period))
        {
            return "okres podaj jako liczbę świec od 1 do 500.";
        }

        if (!ColorUtil.TryParse(Color, out _))
        {
            return "kolor podaj w formacie #RRGGBB.";
        }

        if (!SettingsViewModel.TryParseInt(Thickness, 1, 10, out var thickness))
        {
            return "grubość podaj w pikselach od 1 do 10.";
        }

        result = new BroadMovingAverageStyle
        {
            Enabled = Enabled,
            Kind = Kind,
            Period = period,
            Color = Color.Trim().ToUpperInvariant(),
            Thickness = thickness,
            LineStyle = LineStyle,
        };
        return null;
    }
}

/// <summary>Edycja jednej średniej kroczącej (SMA) wykresu D/W/M.</summary>
public sealed class MovingAverageEditorViewModel : ObservableObject
{
    public MovingAverageEditorViewModel(MovingAverageStyle style)
    {
        Enabled = style.Enabled;
        _period = style.Period.ToString(CultureInfo.InvariantCulture);
        Color = style.Color;
        Thickness = style.Thickness.ToString(CultureInfo.InvariantCulture);
        ShowOnTpo = style.ShowOnTpo;
        TpoLabel = style.TpoLabel;
        TpoLineStyle = style.TpoLineStyle;
        TpoThickness = style.TpoThickness.ToString(CultureInfo.InvariantCulture);
        _tpoShape = style.TpoShape;
        TpoBandHeight = style.TpoBandHeight.ToString(CultureInfo.InvariantCulture);
        TpoBandOpacity = style.TpoBandOpacity.ToString(CultureInfo.InvariantCulture);
        TpoColor = style.EffectiveTpoColor;
        TpoBlink = style.TpoBlink;
        _tpoColorFollowed = string.IsNullOrWhiteSpace(style.TpoColor) ? style.Color : null;
    }

    private TpoAverageShape _tpoShape;
    private readonly string? _tpoColorFollowed; // kolor z wykresu, gdy kolor TPO nie był ustawiony osobno

    /// <summary>Linia albo pasek na wykresie TPO.</summary>
    public TpoAverageShape TpoShape
    {
        get => _tpoShape;
        set
        {
            if (SetProperty(ref _tpoShape, value))
            {
                OnPropertyChanged(nameof(IsTpoBand));
                OnPropertyChanged(nameof(IsTpoLine));
            }
        }
    }

    public bool IsTpoBand => _tpoShape == TpoAverageShape.Band;
    public bool IsTpoLine => _tpoShape == TpoAverageShape.Line;
    public string TpoBandHeight { get; set; }
    public string TpoBandOpacity { get; set; }

    /// <summary>Kolor na wykresie TPO (domyślnie jak na wykresie dziennym).</summary>
    public string TpoColor { get; set; }

    /// <summary>Czy miga, gdy cena zbliży się do średniej.</summary>
    public bool TpoBlink { get; set; }

    private string _period;

    public bool Enabled { get; set; }

    public string Period
    {
        get => _period;
        set
        {
            if (SetProperty(ref _period, value))
            {
                OnPropertyChanged(nameof(DefaultTpoLabel));
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public string Color { get; set; }
    public string Thickness { get; set; }

    // --- Linia na wykresie TPO (wartość średniej dziennej „na dziś”) ---
    public bool ShowOnTpo { get; set; }

    /// <summary>Własny opis linii na wykresie TPO (pusty = automatyczny).</summary>
    public string TpoLabel { get; set; }

    public LineStyle TpoLineStyle { get; set; }
    public string TpoThickness { get; set; }

    /// <summary>Opis automatyczny – podpowiedź przy pustym polu opisu.</summary>
    public string DefaultTpoLabel => int.TryParse(Period, out var p) ? MovingAverageStyle.DefaultTpoLabel(p) : string.Empty;

    /// <summary>Nazwa średniej w liście „na wykresie TPO” (np. „SMA 20”).</summary>
    public string Title => $"SMA {Period}";

    /// <summary>Średnia z formularza; zwraca opis błędu albo null.</summary>
    public string? TryCreate(out MovingAverageStyle? style)
    {
        style = null;
        if (!SettingsViewModel.TryParseInt(Period, 1, 1000, out var period))
        {
            return "okres podaj jako liczbę świec od 1 do 1000.";
        }

        if (!ColorUtil.TryParse(Color, out _))
        {
            return "kolor podaj w formacie #RRGGBB.";
        }

        if (!SettingsViewModel.TryParseInt(Thickness, 1, 10, out var thickness))
        {
            return "grubość podaj w pikselach od 1 do 10.";
        }

        if (!SettingsViewModel.TryParseInt(TpoThickness, 1, 10, out var tpoThickness))
        {
            return "grubość linii na wykresie TPO podaj w pikselach od 1 do 10.";
        }

        if (!SettingsViewModel.TryParseInt(TpoBandHeight, 2, 60, out var bandHeight))
        {
            return "szerokość paska na wykresie TPO podaj w pikselach od 2 do 60.";
        }

        if (!SettingsViewModel.TryParseInt(TpoBandOpacity, 5, 100, out var bandOpacity))
        {
            return "krycie paska na wykresie TPO podaj w procentach od 5 do 100.";
        }

        if (!ColorUtil.TryParse(TpoColor, out _))
        {
            return "kolor na wykresie TPO podaj w formacie #RRGGBB.";
        }

        // Kolor TPO taki jak na wykresie (albo niezmieniony, gdy podążał za wykresem) – zapisywany jako pusty:
        // dalej podąża za kolorem średniej z wykresu dziennego.
        var tpoColor = TpoColor.Trim().ToUpperInvariant();
        var follows = string.Equals(tpoColor, Color.Trim(), StringComparison.OrdinalIgnoreCase) ||
                      (_tpoColorFollowed is { } followed && string.Equals(tpoColor, followed, StringComparison.OrdinalIgnoreCase));

        style = new MovingAverageStyle
        {
            Enabled = Enabled,
            Period = period,
            Color = Color.Trim().ToUpperInvariant(),
            Thickness = thickness,
            ShowOnTpo = ShowOnTpo,
            TpoLabel = (TpoLabel ?? string.Empty).Trim(),
            TpoLineStyle = TpoLineStyle,
            TpoThickness = tpoThickness,
            TpoShape = TpoShape,
            TpoBandHeight = bandHeight,
            TpoBandOpacity = bandOpacity,
            TpoColor = follows ? string.Empty : tpoColor,
            TpoBlink = TpoBlink,
        };
        return null;
    }
}

/// <summary>Pliki kluczy IBKR Web API wybierane w ustawieniach.</summary>
public enum WebApiKeyFile
{
    Signature,
    Encryption,
    DhParam,
}
