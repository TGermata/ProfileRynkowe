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
    private string? _ibContractName;           // np. "ESZ6" – po połączeniu z IB
    private int _loadedHistoryDays;            // ile dni historii wczytano przy ostatnim starcie
    private DateTimeOffset? _oldestLoaded;     // czas najstarszej wczytanej świecy
    private bool _loadingOlder;                // trwa doczytywanie starszej historii
    private bool _noOlderData;                 // źródło nie ma już starszych danych
    private long _lastTickUtcTicks;          // czas ostatniego ticka (UTC) – "teraz" w danych na żywo
    private string _periodTimerText = string.Empty;
    private MarketDataRecorder? _recorder;
    private IMarketDataFeed? _feed;
    private ProfileBuilder? _builder;
    private IReadOnlyList<ProfileSnapshot> _snapshots = [];   // wszystkie migawki (dane)
    private IReadOnlyList<ProfileView> _profiles = [];        // migawki przygotowane do wyświetlenia

    // Profile wyświetlane w trybie Split/Expanded (czas rozpoczęcia profilu/części).
    private readonly HashSet<DateTime> _expanded = [];
    private bool _isRunning;
    private string _status = "Gotowy";
    private string _summary = string.Empty;

    public MainViewModel()
    {
        _settings = SettingsStore.Load();
        _appearance = ChartStyle.Create(_settings);
        _drawings = DrawingsStore.Load();
        LoadDrawingsForInstrument();

        // Po zmianie rozbić przebudowujemy migawki od razu (także gdy dane nie są pobierane).
        _splits.Changed += (_, _) =>
        {
            _snapshotCache.Clear();
            RefreshSnapshots();
        };

        StartStopCommand = new AsyncRelayCommand(() => IsRunning ? StopAsync() : StartAsync());

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshSnapshots();
            UpdatePeriodTimer();
        };
    }

    /// <summary>Bieżące ustawienia aplikacji.</summary>
    public AppSettings Settings => _settings;

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

            _ = ApplySettingsAsync(_settings with { InstrumentSymbol = value.Symbol });
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
            _ = EnsureHistoryForProfilesAsync();
        }
    }

    /// <summary>
    /// Sprawdza, czy wczytana historia wystarcza na ustawioną liczbę profili; jeśli nie – wczytuje więcej.
    /// </summary>
    private async Task EnsureHistoryForProfilesAsync()
    {
        if (!IsRunning || _settings.RequiredHistoryDays() <= _loadedHistoryDays)
        {
            return;
        }

        if (IsSimulationMode)
        {
            // W symulacji nie przerywamy odtwarzania samoczynnie – użytkownik decyduje.
            _loadedSimStart = null;
            Status = $"Na {ProfilesOnScreen} profili potrzeba {_settings.RequiredHistoryDays()} dni historii – naciśnij ▶, aby wczytać symulację od nowa";
            return;
        }

        Status = $"Wczytywanie dłuższej historii ({_settings.RequiredHistoryDays()} dni) dla {ProfilesOnScreen} profili...";
        await StopAsync(Status);
        await StartAsync();
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

        _loadingOlder = true;
        var recorder = _recorder;
        var from = to - OlderHistoryStep;
        var zone = _settings.ExchangeTimeZone;
        Status = $"Doczytywanie starszej historii ({TimeZoneInfo.ConvertTime(from, zone):dd.MM} – {TimeZoneInfo.ConvertTime(to, zone):dd.MM})...";

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
                Status = "Brak starszych danych w źródle";
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

            _oldestLoaded = bars.Min(b => b.Time);
            Status = $"Doczytano {bars.Count} świec – historia od {TimeZoneInfo.ConvertTime(_oldestLoaded.Value, zone):dd.MM.yyyy}";
            RefreshSnapshots();
        }
        catch (Exception ex)
        {
            _noOlderData = true; // nie ponawiamy w kółko po błędzie
            Status = $"Nie udało się doczytać starszej historii: {ex.Message}";
        }
        finally
        {
            _loadingOlder = false;
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
                                  !_settings.HiddenSessions.Contains(s.Session.Name)))
            .ToList();

        UpdateSummary(latest);
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
    public bool IsBrokerLive => !IsSimulationMode && _settings.LiveSource == LiveSourceKind.InteractiveBrokers;

    /// <summary>
    /// Czy pokazać przycisk Start/Stop: dla generatora – zawsze w trybie na żywo; dla brokera – tylko gdy
    /// połączenie nie działa (np. TWS był wyłączony), jako „⟳ Połącz” do ponownej próby.
    /// </summary>
    public bool ShowStartStop => IsLiveMode && (!IsBrokerLive || !IsRunning);

    /// <summary>Prośba do widoku o dopasowanie wykresu (np. po zmianie instrumentu – inne ceny, inne profile).</summary>
    public event Action? FitRequested;

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

    /// <summary>Komunikat stanu (pasek statusu).</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
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
    public Task StartAsync() => _settings.LiveSource == LiveSourceKind.InteractiveBrokers
        ? StartInteractiveBrokersAsync()
        : StartFeedAsync(new SimulatedDataFeed { Speed = _settings.SimulatorSpeed });

    /// <summary>
    /// Start Interactive Brokers: najpierw wskazujemy bibliotekę TWS API ze ścieżki z ustawień,
    /// dopiero potem tworzymy źródło danych (które z tej biblioteki korzysta).
    /// </summary>
    private async Task StartInteractiveBrokersAsync()
    {
        try
        {
            IbApiLoader.Use(string.IsNullOrWhiteSpace(_settings.IbApiPath) ? IbApiLoader.AutoDetect() : _settings.IbApiPath);
        }
        catch (Exception ex)
        {
            Status = $"Błąd biblioteki TWS API: {ex.Message} – sprawdź ścieżkę w Ustawieniach → Interactive Brokers.";
            return;
        }

        if (await StartFeedAsync(IbFeeds.Create(_settings.ToIbSettings())))
        {
            _ibContractName = IbFeeds.ContractName(_feed);
            OnPropertyChanged(nameof(SourceName));
        }
    }

    /// <summary>
    /// Uruchamia źródło danych: łączy się, pobiera historię, buduje z niej profile
    /// i włącza strumień ticków (na żywo albo odtwarzanych w symulacji).
    /// </summary>
    /// <param name="feed">Źródło danych.</param>
    /// <param name="beforeLive">Opcjonalna akcja tuż przed startem strumienia (np. pauza symulacji).</param>
    /// <returns>True, jeśli udało się uruchomić.</returns>
    private async Task<bool> StartFeedAsync(IMarketDataFeed feed, Action<IMarketDataFeed>? beforeLive = null)
    {
        if (IsRunning)
        {
            return false;
        }

        try
        {
            IsRunning = true;
            _snapshotCache.Clear();
            _snapshots = [];
            Profiles = [];

            // Nowy silnik profili z aktualnymi ustawieniami.
            _builder = new ProfileBuilder(_settings.ToProfileSettings());

            _feed = feed;
            _feed.StatusChanged += OnFeedStatusChanged;
            if (_feed is DatabaseReplayFeed replay)
            {
                // Symulacja: zegar dla widoku i informacja o końcu danych.
                replay.Completed += OnReplayCompleted;
                replay.TimeChanged += OnSimTimeChanged;
            }

            await _feed.ConnectAsync(_settings.Instrument);

            // Rejestrator: dane od brokera zapisujemy w lokalnej bazie do późniejszych symulacji.
            // (Danych z symulatora nie zapisujemy – generator jest losowy, a odtwarzane dane już są w bazie.)
            if (_settings.RecordToDatabase && IsBrokerFeed(_feed))
            {
                _recorder = new MarketDataRecorder(GetStore(), _settings.InstrumentSymbol, MarketDataStore.SourceBroker);
            }

            // Historia: pobieramy i przetwarzamy w tle, żeby nie blokować okna.
            Status = "Pobieranie historii...";
            var builder = _builder;
            var recorder = _recorder;

            // Tyle historii, ile potrzeba na ustawioną liczbę profili na ekranie (ale nie mniej niż „Historia (dni)”).
            var historyDays = _settings.RequiredHistoryDays();
            _loadedHistoryDays = historyDays;
            _noOlderData = false;
            _oldestLoaded = await Task.Run(async () =>
            {
                var bars = await feed.GetHistoryAsync(TimeSpan.FromDays(historyDays));
                recorder?.RecordBars(bars);
                foreach (var bar in bars)
                {
                    builder.ProcessBar(bar);
                }

                // Koniec historii – ticki starsze od niego są pomijane (te minuty są już w historii).
                _historyEndUtcTicks = bars.Count > 0 ? bars.Max(b => b.Time).AddMinutes(1).UtcTicks : 0;

                // Najstarsza wczytana świeca – od niej w tył doczytujemy historię przy przesuwaniu w lewo.
                return bars.Count > 0 ? bars.Min(b => b.Time) : (DateTimeOffset?)null;
            });

            // Strumień ticków: każdy tick trafia prosto do silnika profili (z wątku źródła danych).
            _feed.TickReceived += OnTickReceived;
            beforeLive?.Invoke(_feed);
            await _feed.StartLiveAsync();

            _refreshTimer.Start();
            RefreshSnapshots();

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
            await StopAsync($"Błąd: {ex.Message}");
            return false;
        }
    }

    /// <summary>Zatrzymuje pobieranie danych (profile pozostają na ekranie).</summary>
    public async Task StopAsync() => await StopAsync("Zatrzymano");

    private async Task StopAsync(string status)
    {
        _refreshTimer.Stop();

        if (_feed is not null)
        {
            _feed.TickReceived -= OnTickReceived;
            // Najpierw odłączamy komunikaty – inaczej "rozłączono" od źródła nadpisałoby opis błędu.
            _feed.StatusChanged -= OnFeedStatusChanged;
            await _feed.DisposeAsync();
            if (_feed is DatabaseReplayFeed replay)
            {
                replay.Completed -= OnReplayCompleted;
                replay.TimeChanged -= OnSimTimeChanged;
            }

            _feed = null;
        }

        IsSimPlaying = false;

        if (_recorder is not null)
        {
            await _recorder.DisposeAsync(); // zapisuje ostatnie ticki z bufora
            _recorder = null;
        }

        RefreshSnapshots(); // ostatnie dane na ekran
        IsRunning = false;
        Status = status;
        PeriodTimerText = string.Empty; // zegar okresu TPO tylko podczas pobierania/odtwarzania danych
        Interlocked.Exchange(ref _lastTickUtcTicks, 0);
    }

    /// <summary>
    /// Czy źródło to prawdziwy broker (a nie symulator) – tylko takie dane zapisujemy w bazie.
    /// Gdy dodamy Interactive Brokers, ten warunek obejmie go automatycznie.
    /// </summary>
    private static bool IsBrokerFeed(IMarketDataFeed feed) => feed is not SimulatedDataFeed and not DatabaseReplayFeed;

    /// <summary>Lokalna baza danych (otwierana przy pierwszym użyciu).</summary>
    public MarketDataStore GetStore() => _store ??= new MarketDataStore(MarketDataStore.DefaultPath);

    /// <summary>Nazwa źródła danych wyświetlana na pasku narzędzi.</summary>
    public string SourceName => IsSimulationMode
        ? "Źródło: lokalna baza danych"
        : _settings.LiveSource == LiveSourceKind.InteractiveBrokers
            ? $"Źródło: Interactive Brokers {_ibContractName} ({_settings.IbHost}:{_settings.IbPort}, dane {_settings.ToIbSettings().DataTypeName})"
            : $"Źródło: generator (×{_settings.SimulatorSpeed})";

    /// <summary>
    /// Zapisuje nowe ustawienia. Jeśli zmieniły się parametry danych (np. wysokość wiersza albo godziny sesji),
    /// a dane były pobierane – restartuje pobieranie, bo profile trzeba zbudować od nowa.
    /// Zmiana samego wyglądu (kolory, profil wolumenowy) tylko odświeża wykres.
    /// </summary>
    public async Task ApplySettingsAsync(AppSettings newSettings)
    {
        var rebuild = _settings.RequiresRebuild(newSettings);
        var wasRunning = IsRunning && rebuild;
        if (wasRunning)
        {
            await StopAsync();
        }

        var instrumentChanged = newSettings.InstrumentSymbol != _settings.InstrumentSymbol;

        _settings = newSettings;
        SettingsStore.Save(newSettings);
        Appearance = ChartStyle.Create(newSettings);
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(Instruments));
        OnPropertyChanged(nameof(SelectedInstrument));
        OnPropertyChanged(nameof(SelectedVolumeMode));
        ComposeViews(); // np. zmieniono w ustawieniach, dla których profili rysować wolumen
        OnPropertyChanged(nameof(TimeZoneLabel));
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(StartStopText));
        OnPropertyChanged(nameof(ShowStartStop));

        if (instrumentChanged)
        {
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
            Status = "Ustawienia zmienione – naciśnij ▶, aby wczytać symulację od nowa";
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
    }

    public async ValueTask DisposeAsync() => await StopAsync();

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
        // Tick z minuty, która jest już w historii – pomijamy (inaczej TPO i wolumen liczone byłyby dwa razy).
        if (tick.Time.UtcTicks < Interlocked.Read(ref _historyEndUtcTicks))
        {
            return;
        }

        _builder?.ProcessTick(tick);
        Interlocked.Exchange(ref _lastTickUtcTicks, tick.Time.UtcTicks); // czas danych – dla zegara okresu TPO
        _recorder?.Record(tick);
    }

    /// <summary>
    /// Komunikat od źródła danych → pasek statusu. Komunikaty od już zatrzymanego źródła (spóźnione)
    /// są pomijane, żeby nie zasłoniły np. opisu błędu połączenia.
    /// </summary>
    private void OnFeedStatusChanged(object? sender, string message) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(sender, _feed))
            {
                Status = message;
            }
        });

    // =====================================================================
    // Linie cenowe
    // =====================================================================

    /// <summary>Dodaje nową linię cenową.</summary>
    public void AddLine(PriceLine line)
    {
        _lineTemplate = line;
        Lines = [.. Lines, line];
        SaveDrawings();
    }

    /// <summary>Zastępuje linię o tym samym identyfikatorze (edycja koloru, grubości, ceny...).</summary>
    public void UpdateLine(PriceLine line)
    {
        _lineTemplate = line;
        Lines = Lines.Select(l => l.Id == line.Id ? line : l).ToList();
        SaveDrawings();
    }

    /// <summary>Przesuwa linię na nową cenę (po przeciągnięciu myszą).</summary>
    public void MoveLine(PriceLine line, decimal newPrice)
    {
        Lines = Lines.Select(l => l.Id == line.Id ? l with { Price = newPrice } : l).ToList();
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
    private void RefreshSnapshots()
    {
        if (_builder is null)
        {
            return;
        }

        var changed = false;
        var result = new List<ProfileSnapshot>();

        foreach (var profile in _builder.Profiles)
        {
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
