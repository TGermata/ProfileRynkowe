using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using MarketProfile.App.Controls;
using MarketProfile.App.Infrastructure;
using MarketProfile.App.Settings;
using MarketProfile.Core;
using MarketProfile.DataFeed;
using MarketProfile.DataFeed.IB;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Model widoku głównego okna – łączy źródło danych, silnik profili i widok.
///
/// Przepływ danych:
///   źródło danych (IMarketDataFeed) → ProfileBuilder → obiekty SessionProfile
///   → (co 100 ms) migawki ProfileSnapshot → kontrolka ProfileChartControl.
///
/// Odświeżanie widoku jest ograniczone do 10 razy na sekundę, niezależnie od liczby ticków –
/// przy szybkim rynku widok nie "zadławi się" setkami odświeżeń na sekundę.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherTimer _refreshTimer;

    // Pamięć podręczna migawek: profil sesji -> (wersja danych, migawki jego części).
    private readonly Dictionary<SessionProfile, (long Version, IReadOnlyList<ProfileSnapshot> Parts)> _snapshotCache = new();

    // Punkty rozbicia profili i linie cenowe bieżącego instrumentu.
    private readonly ProfileSplits _splits = new();
    private readonly Dictionary<string, InstrumentDrawings> _drawings;
    private IReadOnlyList<PriceLine> _lines = [];

    // Wygląd ostatnio dodanej/edytowanej linii – nowa linia dostaje ten sam kolor, grubość i styl.
    private PriceLine _lineTemplate = new();

    private AppSettings _settings;
    private ChartStyle _appearance;
    private MarketDataStore? _store;
    private int _loadedHistoryDays;            // ile dni historii wczytano przy ostatnim starcie
    private DateTimeOffset? _oldestLoaded;     // czas najstarszej wczytanej świecy
    private bool _loadingOlder;                // trwa doczytywanie starszej historii
    private int _historyLoadId;                // właściciel flagi _loadingOlder (zwalnia ją tylko ten, kto ją ustawił)
    private bool _noOlderData;                 // źródło nie ma już starszych danych
    private CancellationTokenSource? _historyCts; // doczytywanie historii po zwiększeniu liczby profili (do przerwania)
    private long _lastTickUtcTicks;          // czas ostatniego ticka (UTC) – "teraz" w danych na żywo
    private string _periodTimerText = string.Empty;
    private MarketDataRecorder? _recorder;
    private CancellationTokenSource? _backgroundCts;   // pobieranie historii zaznaczonych instrumentów w tle
    private Task? _backgroundTask;
    private IMarketDataFeed? _feed;
    private ProfileBuilder? _builder;
    private IReadOnlyList<ProfileSnapshot> _snapshots = [];   // wszystkie migawki (dane)
    private IReadOnlyList<ProfileView> _profiles = [];        // migawki przygotowane do wyświetlenia

    // Profile wyświetlane w trybie Split/Expanded (czas rozpoczęcia profilu/części).
    private readonly HashSet<DateTime> _expanded = [];
    private bool _isRunning;
    private string _summary = string.Empty;

    public MainViewModel()
    {
        // Problemy z plikami ustawień (uszkodzony plik, nieudany zapis) – do pola informacji zamiast cichej utraty danych.
        JsonFileStore.Problem += OnSettingsFileProblem;
        _settings = SettingsStore.Load();
        StartLog(_settings.EffectiveLogDirectory);
        _appearance = ChartStyle.Create(_settings);
        _drawings = DrawingsStore.Load();
        LoadDrawingsForInstrument();

        // Po zmianie rozbić przebudowujemy migawki od razu (także gdy dane nie są pobierane).
        _splits.Changed += (_, _) =>
        {
            _snapshotCache.Clear();
            RefreshSnapshots();
        };

        StartStopCommand = new AsyncRelayCommand(() =>
        {
            CancelReconnect(); // użytkownik sam decyduje – automatyczne ponowne łączenie przestaje być potrzebne
            return IsRunning ? StopAsync() : StartAsync();
        });

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshSnapshots();
            UpdateTempo();
            UpdateVolumeComparison();
            UpdateProfileStats();
            UpdatePeriodTimer();
            OnCandleTimer();
            OnBroadTimer();
            OnSectorTimer();
            CheckPriceAlerts();
        };

        RefreshTickDownloadText();
        RunSafely(LoadDailyBarsAsync()); // średnie dzienne na wykresie TPO
    }

    /// <summary>Bieżące ustawienia aplikacji.</summary>
    public AppSettings Settings => _settings;

    /// <summary>Skala pasków narzędzi i symulacji (Ustawienia → Okno → Rozmiar kontrolek).</summary>
    public double ControlScale => _settings.ControlScale;

    /// <summary>Instrumenty do wyboru na pasku narzędzi.</summary>
    public IReadOnlyList<Instrument> Instruments => _settings.Instruments;

    /// <summary>Skrót strefy czasowej wybranego instrumentu (np. "ET", "CT", "CET") – do etykiet czasu.</summary>
    public string TimeZoneLabel => _settings.Instrument.TimeZoneId switch
    {
        "America/New_York" => "ET",
        "America/Chicago" => "CT",
        "America/Los_Angeles" => "PT",
        "Europe/Berlin" or "Europe/Paris" or "Europe/Warsaw" or "Europe/Zurich" => "CET",
        "Europe/London" => "UK",
        "Asia/Tokyo" => "JST",
        var other => other,
    };

    /// <summary>Wybrany instrument – zmiana restartuje pobieranie danych.</summary>
    public Instrument SelectedInstrument
    {
        get => _settings.Instrument;
        set
        {
            if (value.Symbol == _settings.InstrumentSymbol)
            {
                return;
            }

            RunSafely(ApplySettingsAsync(_settings with { InstrumentSymbol = value.Symbol }));
        }
    }

    /// <summary>Profile (dane + sposób wyświetlania) przekazywane do kontrolki wykresu.</summary>
    public IReadOnlyList<ProfileView> Profiles
    {
        get => _profiles;
        private set => SetProperty(ref _profiles, value);
    }

    /// <summary>Wygląd wykresu (kolory sesji, profil wolumenowy) przekazywany do kontrolki wykresu.</summary>
    public ChartStyle Appearance
    {
        get => _appearance;
        private set => SetProperty(ref _appearance, value);
    }

    /// <summary>Linie cenowe bieżącego instrumentu przekazywane do kontrolki wykresu.</summary>
    public IReadOnlyList<PriceLine> Lines
    {
        get => _lines;
        private set => SetProperty(ref _lines, value);
    }

    // =====================================================================
    // Opcje wyświetlania (pasek narzędzi)
    // =====================================================================

    /// <summary>Czy pokazywać profile sesji nocnej.</summary>
    public bool ShowNightSession
    {
        get => !_settings.HiddenSessions.Contains("Nocna");
        set => SetSessionVisible("Nocna", value);
    }

    /// <summary>Czy pokazywać profile sesji dziennej.</summary>
    public bool ShowDaySession
    {
        get => !_settings.HiddenSessions.Contains("Dzienna");
        set => SetSessionVisible("Dzienna", value);
    }

    /// <summary>Tryby profilu wolumenowego do wyboru na pasku narzędzi.</summary>
    public IReadOnlyList<KeyValuePair<VolumeProfileMode, string>> VolumeModes { get; } =
    [
        new(VolumeProfileMode.None, "Brak"),
        new(VolumeProfileMode.Current, "Ostatni profil TPO"),
        new(VolumeProfileMode.All, "Wszystkie profile TPO"),
    ];

    /// <summary>Liczby profili na ekranie do wyboru na pasku narzędzi.</summary>
    public IReadOnlyList<int> ProfileCountOptions { get; } = [2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100];

    /// <summary>
    /// Ile ostatnich profili ma się mieścić na ekranie. Gdy wczytana historia jest za krótka na tyle profili,
    /// dane są wczytywane ponownie (na żywo – automatycznie; w symulacji – po naciśnięciu ▶).
    /// </summary>
    public int ProfilesOnScreen
    {
        get => _settings.ProfilesOnScreen;
        set
        {
            if (value == _settings.ProfilesOnScreen || value < 1)
            {
                return;
            }

            SaveDisplaySettings(_settings with { ProfilesOnScreen = value });
            OnPropertyChanged();
            RunSafely(EnsureHistoryForProfilesAsync());
        }
    }

    /// <summary>
    /// Sprawdza, czy wczytana historia wystarcza na ustawioną liczbę profili; jeśli nie – wczytuje więcej.
    /// </summary>
    private async Task EnsureHistoryForProfilesAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        if (_settings.RequiredHistoryDays() <= _loadedHistoryDays)
        {
            // Wystarczy to, co już jest – przerywamy ewentualne doczytywanie dłuższej historii (np. powrót z 20 do 5 profili).
            CancelHistoryExtension();
            return;
        }

        if (IsSimulationMode)
        {
            // W symulacji nie przerywamy odtwarzania samoczynnie – użytkownik decyduje.
            _loadedSimStart = null;
            Hint($"Na {ProfilesOnScreen} profili potrzeba {_settings.RequiredHistoryDays()} dni historii – naciśnij ▶, aby wczytać symulację od nowa");
            return;
        }

        await ExtendHistoryAsync();
    }

    private void CancelHistoryExtension()
    {
        _historyCts?.Cancel();
        _historyCts = null;
    }

    /// <summary>
    /// Dłuższa historia na większą liczbę profili – bez restartu połączenia i bez czyszczenia wykresu:
    ///  1) starsze świece z lokalnej bazy (od razu, wykres dostaje starsze profile w ułamku sekundy),
    ///  2) tylko brakujący początek – z IB w tle (wykres działa dalej; ponowna zmiana liczby profili przerywa pobieranie).
    /// </summary>
    private async Task ExtendHistoryAsync()
    {
        if (_feed is not { } feed || _builder is not { } builder || _oldestLoaded is not { } oldest)
        {
            return;
        }

        CancelHistoryExtension();
        var cts = _historyCts = new CancellationTokenSource();
        var required = _settings.RequiredHistoryDays();
        var from = DateTimeOffset.UtcNow - TimeSpan.FromDays(required);
        if (from >= oldest)
        {
            _loadedHistoryDays = required;
            return;
        }

        // Jedno doczytywanie naraz (także to przy przesuwaniu wykresu w lewo) – IB obsługuje jedno zapytanie o historię.
        while (_loadingOlder)
        {
            await Task.Delay(100);
            if (cts.IsCancellationRequested)
            {
                return;
            }
        }

        var loadId = BeginHistoryLoad();
        var zone = _settings.ExchangeTimeZone;
        try
        {
            // 1. Lokalna baza.
            var store = GetStore();
            var symbol = _settings.InstrumentSymbol;
            var dbBars = await Task.Run(() => BarAggregator.LoadMinuteBars(store, symbol, from, oldest, MarketDataStore.SourceBroker));
            if (!ReferenceEquals(feed, _feed) || !ReferenceEquals(builder, _builder) || cts.IsCancellationRequested)
            {
                return;
            }

            if (dbBars.Count > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var bar in dbBars)
                    {
                        builder.ProcessBar(bar);
                    }
                });
                if (!ReferenceEquals(feed, _feed) || !ReferenceEquals(builder, _builder))
                {
                    return;
                }

                _oldestLoaded = dbBars[0].Time;
                RefreshSnapshots();
            }

            // 2. Brakujący początek (bazy nie starczyło) – z IB, w tle. Krótsze braki niż doba to zwykle weekend / noc.
            var missingTo = _oldestLoaded!.Value;
            if (missingTo - from > TimeSpan.FromDays(1) && IsBrokerFeed(feed))
            {
                var ibBars = await Task.Run(() => feed.GetHistoryRangeAsync(from, missingTo, cts.Token));
                if (!ReferenceEquals(feed, _feed) || !ReferenceEquals(builder, _builder) || cts.IsCancellationRequested)
                {
                    return;
                }

                var recorder = _recorder;
                await Task.Run(() =>
                {
                    recorder?.RecordBars(ibBars);
                    foreach (var bar in ibBars)
                    {
                        builder.ProcessBar(bar);
                    }
                });

                if (!ReferenceEquals(feed, _feed) || !ReferenceEquals(builder, _builder))
                {
                    return;
                }

                if (ibBars.Count > 0)
                {
                    _oldestLoaded = ibBars.Min(b => b.Time);
                    RefreshSnapshots();
                }
            }

            _loadedHistoryDays = required;
        }
        catch (OperationCanceledException)
        {
            // Przerwane (np. zmiana instrumentu albo zatrzymanie) – nic do zgłaszania.
        }
        catch (Exception ex) when (ReferenceEquals(feed, _feed))
        {
            ShowError($"Nie udało się doczytać historii: {ex.Message}");
        }
        catch (Exception)
        {
            // Błąd starego (zatrzymanego) źródła – już nieaktualny.
        }
        finally
        {
            EndHistoryLoad(loadId);
        }
    }

    /// <summary>Zajmuje flagę doczytywania historii (IB obsługuje jedno zapytanie o historię naraz).</summary>
    private int BeginHistoryLoad()
    {
        _loadingOlder = true;
        return ++_historyLoadId;
    }

    /// <summary>Zwalnia flagę – tylko jeśli w międzyczasie nie zajął jej nowszy start / doczytywanie.</summary>
    private void EndHistoryLoad(int loadId)
    {
        if (_historyLoadId == loadId)
        {
            _loadingOlder = false;
        }
    }

    /// <summary>Ile dni wstecz doczytujemy za jednym razem przy przesuwaniu wykresu w lewo.</summary>
    private static readonly TimeSpan OlderHistoryStep = TimeSpan.FromDays(7);

    /// <summary>
    /// Doczytanie starszej historii – wywoływane przez wykres, gdy widać lewą krawędź najstarszego profilu.
    /// Pobiera ~7 dni przed najstarszą wczytaną świecą; nowe (starsze) profile wstawiają się na początek,
    /// a widok nie przeskakuje (układ jest liczony od prawej krawędzi).
    /// </summary>
    public async Task LoadOlderHistoryAsync()
    {
        if (!IsRunning || _loadingOlder || _noOlderData || _feed is not { } feed || _builder is not { } builder ||
            _oldestLoaded is not { } to)
        {
            return;
        }

        var loadId = BeginHistoryLoad();
        var recorder = _recorder;
        var from = to - OlderHistoryStep;
        var zone = _settings.ExchangeTimeZone;

        try
        {
            var bars = await Task.Run(() => feed.GetHistoryRangeAsync(from, to));
            if (!ReferenceEquals(feed, _feed))
            {
                return; // w międzyczasie zatrzymano albo zmieniono źródło danych
            }

            if (bars.Count == 0)
            {
                _noOlderData = true;
                Hint("Brak starszych danych w źródle");
                return;
            }

            await Task.Run(() =>
            {
                recorder?.RecordBars(bars);
                foreach (var bar in bars)
                {
                    builder.ProcessBar(bar);
                }
            });

            if (!ReferenceEquals(feed, _feed) || !ReferenceEquals(builder, _builder))
            {
                return;
            }

            _oldestLoaded = bars.Min(b => b.Time);
            RefreshSnapshots();
        }
        catch (Exception ex) when (ReferenceEquals(feed, _feed))
        {
            _noOlderData = true; // nie ponawiamy w kółko po błędzie
            ShowError($"Nie udało się doczytać starszej historii: {ex.Message}");
        }
        catch (Exception)
        {
            // Błąd starego (zatrzymanego) źródła – już nieaktualny.
        }
        finally
        {
            EndHistoryLoad(loadId);
        }
    }

    /// <summary>Wybrany tryb profilu wolumenowego.</summary>
    public VolumeProfileMode SelectedVolumeMode
    {
        get => _settings.VolumeProfile;
        set
        {
            if (value != _settings.VolumeProfile)
            {
                SaveDisplaySettings(_settings with { VolumeProfile = value });
            }
        }
    }

    /// <summary>Pokazuje lub ukrywa profile danej sesji.</summary>
    private void SetSessionVisible(string sessionName, bool visible)
    {
        var hidden = _settings.HiddenSessions.Where(s => s != sessionName).ToList();
        if (!visible)
        {
            hidden.Add(sessionName);
        }

        SaveDisplaySettings(_settings with { HiddenSessions = hidden });
    }

    /// <summary>Kursor z liniami ceny i czasu na wykresie TPO (panel narzędzi, klawisz C) – zapamiętywany.</summary>
    public bool ShowCrosshair
    {
        get => _settings.ShowCrosshair;
        set
        {
            if (value != _settings.ShowCrosshair)
            {
                _settings = _settings with { ShowCrosshair = value }; // tylko zapis – bez przeliczania profili
                SettingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Czy panel narzędzi jest zwinięty – zapamiętywane.</summary>
    public bool ToolPanelCollapsed
    {
        get => _settings.ToolPanelCollapsed;
        set
        {
            if (value != _settings.ToolPanelCollapsed)
            {
                _settings = _settings with { ToolPanelCollapsed = value }; // tylko zapis – bez przeliczania profili
                SettingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Zapisuje położenie panelu narzędzi (po przesunięciu go myszą).</summary>
    public void SaveToolPanelPosition(double left, double top)
    {
        _settings = _settings with { ToolPanelLeft = left, ToolPanelTop = top };
        SettingsStore.Save(_settings);
    }

    /// <summary>Zapisuje układ okien (okno główne i zakładki w osobnych oknach) – przy zamykaniu aplikacji.</summary>
    public void SaveWindowLayout(WindowLayout main, IReadOnlyList<WindowLayout> detachedTabs)
    {
        _settings = _settings with { MainWindowLayout = main, DetachedTabs = [.. detachedTabs] };
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// Zapisuje ustawienia, które zmieniają tylko wygląd – bez restartu pobierania danych.
    /// </summary>
    private void SaveDisplaySettings(AppSettings newSettings)
    {
        _settings = newSettings;
        SettingsStore.Save(newSettings);
        OnPropertyChanged(nameof(ShowNightSession));
        OnPropertyChanged(nameof(ShowDaySession));
        OnPropertyChanged(nameof(SelectedVolumeMode));
        ComposeViews();
    }

    /// <summary>Przełącza tryb Split/Expanded dla profilu (lub części rozbitego profilu).</summary>
    public void ToggleExpanded(ProfileSnapshot snapshot)
    {
        if (!_expanded.Remove(snapshot.PartStart))
        {
            _expanded.Add(snapshot.PartStart);
        }

        SaveDrawings();
        ComposeViews();
    }

    /// <summary>
    /// Łączy dane (migawki) z opcjami wyświetlania: oznacza ukryte sesje, profile rozwinięte
    /// i te, dla których ma być rysowany profil wolumenowy.
    /// </summary>
    private void ComposeViews()
    {
        var latest = _snapshots.LastOrDefault(s => !s.IsEmpty);

        // "Ostatni profil TPO" = ostatni WIDOCZNY profil – gdy sesja najnowszego profilu jest ukryta
        // (np. wyłączona „Nocna”), profil wolumenowy dostaje ostatni profil, który widać na wykresie.
        var latestVisible = _snapshots.LastOrDefault(s => !s.IsEmpty && !_settings.HiddenSessions.Contains(s.Session.Name));

        Profiles = _snapshots
            .Select(s => new ProfileView(
                s,
                IsVisible: !_settings.HiddenSessions.Contains(s.Session.Name),
                IsExpanded: _expanded.Contains(s.PartStart),
                ShowVolume: _settings.VolumeProfile == VolumeProfileMode.All ||
                            (_settings.VolumeProfile == VolumeProfileMode.Current && ReferenceEquals(s, latestVisible)),
                // Wolumen aktualnie budowanego słupka – tylko w najnowszym profilu (tam powstaje bieżący okres).
                ShowPeriodVolume: _settings.PeriodVolume.Enabled && ReferenceEquals(s, latest) &&
                                  !_settings.HiddenSessions.Contains(s.Session.Name),
                PeriodVolumeReference: ReferenceEquals(s, latest) ? PeriodVolumeReference(latest) : 0))
            .ToList();

        UpdateTempo();
        UpdateProfileStats();
        UpdateSummary(latest);
        UpdateTpoAverages();
    }

    /// <summary>
    /// Typowy największy wolumen wiersza w słupku TPO – mediana z ostatnich N zakończonych słupków tej samej sesji
    /// (dzienna z dziennymi, nocna z nocnymi; w razie potrzeby także z poprzednich dni).
    /// Służy jako pełna długość słupków profilu wolumenowego bieżącego okresu. 0 = skala własnym maksimum.
    /// </summary>
    private long PeriodVolumeReference(ProfileSnapshot current)
    {
        var style = _settings.PeriodVolume;
        if (style.ScaleMode != PeriodVolumeScaleMode.Reference)
        {
            return 0;
        }

        var count = Math.Clamp(style.ReferencePeriods, 1, 200);
        var values = new List<long>(count);

        // Od najnowszej migawki wstecz; w każdej migawce – od najnowszego zakończonego słupka wstecz.
        for (var i = _snapshots.Count - 1; i >= 0 && values.Count < count; i--)
        {
            var snapshot = _snapshots[i];
            if (snapshot.Session.Name != current.Session.Name)
            {
                continue;
            }

            for (var j = snapshot.CompletedPeriodMaxVolumes.Count - 1; j >= 0 && values.Count < count; j--)
            {
                if (snapshot.CompletedPeriodMaxVolumes[j] > 0)
                {
                    values.Add(snapshot.CompletedPeriodMaxVolumes[j]);
                }
            }
        }

        return ProfileCalculator.Median(values);
    }

    /// <summary>Wzór dla nowej linii (kolor, grubość, styl ostatnio używanej linii).</summary>
    public PriceLine CreateLineTemplate(decimal price) => _lineTemplate with { Id = Guid.NewGuid(), Price = price, Label = string.Empty };

    /// <summary>Czy dane są aktualnie pobierane.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StartStopText));
                OnPropertyChanged(nameof(ShowStartStop));
            }
        }
    }

    /// <summary>Tekst przycisku Start/Stop.</summary>
    public string StartStopText => IsRunning ? "■ Stop" : IsBrokerLive ? "⟳ Połącz" : "▶ Start";

    /// <summary>
    /// Tryb na żywo z danymi od brokera (na żywo lub opóźnionymi): połączenie uruchamia się samo,
    /// więc przycisk Start/Stop nie jest potrzebny.
    /// </summary>
    public bool IsBrokerLive => !IsSimulationMode && _settings.UsesInteractiveBrokers;

    /// <summary>
    /// Czy pokazać przycisk Start/Stop: dla generatora – zawsze w trybie na żywo; dla brokera – tylko gdy
    /// połączenie nie działa (np. TWS był wyłączony), jako „⟳ Połącz” do ponownej próby.
    /// </summary>
    public bool ShowStartStop => IsLiveMode && (!IsBrokerLive || !IsRunning);

    /// <summary>Prośba do widoku o dopasowanie wykresu (np. po zmianie instrumentu – inne ceny, inne profile).</summary>
    public event Action? FitRequested;

    /// <summary>
    /// Prośba do widoku o przypięcie najnowszego widocznego profilu w 0,3 szerokości ekranu (bez zmiany powiększenia) –
    /// po cofnięciu czasu symulacji: profil, do którego się cofnęliśmy, jest na ekranie, a po prawej jest miejsce
    /// na dalsze rysowanie.
    /// </summary>
    public event Action? PinLatestRequested;

    /// <summary>Dopasowanie do wykonania po wczytaniu danych (po starcie aplikacji i po zmianie instrumentu).</summary>
    private bool _fitPending = true;

    /// <summary>Uruchamia automatycznie połączenie z brokerem (gdy jest wybrany i jeszcze nie działa).</summary>
    public async Task AutoStartBrokerAsync()
    {
        if (IsBrokerLive && !IsRunning)
        {
            await StartAsync();
        }
    }


    /// <summary>Podsumowanie bieżącego profilu: POC, VAH, VAL, IB itd.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ICommand StartStopCommand { get; }

    /// <summary>
    /// Tryb "Na żywo": uruchamia źródło danych wybrane w ustawieniach – Interactive Brokers albo generator.
    /// </summary>
    public Task StartAsync() => _settings.UsesInteractiveBrokers
        ? StartInteractiveBrokersAsync()
        : StartFeedAsync(new SimulatedDataFeed { Speed = _settings.SimulatorSpeed });

    /// <summary>
    /// Start Interactive Brokers: dla TWS API najpierw wskazujemy bibliotekę TWS API ze ścieżki z ustawień,
    /// dopiero potem tworzymy źródło danych (które z tej biblioteki korzysta). Web API biblioteki nie potrzebuje.
    /// </summary>
    private async Task StartInteractiveBrokersAsync()
    {
        try
        {
            IbFeeds.Prepare(_settings);
        }
        catch (Exception ex)
        {
            ShowError($"Błąd biblioteki TWS API: {ex.Message} – sprawdź ścieżkę w Ustawieniach → Dane i broker → Źródło i połączenie.");
            return;
        }

        if (await StartFeedAsync(IbFeeds.Create(_settings.ToIbSettings())))
        {
            _tickSizeWarning = IbFeeds.TickSizeWarning(_feed);
            NotifyDataSource();
            StartBackgroundDownload();
            RunSafely(StartBroadLiveAsync(), "Szeroki rynek – połączenie na żywo");
        }
    }

    // =====================================================================
    // Pobieranie historii w tle
    // =====================================================================

    /// <summary>
    /// Uruchamia pobieranie w tle historii instrumentów z zaznaczonym „Pobieraj dane” (jeśli jeszcze nie działa).
    /// Działa w obu trybach – na żywo i w symulacji – bo ma własne połączenie z TWS (Client ID + 1).
    /// Zmiana instrumentu na wykresie ani trybu pracy nie przerywa pobierania.
    /// </summary>
    private void StartBackgroundDownload()
    {
        RefreshTickDownloadText();
        if (_backgroundCts is not null || !_settings.RecordToDatabase ||
            !_settings.UsesInteractiveBrokers)
        {
            return;
        }

        var instruments = _settings.DownloadInstruments;
        if (instruments.Count == 0 && _settings.BroadMarketInstrument is null)
        {
            return;
        }

        // Biblioteka TWS API – w trybie na żywo jest już wskazana, w symulacji wskazujemy ją tutaj (Web API jej nie potrzebuje).
        try
        {
            IbFeeds.Prepare(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Error("Pobieranie w tle", "biblioteka TWS API niedostępna", ex);
            _backgroundError = $"biblioteka TWS API: {ex.Message} – sprawdź ścieżkę w Ustawieniach → Dane i broker → Źródło i połączenie";
            RefreshTickDownloadText();
            return;
        }

        var downloader = new BackgroundHistoryDownloader(instruments, _settings.ToIbSettings(), GetStore(),
            _settings.BackgroundHistoryDays, _settings.BackgroundTickDays, _settings.BroadMarketInstrument,
            _settings.NeedsCashSeries);
        var cts = new CancellationTokenSource();
        downloader.Progress += (_, message) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(cts, _backgroundCts))
                {
                    // Bieżąca czynność (łączenie, świece) – na pasku pobierania, gdy akurat nie są pobierane ticki.
                    _backgroundActivity = message;
                    if (_tickActive is null)
                    {
                        RefreshTickDownloadText();
                    }
                }
            });

        downloader.TickProgress += (_, status) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(cts, _backgroundCts))
                {
                    _tickActive = status;
                    _nextTickCheck = null;
                    RefreshTickDownloadText();
                }
            });
        downloader.PassFinished += (_, pass) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(cts, _backgroundCts))
                {
                    _tickActive = null;
                    _backgroundActivity = null;
                    _backgroundError = pass.Error;
                    if (pass.Error is { } passError)
                    {
                        AppLog.Warning("Pobieranie w tle", passError);
                    }
                    _nextTickCheck = pass.MoreTicks ? null : DateTime.Now + BackgroundHistoryDownloader.RepeatAfter;
                    RefreshTickDownloadText();
                    OnCandleBackgroundPass();
                }
            });

        _backgroundCts = cts;
        _tickActive = null;
        _nextTickCheck = null;
        _backgroundActivity = null;
        _backgroundError = null;
        RefreshTickDownloadText();
        _backgroundTask = Task.Run(async () =>
        {
            try
            {
                await downloader.RunAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Zatrzymanie pobierania.
            }
            catch (Exception ex)
            {
                AppLog.Error("Pobieranie w tle", "pobieranie w tle przerwane nieoczekiwanym błędem", ex);
                throw;
            }
        });
    }

    /// <summary>Zatrzymuje pobieranie w tle i czeka na jego zakończenie.</summary>
    private async Task StopBackgroundDownloadAsync()
    {
        if (_backgroundCts is not { } cts)
        {
            return;
        }

        _backgroundCts = null;
        await cts.CancelAsync();
        if (_backgroundTask is { } task)
        {
            await task;
        }

        cts.Dispose();
        _backgroundTask = null;
        _backgroundActivity = null;
        _backgroundError = null;
        _tickActive = null;
        _nextTickCheck = null;
        RefreshTickDownloadText();
    }

    private string _tickDownloadText = string.Empty;
    private TickDownloadStatus? _tickActive;   // właśnie pobierana doba ticków
    private DateTime? _nextTickCheck;          // wszystko pobrane (albo błąd) – kiedy następne sprawdzenie
    private string? _backgroundActivity;       // bieżąca czynność pobierania w tle (łączenie, świece)
    private string? _backgroundError;          // błędy ostatniego przebiegu

    /// <summary>
    /// Stan pobierania ticków: czy pobieranie trwa, dla której doby i ile procent, a także ile dni z ustawionych ma już
    /// komplet ticków. W polu informacji tylko, gdy włączono „Pokazuj postęp pobierania” (błąd pobierania – zawsze).
    /// </summary>
    private string TickDownloadText
    {
        get => _tickDownloadText;
        set
        {
            if (_tickDownloadText != value)
            {
                _tickDownloadText = value;
                NotifyInfo();
            }
        }
    }

    /// <summary>Przelicza tekst stanu pobierania ticków (stan pobierania + komplet dni w bazie).</summary>
    private void RefreshTickDownloadText()
    {
        var instruments = _settings.DownloadInstruments;
        if (instruments.Count == 0)
        {
            TickDownloadText = string.Empty;
            return;
        }

        var days = _settings.BackgroundTickDays;
        if (days == 0)
        {
            TickDownloadText = "Ticki: pobieranie wyłączone (Ustawienia → Dane: dni kompletnych ticków = 0) – symulacja korzysta ze świec 1-min";
            return;
        }

        string state;
        if (_backgroundCts is null)
        {
            state = !_settings.RecordToDatabase
                ? "⏸ Pobieranie ticków wyłączone – włącz „Zapisuj dane od brokera do bazy” w Ustawieniach → Dane i broker → Pobieranie i baza"
                : !_settings.UsesInteractiveBrokers
                    ? "⏸ Pobieranie ticków nieaktywne – źródłem danych nie jest Interactive Brokers"
                    : _backgroundError is { } startError
                        ? $"⚠ Pobieranie ticków nie wystartowało – {startError}"
                        : "⏸ Pobieranie ticków – uruchamianie...";
        }
        else if (_tickActive is { } a)
        {
            state = $"⬇ Pobieranie ticków {a.Symbol} – doba {a.Day:dd.MM}: {a.Percent:P0} " +
                    $"(do {TimeZoneInfo.ConvertTime(a.Position, a.Zone):HH:mm}), {a.Ticks:N0} ticków · zostało dób: {a.DaysLeft}";
        }
        else if (_backgroundError is { } error)
        {
            state = $"⚠ Pobieranie w tle – błąd: {error}" + (_nextTickCheck is { } retry ? $"; ponowienie o {retry:HH:mm}" : string.Empty);
        }
        else if (_nextTickCheck is { } next)
        {
            state = $"✓ Ticki pobrane – następne sprawdzenie o {next:HH:mm}";
        }
        else
        {
            state = _backgroundActivity is { } activity ? $"⬇ {activity}" : "⬇ Pobieranie w tle – sprawdzanie brakujących danych...";
        }

        // IBKR Web API nie ma historii ticków – w tle pobierane są tylko świece (1-min i D/W/M).
        if (_settings.LiveSource == LiveSourceKind.InteractiveBrokersWebApi)
        {
            TickDownloadText = $"{state}     |     Ticki: IBKR Web API nie udostępnia historii ticków – symulacja korzysta ze świec 1-min " +
                               "(transakcje na żywo są zapisywane na bieżąco)";
            return;
        }

        try
        {
            var store = GetStore();
            var now = DateTimeOffset.UtcNow;
            var complete = instruments.Select(i =>
            {
                var (done, total) = HistoryGaps.TickDays(store.GetTickCoverage(i.Symbol),
                    now - TimeSpan.FromDays(days), now - TimeSpan.FromMinutes(2));
                return $"{i.Symbol} {done}/{total}";
            });
            TickDownloadText = $"{state}     |     Doby z kompletem ticków: {string.Join(", ", complete)}";
        }
        catch (Exception ex)
        {
            TickDownloadText = $"{state}     |     Nie udało się odczytać bazy: {ex.Message}";
        }
    }

    /// <summary>
    /// Uruchamia źródło danych: łączy się, pobiera historię, buduje z niej profile
    /// i włącza strumień ticków (na żywo albo odtwarzanych w symulacji).
    /// </summary>
    /// <param name="feed">Źródło danych.</param>
    /// <param name="beforeLive">Opcjonalna akcja tuż przed startem strumienia (np. pauza symulacji).</param>
    /// <param name="keepView">
    /// Nie czyścić wykresu na czas wczytywania – nowe profile zastępują stare w jednym kroku (płynne cofanie czasu symulacji).
    /// </param>
    /// <returns>True, jeśli udało się uruchomić.</returns>
    private async Task<bool> StartFeedAsync(IMarketDataFeed feed, Action<IMarketDataFeed>? beforeLive = null, bool keepView = false)
    {
        if (IsRunning)
        {
            return false;
        }

        try
        {
            IsRunning = true;
            _snapshotCache.Clear();
            _tickSizeWarning = null;
            _replayingBarsOnly = false;
            NotifyDataSource();
            if (!keepView)
            {
                _snapshots = [];
                Profiles = [];
            }

            // Nowy silnik profili z aktualnymi ustawieniami. Alarmy dopiero po wczytaniu historii.
            SetAlertsLive(false);
            _builder = new ProfileBuilder(_settings.ToProfileSettings());

            _feed = feed;
            _feed.StatusChanged += OnFeedStatusChanged;
            SubscribeConnectionLoss(_feed);
            if (_feed is DatabaseReplayFeed replay)
            {
                // Symulacja: zegar dla widoku i informacja o końcu danych.
                replay.Completed += OnReplayCompleted;
                replay.Failed += OnReplayFailed;
                replay.TimeChanged += OnSimTimeChanged;
                replay.BarReceived += OnBarReceived;
                replay.DataKindChanged += OnReplayDataKindChanged;
                replay.StopReached += OnSimStopReached;
            }

            // Od tego miejsca pracujemy na lokalnych `feed` i `builder`: po każdym await sprawdzamy (IsCurrent),
            // czy w międzyczasie nie zatrzymano / nie podmieniono źródła (np. zmiana instrumentu w trakcie wczytywania).
            // Bez tego stare wywołanie podpięłoby się do nowego źródła – ticki liczyłyby się podwójnie.
            var builder = _builder;
            await feed.ConnectAsync(_settings.Instrument);
            if (!IsCurrent(feed, builder))
            {
                return false;
            }

            // Rejestrator: dane od brokera zapisujemy w lokalnej bazie do późniejszych symulacji.
            // (Danych z symulatora nie zapisujemy – generator jest losowy, a odtwarzane dane już są w bazie.)
            if (_settings.RecordToDatabase && IsBrokerFeed(feed))
            {
                _recorder = new MarketDataRecorder(GetStore(), _settings.InstrumentSymbol, MarketDataStore.SourceBroker);
                _recorder.WriteFailed += OnRecorderWriteFailed;
                _recorder.WriteRecovered += OnRecorderWriteRecovered;
            }

            // Historia: pobieramy i przetwarzamy w tle, żeby nie blokować okna.
            var recorder = _recorder;

            // Tyle historii, ile potrzeba na ustawioną liczbę profili na ekranie (ale nie mniej niż „Historia (dni)”).
            var historyDays = _settings.RequiredHistoryDays();
            _loadedHistoryDays = historyDays;
            _noOlderData = false;
            CancelHistoryExtension();

            // Na czas wczytywania historii nie wolno doczytywać starszej (IB obsługuje jedno zapytanie o historię naraz).
            _oldestLoaded = null;
            var loadId = BeginHistoryLoad();

            // Broker: najpierw historia z lokalnej bazy (profile widać od razu), z IB tylko brakujący najnowszy kawałek.
            IReadOnlyList<Bar> dbBars = [];
            if (IsBrokerFeed(feed))
            {
                var store = GetStore();
                var symbol = _settings.InstrumentSymbol;
                var now = DateTimeOffset.UtcNow;
                dbBars = await Task.Run(() =>
                {
                    var loaded = BarAggregator.LoadMinuteBars(store, symbol, now - TimeSpan.FromDays(historyDays), now, MarketDataStore.SourceBroker);
                    foreach (var bar in loaded)
                    {
                        builder.ProcessBar(bar);
                    }

                    return loaded;
                });

                if (!IsCurrent(feed, builder))
                {
                    return false;
                }

                if (dbBars.Count > 0)
                {
                    RefreshSnapshots();
                    if (_fitPending)
                    {
                        _fitPending = false;
                        FitRequested?.Invoke();
                    }
                }
            }

            var lastDbBar = dbBars.Count > 0 ? dbBars[^1].Time : (DateTimeOffset?)null;
            var (oldestLoaded, historyEndUtcTicks) = await Task.Run(async () =>
            {
                // Z IB: od ostatniej świecy z bazy (min. 1 doba – IB liczy w dobach), a bez bazy – cała historia.
                var lookback = lastDbBar is { } last
                    ? TimeSpan.FromDays(Math.Min(historyDays, Math.Max(1, (DateTimeOffset.UtcNow - last).TotalDays)))
                    : TimeSpan.FromDays(historyDays);
                var ibBars = await feed.GetHistoryAsync(lookback);
                recorder?.RecordBars(ibBars);

                // Minuty, które są już w bazie (i w profilach), pomijamy – inaczej wolumen liczyłby się dwa razy.
                // Minuty, których w bazie brak (np. przerwa w danych przed restartem połączenia), bierzemy z IB.
                var dbMinutes = dbBars.Select(b => b.Time).ToHashSet();
                var newBars = ibBars.Where(b => !dbMinutes.Contains(b.Time)).ToList();
                foreach (var bar in newBars)
                {
                    builder.ProcessBar(bar);
                }

                var all = dbBars.Concat(newBars).ToList();

                // Koniec historii – ticki starsze od niego są pomijane (te minuty są już w historii).
                // Najstarsza wczytana świeca – od niej w tył doczytujemy historię przy przesuwaniu w lewo.
                return all.Count > 0
                    ? (all.Min(b => b.Time), all.Max(b => b.Time).AddMinutes(1).UtcTicks)
                    : ((DateTimeOffset?)null, 0L);
            });

            if (!IsCurrent(feed, builder))
            {
                return false;
            }

            _historyEndUtcTicks = historyEndUtcTicks;
            _oldestLoaded = oldestLoaded;
            EndHistoryLoad(loadId);

            // Strumień ticków: każdy tick trafia prosto do silnika profili (z wątku źródła danych).
            feed.TickReceived += OnTickReceived;
            beforeLive?.Invoke(feed);
            await feed.StartLiveAsync();

            // Symulacja: najpierw cały odcinek od początku bieżącego profilu do startu (np. po cofnięciu o słupek),
            // dopiero potem pokazujemy wykres – inaczej profil „rysowałby się” od początku sesji na oczach.
            if (feed is DatabaseReplayFeed caughtUpFeed)
            {
                await caughtUpFeed.CaughtUp;
            }

            if (!IsCurrent(feed, builder))
            {
                return false;
            }

            AppLog.Info("Źródło danych", $"działa: {feed.GetType().Name}, {_settings.InstrumentSymbol}, historia {historyDays} dni " +
                                         $"(z bazy {dbBars.Count} min, koniec historii {new DateTimeOffset(historyEndUtcTicks, TimeSpan.Zero).ToLocalTime():yyyy-MM-dd HH:mm})");
            _refreshTimer.Start();
            RefreshSnapshots();
            SetAlertsLive(true); // od teraz przebicia poziomów alarmowych uruchamiają dźwięk
            ClearError(); // źródło danych działa – wcześniejszy błąd (np. brak połączenia) już nieaktualny

            // Pierwsze dane po starcie / po zmianie instrumentu – dopasowanie, żeby wykres był od razu widoczny.
            if (_fitPending)
            {
                _fitPending = false;
                FitRequested?.Invoke();
            }

            return true;
        }
        catch (Exception ex)
        {
            // Źródło zatrzymane / podmienione w trakcie startu – błąd dotyczy starego źródła, nowego nie ruszamy.
            if (!IsCurrent(feed, _builder))
            {
                return false;
            }

            _historyLoadId++;
            _loadingOlder = false;
            AppLog.Error("Źródło danych", $"start źródła {feed.GetType().Name} ({_settings.InstrumentSymbol}) nie powiódł się", ex);
            await StopAsync();
            ShowError($"Błąd: {ex.Message}");
            return false;
        }
    }

    /// <summary>Czy to źródło i silnik profili są nadal bieżące (nie zatrzymane i nie podmienione).</summary>
    private bool IsCurrent(IMarketDataFeed feed, ProfileBuilder? builder) =>
        ReferenceEquals(_feed, feed) && ReferenceEquals(_builder, builder);

    private void OnRecorderWriteFailed(Exception ex)
    {
        AppLog.Error("Baza", "zapis ticków do bazy nie powiódł się (ticki czekają w pamięci)", ex);
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            ShowError($"Zapis do bazy nie działa (ticki czekają w pamięci i zostaną zapisane później): {ex.Message}"));
    }

    private void OnRecorderWriteRecovered() =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Hint("Zapis do bazy działa ponownie"));

    /// <summary>Zatrzymuje pobieranie danych (profile pozostają na ekranie).</summary>
    public async Task StopAsync()
    {
        _refreshTimer.Stop();
        await StopBroadLiveAsync(); // dodatkowe połączenia zakładki „Szeroki rynek” (SPX, SPY)
        SetAlertsLive(false);
        CancelHistoryExtension();

        if (_feed is { } feed)
        {
            AppLog.Info("Źródło danych", $"zatrzymanie: {feed.GetType().Name}, {_settings.InstrumentSymbol}");

            // Najpierw zerujemy pole – trwający start (StartFeedAsync) po swoim await zobaczy, że źródło już nie jest bieżące.
            _feed = null;
            feed.TickReceived -= OnTickReceived;
            // Najpierw odłączamy komunikaty – inaczej "rozłączono" od źródła nadpisałoby opis błędu.
            feed.StatusChanged -= OnFeedStatusChanged;
            UnsubscribeConnectionLoss(feed);
            if (feed is DatabaseReplayFeed replay)
            {
                replay.Completed -= OnReplayCompleted;
                replay.Failed -= OnReplayFailed;
                replay.TimeChanged -= OnSimTimeChanged;
                replay.BarReceived -= OnBarReceived;
                replay.DataKindChanged -= OnReplayDataKindChanged;
                replay.StopReached -= OnSimStopReached;
            }

            try
            {
                await feed.DisposeAsync();
            }
            catch (Exception ex)
            {
                // Błąd przy rozłączaniu nie może zatrzymać sprzątania (rejestrator musi zapisać bufor).
                ShowError($"Błąd przy zatrzymywaniu źródła danych: {ex.Message}");
            }
        }

        IsSimPlaying = false;

        if (_recorder is not null)
        {
            var recorder = _recorder;
            _recorder = null;
            await recorder.DisposeAsync(); // zapisuje ostatnie ticki z bufora
            recorder.WriteFailed -= OnRecorderWriteFailed;
            recorder.WriteRecovered -= OnRecorderWriteRecovered;
            if (recorder.PendingTicks > 0)
            {
                ShowError($"Nie udało się zapisać do bazy {recorder.PendingTicks} ostatnich ticków");
            }
        }

        RefreshSnapshots(); // ostatnie dane na ekran
        IsRunning = false;
        PeriodTimerText = string.Empty; // zegar okresu TPO tylko podczas pobierania/odtwarzania danych
        Interlocked.Exchange(ref _lastTickUtcTicks, 0);
    }

    /// <summary>
    /// Czy źródło to prawdziwy broker (a nie symulator) – tylko takie dane zapisujemy w bazie.
    /// Gdy dodamy Interactive Brokers, ten warunek obejmie go automatycznie.
    /// </summary>
    private static bool IsBrokerFeed(IMarketDataFeed feed) => feed is not SimulatedDataFeed and not DatabaseReplayFeed;

    /// <summary>
    /// Dziennik zdarzeń w folderze z ustawień; gdy folderu nie da się utworzyć (np. brak dostępu, odłączony dysk) –
    /// w folderze domyślnym, z komunikatem.
    /// </summary>
    private void StartLog(string directory)
    {
        if (AppLog.Initialize(directory) || directory == AppSettings.DefaultLogDirectory)
        {
            return;
        }

        AppLog.Initialize(AppSettings.DefaultLogDirectory);
        AppLog.Warning("Dziennik", $"nie można utworzyć folderu dziennika {directory} – zapis w folderze domyślnym");
        ShowError($"Nie można utworzyć folderu dziennika {directory} – dziennik zapisywany w {AppSettings.DefaultLogDirectory}");
    }

    /// <summary>Lokalna baza danych (otwierana przy pierwszym użyciu).</summary>
    public MarketDataStore GetStore() => _store ??= new MarketDataStore(MarketDataStore.DefaultPath);

    /// <summary>
    /// Zapisuje nowe ustawienia. Jeśli zmieniły się parametry danych (np. wysokość wiersza albo godziny sesji),
    /// a dane były pobierane – restartuje pobieranie, bo profile trzeba zbudować od nowa.
    /// Zmiana samego wyglądu (kolory, profil wolumenowy) tylko odświeża wykres.
    /// </summary>
    public async Task ApplySettingsAsync(AppSettings newSettings)
    {
        CancelReconnect();
        if (newSettings.EffectiveLogDirectory != _settings.EffectiveLogDirectory)
        {
            StartLog(newSettings.EffectiveLogDirectory);
        }

        var rebuild = _settings.RequiresRebuild(newSettings);
        AppLog.Info("Ustawienia", $"zmienione – instrument {newSettings.InstrumentSymbol}, źródło {newSettings.LiveSource}" +
                                  (rebuild ? ", profile budowane od nowa" : string.Empty) +
                                  (newSettings.InstrumentSymbol != _settings.InstrumentSymbol ? $" (poprzednio {_settings.InstrumentSymbol})" : string.Empty));
        if (_settings.RequiresDownloadRestart(newSettings))
        {
            await StopBackgroundDownloadAsync(); // wystartuje ponownie po połączeniu z nowymi ustawieniami
        }

        var wasRunning = IsRunning && rebuild;
        if (wasRunning)
        {
            await StopAsync();
        }

        var instrumentChanged = newSettings.InstrumentSymbol != _settings.InstrumentSymbol;

        // Średnie na TPO z innej serii świec (sesja kasowa / cała doba) – wczytanie od nowa; nowa potrzebna seria
        // kasowa – od razu z IB (gdy połączony).
        var tpoAveragesSourceChanged = newSettings.TpoAverages.IncludeNightSession != _settings.TpoAverages.IncludeNightSession;
        if (newSettings.NeedsCashSeries && !_settings.NeedsCashSeries)
        {
            _nextCandleIbRefresh = null;
        }

        _settings = newSettings;
        SettingsStore.Save(newSettings);
        Appearance = ChartStyle.Create(newSettings);
        NotifyDisplayOptions();
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(ControlScale));
        OnPropertyChanged(nameof(Instruments));
        OnPropertyChanged(nameof(SelectedInstrument));
        OnPropertyChanged(nameof(SelectedVolumeMode));
        ComposeViews(); // np. zmieniono w ustawieniach, dla których profili rysować wolumen
        OnPropertyChanged(nameof(TimeZoneLabel));
        OnCandleSettingsChanged();
        NotifyDataSource();
        OnPropertyChanged(nameof(StartStopText));
        OnPropertyChanged(nameof(ShowStartStop));
        if (tpoAveragesSourceChanged && !instrumentChanged) // nowy instrument wczytuje świece sam
        {
            RunSafely(LoadDailyBarsAsync());
        }

        if (instrumentChanged)
        {
            OnCandleInstrumentChanged();
            // Nowy instrument ma zupełnie inne ceny – wykres trzeba dopasować, żeby go nie szukać.
            _fitPending = true;
            FitRequested?.Invoke();

            // Inny instrument = inne linie i rozbicia; stare profile znikają z ekranu.
            _builder = null;
            _snapshotCache.Clear();
            _snapshots = [];
            Profiles = [];
            Summary = string.Empty;
            LoadDrawingsForInstrument();
            RefreshSimDataRange();
        }

        if (wasRunning && IsSimulationMode)
        {
            // Symulacja nie startuje sama – po zmianie ustawień użytkownik naciska ▶ (dane zostaną wczytane od nowa).
            _loadedSimStart = null;
            Hint("Ustawienia zmienione – naciśnij ▶, aby wczytać symulację od nowa");
        }
        else if (wasRunning)
        {
            await StartAsync();
        }
        else
        {
            // Dane od brokera startują same (np. po wybraniu IB w ustawieniach albo po zmianie instrumentu).
            await AutoStartBrokerAsync();
        }

        // Symulacja bez odtwarzania: nowy podgląd (inny instrument – do końca danych, inne ustawienia profili – na czas z pola).
        if (IsSimulationMode && !IsRunning && (instrumentChanged || rebuild))
        {
            await ShowSimPreviewAsync(!instrumentChanged && TryGetSimStart(out var simTime) ? simTime : null);
        }

        // Np. zaznaczono nowe instrumenty do pobierania albo zmieniono ustawienia pobierania (także w trybie symulacji).
        StartBackgroundDownload();

        RefreshTickDownloadText();
    }

    public async ValueTask DisposeAsync()
    {
        CancelReconnect();
        try
        {
            // Pobieranie w tle może czekać na odpowiedź IB (do minuty) – przy zamykaniu nie czekamy dłużej niż kilka sekund.
            await Task.WhenAny(StopBackgroundDownloadAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            // Zatrzymanie źródła i zapis bufora rejestratora – także gdy pobieranie w tle zakończyło się błędem.
            await StopAsync();
            JsonFileStore.Problem -= OnSettingsFileProblem;
        }
    }

    private void OnSettingsFileProblem(string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ShowError(message);
        }
        else
        {
            dispatcher.BeginInvoke(() => ShowError(message));
        }
    }

    // =====================================================================
    // Zegar bieżącego okresu TPO
    // =====================================================================

    /// <summary>Tekst zegara bieżącego okresu TPO wyświetlany na wykresie (pusty = zegar ukryty).</summary>
    public string PeriodTimerText
    {
        get => _periodTimerText;
        private set => SetProperty(ref _periodTimerText, value);
    }

    /// <summary>
    /// Wylicza tekst zegara: litera aktualnie rysowanego okresu TPO, jego przedział czasu
    /// i czas pozostały do końca okresu, np. "TPO F  10:00–10:30  ⏱ 12:34".
    ///
    /// "Teraz" to czas danych, a nie zegar komputera: w symulacji – zegar symulacji (w pauzie stoi),
    /// na żywo – czas ostatniego ticka.
    /// </summary>
    private void UpdatePeriodTimer()
    {
        if (!IsRunning)
        {
            PeriodTimerText = string.Empty;
            return;
        }

        DateTimeOffset now;
        if (SimFeed is { } sim)
        {
            now = sim.CurrentTime;
        }
        else
        {
            var ticks = Interlocked.Read(ref _lastTickUtcTicks);
            if (ticks == 0)
            {
                PeriodTimerText = string.Empty; // jeszcze nie było żadnego ticka
                return;
            }

            now = new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        // Sesja i okres TPO wyznaczane tą samą logiką co w silniku profili (warstwa Core).
        var profileSettings = _builder?.Settings ?? _settings.ToProfileSettings();
        var local = TimeZoneInfo.ConvertTime(now, profileSettings.TimeZone).DateTime;

        foreach (var session in profileSettings.Sessions)
        {
            if (!session.TryGetSessionStart(local, out var sessionStart))
            {
                continue;
            }

            var index = (int)((local - sessionStart).Ticks / profileSettings.TpoPeriod.Ticks);
            var periodStart = sessionStart + index * profileSettings.TpoPeriod;
            var periodEnd = periodStart + profileSettings.TpoPeriod;
            var sessionEnd = sessionStart + session.Duration;
            if (periodEnd > sessionEnd)
            {
                periodEnd = sessionEnd; // ostatni okres sesji może być krótszy
            }

            var remaining = periodEnd - local;
            var remainingText = remaining.TotalHours >= 1
                ? remaining.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : remaining.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

            PeriodTimerText = $"TPO {TpoLetters.ForPeriod(index)}  {periodStart:HH:mm}–{periodEnd:HH:mm}  ⏱ {remainingText}";
            return;
        }

        PeriodTimerText = $"Poza sesją  {local:HH:mm}";
    }

    /// <summary>Koniec wczytanej historii (UTC, ticki) – ticki wcześniejsze są już w historii.</summary>
    private long _historyEndUtcTicks;

    /// <summary>Nowy tick: do silnika profili i (jeśli włączony) do rejestratora bazy danych.</summary>
    private void OnTickReceived(object? sender, Tick tick)
    {
        RouteTickToBroadMarket(tick); // instrument główny to rynek szeroki albo SPY – ticki także dla zakładki „Szeroki rynek”

        // Tick z minuty, która jest już w historii – pomijamy (inaczej TPO i wolumen liczone byłyby dwa razy).
        if (tick.Time.UtcTicks < Interlocked.Read(ref _historyEndUtcTicks))
        {
            return;
        }

        _builder?.ProcessTick(tick);
        _recorder?.Record(tick);

        // Tick spóźniony (uzupełnianie luki po przerwie w danych) – tylko do profilu i bazy: nie cofa zegara okresu TPO
        // i nie podsuwa alarmom starych cen.
        if (tick.Time.UtcTicks < Interlocked.Read(ref _lastTickUtcTicks))
        {
            return;
        }

        TrackAlertPrice(tick.Price, tick.Price, tick.Price);
        Interlocked.Exchange(ref _lastTickUtcTicks, tick.Time.UtcTicks); // czas danych – dla zegara okresu TPO
    }

    /// <summary>
    /// Symulacja: świeca 1-min dla minuty, z której w bazie nie ma ticków – cała minuta trafia do profilu naraz
    /// (bez wymyślania ticków). Czas danych = koniec minuty.
    /// </summary>
    private void OnBarReceived(object? sender, Bar bar)
    {
        if (bar.Time.UtcTicks < Interlocked.Read(ref _historyEndUtcTicks))
        {
            return; // minuta jest już w historii
        }

        _builder?.ProcessBar(bar);
        TrackAlertPrice(bar.Low, bar.High, bar.Close);
        Interlocked.Exchange(ref _lastTickUtcTicks, bar.Time.AddMinutes(1).UtcTicks);
    }

    /// <summary>
    /// Komunikat od źródła danych → pole informacji (tylko problemy; komunikaty rutynowe są pomijane).
    /// Komunikaty od już zatrzymanego źródła (spóźnione) są pomijane.
    /// </summary>
    private void OnFeedStatusChanged(object? sender, string message) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(sender, _feed))
            {
                OnFeedMessage(sender, message);
            }
        });

    // =====================================================================
    // Linie cenowe
    // =====================================================================

    /// <summary>Dodaje nową linię cenową.</summary>
    public void AddLine(PriceLine line)
    {
        line = RearmedAfterEdit(line);
        if (!line.IsAlert)
        {
            _lineTemplate = line;
        }

        Lines = [.. Lines, line];
        SaveDrawings();
    }

    /// <summary>Zastępuje linię o tym samym identyfikatorze (edycja koloru, grubości, ceny...).</summary>
    public void UpdateLine(PriceLine line)
    {
        line = RearmedAfterEdit(line);
        if (!line.IsAlert)
        {
            _lineTemplate = line;
        }

        Lines = Lines.Select(l => l.Id == line.Id ? line : l).ToList();
        SaveDrawings();
    }

    /// <summary>Przesuwa linię na nową cenę (po przeciągnięciu myszą).</summary>
    public void MoveLine(PriceLine line, decimal newPrice)
    {
        _alertMonitor.Rearm(line.Id);
        Lines = Lines.Select(l => l.Id == line.Id
                ? l with { Price = newPrice, Alert = l.Alert is { } a ? a with { IsTriggered = false } : null }
                : l)
            .ToList();
        SaveDrawings();
    }

    /// <summary>Blokuje lub odblokowuje linię (zablokowanej nie da się przesunąć myszą).</summary>
    public void ToggleLineLock(PriceLine line)
    {
        Lines = Lines.Select(l => l.Id == line.Id ? l with { IsLocked = !l.IsLocked } : l).ToList();
        SaveDrawings();
    }

    /// <summary>Blokuje lub odblokowuje wszystkie linie naraz.</summary>
    public void SetAllLinesLocked(bool locked)
    {
        Lines = Lines.Select(l => l with { IsLocked = locked }).ToList();
        SaveDrawings();
    }

    /// <summary>Usuwa linię.</summary>
    public void DeleteLine(PriceLine line)
    {
        Lines = Lines.Where(l => l.Id != line.Id).ToList();
        SaveDrawings();
    }

    // =====================================================================
    // Rozbijanie i łączenie profili
    // =====================================================================

    /// <summary>Rozbija profil tak, że okres <paramref name="periodIndex"/> zaczyna nową część.</summary>
    public void SplitProfile(ProfileSnapshot snapshot, int periodIndex)
    {
        if (FindProfile(snapshot) is { } profile && _splits.Split(profile, periodIndex))
        {
            SaveDrawings();
        }
    }

    /// <summary>Łączy część profilu z poprzednią częścią (cofa rozbicie).</summary>
    public void MergeProfile(ProfileSnapshot snapshot)
    {
        if (FindProfile(snapshot) is { } profile && _splits.MergeWithPrevious(profile, snapshot.FirstPeriod))
        {
            SaveDrawings();
        }
    }

    /// <summary>Znajduje obiekt profilu sesji, z którego powstała migawka.</summary>
    private SessionProfile? FindProfile(ProfileSnapshot snapshot) =>
        _builder?.Profiles.FirstOrDefault(p =>
            p.SessionStart == snapshot.SessionStart && p.Session.Name == snapshot.Session.Name);

    /// <summary>Wczytuje linie i rozbicia dla bieżącego instrumentu.</summary>
    private void LoadDrawingsForInstrument()
    {
        var drawings = _drawings.GetValueOrDefault(_settings.InstrumentSymbol) ?? new InstrumentDrawings();
        Lines = drawings.Lines;
        ResetAlertTracking();
        _expanded.Clear();
        _expanded.UnionWith(drawings.ExpandedProfiles);
        _splits.Load(drawings.Splits);
    }

    /// <summary>Zapisuje linie i rozbicia bieżącego instrumentu do pliku.</summary>
    private void SaveDrawings()
    {
        _drawings[_settings.InstrumentSymbol] = new InstrumentDrawings
        {
            Lines = [.. Lines],
            Splits = [.. _splits.SplitTimes],
            ExpandedProfiles = [.. _expanded.Order()],
        };
        DrawingsStore.Save(_drawings);
    }

    // =====================================================================
    // Odświeżanie migawek
    // =====================================================================

    /// <summary>
    /// Wywoływane co 100 ms na wątku UI: tworzy nowe migawki tylko dla profili, które się zmieniły.
    /// Rozbity profil daje kilka migawek – po jednej na każdą część.
    /// </summary>
    /// <summary>
    /// Odcięcie widoku (płynne cofanie czasu w symulacji): profile są pokazywane tak, jak wyglądały w tej chwili
    /// (null = bez odcięcia). Zmiana wymusza przerysowanie.
    /// </summary>
    private DateTimeOffset? _viewCutoff;
    private bool _viewCutoffChanged;

    private void SetViewCutoff(DateTimeOffset? cutoff)
    {
        _viewCutoff = cutoff;
        _viewCutoffChanged = true;
        RefreshSnapshots();
    }

    private void RefreshSnapshots()
    {
        if (_builder is null)
        {
            return;
        }

        var changed = _viewCutoffChanged;
        _viewCutoffChanged = false;
        var result = new List<ProfileSnapshot>();
        var cutoff = _viewCutoff;

        foreach (var profile in _builder.Profiles)
        {
            // Płynne cofanie czasu symulacji: profil pokazywany tak, jak wyglądał w chwili odcięcia.
            if (cutoff is { } c)
            {
                var local = TimeZoneInfo.ConvertTime(c, profile.Settings.TimeZone).DateTime;
                if (local <= profile.SessionStart)
                {
                    continue; // profil zaczął się po odcięciu
                }

                if (local < profile.SessionEnd)
                {
                    // Okres zaczynający się dokładnie w chwili odcięcia jeszcze nie istniał.
                    result.AddRange(_splits.CreateSnapshots(profile, profile.GetPeriodIndex(local.AddTicks(-1))));
                    changed = true;
                    continue;
                }
            }

            if (!_snapshotCache.TryGetValue(profile, out var cached) || cached.Version != profile.Version)
            {
                cached = (profile.Version, _splits.CreateSnapshots(profile));
                _snapshotCache[profile] = cached;
                changed = true;
            }

            result.AddRange(cached.Parts);
        }

        if (!changed && result.Count == _snapshots.Count)
        {
            return;
        }

        _snapshots = result;
        ComposeViews();
    }

    /// <summary>Uzupełnia pasek z podsumowaniem najnowszego profilu.</summary>
    private void UpdateSummary(ProfileSnapshot? p)
    {
        _summaryProfile = p;
        RefreshSummaryText();
    }

    private ProfileSnapshot? _summaryProfile;

    /// <summary>Tekst paska z podsumowaniem: poziomy najnowszego profilu i tempo budowania TPO.</summary>
    private void RefreshSummaryText()
    {
        var p = _summaryProfile;
        if (p is null)
        {
            Summary = string.Empty;
            return;
        }

        var tick = _settings.Instrument.TickSize;
        string F(decimal? v) => v is { } value ? PriceFormat.Format(value, tick) : "–"; // miejsca po przecinku wg ticka

        Summary = $"{_settings.InstrumentSymbol} | {p.Session.Name} {p.TradingDate:dd.MM}  " +
                  $"Ostatnia: {F(p.LastPrice)}  POC: {F(p.Poc)}  VPOC: {F(p.VolumePoc)}  VAH: {F(p.ValueAreaHigh)}  VAL: {F(p.ValueAreaLow)}  " +
                  $"IBH: {F(p.InitialBalanceHigh)}  IBL: {F(p.InitialBalanceLow)}  " +
                  $"H: {F(p.High)}  L: {F(p.Low)}  TPO: {p.TotalTpo}";
    }
}
