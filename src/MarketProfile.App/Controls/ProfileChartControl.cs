using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MarketProfile.App.Settings;
using MarketProfile.App.ViewModels;
using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>
/// Kontrolka rysująca profile rynkowe (litery TPO) i linie cenowe – czysta warstwa prezentacji.
/// Dostaje gotowe migawki profili (<see cref="ProfileSnapshot"/>) oraz listę linii (<see cref="PriceLine"/>)
/// i tylko je rysuje. Akcje użytkownika (dodanie linii, rozbicie profilu...) NIE zmieniają danych tutaj –
/// kontrolka zgłasza je zdarzeniami, a obsługuje je model widoku.
///
/// Obsługa myszy:
///  - przeciąganie lewym przyciskiem – przesuwanie wykresu,
///  - przeciąganie odblokowanej linii cenowej – zmiana jej ceny (zablokowanej nie da się przesunąć),
///  - kółko – zmiana wysokości wierszy (zoom w pionie),
///  - Ctrl + kółko – zmiana szerokości liter (zoom w poziomie),
///  - podwójne kliknięcie na linii – edycja linii,
///  - podwójne kliknięcie w innym miejscu – powrót do śledzenia bieżącej ceny,
///  - prawy przycisk – menu: dodaj/edytuj/zablokuj/usuń linię, rozbij profil, połącz części,
///    rozwiń/zwiń profil (tryb Split).
/// </summary>
public sealed partial class ProfileChartControl : FrameworkElement
{
    /// <summary>Lista profili do narysowania (w kolejności chronologicznej) wraz ze sposobem wyświetlania.</summary>
    public static readonly DependencyProperty ProfilesProperty = DependencyProperty.Register(
        nameof(Profiles),
        typeof(IReadOnlyList<ProfileView>),
        typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Lista poziomych linii cenowych.</summary>
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines),
        typeof(IReadOnlyList<PriceLine>),
        typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ProfileView>? Profiles
    {
        get => (IReadOnlyList<ProfileView>?)GetValue(ProfilesProperty);
        set => SetValue(ProfilesProperty, value);
    }

    public IReadOnlyList<PriceLine>? Lines
    {
        get => (IReadOnlyList<PriceLine>?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    /// <summary>Tryb symulacji – menu podręczne profilu ma pozycje „Symulacja od początku profilu / od słupka”.</summary>
    public static readonly DependencyProperty SimulationMenuProperty = DependencyProperty.Register(
        nameof(SimulationMenu), typeof(bool), typeof(ProfileChartControl), new PropertyMetadata(false));

    public bool SimulationMenu
    {
        get => (bool)GetValue(SimulationMenuProperty);
        set => SetValue(SimulationMenuProperty, value);
    }

    /// <summary>
    /// Prośba o cofnięcie symulacji do początku profilu albo klikniętego słupka (czas lokalny giełdy) –
    /// bez zmiany widoku wykresu.
    /// </summary>
    public event Action<DateTime>? SimulateBackToRequested;

    /// <summary>
    /// Tempo budowania TPO (× normy, ze znakiem; NaN = brak) – wskaźnik ze wskazówką i kolor drugiej linii zegara.
    /// </summary>
    public static readonly DependencyProperty TempoValueProperty = DependencyProperty.Register(
        nameof(TempoValue), typeof(double), typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public double TempoValue
    {
        get => (double)GetValue(TempoValueProperty);
        set => SetValue(TempoValueProperty, value);
    }

    /// <summary>
    /// Czy wskaźnik tempa jest aktywny. Nieaktywny (np. początek sesji – za wcześnie na pomiar) jest rysowany na szaro,
    /// z opisem stanu pod spodem.
    /// </summary>
    public static readonly DependencyProperty TempoActiveProperty = DependencyProperty.Register(
        nameof(TempoActive), typeof(bool), typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool TempoActive
    {
        get => (bool)GetValue(TempoActiveProperty);
        set => SetValue(TempoActiveProperty, value);
    }

    /// <summary>Opis stanu tempa pod wskaźnikiem (np. „Zwalnia ↑”, „Zatrzymanie przy 7674.75”).</summary>
    public static readonly DependencyProperty TempoStateTextProperty = DependencyProperty.Register(
        nameof(TempoStateText), typeof(string), typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public string TempoStateText
    {
        get => (string)GetValue(TempoStateTextProperty);
        set => SetValue(TempoStateTextProperty, value);
    }

    /// <summary>
    /// Tekst zegara bieżącego okresu TPO (np. "TPO F  10:00–10:30  ⏱ 12:34").
    /// Tekst przygotowuje model widoku; kontrolka tylko go rysuje w miejscu i stylu z ustawień.
    /// Pusty tekst = zegar niewidoczny (np. gdy dane nie są pobierane).
    /// </summary>
    public static readonly DependencyProperty PeriodTimerTextProperty = DependencyProperty.Register(
        nameof(PeriodTimerText),
        typeof(string),
        typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public string PeriodTimerText
    {
        get => (string)GetValue(PeriodTimerTextProperty);
        set => SetValue(PeriodTimerTextProperty, value);
    }

    /// <summary>
    /// Ile ostatnich profili ma się mieścić na ekranie (0 = bez dopasowania). Zmiana wartości
    /// wyłącza ręczną szerokość liter i przesunięcie w poziomie – wykres pokazuje znów najnowsze profile.
    /// </summary>
    public static readonly DependencyProperty ProfilesToFitProperty = DependencyProperty.Register(
        nameof(ProfilesToFit),
        typeof(int),
        typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) =>
        {
            var chart = (ProfileChartControl)d;
            chart._manualLetterWidth = false;
            chart._manualRowHeight = false;
            chart._manualCenter = null;
            chart._panX = 0;
            chart.ClearPin();
        }));

    public int ProfilesToFit
    {
        get => (int)GetValue(ProfilesToFitProperty);
        set => SetValue(ProfilesToFitProperty, value);
    }

    /// <summary>
    /// Wygląd wykresu z ustawień: kolory sesji dziennej/nocnej i parametry profilu wolumenowego.
    /// Zmiana stylu czyści pamięć podręczną liter (inne kolory) i odświeża wykres.
    /// </summary>
    public static readonly DependencyProperty AppearanceProperty = DependencyProperty.Register(
        nameof(Appearance),
        typeof(ChartStyle),
        typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(ChartStyle.Default, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) =>
            {
                var chart = (ProfileChartControl)d;
                chart._letterCache.Clear();
                chart._volumeLabelCache.Clear();
            }));

    public ChartStyle Appearance
    {
        get => (ChartStyle)GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    /// <summary>
    /// Poziomy średnich kroczących z wykresu dziennego (wartości „na dziś” dla bieżącego profilu) – linie poziome
    /// przez cały wykres z opisem (np. „SMA20 D”). Tylko do odczytu – nie da się ich przeciągać ani edytować.
    /// </summary>
    public static readonly DependencyProperty AveragesProperty = DependencyProperty.Register(
        nameof(Averages), typeof(TpoAverageOverlay), typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(TpoAverageOverlay.Empty, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => ((ProfileChartControl)d).UpdateBlinkTimer((TpoAverageOverlay?)e.NewValue)));

    // Miganie linii średnich, do których zbliżyła się cena: zegar działa tylko, gdy któraś linia miga.
    private System.Windows.Threading.DispatcherTimer? _blinkTimer;
    private bool _blinkVisible = true;

    private void UpdateBlinkTimer(TpoAverageOverlay? overlay)
    {
        if (overlay is not { AnyBlink: true })
        {
            _blinkTimer?.Stop();
            _blinkVisible = true;
            return;
        }

        if (_blinkTimer is null)
        {
            _blinkTimer = new System.Windows.Threading.DispatcherTimer();
            _blinkTimer.Tick += (_, _) =>
            {
                _blinkVisible = !_blinkVisible;
                InvalidateVisual();
            };
        }

        _blinkTimer.Interval = TimeSpan.FromMilliseconds(overlay.BlinkIntervalMs);
        if (!_blinkTimer.IsEnabled)
        {
            _blinkTimer.Start();
        }
    }

    public TpoAverageOverlay Averages
    {
        get => (TpoAverageOverlay)GetValue(AveragesProperty);
        set => SetValue(AveragesProperty, value);
    }

    // --- Zdarzenia zgłaszane do okna / modelu widoku ---

    /// <summary>Użytkownik chce dodać linię na podanej cenie.</summary>
    public event Action<decimal>? AddLineRequested;

    /// <summary>Użytkownik chce edytować linię.</summary>
    public event Action<PriceLine>? EditLineRequested;

    /// <summary>Użytkownik chce usunąć linię.</summary>
    public event Action<PriceLine>? DeleteLineRequested;

    /// <summary>Użytkownik przeciągnął linię na nową cenę.</summary>
    public event Action<PriceLine, decimal>? LineMoved;

    /// <summary>Użytkownik chce rozbić profil – podany okres ma rozpocząć nową część.</summary>
    public event Action<ProfileSnapshot, int>? SplitRequested;

    /// <summary>Użytkownik chce połączyć część profilu z poprzednią częścią.</summary>
    public event Action<ProfileSnapshot>? MergeRequested;

    /// <summary>Użytkownik chce przełączyć tryb Split/Expanded profilu.</summary>
    public event Action<ProfileSnapshot>? ToggleExpandRequested;

    /// <summary>Użytkownik chce zablokować/odblokować linię.</summary>
    public event Action<PriceLine>? ToggleLockRequested;

    /// <summary>Użytkownik chce zablokować (true) lub odblokować (false) wszystkie linie.</summary>
    public event Action<bool>? LockAllRequested;

    /// <summary>Widać lewą krawędź najstarszego wczytanego profilu – trzeba doczytać starszą historię.</summary>
    public event Action? OlderHistoryRequested;

    // --- Stałe układu (w pikselach) ---
    private const double AxisWidth = 75;       // szerokość osi cen po prawej
    private const double HeaderHeight = 36;    // pasek z nazwą sesji i datą u góry
    private const double ColumnGap = 26;       // odstęp między profilami
    private const double IbBarWidth = 4;       // szerokość paska Initial Balance
    private const double IbGap = 5;            // odstęp paska IB od liter
    private const double MinColumnWidth = 110; // minimalna szerokość kolumny profilu
    private const double LineHitTolerance = 4; // jak blisko linii trzeba kliknąć, żeby ją "złapać"

    // --- Stałe kolory interfejsu (kolory profili pochodzą z ustawień – patrz Appearance) ---
    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)));
    private static readonly Brush HeaderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x29)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)));
    private static readonly Brush DimTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x88, 0x94)));
    private static readonly Brush TimerBackgroundBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0x1E, 0x22, 0x29)));
    private static readonly Brush TagBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x30)));
    private static readonly Brush LastPriceBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0xC0, 0x70)));
    private static readonly Pen SeparatorPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3A)), 1));
    private static readonly Pen SplitSeparatorPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3A)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Pen LastPricePen = Frozen(new Pen(LastPriceBrush, 1) { DashStyle = DashStyles.Dash });

    private static readonly Typeface LetterTypeface = new("Consolas");
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Typeface SymbolTypeface = new("Segoe UI Emoji, Segoe UI"); // obsługuje ikonę kłódki

    // --- Stan widoku ---
    private const double DefaultRowPx = 14;
    private double _rowPx = DefaultRowPx; // wysokość wiersza w pikselach
    private bool _manualRowHeight;    // użytkownik sam ustawił wysokość wierszy (powiększenie) – bez dopasowania w pionie
    private decimal? _fitCenter;      // cena na środku przy dopasowaniu w pionie (środek zakresu dopasowanych profili)
    private double _letterPx = 10;    // szerokość litery w pikselach
    private bool _manualLetterWidth;  // użytkownik sam ustawił szerokość liter (Ctrl + kółko) – bez dopasowania
    private double _minColumnWidth = MinColumnWidth; // minimalna szerokość kolumny (mniejsza przy wielu profilach)

    /// <summary>
    /// Współczynnik "ściśnięcia" stałych elementów kolumny (odstępy, strzałki, profil wolumenowy):
    /// 1 = pełna wielkość; mniej, gdy trzeba zmieścić dużo profili na ekranie.
    /// </summary>
    private double _compact = 1;

    // Wielkości elementów kolumny po uwzględnieniu ściśnięcia.
    private double Gap => Math.Max(6, ColumnGap * _compact);
    private double OpenSpace => Appearance.OpenArrowSpace * _compact;
    private double CloseSpace => Appearance.CloseArrowSpace * _compact;
    private double ArrowLen => Appearance.ArrowLength * _compact;
    private double VolGap => Appearance.VolumeGap * _compact;
    private double VolWidth => Appearance.VolumeWidth * _compact;
    private double PvGapFromTpo => Appearance.PeriodVolumeGapFromTpo * _compact;
    private double PvGapToSession => Appearance.PeriodVolumeGapToSession * _compact;
    private double PvWidth => Appearance.PeriodVolumeWidth * _compact;

    /// <summary>
    /// Szerokość obszaru wolumenu na prawo od strzałki zamknięcia:
    /// [odstęp] [wolumen bieżącego słupka] [odstęp] [wolumen sesji].
    /// </summary>
    private double VolumeAreaWidth(ProfileView view)
    {
        var period = view.ShowPeriodVolume ? PvGapFromTpo + PvWidth : 0;
        var session = view.ShowVolume ? (view.ShowPeriodVolume ? PvGapToSession : VolGap) + VolWidth : 0;
        return period + session;
    }
    private double _panX;             // przesunięcie w poziomie (0 = najnowszy profil przy prawej krawędzi)
    private double? _pinFraction;     // przypięcie profilu: jego lewa krawędź w tym ułamku szerokości wykresu (null = brak)
    private string? _pinKey;          // klucz przypiętego profilu (null przy _pinFraction = przypnij najnowszy przy rysowaniu)
    private bool _pinAnywhere;        // przypięcie wybranego profilu (z tabeli parametrów) – także daleko w historii
    private (string Session, DateTime Start)? _highlight; // profil podświetlony (wybrany w tabeli parametrów)
    private decimal? _manualCenter;   // cena na środku ekranu ustawiona ręcznie (null = śledzenie ceny)
    private decimal? _autoCenter;     // cena na środku ekranu w trybie śledzenia
    private decimal _lastCenter;      // środek użyty w ostatnim rysowaniu
    private decimal _lastRowHeight = 1;
    private decimal _lastTickSize = 0.25m;
    private bool _hasData;

    /// <summary>Położenie narysowanej kolumny profilu – potrzebne, żeby wiedzieć, w co kliknął użytkownik.</summary>
    private sealed record ColumnLayout(ProfileSnapshot Profile, bool IsExpanded, double X, double Width, double LettersX);

    private readonly List<ColumnLayout> _columns = [];

    // --- Przeciąganie myszą ---
    private Point? _dragStart;
    private double _dragPanX;
    private decimal _dragCenter;
    private PriceLine? _draggedLine;   // linia przeciągana myszą (null = przesuwamy wykres)
    private decimal _draggedLinePrice; // bieżąca cena przeciąganej linii

    // Pamięć podręczna sformatowanych liter – tworzenie FormattedText jest kosztowne.
    private readonly Dictionary<(char, Brush), FormattedText> _letterCache = new();
    private readonly Dictionary<long, FormattedText> _volumeLabelCache = new(); // liczby wolumenu (wg wartości)

    public ProfileChartControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    // =====================================================================
    // Rysowanie
    // =====================================================================

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        _columns.Clear();

        var all = Profiles?.Where(p => !p.Snapshot.IsEmpty).ToList();
        _hasData = all is { Count: > 0 };
        if (!_hasData)
        {
            DrawText(dc, "Brak danych", 14, DimTextBrush, new Point(20, 20)); // przyczyna – w polu informacji
            UpdateCrosshair();
            return;
        }

        // Ostatnią cenę bierzemy z najnowszego profilu, nawet jeśli jego sesja jest ukryta.
        var latest = all![^1].Snapshot;
        _lastRowHeight = latest.RowHeight;
        _lastTickSize = latest.TickSize;

        var profiles = all.Where(p => p.IsVisible).ToList();
        var chartRight = ActualWidth - AxisWidth;

        // Dopasowanie szerokości liter i wysokości wierszy, żeby ostatnie N profili zmieściło się na ekranie
        // (gdy użytkownik nie ustawił powiększenia ręcznie). Środek ceny – po dopasowaniu (zależy od niego).
        FitProfilesToScreen(profiles, chartRight - 10);
        FitRowsToScreen(profiles);
        _lastCenter = GetCenterPrice(latest);
        UpdateZoomDerived();

        // 1. Układ: pozycje wszystkich kolumn – od najnowszej (przy prawej krawędzi) do najstarszych (w lewo).
        var layout = ComputeLayout(profiles, chartRight);

        // Po zmianie powiększenia: przesuwamy widok tak, żeby punkt spod kursora został pod kursorem.
        if (_pendingAnchor is { } anchor)
        {
            _pendingAnchor = null;
            var hit = layout.FirstOrDefault(c => ProfileKey(c.View.Snapshot) == anchor.Key);
            if (hit is not null)
            {
                _panX = ChartZoom.PanForAnchor(_panX, anchor.CursorX, hit.X, hit.Width, anchor.Fraction);
                layout = ComputeLayout(profiles, chartRight);
            }
        }

        // Przypięty profil (np. po cofnięciu czasu symulacji) – jego lewa krawędź w ustalonym miejscu ekranu.
        if (_pinFraction is { } fraction && layout.Count > 0)
        {
            _pinKey ??= ProfileKey(layout[^1].View.Snapshot);
            var pinned = layout.FirstOrDefault(c => ProfileKey(c.View.Snapshot) == _pinKey);
            if (pinned is not null)
            {
                // Najwyżej 0: gdy nowe profile dojdą do prawej krawędzi, wykres przesuwa się jak zwykle.
                var pan = _panX + (chartRight * fraction - pinned.X);
                if (!_pinAnywhere)
                {
                    pan = Math.Min(0, pan);
                }

                if (Math.Abs(pan - _panX) > 0.01)
                {
                    _panX = pan;
                    layout = ComputeLayout(profiles, chartRight);
                }
            }
        }

        // Widać lewą krawędź najstarszego wczytanego profilu → prosimy o starszą historię.
        RequestOlderHistoryIfNeeded(layout, all.Count);

        // 2. Rysowanie widocznych kolumn.
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(0, chartRight), ActualHeight)));

        var latestDayStart = LatestDayStart();
        foreach (var column in layout)
        {
            if (column.X > chartRight || column.X + column.Width < 0)
            {
                continue; // poza ekranem
            }

            var profile = column.View.Snapshot;

            // Profil wybrany w tabeli parametrów – podświetlone tło (pod literami; wszystkie części rozbitego profilu).
            if (_highlight is { } h && profile.Session.Name == h.Session && profile.SessionStart == h.Start)
            {
                dc.DrawRectangle(Appearance.SelectedProfileBrush, null, new Rect(column.X, 0, column.Width, ActualHeight));
            }

            _columns.Add(new ColumnLayout(profile, column.View.IsExpanded, column.X, column.Width, column.X + OpenSpace + IbBarWidth + IbGap));
            DrawProfile(dc, column.View, column.X, column.Width, LetterColumns(column.View) * _letterPx,
                isLatest: ReferenceEquals(profile, latest));
            DrawProfileNotes(dc, profile, column.X, column.Width, latestDayStart);
        }

        DrawAverageLines(dc, chartRight);
        DrawPriceLines(dc, chartRight);
        dc.Pop();

        DrawPriceAxis(dc, chartRight, latest);
        DrawTempoGauge(dc, chartRight, DrawPeriodTimer(dc, chartRight));
        UpdateCrosshair(); // nowe dane / przesunięcie – etykiety kursora (cena, słupek) na nowo
    }

    /// <summary>Pozycja kolumny profilu w układzie (także poza ekranem).</summary>
    private sealed record LayoutColumn(ProfileView View, double X, double Width);

    /// <summary>Punkt pod kursorem, który ma zostać w miejscu po zmianie powiększenia.</summary>
    private sealed record ZoomAnchor(string Key, double Fraction, double CursorX);

    private ZoomAnchor? _pendingAnchor;
    private List<LayoutColumn> _lastLayout = [];
    private int _olderRequestedAtCount = -1;

    /// <summary>Jednoznaczny klucz profilu (części profilu) – do odnalezienia go po zmianie powiększenia.</summary>
    private static string ProfileKey(ProfileSnapshot profile) => $"{profile.Session.Name}|{profile.PartStart:O}";

    /// <summary>
    /// Pozycje wszystkich kolumn (od najstarszej do najnowszej), liczone od prawej krawędzi wykresu
    /// z uwzględnieniem przesunięcia <see cref="_panX"/>.
    /// </summary>
    private List<LayoutColumn> ComputeLayout(IReadOnlyList<ProfileView> profiles, double chartRight)
    {
        var result = new LayoutColumn[profiles.Count];
        var right = chartRight - 10 + _panX;
        for (var i = profiles.Count - 1; i >= 0; i--)
        {
            var width = ColumnWidth(profiles[i], _letterPx);
            right -= width;
            result[i] = new LayoutColumn(profiles[i], right, width);
        }

        _lastLayout = [.. result];
        return _lastLayout;
    }

    /// <summary>
    /// Gdy lewa krawędź najstarszego wczytanego profilu jest widoczna na ekranie, zgłaszamy prośbę
    /// o starszą historię (raz na każdą liczbę profili – po doczytaniu przybywa profili i można prosić znowu).
    /// </summary>
    private void RequestOlderHistoryIfNeeded(List<LayoutColumn> layout, int profileCount)
    {
        if (layout.Count == 0 || layout[0].X < 0 || _olderRequestedAtCount == profileCount)
        {
            return;
        }

        _olderRequestedAtCount = profileCount;
        Dispatcher.BeginInvoke(() => OlderHistoryRequested?.Invoke());
    }

    /// <summary>Wielkości zależne od powiększenia: ściśnięcie stałych elementów i minimalna szerokość kolumny.</summary>
    private void UpdateZoomDerived()
    {
        _compact = ChartZoom.Compact(_letterPx);
        _minColumnWidth = MinColumnWidth * _compact;
    }

    /// <summary>
    /// Szerokość obszaru liter w kolumnach: w trybie Split jedna kolumna na każdy okres TPO,
    /// w trybie zwykłym – tyle, ile liter ma najdłuższy wiersz.
    /// </summary>
    private static int LetterColumns(ProfileView view) => view.IsExpanded
        ? view.Snapshot.Periods.Max(p => p.Index) - view.Snapshot.FirstPeriod + 1
        : view.Snapshot.Levels.Max(l => l.TpoCount);

    /// <summary>
    /// Szerokość całej kolumny profilu przy danej szerokości litery:
    /// [strzałka otwarcia] [pasek IB] [litery] [strzałka zamknięcia] [wolumen] [odstęp].
    /// Kolumna nie bywa węższa niż minimum (żeby zmieścił się nagłówek).
    /// </summary>
    private double ColumnWidth(ProfileView view, double letterPx)
    {
        var volumeWidth = VolumeAreaWidth(view);
        var arrows = OpenSpace + CloseSpace;
        return Math.Max(_minColumnWidth, IbBarWidth + IbGap + LetterColumns(view) * letterPx + arrows + volumeWidth + Gap);
    }

    /// <summary>
    /// Dopasowuje powiększenie tak, żeby ostatnie <see cref="ProfilesToFit"/> profili zmieściło się w szerokości wykresu:
    /// szukamy największej szerokości litery (razem z nią skalują się odstępy, strzałki i profil wolumenowy).
    /// Nie działa, gdy użytkownik sam ustawił powiększenie (kółko myszy) – do czasu „Dopasuj”,
    /// podwójnego kliknięcia albo zmiany liczby profili.
    /// </summary>
    private void FitProfilesToScreen(IReadOnlyList<ProfileView> profiles, double available)
    {
        var count = ProfilesToFit;
        if (count <= 0 || _manualLetterWidth || profiles.Count == 0 || available <= 0)
        {
            return;
        }

        var lastProfiles = profiles.Skip(Math.Max(0, profiles.Count - count)).ToList();

        bool Fits(double letterPx)
        {
            _compact = ChartZoom.Compact(letterPx);
            _minColumnWidth = MinColumnWidth * _compact;
            return lastProfiles.Sum(p => ColumnWidth(p, letterPx)) <= available;
        }

        var fitted = Fits(ChartZoom.MinLetterPx)
            ? LargestFitting(ChartZoom.MinLetterPx, ChartZoom.MaxLetterPx, Fits)
            : ChartZoom.MinLetterPx;

        fitted = Math.Floor(fitted * 100) / 100;
        if (Math.Abs(fitted - _letterPx) > 0.005)
        {
            _letterPx = fitted;
            _letterCache.Clear(); // zmienił się rozmiar czcionki liter
        }
    }

    /// <summary>
    /// Dopasowanie w pionie: wysokość wierszy tak, żeby cały zakres cen (High–Low) ostatnich
    /// <see cref="ProfilesToFit"/> profili mieścił się na ekranie, a środek ekranu = środek tego zakresu.
    /// Nie większa niż domyślna (14 px) – wąski zakres nie daje olbrzymich liter. Nie działa, gdy użytkownik
    /// sam powiększył wykres w pionie albo przesunął go w górę/dół – do czasu „Dopasuj” lub zmiany liczby profili.
    /// </summary>
    private void FitRowsToScreen(IReadOnlyList<ProfileView> profiles)
    {
        _fitCenter = null;
        var count = ProfilesToFit;
        if (_manualRowHeight || _manualCenter is not null || count <= 0)
        {
            return;
        }

        var fitted = profiles.Skip(Math.Max(0, profiles.Count - count)).Select(p => p.Snapshot).Where(s => !s.IsEmpty).ToList();
        var available = (ActualHeight - HeaderHeight) * 0.92; // trochę miejsca nad i pod profilami
        if (fitted.Count == 0 || available <= 0)
        {
            return;
        }

        var high = fitted.Max(s => s.High);
        var low = fitted.Min(s => s.Low);
        var rowHeight = fitted[^1].RowHeight;
        var rows = (double)((high - low) / rowHeight) + 2;
        var rowPx = Math.Clamp(Math.Floor(available / rows * 100) / 100, ChartZoom.MinRowPx, DefaultRowPx);
        if (Math.Abs(rowPx - _rowPx) > 0.005)
        {
            _rowPx = rowPx;
            _letterCache.Clear();
        }

        _fitCenter = ToRow((high + low) / 2, rowHeight);
    }

    /// <summary>
    /// Wyszukiwanie połówkowe: największa wartość z przedziału [low, high], dla której warunek jest spełniony
    /// (zakładamy, że dla <paramref name="low"/> jest spełniony, a im większa wartość, tym trudniej).
    /// </summary>
    private static double LargestFitting(double low, double high, Func<double, bool> fits)
    {
        if (fits(high))
        {
            return high;
        }

        for (var i = 0; i < 20 && high - low > 0.01; i++)
        {
            var middle = (low + high) / 2;
            if (fits(middle))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Zegar bieżącego okresu TPO – rysowany na wierzchu, pod paskiem nagłówków profili,
    /// w lewym górnym rogu, na środku u góry albo w prawym górnym rogu wykresu (wg ustawień).
    /// </summary>
    /// <returns>Prostokąt zegara (null = zegar niewidoczny) – pod nim rysowany jest wskaźnik tempa.</returns>
    private Rect? DrawPeriodTimer(DrawingContext dc, double chartRight)
    {
        var style = Appearance;
        if (!style.TimerEnabled || string.IsNullOrEmpty(PeriodTimerText))
        {
            return null;
        }

        var text = new FormattedText(PeriodTimerText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(SymbolTypeface.FontFamily, FontStyles.Normal, style.TimerFontWeight, FontStretches.Normal),
            style.TimerFontSize, style.TimerBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        const double margin = 10;
        const double padding = 6;
        var boxWidth = text.Width + 2 * padding;
        var boxHeight = text.Height + padding;

        var x = style.TimerPosition switch
        {
            TimerPosition.TopLeft => margin,
            TimerPosition.TopCenter => (chartRight - boxWidth) / 2,
            _ => chartRight - boxWidth - margin,
        };
        var y = HeaderHeight + margin;

        // Półprzezroczyste tło – zegar jest czytelny także nad literami profili.
        var box = new Rect(x, y, boxWidth, boxHeight);
        dc.DrawRoundedRectangle(TimerBackgroundBrush, null, box, 4, 4);
        dc.DrawText(text, new Point(x + padding, y + padding / 2));
        return box;
    }

    // --- Wskaźnik tempa budowania TPO (jak prędkościomierz): łuk w kolorach „temperatury” i wskazówka. ---

    private const double GaugeRadius = 58;
    private const double GaugeArcThickness = 9;
    private const int GaugeSegments = 72;
    private static readonly Pen[] GaugeArcPens = CreateGaugeArcPens();
    private static readonly Pen GaugeTickPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)), 1.5));
    private static readonly Pen GaugeNeedlePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)), 3)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Triangle,
    });
    private static readonly Brush GaugeHubBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)));
    private static readonly Brush GaugeLabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x90, 0x98, 0xA4)));

    // Wskaźnik nieaktywny (początek sesji – za wcześnie na pomiar): wszystko w szarościach.
    private static readonly Brush GaugeInactiveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x60, 0x6A)));
    private static readonly Pen GaugeInactiveArcPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3E, 0x44, 0x4D)), GaugeArcThickness));
    private static readonly Pen GaugeInactiveTickPen = Frozen(new Pen(GaugeInactiveBrush, 1.5));
    private static readonly Pen GaugeInactiveNeedlePen = Frozen(new Pen(GaugeInactiveBrush, 3)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Triangle,
    });

    private static Pen[] CreateGaugeArcPens()
    {
        var pens = new Pen[GaugeSegments];
        for (var i = 0; i < GaugeSegments; i++)
        {
            // Kolor środka odcinka skali.
            var value = TempoColors.ValueAt((i + 0.5) / GaugeSegments);
            var pen = new Pen(new SolidColorBrush(TempoColors.ColorFor(value)), GaugeArcThickness);
            pen.Freeze();
            pens[i] = pen;
        }

        return pens;
    }

    /// <summary>Punkt na łuku wskaźnika dla położenia na skali (0 = lewa strona, 1 = prawa).</summary>
    private static Point GaugePoint(Point center, double radius, double position)
    {
        var angle = Math.PI * (1 - Math.Clamp(position, 0, 1));
        return new Point(center.X + radius * Math.Cos(angle), center.Y - radius * Math.Sin(angle));
    }

    /// <summary>
    /// Wskaźnik tempa: półokrągła skala 0–3 × normy (niebieski → zielony → czerwony), wskazówka na wielkości tempa,
    /// pod nią wartość ze strzałką kierunku w kolorze tempa i opis stanu. Rysowany pod zegarem TPO (albo w jego miejscu).
    /// </summary>
    private void DrawTempoGauge(DrawingContext dc, double chartRight, Rect? timer)
    {
        var tempo = TempoValue;
        if (double.IsNaN(tempo) || !Appearance.TempoGaugeEnabled)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var regular = new Typeface(SymbolTypeface.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(SymbolTypeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var active = TempoActive;
        var arrow = tempo > 0 ? " ↑" : tempo < 0 ? " ↓" : string.Empty;
        var valueText = new FormattedText(
            active ? Math.Abs(tempo).ToString("0.0", CultureInfo.CurrentCulture) + "×" + arrow : "—",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold, 16,
            active ? TempoColors.BrushFor(tempo) : GaugeInactiveBrush, dpi);
        var stateText = new FormattedText(TempoStateText ?? string.Empty, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            regular, 11, GaugeLabelBrush, dpi);

        const double margin = 10;
        const double padding = 10;
        var width = Math.Max(2 * (GaugeRadius + GaugeArcThickness + 14) + 2 * padding, stateText.Width + 2 * padding);
        var height = padding + 12 + GaugeArcThickness / 2 + GaugeRadius + 6 + valueText.Height + stateText.Height + padding / 2;

        // Położenie wg ustawień. „Pod zegarem” – wyrównany do zegara, a bez zegara – w jego miejscu.
        // W rogach górnych omijamy zegar, jeśli stoi w tym samym miejscu.
        var top = HeaderHeight + margin;
        var bottom = ActualHeight - height - margin;
        var left = margin;
        var middle = (chartRight - width) / 2;
        var right = chartRight - width - margin;
        double TopBelowTimer(TimerPosition corner) =>
            timer is { } r && Appearance.TimerPosition == corner ? r.Bottom + 6 : top;

        var (x, y) = Appearance.TempoGaugePosition switch
        {
            TempoGaugePosition.TopLeft => (left, TopBelowTimer(TimerPosition.TopLeft)),
            TempoGaugePosition.TopCenter => (middle, TopBelowTimer(TimerPosition.TopCenter)),
            TempoGaugePosition.TopRight => (right, TopBelowTimer(TimerPosition.TopRight)),
            TempoGaugePosition.BottomLeft => (left, bottom),
            TempoGaugePosition.BottomRight => (right, bottom),
            _ when timer is { } t => (Appearance.TimerPosition switch
            {
                TimerPosition.TopLeft => t.Left,
                TimerPosition.TopCenter => t.Left + (t.Width - width) / 2,
                _ => t.Right - width,
            }, t.Bottom + 6),
            _ => (Appearance.TimerPosition switch
            {
                TimerPosition.TopLeft => left,
                TimerPosition.TopCenter => middle,
                _ => right,
            }, top),
        };

        dc.DrawRoundedRectangle(TimerBackgroundBrush, null, new Rect(x, y, width, height), 6, 6);

        var center = new Point(x + width / 2, y + padding + 12 + GaugeArcThickness / 2 + GaugeRadius); // 12 – miejsce na podpisy nad łukiem

        // Łuk skali – odcinki w kolorach gradientu (z lekką zakładką, żeby nie było szczelin).
        for (var i = 0; i < GaugeSegments; i++)
        {
            var from = (double)i / GaugeSegments;
            var to = Math.Min((double)(i + 1) / GaugeSegments + 0.002, 1);
            dc.DrawLine(active ? GaugeArcPens[i] : GaugeInactiveArcPen, GaugePoint(center, GaugeRadius, from), GaugePoint(center, GaugeRadius, to));
        }

        // Podziałka 0, 1, 2, 5, 10, 20, 30+ (skala logarytmiczna).
        foreach (var mark in TempoColors.ScaleMarks)
        {
            var position = TempoColors.Position(mark);
            dc.DrawLine(active ? GaugeTickPen : GaugeInactiveTickPen, GaugePoint(center, GaugeRadius - GaugeArcThickness / 2 - 1, position),
                GaugePoint(center, GaugeRadius - GaugeArcThickness / 2 - 6, position));
            var text = mark.ToString("0", CultureInfo.CurrentCulture) + (mark >= TempoColors.ScaleMax ? "+" : string.Empty);
            var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, regular, 9,
                active ? GaugeLabelBrush : GaugeInactiveBrush, dpi);
            var at = GaugePoint(center, GaugeRadius + GaugeArcThickness / 2 + 10, position); // podpisy na zewnątrz łuku – wskazówka ich nie zasłania
            dc.DrawText(label, new Point(at.X - label.Width / 2, at.Y - label.Height / 2));
        }

        // Wskazówka na wielkości tempa (kierunek pokazuje strzałka przy wartości).
        dc.DrawLine(active ? GaugeNeedlePen : GaugeInactiveNeedlePen, center, GaugePoint(center, GaugeRadius - 4, TempoColors.Position(tempo)));
        dc.DrawEllipse(active ? GaugeHubBrush : GaugeInactiveBrush, null, center, 4.5, 4.5);

        var textY = center.Y + 6;
        dc.DrawText(valueText, new Point(center.X - valueText.Width / 2, textY));
        dc.DrawText(stateText, new Point(center.X - stateText.Width / 2, textY + valueText.Height));
    }

    /// <summary>
    /// Profil wolumenowy bieżącego okresu TPO: słupki od <paramref name="left"/> w prawo.
    /// Skala = większa z wartości: maksimum bieżącego słupka albo typowy wolumen poprzednich słupków
    /// (<paramref name="reference"/>) – dzięki temu na początku okresu słupki są krótkie i rosną z wolumenem,
    /// zamiast od razu mieć pełną długość przy kilku kontraktach.
    /// </summary>
    private void DrawPeriodVolume(DrawingContext dc, ProfileSnapshot profile, double left, long reference)
    {
        var max = Math.Max(profile.MaxCurrentPeriodVolume, reference);
        if (max <= 0)
        {
            return;
        }

        var style = Appearance;
        foreach (var (row, volume) in profile.CurrentPeriodVolume)
        {
            var y = PriceToY(row);
            if (y < HeaderHeight - _rowPx || y > ActualHeight + _rowPx)
            {
                continue;
            }

            var length = Math.Max(1, PvWidth * volume / max);
            var rect = new Rect(left, y - _rowPx / 2 + 1, length, Math.Max(1, _rowPx - 2));
            dc.DrawRectangle(style.PeriodVolumeBrush, rect.Height >= 3 ? style.PeriodVolumeOutlinePen : null, rect);

            if (style.VolumeLabelsOnPeriod)
            {
                DrawVolumeLabel(dc, volume, rect, mirrored: false);
            }
        }
    }

    /// <summary>
    /// Liczba wolumenu przy słupku: na słupku (od strony jego początku), z prawej albo z lewej strony.
    /// Przy wierszach niższych niż tekst liczby nie są rysowane (zlewałyby się w nieczytelną plamę).
    /// </summary>
    /// <param name="mirrored">Słupek rośnie od prawej do lewej (jego początek jest z prawej strony).</param>
    private void DrawVolumeLabel(DrawingContext dc, long volume, Rect bar, bool mirrored)
    {
        var style = Appearance;
        if (_rowPx < style.VolumeLabelFontSize * 0.9)
        {
            return;
        }

        if (!_volumeLabelCache.TryGetValue(volume, out var text))
        {
            if (_volumeLabelCache.Count > 5000)
            {
                _volumeLabelCache.Clear(); // ochrona przed rozrastaniem się pamięci podręcznej
            }

            text = new FormattedText(FormatVolume(volume), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(UiTypeface.FontFamily, FontStyles.Normal, style.VolumeLabelFontWeight, FontStretches.Normal),
                style.VolumeLabelFontSize, style.VolumeLabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _volumeLabelCache[volume] = text;
        }

        var x = style.VolumeLabelPosition switch
        {
            VolumeLabelPosition.Right => bar.Right + 2,
            VolumeLabelPosition.Left => bar.Left - 2 - text.Width,
            _ => mirrored ? bar.Right - 2 - text.Width : bar.Left + 2,
        };
        var y = bar.Top + bar.Height / 2 - text.Height / 2;
        dc.DrawText(text, new Point(x, y));
    }

    /// <summary>Wolumen w krótkiej postaci: do 99 999 pełna liczba, potem tysiące (k) i miliony (M).</summary>
    internal static string FormatVolume(long volume) => volume switch
    {
        >= 10_000_000 => (volume / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 100_000 => (volume / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k",
        _ => volume.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Rysuje jeden profil w kolumnie zaczynającej się od <paramref name="x"/>.</summary>
    /// <param name="isLatest">Czy to najnowszy profil (jego ostatni okres może być jeszcze w trakcie budowy).</param>
    private void DrawProfile(DrawingContext dc, ProfileView view, double x, double width, double lettersWidth, bool isLatest)
    {
        var profile = view.Snapshot;
        var style = Appearance;
        var colors = style.ForSession(profile.Session.Name); // osobne kolory dla sesji dziennej i nocnej
        var volumeArea = VolumeAreaWidth(view);
        // Układ kolumny (od lewej): [strzałka otwarcia] [pasek IB] [litery TPO] [strzałka zamknięcia]
        //                           [wolumen bieżącego słupka] [wolumen sesji]
        var ibX = x + OpenSpace;
        var lettersX = ibX + IbBarWidth + IbGap;
        var lettersRight = Math.Max(lettersX + lettersWidth, x + width - Gap - volumeArea - CloseSpace);

        // 1. Obszar wartości (Value Area) – półprzezroczysty prostokąt pod literami.
        var vaTop = PriceToY(profile.ValueAreaHigh) - _rowPx / 2;
        var vaBottom = PriceToY(profile.ValueAreaLow) + _rowPx / 2;
        dc.DrawRectangle(colors.ValueAreaBrush, null, new Rect(lettersX - 2, vaTop, lettersRight - lettersX + 4, vaBottom - vaTop));

        // 2. Initial Balance – pasek po lewej stronie profilu (jeśli włączony w ustawieniach).
        if (style.ShowInitialBalance && profile.InitialBalanceHigh is { } ibHigh && profile.InitialBalanceLow is { } ibLow)
        {
            var ibTop = PriceToY(ToRow(ibHigh, profile.RowHeight)) - _rowPx / 2;
            var ibBottom = PriceToY(ToRow(ibLow, profile.RowHeight)) + _rowPx / 2;
            dc.DrawRectangle(colors.IbBrush, null, new Rect(ibX, ibTop, IbBarWidth, ibBottom - ibTop));
        }

        // HalfBack – linia na połowie wysokości profilu (środek między najwyższym a najniższym wierszem),
        // przez całą szerokość liter. Rysowana pod literami, żeby ich nie zasłaniała.
        if (style.HalfBackPen is { } halfBackPen)
        {
            var highRow = ToRow(profile.High, profile.RowHeight);
            var lowRow = ToRow(profile.Low, profile.RowHeight);
            var y = Math.Round(PriceToY((highRow + lowRow) / 2)) + (halfBackPen.Thickness % 2 == 1 ? 0.5 : 0);
            if (y >= HeaderHeight && y <= ActualHeight)
            {
                dc.DrawLine(halfBackPen, new Point(lettersX - 2, y), new Point(lettersRight + 2, y));
            }
        }

        // Tryb Split: kreska połowy każdego zakończonego słupka (okresu TPO).
        // Rysowana PRZED literami – litery leżą na wierzchu i nie są zasłaniane przez kreski.
        if (view.IsExpanded && style.SplitMidlinePen is { } midlinePen)
        {
            DrawSplitMidlines(dc, profile, lettersX, midlinePen, isLatest);
        }

        // 3. Litery TPO.
        //    Tryb zwykły: w każdym wierszu litery zsunięte do lewej, w kolejności okresów.
        //    Tryb Split/Expanded: każda litera w kolumnie swojego okresu (A w 1. kolumnie, B w 2. itd.),
        //    dzięki czemu widać, jak profil budował się w czasie.
        //    Po oddaleniu litery byłyby nieczytelne – wtedy rysujemy bloki (poziom szczegółów zależy od powiększenia):
        //      Letters – litery, Cells – kolorowy prostokąt zamiast każdej litery, Rows – jeden pasek na wiersz.
        var fontSize = GetFontSize();
        var detail = ChartZoom.Detail(_letterPx, _rowPx);
        var cellGapX = _letterPx > 3 ? 1 : 0;
        var cellGapY = _rowPx > 4 ? 1 : 0;
        var cellHeight = Math.Max(0.8, _rowPx - 2 * cellGapY);

        foreach (var level in profile.Levels)
        {
            var y = PriceToY(level.Price);
            if (y < HeaderHeight - _rowPx || y > ActualHeight + _rowPx)
            {
                continue; // wiersz poza ekranem
            }

            // Wiersz POC wyróżniamy tylko, gdy POC jest włączony w ustawieniach.
            var isPoc = style.ShowPoc && level.Price == profile.Poc;

            if (detail == ChartDetail.Rows && !view.IsExpanded)
            {
                // Duże oddalenie: jeden pasek o długości równej liczbie liter w wierszu.
                var rowBrush = isPoc ? colors.PocBrush : colors.LetterBrush(level.Periods.Count > 0 ? level.Periods[^1] : 0);
                dc.DrawRectangle(rowBrush, null,
                    new Rect(lettersX, y - cellHeight / 2, Math.Max(0.8, level.Periods.Count * _letterPx), cellHeight));
            }
            else
            {
                for (var j = 0; j < level.Periods.Count; j++)
                {
                    var period = level.Periods[j];
                    var brush = isPoc ? colors.PocBrush : colors.LetterBrush(period);
                    var column = view.IsExpanded ? period - profile.FirstPeriod : j;
                    var cellX = lettersX + column * _letterPx;

                    if (detail == ChartDetail.Letters)
                    {
                        var text = GetLetter(TpoLetters.ForPeriod(period), brush, fontSize);
                        dc.DrawText(text, new Point(cellX, y - text.Height / 2));
                    }
                    else
                    {
                        // Blok zamiast litery – kolor ten sam co litery.
                        dc.DrawRectangle(brush, null,
                            new Rect(cellX, y - cellHeight / 2, Math.Max(0.8, _letterPx - cellGapX), cellHeight));
                    }
                }
            }

            // Profil wolumenowy – poziomy słupek proporcjonalny do wolumenu w wierszu (VPOC wyróżniony kolorem).
            //   Zwykły:     słupki zaczynają się przy literach i rosną w prawo.
            //   Odwrócony:  słupki zaczynają się przy prawej krawędzi i rosną w lewo – w stronę liter TPO.
            //   Długość zależy od wybranej skali (liniowa/pierwiastkowa/logarytmiczna) i limitu –
            //   słupki powyżej limitu mają pełną długość i znacznik obcięcia na końcu.
            if (view.ShowVolume && level.Volume > 0 && profile.MaxRowVolume > 0)
            {
                var (barLength, clipped) = style.VolumeBarLength(level.Volume, profile.MaxRowVolume);
                var areaLeft = view.ShowPeriodVolume
                    ? lettersRight + CloseSpace + PvGapFromTpo + PvWidth + PvGapToSession
                    : lettersRight + CloseSpace + VolGap;
                barLength *= _compact; // przy ściśniętych kolumnach słupki są proporcjonalnie krótsze
                var barLeft = style.VolumeMirrored ? areaLeft + VolWidth - barLength : areaLeft;
                var barBrush = level.Price == profile.VolumePoc ? style.VolumePocBrush : style.VolumeBrush;
                var barRect = new Rect(barLeft, y - _rowPx / 2 + 1, barLength, Math.Max(1, _rowPx - 2));

                // Obrys (wyraźniejszy niż wypełnienie) – słupki są widoczne także przy niskim kryciu;
                // przy bardzo niskich wierszach rysujemy samo wypełnienie (obrys zlałby słupki w jedną plamę).
                var outline = level.Price != profile.VolumePoc && barRect.Height >= 3 ? style.VolumeOutlinePen : null;
                dc.DrawRectangle(barBrush, outline, barRect);

                if (style.VolumeLabelsOnSession)
                {
                    DrawVolumeLabel(dc, level.Volume, barRect, style.VolumeMirrored);
                }

                if (clipped)
                {
                    // Znacznik obcięcia – jasny pasek na zewnętrznym końcu słupka.
                    var markerX = style.VolumeMirrored ? barRect.Left : barRect.Right - 2;
                    dc.DrawRectangle(Brushes.White, null, new Rect(markerX, barRect.Top, 2, barRect.Height));
                }
            }

            // 4. POC – ramka wokół całego wiersza.
            if (isPoc)
            {
                dc.DrawRectangle(null, colors.PocPen, new Rect(lettersX - 2, y - _rowPx / 2, lettersRight - lettersX + 4, _rowPx));
            }
        }

        // Profil wolumenowy aktualnie budowanego słupka TPO – tuż za literami (w trybie Split ostatnia kolumna liter
        // to właśnie bieżący słupek), przed profilem wolumenowym sesji. Skala: własne maksimum bieżącego okresu.
        if (view.ShowPeriodVolume)
        {
            DrawPeriodVolume(dc, profile, lettersRight + CloseSpace + PvGapFromTpo, view.PeriodVolumeReference);
        }

        // Strzałki początku i końca rysowania profilu – obie skierowane w stronę profilu.
        //   Otwarcie (pierwsza cena):  po lewej stronie, grot w prawo – wskazuje profil.
        //   Zamknięcie (ostatnia cena): po prawej stronie liter, grot w lewo – wskazuje profil.
        //   W bieżącym profilu strzałka zamknięcia przesuwa się razem z ceną.
        //   Przy dużym oddaleniu strzałki byłyby nieczytelnymi kropkami – wtedy ich nie rysujemy.
        var arrowsVisible = _compact > 0.3;
        if (style.ShowOpenArrow && arrowsVisible)
        {
            var y = PriceToY(ToRow(profile.Open, profile.RowHeight));
            DrawArrow(dc, new Point(ibX - 3, y), pointsRight: true, style.OpenArrowBrush, style, ArrowLen);
        }

        if (style.ShowCloseArrow && arrowsVisible)
        {
            var y = PriceToY(ToRow(profile.LastPrice, profile.RowHeight));
            DrawArrow(dc, new Point(lettersRight + 3, y), pointsRight: false, style.CloseArrowBrush, style, ArrowLen);
        }

        // 5. Separator między profilami (kropkowany, gdy dzieli części tego samego rozbitego profilu).
        var sepX = Math.Round(x + width - Gap / 2) + 0.5;
        var nextIsPartOfSameSession = profile.PartNumber < profile.PartCount;
        dc.DrawLine(nextIsPartOfSameSession ? SplitSeparatorPen : SeparatorPen,
            new Point(sepX, HeaderHeight), new Point(sepX, ActualHeight));

        // 6. Nagłówek: nazwa sesji, data, POC; dla części rozbitego profilu – numer części i litera początkowa.
        //    Tekst jest przycinany do szerokości kolumny, żeby nie nachodził na sąsiedni profil.
        var headerRect = new Rect(x, 0, width - Gap / 2, HeaderHeight);
        dc.DrawRectangle(HeaderBrush, null, headerRect);
        dc.PushClip(new RectangleGeometry(headerRect));

        // Wąska kolumna (oddalenie): skrócony nagłówek, np. "D 13.11" / "N 13.11"; bardzo wąska – sam dzień.
        if (headerRect.Width < 70)
        {
            var shortTitle = headerRect.Width < 34
                ? $"{profile.TradingDate:dd}"
                : $"{profile.Session.Name[0]} {profile.TradingDate:dd.MM}";
            DrawText(dc, shortTitle, 10, TextBrush, new Point(x + 2, 2));
            dc.Pop();
            return;
        }

        DrawText(dc, $"{profile.Session.Name} {profile.TradingDate:dd.MM}", 12, TextBrush, new Point(x + 4, 2));

        // Wartość POC w nagłówku – tylko gdy POC jest włączony w ustawieniach.
        var tagsX = x + 4;
        if (style.ShowPoc)
        {
            var pocText = CreateText($"POC {FormatPrice(profile.Poc)}", 10, colors.PocBrush, UiTypeface);
            dc.DrawText(pocText, new Point(x + 4, 19));
            tagsX += pocText.Width + 6;
        }

        // Dodatkowe oznaczenia: część rozbitego profilu (np. "2/3 od D") i tryb Split.
        var tags = new List<string>();
        if (profile.PartCount > 1)
        {
            tags.Add($"{profile.PartNumber}/{profile.PartCount} od {TpoLetters.ForPeriod(profile.FirstPeriod)}");
        }

        if (view.IsExpanded)
        {
            tags.Add("Split");
        }

        if (tags.Count > 0)
        {
            DrawText(dc, string.Join(" · ", tags), 10, TagBrush, new Point(tagsX, 19));
        }
        dc.Pop();
    }

    /// <summary>
    /// Rysuje poziomą strzałkę: trzon (linia) i grot (wypełniony trójkąt).
    /// </summary>
    /// <param name="tip">Punkt, w który celuje grot.</param>
    /// <param name="pointsRight">True – grot skierowany w prawo; false – w lewo.</param>
    private static void DrawArrow(DrawingContext dc, Point tip, bool pointsRight, Brush brush, ChartStyle style, double length)
    {
        var direction = pointsRight ? 1 : -1;
        var head = Math.Min(style.ArrowHeadSize, length);
        var tailX = tip.X - direction * length;
        var headBaseX = tip.X - direction * head;

        // Trzon – od końca strzałki do podstawy grotu.
        var pen = new Pen(brush, style.ArrowThickness);
        pen.Freeze();
        dc.DrawLine(pen, new Point(tailX, tip.Y), new Point(headBaseX, tip.Y));

        // Grot – trójkąt o wysokości równej wielkości grotu.
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(tip, isFilled: true, isClosed: true);
            context.LineTo(new Point(headBaseX, tip.Y - head / 2), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(headBaseX, tip.Y + head / 2), isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>
    /// Tryb Split: dla każdego zakończonego okresu TPO rysuje poziomą kreskę na wysokości połowy
    /// jego zakresu (środek między najwyższym a najniższym wierszem okresu), o szerokości jednej kolumny TPO.
    ///
    /// Okres jest zakończony, gdy po nim jest już kolejny okres. Ostatni okres jest zakończony tylko
    /// w profilu, który nie jest najnowszy (sesja lub część profilu już się skończyła) –
    /// słupek, który dopiero rośnie, nie ma kreski.
    /// </summary>
    private void DrawSplitMidlines(DrawingContext dc, ProfileSnapshot profile, double lettersX, Pen pen, bool isLatest)
    {
        var lastPeriod = profile.Periods.Count > 0 ? profile.Periods[^1].Index : -1;

        foreach (var period in profile.Periods)
        {
            if (isLatest && period.Index == lastPeriod)
            {
                continue; // słupek w trakcie budowy
            }

            // Środek okresu liczony z wierszy, żeby kreska była wyrównana z literami na wykresie.
            var highRow = ToRow(period.High, profile.RowHeight);
            var lowRow = ToRow(period.Low, profile.RowHeight);
            var y = Math.Round(PriceToY((highRow + lowRow) / 2)) + (pen.Thickness % 2 == 1 ? 0.5 : 0);

            if (y < HeaderHeight || y > ActualHeight)
            {
                continue;
            }

            // Szerokość kolumny TPO pomniejszona o 2 px – kreski sąsiednich okresów na tej samej wysokości się nie zlewają.
            var columnX = lettersX + (period.Index - profile.FirstPeriod) * _letterPx;
            dc.DrawLine(pen, new Point(columnX, y), new Point(columnX + Math.Max(2, _letterPx - 2), y));
        }
    }

    /// <summary>
    /// Linie średnich kroczących (z wykresu dziennego) przez całą szerokość wykresu, pod liniami użytkownika.
    /// Opisy nad liniami przy lewej albo prawej krawędzi; opisy linii leżących blisko siebie są rozsuwane w pionie.
    /// </summary>
    private void DrawAverageLines(DrawingContext dc, double chartRight)
    {
        var overlay = Averages ?? TpoAverageOverlay.Empty;
        if (overlay.Lines.Count == 0)
        {
            return;
        }

        var labels = new List<(FormattedText Text, double LineY)>();
        foreach (var line in overlay.Lines)
        {
            // Miganie: w fazie wygaszonej linia / pasek znika (opis i cena na osi zostają),
            // w fazie widocznej jest wyróżniony – grubsza linia, pełniejszy pasek.
            var hidden = line.Blink && !_blinkVisible;

            if (line.BandBrush is { } bandBrush)
            {
                // Pasek: strefa wokół średniej, wyśrodkowana na jej cenie (wyrównana do pikseli).
                var center = PriceToY(line.Price);
                var top = Math.Round(center - line.BandHeight / 2);
                if (top + line.BandHeight < HeaderHeight || top > ActualHeight)
                {
                    continue;
                }

                if (!hidden)
                {
                    dc.DrawRectangle(line.Blink ? line.AxisBrush : bandBrush, null,
                        new Rect(0, top, Math.Max(0, chartRight), line.BandHeight));
                }

                labels.Add((CreateText(line.Label, overlay.FontSize, line.Brush, UiTypeface), top - 1));
                continue;
            }

            var y = Math.Round(PriceToY(line.Price)) + (line.Pen.Thickness % 2 == 1 ? 0.5 : 0);
            if (y < HeaderHeight || y > ActualHeight)
            {
                continue;
            }

            if (!hidden)
            {
                var pen = line.Blink ? Frozen(new Pen(line.AxisBrush, line.Pen.Thickness + 1) { DashStyle = line.Pen.DashStyle }) : line.Pen;
                dc.DrawLine(pen, new Point(0, y), new Point(chartRight, y));
            }

            labels.Add((CreateText(line.Label, overlay.FontSize, line.Brush, UiTypeface), y - line.Pen.Thickness / 2 - 1));
        }

        // Od góry: opis tuż nad linią, ale nie wyżej niż koniec poprzedniego opisu (bez nakładania).
        var previousBottom = double.NegativeInfinity;
        foreach (var (text, lineY) in labels.OrderBy(l => l.LineY))
        {
            var top = Math.Max(lineY - text.Height, previousBottom);
            var x = overlay.LabelPosition == LabelPosition.Left ? 8 : chartRight - text.Width - 8;
            // Półprzezroczyste tło – opis czytelny także nad literami profili.
            dc.DrawRoundedRectangle(TimerBackgroundBrush, null, new Rect(x - 3, top, text.Width + 6, text.Height), 3, 3);
            dc.DrawText(text, new Point(x, top));
            previousBottom = top + text.Height;
        }
    }

    /// <summary>Rysuje poziome linie cenowe przez całą szerokość wykresu.</summary>
    private void DrawPriceLines(DrawingContext dc, double chartRight)
    {
        if (Lines is not { Count: > 0 } lines)
        {
            return;
        }

        foreach (var line in lines)
        {
            var price = GetDisplayedPrice(line);
            var y = Math.Round(PriceToY(price)) + (line.Thickness % 2 == 1 ? 0.5 : 0); // ostra linia na pikselach
            if (y < HeaderHeight || y > ActualHeight)
            {
                continue;
            }

            var brush = CreateBrush(line.Color);
            dc.DrawLine(CreatePen(brush, line.Thickness, line.Style), new Point(0, y), new Point(chartRight, y));

            // Opis linii – nad linią, przy lewej albo prawej krawędzi wykresu (wg ustawienia linii).
            // Zablokowana linia ma ikonę kłódki.
            var labelText = (line.IsLocked ? "🔒 " : string.Empty) + line.Label;
            if (!string.IsNullOrWhiteSpace(labelText))
            {
                var label = CreateText(labelText, 11, brush, SymbolTypeface);
                var labelX = line.LabelPosition == LabelPosition.Left ? 8 : chartRight - label.Width - 8;
                dc.DrawText(label, new Point(labelX, y - label.Height - line.Thickness / 2 - 1));
            }
        }
    }

    /// <summary>Rysuje oś cen po prawej stronie, znaczniki linii cenowych oraz ostatniej ceny.</summary>
    private void DrawPriceAxis(DrawingContext dc, double chartRight, ProfileSnapshot latest)
    {
        dc.DrawRectangle(HeaderBrush, null, new Rect(chartRight, 0, AxisWidth, ActualHeight));

        // Etykieta co tyle wierszy, żeby odstęp między etykietami wynosił co najmniej ~22 px.
        var rowsPerLabel = Math.Max(1, (int)Math.Ceiling(22 / _rowPx));
        var step = latest.RowHeight * rowsPerLabel;

        var topPrice = YToPrice(HeaderHeight);
        var bottomPrice = YToPrice(ActualHeight);
        var firstLabel = Math.Floor(bottomPrice / step) * step;

        for (var price = firstLabel; price <= topPrice; price += step)
        {
            var y = PriceToY(price);
            if (y < HeaderHeight)
            {
                continue;
            }

            dc.DrawLine(SeparatorPen, new Point(chartRight, y), new Point(chartRight + 4, y));
            var label = CreateText(FormatPrice(price), 11, DimTextBrush, UiTypeface);
            dc.DrawText(label, new Point(chartRight + 8, y - label.Height / 2));
        }

        // Etykiety średnich (z wykresu dziennego) na osi – w kolorze średniej.
        if (Averages is { ShowAxisLabel: true } averages)
        {
            foreach (var line in averages.Lines)
            {
                var y = PriceToY(line.Price);
                if (y >= HeaderHeight && y <= ActualHeight)
                {
                    DrawAxisLabel(dc, chartRight, y, line.Price, line.AxisBrush);
                }
            }
        }

        // Etykiety linii cenowych na osi (w kolorze linii).
        foreach (var line in Lines ?? [])
        {
            var price = GetDisplayedPrice(line);
            var y = PriceToY(price);
            if (y >= HeaderHeight && y <= ActualHeight)
            {
                DrawAxisLabel(dc, chartRight, y, price, CreateBrush(line.Color));
            }
        }

        // Ostatnia cena – przerywana linia przez wykres i zielona etykieta na osi.
        var lastY = PriceToY(ToRow(latest.LastPrice, latest.RowHeight));
        dc.DrawLine(LastPricePen, new Point(0, lastY), new Point(chartRight, lastY));
        DrawAxisLabel(dc, chartRight, lastY, latest.LastPrice, LastPriceBrush);
    }

    /// <summary>Kolorowa etykieta z ceną na osi.</summary>
    private void DrawAxisLabel(DrawingContext dc, double chartRight, double y, decimal price, Brush background)
    {
        var text = CreateText(FormatPrice(price), 11, Brushes.Black, UiTypeface);
        dc.DrawRectangle(background, null, new Rect(chartRight + 2, y - text.Height / 2 - 1, AxisWidth - 4, text.Height + 2));
        dc.DrawText(text, new Point(chartRight + 8, y - text.Height / 2));
    }

    // =====================================================================
    // Przeliczanie ceny <-> pozycji na ekranie
    // =====================================================================

    /// <summary>
    /// Ustala cenę na środku ekranu. W trybie śledzenia wykres przesuwa się dopiero wtedy,
    /// gdy cena wyjdzie poza środkowe 60% wysokości – dzięki temu obraz nie "skacze" przy każdym ticku.
    /// </summary>
    private decimal GetCenterPrice(ProfileSnapshot latest)
    {
        if (_manualCenter is { } manual)
        {
            return manual;
        }

        if (_fitCenter is { } fit)
        {
            return fit;
        }

        var visibleRows = (ActualHeight - HeaderHeight) / _rowPx;
        var tolerance = (decimal)(visibleRows * 0.3) * latest.RowHeight;

        if (_autoCenter is null || Math.Abs(latest.LastPrice - _autoCenter.Value) > tolerance)
        {
            _autoCenter = ToRow(latest.LastPrice, latest.RowHeight);
        }

        return _autoCenter.Value;
    }

    private double MiddleY => HeaderHeight + (ActualHeight - HeaderHeight) / 2;

    /// <summary>Zamienia cenę na współrzędną Y (środek wiersza).</summary>
    private double PriceToY(decimal price) =>
        MiddleY - (double)((price - _lastCenter) / _lastRowHeight) * _rowPx;

    /// <summary>Zamienia współrzędną Y na cenę.</summary>
    private decimal YToPrice(double y) =>
        _lastCenter + (decimal)((MiddleY - y) / _rowPx) * _lastRowHeight;

    /// <summary>Cena pod kursorem zaokrąglona do pełnego ticka.</summary>
    private decimal YToTickPrice(double y) => Math.Round(YToPrice(y) / _lastTickSize) * _lastTickSize;

    private static decimal ToRow(decimal price, decimal rowHeight) => Math.Floor(price / rowHeight) * rowHeight;

    /// <summary>Cena linii do narysowania (podczas przeciągania – bieżąca pozycja myszy).</summary>
    private decimal GetDisplayedPrice(PriceLine line) =>
        _draggedLine?.Id == line.Id ? _draggedLinePrice : line.Price;

    // =====================================================================
    // Wyszukiwanie elementu pod kursorem
    // =====================================================================

    /// <summary>Linia cenowa pod kursorem (lub null).</summary>
    private PriceLine? HitTestLine(Point point)
    {
        if (!_hasData || point.X > ActualWidth - AxisWidth)
        {
            return null;
        }

        return (Lines ?? [])
            .Select(l => (Line: l, Distance: Math.Abs(PriceToY(l.Price) - point.Y)))
            .Where(x => x.Distance <= Math.Max(LineHitTolerance, x.Line.Thickness / 2 + 2))
            .OrderBy(x => x.Distance)
            .Select(x => x.Line)
            .FirstOrDefault();
    }

    /// <summary>Kolumna profilu pod kursorem (lub null).</summary>
    private ColumnLayout? HitTestColumn(Point point) =>
        _columns.FirstOrDefault(c => point.X >= c.X && point.X < c.X + c.Width - Gap / 2);

    /// <summary>
    /// Okres TPO (litera) pod kursorem. Litery w wierszu są ułożone kolejno, więc numer litery
    /// wyliczamy z pozycji X, a wiersz – z pozycji Y.
    /// </summary>
    private (ColumnLayout Column, int Period)? HitTestLetter(Point point)
    {
        if (HitTestColumn(point) is not { } column)
        {
            return null;
        }

        var rowPrice = ToRow(YToPrice(point.Y) + _lastRowHeight / 2, _lastRowHeight); // wiersz najbliższy kursorowi
        var level = column.Profile.Levels.FirstOrDefault(l => l.Price == rowPrice);
        var index = (int)Math.Floor((point.X - column.LettersX) / _letterPx);

        if (level is null || index < 0)
        {
            return null;
        }

        if (column.IsExpanded)
        {
            // Tryb Split: kolumna = okres, ale tylko jeśli w tym wierszu jest litera tego okresu.
            var period = column.Profile.FirstPeriod + index;
            return level.Periods.Contains(period) ? (column, period) : null;
        }

        return index < level.Periods.Count ? (column, level.Periods[index]) : null;
    }

    // =====================================================================
    // Obsługa myszy
    // =====================================================================

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var position = e.GetPosition(this);
        var line = HitTestLine(position);

        if (e.ClickCount == 2)
        {
            if (line is not null)
            {
                // Podwójne kliknięcie na linii – edycja.
                EditLineRequested?.Invoke(line);
            }
            else
            {
                // Podwójne kliknięcie w innym miejscu – powrót do śledzenia bieżącej ceny
                // i do automatycznego dopasowania ustawionej liczby profili na ekranie.
                FitAndPinLatest();
            }

            return;
        }

        // Zablokowanej linii nie przesuwamy – wtedy przeciąganie przesuwa cały wykres.
        var movableLine = line is { IsLocked: false } ? line : null;

        _dragStart = position;
        _dragPanX = _panX;
        _dragCenter = _lastCenter;
        _draggedLine = movableLine;
        _draggedLinePrice = movableLine?.Price ?? 0;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        TrackCursor(position);

        if (_dragStart is not { } start)
        {
            // Bez przeciągania – zmieniamy kursor nad odblokowaną linią, żeby było widać, że można ją złapać.
            Cursor = HitTestLine(position) is { IsLocked: false } ? Cursors.SizeNS : CrosshairEnabled ? Cursors.Cross : null;
            return;
        }

        if (_draggedLine is not null)
        {
            // Przeciąganie linii – linia "przykleja się" do pełnych ticków.
            _draggedLinePrice = YToTickPrice(position.Y);
        }
        else
        {
            // Przesuwanie całego wykresu.
            _panX = _dragPanX + (position.X - start.X);
            ClearPin();
            _manualCenter = _dragCenter + (decimal)((position.Y - start.Y) / _rowPx) * _lastRowHeight;
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_draggedLine is { } line && _draggedLinePrice != line.Price)
        {
            LineMoved?.Invoke(line, _draggedLinePrice);
        }

        _dragStart = null;
        _draggedLine = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    /// <summary>Prawy przycisk – menu kontekstowe zależne od tego, co jest pod kursorem.</summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (!_hasData)
        {
            return;
        }

        var position = e.GetPosition(this);
        var menu = new ContextMenu { PlacementTarget = this };

        if (HitTestLine(position) is { } line)
        {
            // Menu linii cenowej.
            menu.Items.Add(CreateMenuItem($"Edytuj linię {FormatPrice(line.Price)}…", () => EditLineRequested?.Invoke(line)));
            menu.Items.Add(CreateMenuItem(line.IsLocked ? "🔓 Odblokuj linię" : "🔒 Zablokuj linię",
                () => ToggleLockRequested?.Invoke(line)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Usuń linię", () => DeleteLineRequested?.Invoke(line)));
        }
        else
        {
            var price = YToTickPrice(position.Y);
            menu.Items.Add(CreateMenuItem($"Dodaj linię na cenie {FormatPrice(price)}…", () => AddLineRequested?.Invoke(price)));

            // Rozbicie profilu – tylko po kliknięciu w literę, która nie jest pierwszym okresem części.
            if (HitTestLetter(position) is { } hit && hit.Period > hit.Column.Profile.FirstPeriod)
            {
                var letter = TpoLetters.ForPeriod(hit.Period);
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem($"Rozbij profil od okresu {letter}",
                    () => SplitRequested?.Invoke(hit.Column.Profile, hit.Period)));
            }

            if (HitTestColumn(position) is { } column)
            {
                if (menu.Items.Count == 1)
                {
                    menu.Items.Add(new Separator());
                }

                // Połączenie – dla każdej części rozbitego profilu oprócz pierwszej.
                if (column.Profile.FirstPeriod > 0)
                {
                    menu.Items.Add(CreateMenuItem("Połącz z poprzednią częścią", () => MergeRequested?.Invoke(column.Profile)));
                }

                // Sesja dzienna: ręczny wpis ceny rozliczeniowej (gdy CME jej nie podało).
                if (SessionNames.IsDay(column.Profile.Session.Name))
                {
                    var day = column.Profile;
                    menu.Items.Add(CreateMenuItem($"SETTLE {day.TradingDate:dd.MM} (domyślnie Close) – wpisz ręcznie…",
                        () => SettleRequested?.Invoke(day.TradingDate, day.LastPrice)));
                }

                // Tryb Split/Expanded – osobna kolumna dla każdego okresu TPO.
                menu.Items.Add(CreateMenuItem(column.IsExpanded ? "Zwiń profil" : "Rozwiń profil (Split)",
                    () => ToggleExpandRequested?.Invoke(column.Profile)));

                // Symulacja: start od początku tego profilu albo od klikniętego słupka (litery TPO).
                if (SimulationMenu)
                {
                    var profile = column.Profile;
                    menu.Items.Add(new Separator());
                    menu.Items.Add(CreateMenuItem(
                        $"▶ Symulacja od początku profilu ({profile.Session.Name} {profile.TradingDate:dd.MM}, {profile.PartStart:HH:mm})",
                        () => SimulateBackToRequested?.Invoke(profile.PartStart)));

                    if (HitTestLetter(position) is { } bar && profile.Periods.FirstOrDefault(p => p.Index == bar.Period) is { } period)
                    {
                        menu.Items.Add(CreateMenuItem($"▶ Symulacja od słupka {period.Letter} ({period.Start:HH:mm})",
                            () => SimulateBackToRequested?.Invoke(period.Start)));
                    }
                }
            }

            // Blokada wszystkich linii naraz.
            if (Lines is { Count: > 0 })
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("🔒 Zablokuj wszystkie linie", () => LockAllRequested?.Invoke(true)));
                menu.Items.Add(CreateMenuItem("🔓 Odblokuj wszystkie linie", () => LockAllRequested?.Invoke(false)));
            }
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Kółko myszy – powiększanie "jak na mapie" względem punktu pod kursorem:
    ///   samo kółko – obie osie (przybliżenie na profil / oddalenie, żeby zobaczyć więcej profili),
    ///   Ctrl + kółko – tylko w poziomie, Shift + kółko – tylko w pionie.
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var modifiers = Keyboard.Modifiers;
        var horizontal = !modifiers.HasFlag(ModifierKeys.Shift);
        var vertical = !modifiers.HasFlag(ModifierKeys.Control);

        var position = e.GetPosition(this);
        Zoom(factor, horizontal ? position : null, vertical ? position : null, horizontal, vertical);
        e.Handled = true;
    }

    /// <summary>Klawisze: + przybliż, − oddal, 0 dopasuj (do liczby profili z paska narzędzi).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus:
                ZoomIn();
                e.Handled = true;
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomOut();
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                FitAndPinLatest();
                e.Handled = true;
                break;
            case Key.C:
                SetCurrentValue(CrosshairEnabledProperty, !CrosshairEnabled); // kursor z liniami – wł./wył.
                e.Handled = true;
                break;
        }
    }

    /// <summary>Przybliżenie (przycisk „+”, klawisz +).</summary>
    public void ZoomIn() => ZoomFromButton(1.25);

    /// <summary>Oddalenie (przycisk „−”, klawisz −).</summary>
    public void ZoomOut() => ZoomFromButton(1 / 1.25);

    /// <summary>
    /// Powiększenie z przycisku/klawisza (bez kursora):
    ///  - w poziomie: gdy oglądamy najnowsze profile, najnowszy zostaje przy prawej krawędzi
    ///    (oddalanie odsłania starsze profile po lewej); po przesunięciu widoku – względem środka wykresu,
    ///  - w pionie: gdy wykres śledzi cenę, nadal ją śledzi; w przeciwnym razie cena na środku zostaje na środku.
    /// </summary>
    private void ZoomFromButton(double factor)
    {
        Point? horizontalAnchor = Math.Abs(_panX) < 0.5 ? null : ChartCenter;
        Zoom(factor, horizontalAnchor, verticalAnchor: null, horizontal: true, vertical: true);
    }

    /// <summary>
    /// Dopasowanie: ostatnie N profili (pole „Profile na ekranie”) mieści się na ekranie, widok wraca do najnowszych
    /// profili i śledzenia ceny.
    /// </summary>
    public void FitToScreen()
    {
        _manualLetterWidth = false;
        _manualRowHeight = false;
        _panX = 0;
        _manualCenter = null;
        _autoCenter = null;
        ClearPin();
        _letterCache.Clear();
        InvalidateVisual();
    }

    /// <summary>
    /// Przypina najnowszy widoczny profil tak, żeby jego lewa krawędź była w <paramref name="fraction"/> szerokości
    /// wykresu (np. 0.3 – po cofnięciu czasu symulacji: starsze profile po lewej, miejsce na dalsze rysowanie po prawej).
    /// Przypięcie trwa do przesunięcia wykresu, zmiany powiększenia albo „Dopasuj”; gdy nowe profile dojdą
    /// do prawej krawędzi, wykres przewija się jak zwykle. Pion – śledzenie ceny.
    /// </summary>
    public void PinLatest(double fraction = LatestPinFraction)
    {
        _pinAnywhere = false;
        _panX = 0;
        _manualCenter = null;
        _autoCenter = null;
        _pinFraction = fraction;
        _pinKey = null;
        InvalidateVisual();
    }

    /// <summary>Miejsce lewej krawędzi bieżącego profilu po „Dopasuj” – ułamek szerokości wykresu od lewej.</summary>
    public const double LatestPinFraction = 0.3;

    /// <summary>
    /// „Dopasuj” (przycisk, klawisz 0, podwójne kliknięcie) – tak samo na żywo i w symulacji: ostatnie N profili
    /// w skali ekranu, bieżący profil od 0,3 szerokości wykresu, po prawej miejsce na dalsze rysowanie.
    /// </summary>
    /// <summary>
    /// Zamraża bieżący widok: obecna szerokość liter, wysokość wierszy, środek ceny i przesunięcie zostają jak są
    /// (bez dopasowania i śledzenia ceny) – np. przy cofnięciu symulacji do słupka wykres się nie przesuwa.
    /// „Dopasuj” albo podwójne kliknięcie przywraca dopasowanie.
    /// </summary>
    public void FreezeView()
    {
        _manualLetterWidth = true;
        _manualRowHeight = true;
        _manualCenter = _lastCenter;
        ClearPin();
    }

    public void FitAndPinLatest()
    {
        FitToScreen();
        PinLatest();
    }

    private void ClearPin()
    {
        _pinFraction = null;
        _pinKey = null;
        _pinAnywhere = false;
    }

    /// <summary>Usuwa podświetlenie profilu wybranego w tabeli parametrów (np. po zamknięciu tabeli).</summary>
    public void ClearHighlight()
    {
        _highlight = null;
        InvalidateVisual();
    }

    /// <summary>
    /// Pokazuje i podświetla wybrany profil (np. z tabeli parametrów profili): jego początek na środku szerokości wykresu,
    /// a jego POC na środku wysokości. Zwraca false, gdy profilu nie ma na wykresie (poza wczytaną historią
    /// albo jego sesja jest ukryta).
    /// </summary>
    public bool ShowProfile(string sessionName, DateTime sessionStart)
    {
        var view = Profiles?.FirstOrDefault(p => p.IsVisible && p.Snapshot.Session.Name == sessionName &&
                                                 p.Snapshot.SessionStart == sessionStart);
        if (view is null)
        {
            return false;
        }

        _pinFraction = 0.5;
        _pinKey = ProfileKey(view.Snapshot);
        _pinAnywhere = true;
        _highlight = (sessionName, sessionStart);
        _manualCenter = view.Snapshot.Poc;
        _autoCenter = null;
        InvalidateVisual();
        return true;
    }

    private Point ChartCenter =>new((ActualWidth - AxisWidth) / 2, MiddleY);

    /// <summary>
    /// Zmiana powiększenia o <paramref name="factor"/>. Punkty kotwic (profil i cena pod nimi) zostają w tym samym
    /// miejscu ekranu; brak kotwicy poziomej = układ zostaje przyklejony do prawej krawędzi, brak kotwicy pionowej =
    /// środek ceny się nie zmienia (albo dalej śledzi bieżącą cenę). Ręczne powiększenie wyłącza dopasowanie liczby profili.
    /// </summary>
    private void Zoom(double factor, Point? horizontalAnchor, Point? verticalAnchor, bool horizontal, bool vertical)
    {
        if (!_hasData)
        {
            return;
        }

        if (horizontal)
        {
            // Zapamiętujemy profil i miejsce w nim pod kursorem – po przeliczeniu układu wróci pod kursor.
            if (horizontalAnchor is { } h &&
                _lastLayout.FirstOrDefault(c => h.X >= c.X && h.X < c.X + c.Width) is { } column)
            {
                _pendingAnchor = new ZoomAnchor(ProfileKey(column.View.Snapshot), (h.X - column.X) / column.Width, h.X);
            }

            _letterPx = Math.Clamp(_letterPx * factor, ChartZoom.MinLetterPx, ChartZoom.MaxLetterPx);
            _manualLetterWidth = true;
            ClearPin();
        }

        if (vertical)
        {
            var newRowPx = Math.Clamp(_rowPx * factor, ChartZoom.MinRowPx, ChartZoom.MaxRowPx);
            if (verticalAnchor is { } v)
            {
                // Cena pod kursorem zostaje pod kursorem.
                var priceUnderCursor = YToPrice(v.Y);
                _manualCenter = ChartZoom.CenterForAnchor(priceUnderCursor, v.Y, MiddleY, newRowPx, _lastRowHeight);
            }

            _rowPx = newRowPx;
            _manualRowHeight = true;
            _autoCenter = null; // przy śledzeniu ceny – wyśrodkuj ponownie w nowej skali
        }

        _letterCache.Clear(); // zmienił się rozmiar czcionki
        InvalidateVisual();
    }

    // =====================================================================
    // Pomocnicze
    // =====================================================================

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Cena jako tekst – liczba miejsc po przecinku wg ticka instrumentu (np. 6E: 1.08455).</summary>
    private string FormatPrice(decimal price) => PriceFormat.Format(price, _lastTickSize);

    /// <summary>Zamienia kolor zapisany jako tekst "#RRGGBB" na pędzel (przy błędnym kolorze – biały).</summary>
    private static Brush CreateBrush(string color)
    {
        try
        {
            return Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));
        }
        catch (FormatException)
        {
            return Brushes.White;
        }
    }

    /// <summary>Tworzy pióro o zadanej grubości i stylu.</summary>
    private static Pen CreatePen(Brush brush, double thickness, LineStyle style)
    {
        var pen = new Pen(brush, thickness) { DashStyle = ChartStyle.ToDashStyle(style) };

        return Frozen(pen);
    }

    /// <summary>Rozmiar czcionki liter dopasowany do wysokości wiersza i szerokości litery.</summary>
    private double GetFontSize() => Math.Max(4, Math.Min(_rowPx * 0.85, _letterPx * 1.6));

    private FormattedText GetLetter(char letter, Brush brush, double fontSize)
    {
        if (!_letterCache.TryGetValue((letter, brush), out var text))
        {
            text = CreateText(letter.ToString(), fontSize, brush, LetterTypeface);
            _letterCache[(letter, brush)] = text;
        }

        return text;
    }

    private void DrawText(DrawingContext dc, string text, double size, Brush brush, Point origin) =>
        dc.DrawText(CreateText(text, size, brush, UiTypeface), origin);

    private FormattedText CreateText(string text, double size, Brush brush, Typeface typeface) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
