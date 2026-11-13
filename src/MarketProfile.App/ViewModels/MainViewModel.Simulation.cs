using System.Globalization;
using System.Windows.Input;
using MarketProfile.App.Infrastructure;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Tryb symulacji (odtwarzanie danych z lokalnej bazy) – sterowanie z paska w oknie głównym:
///  - wybór momentu startu (data + godzina, czas Nowego Jorku),
///  - ▶ Play / ⏸ Pauza,
///  - ⏭ Krok – przesunięcie czasu o jeden okres TPO (działa także w pauzie),
///  - ⏹ Reset – zatrzymanie symulacji (kolejne ▶ zaczyna od ustawionego startu),
///  - suwak prędkości – zmiana tempa w trakcie odtwarzania, bez restartu.
///
/// Wykres pokazuje dokładnie to, co "wydarzyło się" do bieżącego czasu symulacji: historię sprzed startu
/// oraz profile budowane na bieżąco z odtwarzanych ticków.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Największa prędkość symulacji (×3600 = 1 s to 1 godzina danych).</summary>
    private const double MaxSimSpeed = 3600;

    private bool _isSimulationMode;
    private bool _isSimPlaying;
    private DateTime? _simStartDate;
    private string _simStartTime = "09:30";
    private double _simSpeedSlider = SpeedToSlider(60);
    private string _simClock = "–";
    private string _simDataRange = string.Empty;
    private DateTimeOffset? _loadedSimStart;   // start, od którego wczytano bieżącą symulację
    private ICommand? _simPlayCommand, _simPauseCommand, _simStepCommand, _simResetCommand;

    /// <summary>Bieżąca symulacja (jeśli wczytana).</summary>
    private DatabaseReplayFeed? SimFeed => _feed as DatabaseReplayFeed;

    // =====================================================================
    // Tryb pracy
    // =====================================================================

    /// <summary>Czy aplikacja jest w trybie symulacji (w przeciwnym razie – na żywo).</summary>
    public bool IsSimulationMode
    {
        get => _isSimulationMode;
        set
        {
            if (value == _isSimulationMode)
            {
                return;
            }

            // Tryb zmieniamy od razu (synchronicznie), żeby drugi przycisk opcji nie wywołał przełączenia ponownie.
            _isSimulationMode = value;
            OnPropertyChanged(nameof(IsSimulationMode));
            OnPropertyChanged(nameof(IsLiveMode));
            OnPropertyChanged(nameof(SourceName));
            OnPropertyChanged(nameof(StartStopText));
            OnPropertyChanged(nameof(ShowStartStop));
            _ = SwitchModeAsync(value);
        }
    }

    /// <summary>Czy aplikacja jest w trybie na żywo (przeciwieństwo <see cref="IsSimulationMode"/>).</summary>
    public bool IsLiveMode
    {
        get => !_isSimulationMode;
        set => IsSimulationMode = !value;
    }

    /// <summary>
    /// Przełącza tryb pracy: zatrzymuje bieżące źródło danych i czyści wykres.
    /// </summary>
    private async Task SwitchModeAsync(bool simulation)
    {
        await StopAsync(simulation
            ? "Tryb symulacji – ustaw start i naciśnij ▶"
            : "Tryb na żywo – naciśnij Start");

        _loadedSimStart = null;
        _builder = null;
        _snapshotCache.Clear();
        _snapshots = [];
        Profiles = [];
        Summary = string.Empty;

        _fitPending = true;

        if (simulation)
        {
            InitializeSimulation();
        }
        else
        {
            await AutoStartBrokerAsync(); // dane od brokera – bez naciskania Start
        }
    }

    /// <summary>Wczytuje ostatnio używany start i prędkość oraz informację o danych w bazie.</summary>
    private void InitializeSimulation()
    {
        SimSpeedSlider = SpeedToSlider(_settings.SimSpeed);

        if (_settings.TryParseExchangeTime(_settings.SimStart, out var saved))
        {
            SetSimStart(saved);
        }

        RefreshSimDataRange();
    }

    /// <summary>Odświeża informację o zakresie danych w bazie; jeśli start nie jest ustawiony – proponuje go.</summary>
    private void RefreshSimDataRange()
    {
        if (GetStore().GetDataRange(_settings.InstrumentSymbol) is not { } range)
        {
            SimDataRange = $"Brak danych {_settings.InstrumentSymbol} w bazie – wygeneruj dane testowe w Ustawieniach → Dane.";
            return;
        }

        var zone = _settings.ExchangeTimeZone;
        var from = TimeZoneInfo.ConvertTime(range.From, zone);
        var to = TimeZoneInfo.ConvertTime(range.To, zone);
        SimDataRange = $"Dane w bazie: {from:yyyy-MM-dd HH:mm} – {to:yyyy-MM-dd HH:mm}";

        // Propozycja startu: początek sesji dziennej przedostatniego dnia z danymi.
        if (SimStartDate is null && _settings.TryParseExchangeTime(
                $"{to.Date.AddDays(-1):yyyy-MM-dd} {_settings.Instrument.DayStart:HH\\:mm}", out var proposed))
        {
            SetSimStart(proposed < range.From ? range.From : proposed);
        }
    }

    // =====================================================================
    // Właściwości paska symulacji
    // =====================================================================

    /// <summary>Data startu symulacji (kalendarz).</summary>
    public DateTime? SimStartDate
    {
        get => _simStartDate;
        set => SetProperty(ref _simStartDate, value);
    }

    /// <summary>Godzina startu symulacji "GG:MM" (czas Nowego Jorku).</summary>
    public string SimStartTime
    {
        get => _simStartTime;
        set => SetProperty(ref _simStartTime, value);
    }

    /// <summary>
    /// Pozycja suwaka prędkości (0–100). Skala jest logarytmiczna: 0 = ×1, 50 = ×60, 100 = ×3600 –
    /// dzięki temu suwak jest równie wygodny przy małych i dużych prędkościach.
    /// </summary>
    public double SimSpeedSlider
    {
        get => _simSpeedSlider;
        set
        {
            if (SetProperty(ref _simSpeedSlider, Math.Clamp(value, 0, 100)))
            {
                // Zmiana prędkości działa od razu – także w trakcie odtwarzania.
                if (SimFeed is { } feed)
                {
                    feed.Speed = SimSpeed;
                }

                OnPropertyChanged(nameof(SimSpeedText));
            }
        }
    }

    /// <summary>Prędkość symulacji wynikająca z położenia suwaka.</summary>
    public double SimSpeed => SliderToSpeed(_simSpeedSlider);

    /// <summary>Prędkość do wyświetlenia, np. "×60 (1 s = 1 min)".</summary>
    public string SimSpeedText
    {
        get
        {
            var speed = SimSpeed;
            var perSecond = TimeSpan.FromSeconds(speed);
            var description = perSecond.TotalMinutes >= 1
                ? perSecond.TotalHours >= 1 ? $"{perSecond.TotalHours:0.#} h" : $"{perSecond.TotalMinutes:0.#} min"
                : $"{perSecond.TotalSeconds:0} s";
            return $"×{speed:0}  (1 s = {description})";
        }
    }

    /// <summary>Bieżący czas symulacji (zegar).</summary>
    public string SimClock
    {
        get => _simClock;
        private set => SetProperty(ref _simClock, value);
    }

    /// <summary>Zakres danych dostępnych w bazie dla bieżącego instrumentu.</summary>
    public string SimDataRange
    {
        get => _simDataRange;
        private set => SetProperty(ref _simDataRange, value);
    }

    /// <summary>Czy symulacja jest odtwarzana (nie wstrzymana).</summary>
    public bool IsSimPlaying
    {
        get => _isSimPlaying;
        private set
        {
            if (SetProperty(ref _isSimPlaying, value))
            {
                OnPropertyChanged(nameof(CanSimPlay));
            }
        }
    }

    /// <summary>Czy przycisk ▶ jest aktywny.</summary>
    public bool CanSimPlay => !IsSimPlaying;

    // =====================================================================
    // Komendy: ▶ ⏸ ⏭ ⏹
    // =====================================================================

    public ICommand SimPlayCommand => _simPlayCommand ??= new AsyncRelayCommand(PlayAsync);
    public ICommand SimPauseCommand => _simPauseCommand ??= new AsyncRelayCommand(PauseAsync);
    public ICommand SimStepCommand => _simStepCommand ??= new AsyncRelayCommand(StepAsync);
    public ICommand SimResetCommand => _simResetCommand ??= new AsyncRelayCommand(ResetAsync);

    /// <summary>
    /// ▶ Play. Jeśli symulacja jest wczytana od tego samego startu – wznawia ją.
    /// W przeciwnym razie wczytuje dane od nowa: historię sprzed startu i odtwarzanie od startu.
    /// </summary>
    private async Task PlayAsync()
    {
        if (!TryGetSimStart(out var start))
        {
            return;
        }

        if (SimFeed is { } loaded && _loadedSimStart == start)
        {
            loaded.Resume();
            IsSimPlaying = true;
            return;
        }

        if (await LoadSimulationAsync(start, paused: false))
        {
            IsSimPlaying = true;
        }
    }

    /// <summary>⏸ Pauza – zatrzymuje zegar symulacji (wykres zostaje w bieżącym stanie).</summary>
    private Task PauseAsync()
    {
        SimFeed?.Pause();
        IsSimPlaying = false;
        SaveSimSettings();
        return Task.CompletedTask;
    }

    /// <summary>
    /// ⏭ Krok – przesuwa czas symulacji o jeden okres TPO. Działa w pauzie (przegląd okres po okresie).
    /// Jeśli symulacja nie jest wczytana – wczytuje ją w pauzie i wykonuje krok.
    /// </summary>
    private async Task StepAsync()
    {
        if (!TryGetSimStart(out var start))
        {
            return;
        }

        if (SimFeed is null || _loadedSimStart != start)
        {
            if (!await LoadSimulationAsync(start, paused: true))
            {
                return;
            }
        }

        SimFeed?.Step(TimeSpan.FromMinutes(_settings.TpoMinutes));
    }

    /// <summary>⏹ Reset – zatrzymuje symulację; kolejne ▶ zacznie od ustawionego startu.</summary>
    private async Task ResetAsync()
    {
        SaveSimSettings();
        await StopAsync("Symulacja zatrzymana – ▶ zaczyna od ustawionego startu");
        _loadedSimStart = null;
        SimClock = TryGetSimStart(out var start) ? FormatSimTime(start) : "–"; // zegar wraca do punktu startu
    }

    /// <summary>
    /// Wczytuje symulację od podanego startu do końca danych w bazie.
    /// </summary>
    private async Task<bool> LoadSimulationAsync(DateTimeOffset start, bool paused)
    {
        await StopAsync("Wczytywanie symulacji...");

        if (GetStore().GetDataRange(_settings.InstrumentSymbol) is not { } range)
        {
            Status = $"Brak danych {_settings.InstrumentSymbol} w bazie – wygeneruj dane testowe w Ustawieniach → Dane.";
            return false;
        }

        SaveSimSettings();

        var feed = new DatabaseReplayFeed(GetStore(), start, range.To.AddMinutes(1), SimSpeed, _settings.ExchangeTimeZone);
        if (paused)
        {
            feed.Pause();
        }

        if (!await StartFeedAsync(feed))
        {
            return false;
        }

        _loadedSimStart = start;
        SimClock = FormatSimTime(start);
        return true;
    }

    // =====================================================================
    // Pomocnicze
    // =====================================================================

    /// <summary>
    /// Zegar symulacji – zdarzenie z wątku odtwarzania przekazujemy do wątku okna.
    /// Komunikaty od już zatrzymanej symulacji (spóźnione) są pomijane.
    /// </summary>
    private void OnSimTimeChanged(object? sender, DateTimeOffset time) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(sender, _feed))
            {
                SimClock = FormatSimTime(time);
            }
        });

    /// <summary>Koniec danych w bazie – symulacja zatrzymuje się, profile zostają na ekranie.</summary>
    private void OnReplayCompleted(object? sender, EventArgs e) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            var message = Status; // komunikat "Symulacja zakończona..." od źródła danych
            await StopAsync(message);
            _loadedSimStart = null;
        });

    /// <summary>Odczytuje moment startu z pól daty i godziny.</summary>
    private bool TryGetSimStart(out DateTimeOffset start)
    {
        start = default;
        if (SimStartDate is not { } date ||
            !_settings.TryParseExchangeTime($"{date:yyyy-MM-dd} {SimStartTime?.Trim()}", out start))
        {
            Status = "Ustaw datę i godzinę startu symulacji (godzina w formacie GG:MM, czas Nowego Jorku).";
            return false;
        }

        return true;
    }

    /// <summary>Ustawia pola daty i godziny startu.</summary>
    private void SetSimStart(DateTimeOffset start)
    {
        var local = TimeZoneInfo.ConvertTime(start, _settings.ExchangeTimeZone);
        SimStartDate = local.Date;
        SimStartTime = local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Zapamiętuje start i prędkość symulacji w ustawieniach (wrócą po ponownym uruchomieniu).</summary>
    private void SaveSimSettings()
    {
        var simStart = SimStartDate is { } date ? $"{date:yyyy-MM-dd} {SimStartTime?.Trim()}" : string.Empty;
        if (simStart == _settings.SimStart && SimSpeed == _settings.SimSpeed)
        {
            return;
        }

        _settings = _settings with { SimStart = simStart, SimSpeed = SimSpeed };
        MarketProfile.App.Settings.SettingsStore.Save(_settings);
    }

    private string FormatSimTime(DateTimeOffset time) =>
        TimeZoneInfo.ConvertTime(time, _settings.ExchangeTimeZone).ToString("yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Pozycja suwaka (0–100) → prędkość (×1 … ×3600), skala logarytmiczna.</summary>
    private static double SliderToSpeed(double slider) => Math.Round(Math.Pow(MaxSimSpeed, slider / 100));

    /// <summary>Prędkość → pozycja suwaka (odwrotność <see cref="SliderToSpeed"/>).</summary>
    private static double SpeedToSlider(double speed) =>
        100 * Math.Log(Math.Clamp(speed, 1, MaxSimSpeed)) / Math.Log(MaxSimSpeed);
}
