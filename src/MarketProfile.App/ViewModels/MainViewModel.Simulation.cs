using System.Globalization;
using System.Windows.Input;
using MarketProfile.App.Infrastructure;
using MarketProfile.App.Settings;
using MarketProfile.Core;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Tryb symulacji (odtwarzanie danych z lokalnej bazy) – sterowanie z paska w oknie głównym:
///  - pole czasu (data + godzina) – pokazuje czas aktualnie rysowanego TPO; wpisanie innego czasu dorysowuje
///    płynnie (w przód) części profili albo od razu chowa (wstecz) profile po tym momencie; nowa data/godzina = „Dopasuj”
///    i bieżący profil od 0,3 szerokości ekranu,
///  - ▶ Play / ⏸ Pauza,
///  - ◄ / ► – słupek wstecz / w przód – przesunięcie czasu o jeden okres TPO wstecz / w przód (działa także w pauzie),
///  - ⏮ Profil / Profil ⏭ – przeskok do początku bieżącego (poprzedniego) / następnego profilu,
///  - ⏹ Reset – pauza i powrót do momentu, od którego wczytano symulację,
///  - suwak prędkości – zmiana tempa w trakcie odtwarzania, bez restartu,
///  - czas końca (opcjonalny) – po dojściu do niego odtwarzanie się wstrzymuje; puste = bez ograniczeń.
///
/// Przejście do trybu „Na żywo” wstrzymuje symulację i zapamiętuje dokładny moment – po powrocie wykres
/// pokazuje stan z tej chwili (w pauzie), a ▶ rysuje dalej od tego miejsca.
///
/// Wykres pokazuje dokładnie to, co "wydarzyło się" do bieżącego czasu symulacji: historię sprzed startu
/// oraz profile budowane na bieżąco z odtwarzanych ticków – prawdziwych transakcji z giełdy, przesuniętych w czasie.
/// Minuty, z których w bazie nie ma ticków, dochodzą do profilu całymi świecami 1-min (z wyraźnym ostrzeżeniem).
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
    private string? _simSource; // źródło danych symulacji wybranego instrumentu (MarketDataStore.SimulationSource)
    private DateTimeOffset? _loadedSimStart;   // moment, od którego wczytano bieżącą symulację (powrót przez ⏹)
    private bool _simNavigating;               // trwa płynne przewijanie czasu
    private bool _settingSimTimeFromCode;      // pole czasu ustawia program (zegar symulacji), nie użytkownik
    private bool _isEditingSimTime;            // użytkownik edytuje pole godziny – nie nadpisujemy go zegarem
    private string? _simTimeTextAtEditStart;   // treść pola godziny w chwili rozpoczęcia edycji
    private string? _tickSizeWarning;          // niezgodny tick instrumentu z tickiem kontraktu w IB
    private bool _replayingBarsOnly;           // symulacja odtwarza okres bez ticków (tylko świece 1-min)

    /// <summary>Odstęp między klatkami płynnego przewijania.</summary>
    private static readonly TimeSpan SimAnimationFrame = TimeSpan.FromMilliseconds(40);

    /// <summary>Najwięcej klatek płynnego przewijania (dłuższe odcinki – kilka okresów TPO na klatkę).</summary>
    private const int SimAnimationMaxFrames = 40;
    private ICommand? _simPlayPauseCommand;
    private ICommand? _simPlayCommand, _simPauseCommand, _simStepCommand, _simStepBackCommand, _simResetCommand;
    private ICommand? _simPrevProfileCommand, _simNextProfileCommand, _simClearEndCommand;

    private DateTime? _simEndDate;             // dzień końca symulacji (null = bez ograniczeń)
    private string _simEndTime = string.Empty; // godzina końca "GG:MM" (pusta = bez ograniczeń)
    private bool _loadingSimEnd;               // pola końca ustawia program (wczytanie ustawień) – bez zapisu

    /// <summary>
    /// Miejsce, w którym symulację przerwano przejściem do trybu „Na żywo”.
    /// <paramref name="Loaded"/> – czy symulacja była wczytana (false = tylko podgląd na wybraną chwilę);
    /// <paramref name="Origin"/> – moment, od którego ją wczytano (powrót przez ⏹).
    /// </summary>
    private sealed record SimResumePoint(string Symbol, DateTimeOffset At, bool Loaded, DateTimeOffset? Origin);

    private SimResumePoint? _simResume;

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
            NotifyDataSource();
            OnPropertyChanged(nameof(StartStopText));
            OnPropertyChanged(nameof(ShowStartStop));
            RunSafely(SwitchModeAsync(value), "Nie udało się przełączyć trybu");
        }
    }

    /// <summary>Czy aplikacja jest w trybie na żywo (przeciwieństwo <see cref="IsSimulationMode"/>).</summary>
    public bool IsLiveMode
    {
        get => !_isSimulationMode;
        set => IsSimulationMode = !value;
    }

    /// <summary>
    /// Przełącza tryb pracy: zatrzymuje bieżące źródło danych.
    ///  - symulacja: wykres nie jest czyszczony – od razu pokazuje profile jak na żywo (podgląd ze świec z bazy),
    ///  - na żywo: wykres jest czyszczony i dane od brokera wczytują się od nowa.
    /// </summary>
    private async Task SwitchModeAsync(bool simulation)
    {
        CancelReconnect();
        if (!simulation)
        {
            RememberSimPoint(); // przed zatrzymaniem – potem symulacji już nie ma
        }

        await StopAsync();
        ClearError(); // błąd poprzedniego trybu (np. brak połączenia z TWS) nie dotyczy nowego

        _loadedSimStart = null;
        _previewFrom = _previewEnd = null;
        SetViewCutoff(null);

        if (simulation)
        {
            InitializeSimulation();
            StartBackgroundDownload(); // ticki pobierają się także w trakcie symulacji (jeśli pobieranie jeszcze nie działa)
            if (!await ResumeSimAsync())
            {
                await ShowSimPreviewAsync(null);
            }

            return;
        }

        _builder = null;
        _snapshotCache.Clear();
        _snapshots = [];
        Profiles = [];
        Summary = string.Empty;
        _fitPending = true;

        await AutoStartBrokerAsync(); // dane od brokera – bez naciskania Start
    }

    /// <summary>
    /// Przejście do trybu „Na żywo”: wstrzymuje symulację i zapamiętuje dokładny moment (także w ustawieniach,
    /// żeby przetrwał ponowne uruchomienie). Podgląd zapamiętujemy tylko, gdy wybrano konkretną chwilę.
    /// </summary>
    private void RememberSimPoint()
    {
        var symbol = _settings.InstrumentSymbol;
        SimResumePoint? point = null;

        if (SimFeed is { } feed)
        {
            feed.Pause();
            point = new SimResumePoint(symbol, feed.CurrentTime, Loaded: true, _loadedSimStart);
        }
        else if (_viewCutoff is { } cutoff)
        {
            point = new SimResumePoint(symbol, cutoff, Loaded: false, Origin: null);
        }

        IsSimPlaying = false;
        if (point is null)
        {
            return;
        }

        _simResume = point;
        SaveSimSettings();
        if (point.Loaded)
        {
            _settings = _settings with
            {
                SimResumeAt = point.At.ToString("o", CultureInfo.InvariantCulture),
                SimResumeSymbol = symbol,
            };
            MarketProfile.App.Settings.SettingsStore.Save(_settings);
        }
    }

    /// <summary>
    /// Powrót do symulacji: jeśli zapamiętano miejsce przerwania (dla tego instrumentu i w zakresie danych) –
    /// wczytuje symulację dokładnie od niego, w pauzie (rysowanie rusza po ▶). False = nie ma do czego wracać.
    /// </summary>
    private async Task<bool> ResumeSimAsync()
    {
        var symbol = _settings.InstrumentSymbol;
        var point = _simResume;
        if (point is null && _settings.SimResumeSymbol == symbol && _settings.ParseSimResumeAt() is { } saved)
        {
            point = new SimResumePoint(symbol, saved, Loaded: true, Origin: null); // po ponownym uruchomieniu aplikacji
        }

        if (point is null || point.Symbol != symbol ||
            SimDataRangeOf(symbol) is not { } range || point.At < range.From || point.At >= range.To)
        {
            return false;
        }

        if (!point.Loaded)
        {
            await ShowSimPreviewAsync(point.At);
            return true;
        }

        if (!await LoadSimulationAsync(point.At, paused: true))
        {
            return false;
        }

        _loadedSimStart = point.Origin ?? point.At;
        IsSimPlaying = false;
        return true;
    }

    /// <summary>Wczytuje ostatnio używany start, prędkość i czas końca oraz informację o danych w bazie.</summary>
    private void InitializeSimulation()
    {
        SimSpeedSlider = SpeedToSlider(_settings.SimSpeed);

        if (_settings.TryParseExchangeTime(_settings.SimStart, out var saved))
        {
            SetSimStart(saved);
        }

        LoadSimEnd();
        RefreshSimDataRange();
    }

    /// <summary>
    /// Sprawdza, skąd i czy są dane do symulacji (brak danych – ostrzeżenie w polu informacji);
    /// jeśli start nie jest ustawiony – proponuje go.
    /// </summary>
    private void RefreshSimDataRange()
    {
        _simSource = GetStore().SimulationSource(_settings.InstrumentSymbol);
        NotifyDataSource();

        if (SimDataRangeOf(_settings.InstrumentSymbol) is not { } range)
        {
            return;
        }

        var to = TimeZoneInfo.ConvertTime(range.To, _settings.ExchangeTimeZone);

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
        set
        {
            if (SetProperty(ref _simStartDate, value) && !_settingSimTimeFromCode)
            {
                RunSafely(CommitSimTimeAsync()); // dzień zmieniony przez użytkownika – przejście do tego momentu
            }
        }
    }

    /// <summary>
    /// Godzina "GG:MM" (w strefie instrumentu). W trakcie symulacji pokazuje czas aktualnie rysowanego TPO;
    /// wpisanie innej godziny (Enter albo wyjście z pola) przenosi symulację do tego momentu.
    /// </summary>
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
                OnPropertyChanged(nameof(SimSpeedShortText));
                OnPropertyChanged(nameof(SelectedSimSpeedPreset));
            }
        }
    }

    /// <summary>Prędkość symulacji wynikająca z położenia suwaka.</summary>
    public double SimSpeed => SliderToSpeed(_simSpeedSlider);

    /// <summary>Prędkość do wyświetlenia, np. "×60 (1 s = 1 min)".</summary>
    public string SimSpeedText => FormatSimSpeed(SimSpeed);

    /// <summary>Prędkość krótko, np. "×60" – w polu listy (pełny opis na liście i w podpowiedzi).</summary>
    public string SimSpeedShortText => $"×{SimSpeed:0}";

    /// <summary>Gotowa prędkość symulacji do wyboru z listy (i krok strzałek przy suwaku).</summary>
    public sealed record SimSpeedPreset(double Speed, string Label)
    {
        /// <summary>Krótko, np. "×60" – tekst pola po wybraniu z listy.</summary>
        public string ShortLabel => $"×{Speed:0}";

        public override string ToString() => Label;
    }

    /// <summary>Zdefiniowane prędkości symulacji – lista rozwijana i kolejne kroki strzałek ◂ ▸.</summary>
    public static IReadOnlyList<SimSpeedPreset> SimSpeedPresets { get; } =
        [.. new double[] { 1, 2, 5, 10, 20, 30, 60, 120, 300, 600, 900, 1800, 3600 }
            .Select(speed => new SimSpeedPreset(speed, FormatSimSpeed(speed)))];

    /// <summary>
    /// Prędkość wybrana z listy; null, gdy suwak ustawiono pomiędzy gotowymi prędkościami.
    /// Wybór z listy przestawia suwak (działa od razu, także w trakcie odtwarzania).
    /// </summary>
    public SimSpeedPreset? SelectedSimSpeedPreset
    {
        get => SimSpeedPresets.FirstOrDefault(p => p.Speed == SimSpeed);
        set
        {
            if (value is not null)
            {
                SimSpeedSlider = SpeedToSlider(value.Speed);
            }
        }
    }

    /// <summary>◂ – następna mniejsza gotowa prędkość.</summary>
    public ICommand SimSpeedDownCommand => _simSpeedDownCommand ??= new AsyncRelayCommand(() =>
    {
        if (SimSpeedPresets.LastOrDefault(p => p.Speed < SimSpeed) is { } lower)
        {
            SelectedSimSpeedPreset = lower;
        }

        return Task.CompletedTask;
    });

    /// <summary>▸ – następna większa gotowa prędkość.</summary>
    public ICommand SimSpeedUpCommand => _simSpeedUpCommand ??= new AsyncRelayCommand(() =>
    {
        if (SimSpeedPresets.FirstOrDefault(p => p.Speed > SimSpeed) is { } higher)
        {
            SelectedSimSpeedPreset = higher;
        }

        return Task.CompletedTask;
    });

    private ICommand? _simSpeedDownCommand, _simSpeedUpCommand;

    /// <summary>Prędkość do wyświetlenia, np. "×60  (1 s = 1 min)".</summary>
    private static string FormatSimSpeed(double speed)
    {
        var perSecond = TimeSpan.FromSeconds(speed);
        var description = perSecond.TotalMinutes >= 1
            ? perSecond.TotalHours >= 1 ? $"{perSecond.TotalHours:0.#} h" : $"{perSecond.TotalMinutes:0.#} min"
            : $"{perSecond.TotalSeconds:0} s";
        return $"×{speed:0}  (1 s = {description})";
    }

    /// <summary>Bieżący czas symulacji (zegar).</summary>
    public string SimClock
    {
        get => _simClock;
        private set => SetProperty(ref _simClock, value);
    }

    // =====================================================================
    // Czas końca symulacji (opcjonalny)
    // =====================================================================

    /// <summary>Dzień końca symulacji (null = bez ograniczeń).</summary>
    public DateTime? SimEndDate
    {
        get => _simEndDate;
        set
        {
            if (SetProperty(ref _simEndDate, value))
            {
                ApplySimEnd();
            }
        }
    }

    /// <summary>Godzina końca symulacji "GG:MM" w strefie instrumentu (pusta = bez ograniczeń).</summary>
    public string SimEndTime
    {
        get => _simEndTime;
        set
        {
            if (SetProperty(ref _simEndTime, value ?? string.Empty))
            {
                ApplySimEnd();
            }
        }
    }

    /// <summary>
    /// Czas końca symulacji – tylko gdy wpisano i dzień, i poprawną godzinę; w przeciwnym razie null
    /// (odtwarzanie bez ograniczeń).
    /// </summary>
    private DateTimeOffset? SimEnd =>
        SimEndDate is { } date && !string.IsNullOrWhiteSpace(SimEndTime) &&
        TimeOnly.TryParseExact(SimEndTime.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
        _settings.TryParseExchangeTime($"{date:yyyy-MM-dd} {SimEndTime.Trim()}", out var end)
            ? end
            : null;

    /// <summary>Zmiana czasu końca: działa od razu (także w trakcie odtwarzania) i jest zapamiętywana w ustawieniach.</summary>
    private void ApplySimEnd()
    {
        if (_loadingSimEnd)
        {
            return;
        }

        var end = SimEnd;
        if (SimEndDate is not null && !string.IsNullOrWhiteSpace(SimEndTime) && end is null)
        {
            Hint("Czas końca symulacji: godzinę podaj w formacie GG:MM – do tego czasu odtwarzanie jest bez ograniczeń.");
        }

        if (SimFeed is { } feed)
        {
            feed.StopAt = end;
        }

        var text = SimEndDate is { } date ? $"{date:yyyy-MM-dd} {SimEndTime.Trim()}".Trim() : string.Empty;
        if (text != _settings.SimEnd)
        {
            _settings = _settings with { SimEnd = text };
            MarketProfile.App.Settings.SettingsStore.Save(_settings);
        }
    }

    /// <summary>Wczytuje zapisany czas końca do pól (bez ponownego zapisu).</summary>
    private void LoadSimEnd()
    {
        var parts = _settings.SimEnd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _loadingSimEnd = true;
        try
        {
            SimEndDate = parts.Length > 0 && DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) ? date : null;
            SimEndTime = parts.Length > 1 ? parts[1] : string.Empty;
        }
        finally
        {
            _loadingSimEnd = false;
        }
    }

    /// <summary>✕ – usuwa czas końca (odtwarzanie bez ograniczeń).</summary>
    private Task ClearSimEndAsync()
    {
        _loadingSimEnd = true;
        try
        {
            SimEndDate = null;
            SimEndTime = string.Empty;
        }
        finally
        {
            _loadingSimEnd = false;
        }

        ApplySimEnd();
        Hint("Czas końca usunięty – symulacja odtwarza bez ograniczeń");
        return Task.CompletedTask;
    }

    /// <summary>Komunikat o osiągnięciu czasu końca.</summary>
    private string SimEndReachedMessage(DateTimeOffset end) =>
        $"Osiągnięto czas końca symulacji ({TimeZoneInfo.ConvertTime(end, _settings.ExchangeTimeZone):yyyy-MM-dd HH:mm}) – " +
        "▶ usuwa czas końca i odtwarza dalej";

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

    /// <summary>Jeden przycisk ▶/⏸: w pauzie uruchamia odtwarzanie, w trakcie odtwarzania – pauza.</summary>
    public ICommand SimPlayPauseCommand => _simPlayPauseCommand ??= new AsyncRelayCommand(() => IsSimPlaying ? PauseAsync() : PlayAsync());
    public ICommand SimStepCommand => _simStepCommand ??= new AsyncRelayCommand(StepAsync);
    public ICommand SimStepBackCommand => _simStepBackCommand ??= new AsyncRelayCommand(StepBackAsync);
    public ICommand SimClearEndCommand => _simClearEndCommand ??= new AsyncRelayCommand(ClearSimEndAsync);
    public ICommand SimResetCommand => _simResetCommand ??= new AsyncRelayCommand(ResetAsync);
    public ICommand SimPrevProfileCommand => _simPrevProfileCommand ??= new AsyncRelayCommand(PrevProfileAsync);
    public ICommand SimNextProfileCommand => _simNextProfileCommand ??= new AsyncRelayCommand(NextProfileAsync);

    /// <summary>
    /// ▶ Play. Wczytana symulacja jest wznawiana; jeśli nie jest wczytana – wczytuje dane od czasu z pola
    /// (historia sprzed tego momentu i odtwarzanie od niego). Czas końca, który już minął, jest usuwany.
    /// </summary>
    private async Task PlayAsync()
    {
        if (_simNavigating)
        {
            return;
        }

        // Czas końca już osiągnięty (albo przestarzały) – ▶ go usuwa i odtwarza dalej bez ograniczeń.
        var at = SimFeed?.CurrentTime ?? (TryGetSimStart(out var start) ? start : (DateTimeOffset?)null);
        if (SimEnd is { } end && at >= end)
        {
            await ClearSimEndAsync();
        }

        if (SimFeed is { } loaded)
        {
            loaded.Resume();
            IsSimPlaying = true;
            return;
        }

        if (await EnsureSimLoadedAsync(paused: false))
        {
            IsSimPlaying = true;
        }
    }

    /// <summary>
    /// Wczytuje symulację od czasu z pola, jeśli nie jest wczytana: historia ze świec do otwarcia bieżącej sesji,
    /// bieżący profil z ticków z bazy. Podgląd na wykresie jest zastępowany w jednym kroku (bez pustego wykresu).
    /// </summary>
    private async Task<bool> EnsureSimLoadedAsync(bool paused)
    {
        if (SimFeed is not null)
        {
            return true;
        }

        // Wczytywanie już trwa (np. szybkie ▶ i ⏭) – czekamy na nie zamiast uruchamiać drugie równolegle.
        if (_simLoadTask is { } pending)
        {
            return await pending;
        }

        if (!TryGetSimStart(out var start))
        {
            return false;
        }

        var task = _simLoadTask = LoadAndShowAsync();
        try
        {
            return await task;
        }
        finally
        {
            if (ReferenceEquals(_simLoadTask, task))
            {
                _simLoadTask = null;
            }
        }

        async Task<bool> LoadAndShowAsync()
        {
            var loaded = await LoadSimulationAsync(start, paused, keepView: true);
            SetViewCutoff(null);
            return loaded;
        }
    }

    /// <summary>Trwające wczytywanie symulacji (<see cref="EnsureSimLoadedAsync"/>).</summary>
    private Task<bool>? _simLoadTask;

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
    /// Jeśli symulacja nie jest wczytana – wczytuje ją w pauzie od czasu z pola i wykonuje krok.
    /// </summary>
    private async Task StepAsync()
    {
        if (_simNavigating)
        {
            return;
        }

        if (!await EnsureSimLoadedAsync(paused: true))
        {
            return;
        }

        SimFeed?.Step(TimeSpan.FromMinutes(_settings.TpoMinutes));
    }

    /// <summary>
    /// ◄ (słupek wstecz) – cofa czas symulacji o jeden okres TPO (profile po tym momencie od razu znikają, stan Play/Pauza zostaje zachowany).
    /// Jeśli w tym okresie nie ma danych (noc, weekend), cofa się do ostatniego okresu z danymi –
    /// tak jak krok w przód przeskakuje przerwy, żeby krok nie był „pusty”.
    /// </summary>
    private async Task StepBackAsync()
    {
        if (_simNavigating || !await EnsureSimLoadedAsync(paused: true))
        {
            return;
        }

        var symbol = _settings.InstrumentSymbol;
        if (SimFeed is not { } feed || SimDataRangeOf(symbol) is not { } range)
        {
            return;
        }

        var now = feed.CurrentTime;
        var period = TimeSpan.FromMinutes(_settings.TpoMinutes);
        var target = now - period;

        var store = GetStore();
        var source = store.SimulationSource(symbol);
        if (!store.HasData(symbol, target, now, source) && store.LastDataTimeBefore(symbol, now, source) is { } last)
        {
            target = last + TimeSpan.FromMinutes(1) - period; // ostatni okres z transakcjami przed przerwą
        }

        if (target < range.From)
        {
            target = range.From;
        }

        if (target >= now)
        {
            Hint("To początek danych w bazie – nie można cofnąć dalej");
            return;
        }

        await MoveSimTimeAsync(target);
    }

    /// <summary>⏹ Reset – pauza i powrót do momentu, od którego wczytano symulację.</summary>
    private async Task ResetAsync()
    {
        if (SimFeed is not { } feed || _loadedSimStart is not { } origin)
        {
            return;
        }

        feed.Pause();
        IsSimPlaying = false;
        await MoveSimTimeAsync(origin);
        SaveSimSettings();
    }

    /// <summary>
    /// Prośba do widoku: bez zmiany skali przesuń wykres tak, żeby miejsce startu symulacji było 1/3 szerokości
    /// od lewej, a cena startu na środku wysokości.
    /// </summary>
    public event Action? SimulationStartFocusRequested;

    /// <summary>
    /// Menu podręczne: „Symulacja od początku profilu / od słupka” – cofnięcie czasu symulacji do tego momentu.
    /// Skala wykresu bez zmian; wykres przesunięty tak, że miejsce startu jest 1/3 szerokości od lewej, a cena startu
    /// na środku wysokości. Symulacja wstrzymana; ▶ rysuje dalej od słupka, ⏹ wraca do niego.
    /// </summary>
    public async Task SimulateBackToAsync(DateTime localTime)
    {
        if (!IsSimulationMode || _simNavigating)
        {
            return;
        }

        var zone = _settings.ExchangeTimeZone;
        var target = new DateTimeOffset(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), zone.GetUtcOffset(localTime));
        if (SimDataRangeOf(_settings.InstrumentSymbol) is not { } range || target < range.From || target >= range.To)
        {
            Hint($"Brak danych w bazie dla {localTime:dd.MM HH:mm} – symulacja może zacząć się tylko w zakresie danych");
            return;
        }

        _simNavigating = true;
        try
        {
            SimFeed?.Pause();
            IsSimPlaying = false;

            // Od razu chowamy wszystko po tym momencie, potem dokładny stan (ticki do tej chwili) bez czyszczenia wykresu.
            SetViewCutoff(target);
            SimulationStartFocusRequested?.Invoke();
            ShowSimTime(target);
            await LoadSimulationAsync(target, paused: true, keepView: true);
        }
        finally
        {
            SetViewCutoff(null);
            _simNavigating = false;
        }
    }

    /// <summary>
    /// Wczytuje symulację od podanego momentu do końca danych w bazie.
    /// </summary>
    /// <param name="start">Moment, od którego zaczyna się odtwarzanie.</param>
    /// <param name="paused">Czy wczytać w pauzie.</param>
    /// <param name="keepView">
    /// Wykres nie jest czyszczony – nowe profile zastępują dotychczasowe (podgląd albo stan przed cofnięciem) w jednym kroku.
    /// </param>
    /// <param name="keepOrigin">Ponowne wczytanie przy cofaniu – moment powrotu ⏹ zostaje bez zmian.</param>
    private async Task<bool> LoadSimulationAsync(DateTimeOffset start, bool paused, bool keepView = false, bool keepOrigin = false)
    {
        var origin = _loadedSimStart;
        await StopAsync();
        RefreshSimDataRange(); // np. w międzyczasie pobrano pierwsze dane od brokera – wtedy one mają pierwszeństwo

        if (SimDataRangeOf(_settings.InstrumentSymbol) is not { } range)
        {
            return false; // brak danych – ostrzeżenie w polu informacji
        }

        // Bieżący profil (sesja, w której jest start) – w całości z ticków z bazy; wcześniejsze profile – ze świec 1-min.
        var ticksFrom = SessionBoundaries.CurrentSessionStart(_settings.ToProfileSettings(), start);
        var feed = new DatabaseReplayFeed(GetStore(), start, range.To.AddMinutes(1), SimSpeed, _settings.ExchangeTimeZone, ticksFrom)
        {
            StopAt = SimEnd, // opcjonalny czas końca – po dojściu do niego pauza
        };
        if (paused)
        {
            feed.Pause();
        }

        if (!await StartFeedAsync(feed, keepView: keepView))
        {
            return false;
        }

        _loadedSimStart = keepOrigin && origin is not null ? origin : start;
        _previewFrom = _previewEnd = null; // podgląd zastąpiony wczytaną symulacją
        ShowSimTime(start);
        SaveSimSettings();
        return true;
    }

    // =====================================================================
    // Płynne przewijanie czasu (pole czasu, ⏮ ⏩ ⏹)
    // =====================================================================

    /// <summary>Użytkownik zaczął edytować pole godziny – zegar symulacji go nie nadpisuje.</summary>
    public void BeginEditSimTime()
    {
        _isEditingSimTime = true;
        _simTimeTextAtEditStart = SimStartTime;
    }

    /// <summary>Koniec edycji pola godziny (Enter albo wyjście z pola): jeśli godzina się zmieniła – przejście do niej.</summary>
    public async Task EndEditSimTimeAsync()
    {
        var changed = _isEditingSimTime && SimStartTime != _simTimeTextAtEditStart;
        _isEditingSimTime = false;

        if (changed)
        {
            await CommitSimTimeAsync();
        }
        else if (SimFeed is { } feed)
        {
            ShowSimTime(feed.CurrentTime); // pole wraca do bieżącego czasu symulacji
        }
    }

    /// <summary>
    /// Przejście do czasu z pola: wczytana symulacja jest płynnie przewijana, a niewczytana – wczytywana
    /// w pauzie od tego momentu.
    /// </summary>
    private async Task CommitSimTimeAsync()
    {
        if (!IsSimulationMode || _simNavigating || !TryGetSimStart(out var target))
        {
            return;
        }

        // Nowa data/godzina startu – „Dopasuj”, żeby profile z tego momentu były od razu widoczne.
        FitRequested?.Invoke();

        if (SimFeed is { } feed)
        {
            // Pole pokazuje pełne minuty – ta sama minuta co bieżący czas = brak zmiany.
            if (BarAggregator.FloorToMinute(feed.CurrentTime) == BarAggregator.FloorToMinute(target))
            {
                return;
            }

            await MoveSimTimeAsync(target);
        }
        else
        {
            await MovePreviewAsync(target);
        }

        // Profil z tego momentu (w przód i wstecz) od 0,3 szerokości ekranu, po prawej miejsce na dalsze rysowanie.
        PinLatestRequested?.Invoke();
    }

    // =====================================================================
    // Podgląd symulacji (przed ▶): profile jak na żywo, ucięte na wybranym momencie
    // =====================================================================

    private DateTimeOffset? _previewFrom;   // początek historii wczytanej do podglądu
    private DateTimeOffset? _previewEnd;    // koniec danych wczytanych do podglądu
    private int _previewVersion;            // wersja podglądu – starsze (spóźnione) wczytania są pomijane

    /// <summary>
    /// Podgląd symulacji bez odtwarzania: profile ze świec 1-min z bazy – tak jak na żywo (ta sama długość historii) –
    /// kończące się w <paramref name="at"/> (null = do końca danych). Profile po tym momencie są schowane.
    /// Wykres nie jest czyszczony – gotowy podgląd zastępuje poprzedni obraz w jednym kroku.
    /// </summary>
    private async Task ShowSimPreviewAsync(DateTimeOffset? at)
    {
        if (!IsSimulationMode || IsRunning)
        {
            return;
        }

        RefreshSimDataRange();
        var symbol = _settings.InstrumentSymbol;
        if (_simSource is not { } source || SimDataRangeOf(symbol) is not { } range)
        {
            _builder = null;
            _previewFrom = _previewEnd = null;
            _snapshotCache.Clear();
            _snapshots = [];
            Profiles = [];
            return; // brak danych – ostrzeżenie w polu informacji
        }

        var end = at is { } a && a < range.To ? a : range.To;
        var from = end - TimeSpan.FromDays(_settings.RequiredHistoryDays());
        var settings = _settings.ToProfileSettings();
        var store = GetStore();
        var version = ++_previewVersion;

        var builder = await Task.Run(() =>
        {
            var b = new ProfileBuilder(settings);
            foreach (var bar in BarAggregator.LoadMinuteBars(store, symbol, from, end, source))
            {
                b.ProcessBar(bar);
            }

            return b;
        });

        if (version != _previewVersion || !IsSimulationMode || IsRunning)
        {
            return; // w międzyczasie zażądano innego podglądu albo uruchomiono symulację
        }

        _builder = builder;
        _snapshotCache.Clear();
        _previewFrom = from;
        _previewEnd = end;
        SetViewCutoff(at is { } cut && cut < range.To ? cut : null);
        if (_fitPending)
        {
            _fitPending = false;
            FitRequested?.Invoke();
        }

        ShowSimTime(at ?? range.To);
    }

    /// <summary>
    /// Zmiana czasu przed uruchomieniem symulacji: profile po tym momencie od razu znikają (wstecz) albo wracają (w przód),
    /// okres po okresie. Moment spoza wczytanego podglądu – nowy podgląd kończący się w tym momencie.
    /// </summary>
    private async Task MovePreviewAsync(DateTimeOffset target)
    {
        if (_simNavigating)
        {
            return;
        }

        var range = SimDataRangeOf(_settings.InstrumentSymbol);
        var inPreview = _builder is not null && _previewFrom is { } from && _previewEnd is { } end &&
                        target >= from + TimeSpan.FromDays(1) &&                    // przed celem jest jeszcze historia
                        (target <= end || (range is { } r && end >= r.To));         // za końcem podglądu nie ma danych
        if (!inPreview)
        {
            await ShowSimPreviewAsync(target);
            return;
        }

        _simNavigating = true;
        try
        {
            var current = _viewCutoff ?? _previewEnd!.Value;
            if (target < current)
            {
                // Wstecz bez animacji – profile po tym momencie od razu znikają.
                SetViewCutoff(target);
                ShowSimTime(target);
                PinLatestRequested?.Invoke();
            }
            else
            {
                foreach (var step in TimeSteps.Between(current, target, TimeSpan.FromMinutes(_settings.TpoMinutes), SimAnimationMaxFrames))
                {
                    SetViewCutoff(step >= _previewEnd ? null : step);
                    ShowSimTime(step);
                    await Task.Delay(SimAnimationFrame);
                }
            }

        }
        finally
        {
            _simNavigating = false;
        }
    }

    /// <summary>
    /// Płynne przejście symulacji do podanego momentu; stan Play/Pauza zostaje zachowany.
    ///  - w przód: odtwarzanie przeskakuje okres po okresie TPO – części profili dorysowują się po kolei,
    ///  - wstecz: profile po tym momencie od razu znikają (odcięcie widoku, bez animacji), a potem symulacja jest wczytywana
    ///    od nowa od tego momentu – bez czyszczenia wykresu, więc nic nie miga.
    /// </summary>
    private async Task MoveSimTimeAsync(DateTimeOffset target)
    {
        if (_simNavigating)
        {
            return;
        }

        if (SimFeed is not { } feed)
        {
            await LoadSimulationAsync(target, paused: true);
            return;
        }

        _simNavigating = true;
        var wasPlaying = IsSimPlaying;
        try
        {
            feed.Pause();
            var now = feed.CurrentTime;
            var steps = TimeSteps.Between(now, target, TimeSpan.FromMinutes(_settings.TpoMinutes), SimAnimationMaxFrames);

            if (target > now)
            {
                foreach (var step in steps)
                {
                    feed.SeekTo(step);

                    // Czekamy, aż pętla odtwarzania przekaże ticki do tego momentu (zwykle 20–40 ms).
                    for (var wait = 0; wait < 25 && ReferenceEquals(feed, SimFeed) && feed.CurrentTime < step; wait++)
                    {
                        await Task.Delay(20);
                    }

                    if (!ReferenceEquals(feed, SimFeed))
                    {
                        return; // koniec danych – symulacja się zakończyła
                    }

                    RefreshSnapshots();
                    ShowSimTime(step);
                    await Task.Delay(SimAnimationFrame);
                }

                if (wasPlaying)
                {
                    feed.Resume();
                }

                IsSimPlaying = wasPlaying;
            }
            else if (target < now)
            {
                // Wstecz bez animacji: profile po tym momencie od razu znikają, a widok wraca do profilu,
                // do którego się cofnęliśmy (najnowszy widoczny – przy prawej krawędzi).
                SetViewCutoff(target);
                ShowSimTime(target);
                PinLatestRequested?.Invoke();

                // Dokładny stan w tym momencie (z tickami do tej chwili) – nowe wczytanie bez czyszczenia wykresu.
                var loaded = await LoadSimulationAsync(target, paused: !wasPlaying, keepView: true, keepOrigin: true);
                SetViewCutoff(null);
                IsSimPlaying = loaded && wasPlaying;
            }
        }
        finally
        {
            _simNavigating = false;
            if (_viewCutoff is not null)
            {
                SetViewCutoff(null);
            }
        }
    }

    // =====================================================================
    // Profil wstecz / w przód
    // =====================================================================

    /// <summary>
    /// ⏩ Profil w przód – płynne przejście do pierwszej transakcji następnego profilu, w którym są dane (od razu widać otwarcie).
    /// Noce bez danych i weekendy są pomijane. Działa w pauzie i w trakcie odtwarzania.
    /// </summary>
    private async Task NextProfileAsync()
    {
        if (_simNavigating)
        {
            return;
        }

        if (!await EnsureSimLoadedAsync(paused: true))
        {
            return;
        }

        if (SimFeed is not { } feed || SimDataRangeOf(_settings.InstrumentSymbol) is not { } range)
        {
            return;
        }

        if (FindProfileStart(feed.CurrentTime, range.To, forward: true) is not { } target)
        {
            Hint("To ostatni profil w bazie – brak dalszych danych");
            return;
        }

        await MoveSimTimeAsync(target);
    }

    /// <summary>
    /// ⏮ Profil wstecz – powrót do pierwszej transakcji bieżącego profilu, a jeśli już na niej jesteśmy –
    /// do początku poprzedniego. Stan Play/Pauza zostaje zachowany.
    /// </summary>
    private async Task PrevProfileAsync()
    {
        if (_simNavigating || SimDataRangeOf(_settings.InstrumentSymbol) is not { } range)
        {
            return;
        }

        DateTimeOffset now;
        if (SimFeed is { } feed)
        {
            now = feed.CurrentTime;
        }
        else if (!TryGetSimStart(out now))
        {
            return;
        }

        if (FindProfileStart(now, range.From, forward: false) is not { } target)
        {
            Hint("To pierwszy profil w bazie – brak wcześniejszych danych");
            return;
        }

        await MoveSimTimeAsync(target);
    }

    /// <summary>
    /// Szuka pierwszej transakcji profilu z danymi: w przód – w pierwszej sesji po <paramref name="from"/>;
    /// wstecz – w ostatniej sesji, której pierwsza transakcja jest przed <paramref name="from"/> (żeby z samego otwarcia
    /// profilu cofnąć się do poprzedniego). Profile bez danych są pomijane; <paramref name="limit"/> to koniec
    /// (w przód) lub początek (wstecz) danych w bazie.
    /// </summary>
    private DateTimeOffset? FindProfileStart(DateTimeOffset from, DateTimeOffset limit, bool forward)
    {
        var settings = _settings.ToProfileSettings();
        var store = GetStore();
        var symbol = _settings.InstrumentSymbol;
        var source = store.SimulationSource(symbol);
        var cursor = forward ? from : from.AddSeconds(-1);

        // Ograniczenie liczby kroków – np. kilka lat sesji; zabezpieczenie przed pętlą bez końca.
        for (var i = 0; i < 5000; i++)
        {
            var start = forward
                ? SessionBoundaries.NextSessionStart(settings, cursor)
                : SessionBoundaries.PreviousSessionStart(settings, cursor);
            if (start is not { } s)
            {
                return null;
            }

            var end = SessionBoundaries.NextSessionStart(settings, s) ?? s.AddDays(1);
            if (forward ? s >= limit : end <= limit)
            {
                return null; // poza zakresem danych
            }

            // Wstecz nie schodzimy przed początek danych (pierwszy profil może zaczynać się w środku sesji).
            var from2 = !forward && s < limit ? limit : s;
            if (store.HasData(symbol, from2, end, source) &&
                BarAggregator.Replay(store, symbol, from2, end, source).Select(i => (DateTimeOffset?)i.Time).FirstOrDefault() is { } firstTrade &&
                (forward || firstTrade < from))
            {
                // Pierwsza transakcja profilu – od razu jest cena otwarcia (litera A), inwentarz, GAP itd.
                // Wstecz: jeśli stoimy na pierwszej transakcji tego profilu (albo przed nią), szukamy wcześniejszego.
                return firstTrade;
            }

            cursor = forward ? s : s.AddSeconds(-1);
        }

        return null;
    }

    // =====================================================================
    // Informacja o danych testowych
    // =====================================================================

    /// <summary>
    /// Czy wykres pokazuje dane testowe zamiast danych od brokera: w symulacji – gdy w bazie nie ma żadnych
    /// danych instrumentu od brokera (wtedy i tylko wtedy odtwarzane są dane testowe); na żywo – gdy źródłem
    /// jest generator losowych cen.
    /// </summary>
    public bool IsUsingTestData => IsSimulationMode
        ? _simSource == MarketDataStore.SourceTest
        : _settings.LiveSource == LiveSourceKind.Generator;

    /// <summary>
    /// Stałe ostrzeżenia o danych na wykresie (do pola informacji; brak = dane od brokera, bez zastrzeżeń):
    /// dane testowe, odtwarzanie okresu bez ticków (tylko świece 1-min), brak danych do symulacji, niezgodny tick z IB.
    /// </summary>
    private IEnumerable<string> DataWarnings()
    {
        if (IsUsingTestData)
        {
            yield return IsSimulationMode
                ? $"DANE TESTOWE – brak danych {_settings.InstrumentSymbol} od brokera w lokalnej bazie. Symulacja odtwarza losowe ceny " +
                  "z generatora, NIE prawdziwy rynek. Uruchom TWS – prawdziwe dane pobiorą się w tle do bazy (także w trakcie " +
                  "symulacji) i będą używane zamiast testowych."
                : "DANE TESTOWE – brak dostępu do danych brokera: źródłem jest generator losowych cen, NIE prawdziwy rynek. " +
                  "Wybierz Interactive Brokers w Ustawieniach → Dane i broker → Źródło i połączenie.";
        }

        if (IsSimulationMode && _replayingBarsOnly && !IsUsingTestData)
        {
            yield return "BRAK TICKÓW dla tego okresu – odtwarzane są świece 1-min: każda minuta trafia do profilu w całości, " +
                         "bez przebiegu ceny wewnątrz minuty. Prawdziwe ticki pobierają się w tle, gdy działa TWS.";
        }

        if (IsSimulationMode && _simSource is null)
        {
            yield return NoSimDataMessage();
        }

        if (_tickSizeWarning is { } tick)
        {
            yield return tick;
        }
    }

    /// <summary>
    /// Dlaczego w bazie nie ma danych instrumentu do symulacji i co zrobić: przy wyłączonym zapisie danych od brokera
    /// nic się nie pobiera; przy włączonym – dane pobierają się w tle, gdy działa TWS (w obu trybach pracy).
    /// </summary>
    private string NoSimDataMessage()
    {
        var symbol = _settings.InstrumentSymbol;
        if (!_settings.RecordToDatabase)
        {
            return $"Brak danych {symbol} w bazie: zapisywanie danych od brokera jest WYŁĄCZONE – włącz „Zapisuj dane od brokera do bazy” " +
                   "w Ustawieniach → Dane i broker → Pobieranie i baza (pobieranie ruszy w tle, gdy działa TWS).";
        }

        return _settings.Instrument.DownloadData
            ? $"Brak danych {symbol} w bazie – pobierają się w tle, gdy działa TWS (świece w kilka minut, ticki dłużej)."
            : $"Brak danych {symbol} w bazie – zaznacz „Pobieraj dane w tle” dla {symbol} w Ustawieniach → Instrumenty i połącz się z brokerem.";
    }

    /// <summary>Odświeża ostrzeżenia o danych (pole informacji).</summary>
    private void NotifyDataSource()
    {
        OnPropertyChanged(nameof(IsUsingTestData));
        NotifyInfo();
    }

    // =====================================================================
    // Pomocnicze
    // =====================================================================

    /// <summary>
    /// Zakres danych instrumentu do symulacji – tylko ze źródła, które będzie odtwarzane
    /// (<see cref="MarketDataStore.SimulationSource"/>: broker, a dane testowe tylko gdy od brokera nic nie ma).
    /// </summary>
    private (DateTimeOffset From, DateTimeOffset To, long Ticks, long Bars)? SimDataRangeOf(string symbol)
    {
        var store = GetStore();
        return store.SimulationSource(symbol) is { } source ? store.GetDataRange(symbol, source) : null;
    }

    /// <summary>
    /// Zegar symulacji – zdarzenie z wątku odtwarzania przekazujemy do wątku okna.
    /// Komunikaty od już zatrzymanej symulacji (spóźnione) są pomijane.
    /// </summary>
    private void OnSimTimeChanged(object? sender, DateTimeOffset time) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(sender, _feed) && !_simNavigating)
            {
                ShowSimTime(time);
            }
        });

    /// <summary>
    /// Pokazuje czas symulacji: zegar (z sekundami) oraz pole daty i godziny – czas aktualnie rysowanego TPO
    /// (pola nie zmieniamy, gdy użytkownik je właśnie edytuje).
    /// </summary>
    private void ShowSimTime(DateTimeOffset time)
    {
        SimClock = FormatSimTime(time);
        if (!_isEditingSimTime)
        {
            SetSimStart(time);
        }
    }

    /// <summary>Okres bez ticków / z tickami w odtwarzaniu – do paska ostrzeżeń.</summary>
    private void OnReplayDataKindChanged(object? sender, bool isTicks) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(sender, _feed))
            {
                _replayingBarsOnly = !isTicks;
                NotifyDataSource();
            }
        });

    /// <summary>Koniec danych w bazie – symulacja zatrzymuje się, profile zostają na ekranie.</summary>
    private void OnReplayCompleted(object? sender, EventArgs e) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            // Zdarzenie z podmienionego (starego) źródła nie może zatrzymać nowej symulacji.
            if (!ReferenceEquals(sender, _feed))
            {
                return;
            }

            try
            {
                await StopAsync();
                Hint("Symulacja zakończona – koniec danych w bazie");
                _loadedSimStart = null;
            }
            catch (Exception ex)
            {
                ShowError($"Błąd przy zatrzymywaniu symulacji: {ex.Message}");
            }
        });

    /// <summary>Odtwarzanie przerwane błędem – zatrzymujemy symulację i pokazujemy przyczynę.</summary>
    private void OnReplayFailed(object? sender, Exception error) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            if (!ReferenceEquals(sender, _feed))
            {
                return;
            }

            try
            {
                await StopAsync();
                _loadedSimStart = null;
            }
            finally
            {
                ShowError($"Symulacja przerwana – błąd: {error.Message}");
            }
        });

    /// <summary>Odtwarzanie doszło do czasu końca – feed sam się wstrzymał; pokazujemy stan z tej chwili.</summary>
    private void OnSimStopReached(object? sender, DateTimeOffset end) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _feed))
            {
                return;
            }

            IsSimPlaying = false;
            RefreshSnapshots();
            ShowSimTime(end);
            SaveSimSettings();
            Hint(SimEndReachedMessage(end));
        });

    /// <summary>Odczytuje moment z pól daty i godziny.</summary>
    private bool TryGetSimStart(out DateTimeOffset start)
    {
        start = default;
        if (SimStartDate is not { } date ||
            !_settings.TryParseExchangeTime($"{date:yyyy-MM-dd} {SimStartTime?.Trim()}", out start))
        {
            Hint("Ustaw datę i godzinę symulacji (godzina w formacie GG:MM, w strefie czasowej instrumentu).");
            return false;
        }

        return true;
    }

    /// <summary>Ustawia pola daty i godziny (bez przewijania symulacji – to zmiana z programu).</summary>
    private void SetSimStart(DateTimeOffset start)
    {
        var local = TimeZoneInfo.ConvertTime(start, _settings.ExchangeTimeZone);
        _settingSimTimeFromCode = true;
        try
        {
            SimStartDate = local.Date;
            SimStartTime = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        finally
        {
            _settingSimTimeFromCode = false;
        }
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
