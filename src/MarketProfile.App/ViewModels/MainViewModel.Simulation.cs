using System.Globalization;
using System.Windows.Input;
using MarketProfile.App.Infrastructure;
using MarketProfile.App.Settings;
using MarketProfile.Core;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Tryb symulacji (odtwarzanie danych z lokalnej bazy) – sterowanie z paska w oknie głównym:
///  - pole czasu (data + godzina) – pokazuje czas aktualnie rysowanego TPO; wpisanie innego czasu płynnie
///    dorysowuje (w przód) albo wymazuje (wstecz) części profili,
///  - ▶ Play / ⏸ Pauza,
///  - ⏭ Krok – przesunięcie czasu o jeden okres TPO (działa także w pauzie),
///  - ⏮ / ⏩ Profil wstecz / w przód – przeskok do początku bieżącego (poprzedniego) / następnego profilu,
///  - ⏹ Reset – pauza i płynny powrót do momentu, od którego wczytano symulację,
///  - suwak prędkości – zmiana tempa w trakcie odtwarzania, bez restartu.
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
    private string _simDataRange = string.Empty;
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
    private ICommand? _simPlayCommand, _simPauseCommand, _simStepCommand, _simResetCommand;
    private ICommand? _simPrevProfileCommand, _simNextProfileCommand;

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
    /// Przełącza tryb pracy: zatrzymuje bieżące źródło danych.
    ///  - symulacja: wykres nie jest czyszczony – od razu pokazuje profile jak na żywo (podgląd ze świec z bazy),
    ///  - na żywo: wykres jest czyszczony i dane od brokera wczytują się od nowa.
    /// </summary>
    private async Task SwitchModeAsync(bool simulation)
    {
        await StopAsync(simulation
            ? "Tryb symulacji – ustaw datę i godzinę, potem ▶"
            : "Tryb na żywo – naciśnij Start");

        _loadedSimStart = null;
        _previewFrom = _previewEnd = null;
        SetViewCutoff(null);

        if (simulation)
        {
            InitializeSimulation();
            StartBackgroundDownload(); // ticki pobierają się także w trakcie symulacji (jeśli pobieranie jeszcze nie działa)
            await ShowSimPreviewAsync(null);
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
        _simSource = GetStore().SimulationSource(_settings.InstrumentSymbol);
        NotifyDataSource();

        if (SimDataRangeOf(_settings.InstrumentSymbol) is not { } range)
        {
            SimDataRange = NoSimDataMessage();
            return;
        }

        var zone = _settings.ExchangeTimeZone;
        var from = TimeZoneInfo.ConvertTime(range.From, zone);
        var to = TimeZoneInfo.ConvertTime(range.To, zone);
        var kind = _simSource == MarketDataStore.SourceTest ? "testowe" : "od brokera";
        SimDataRange = $"Dane w bazie ({kind}): {from:yyyy-MM-dd HH:mm} – {to:yyyy-MM-dd HH:mm}";

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
                _ = CommitSimTimeAsync(); // dzień zmieniony przez użytkownika – przejście do tego momentu
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
    public ICommand SimPrevProfileCommand => _simPrevProfileCommand ??= new AsyncRelayCommand(PrevProfileAsync);
    public ICommand SimNextProfileCommand => _simNextProfileCommand ??= new AsyncRelayCommand(NextProfileAsync);

    /// <summary>
    /// ▶ Play. Wczytana symulacja jest wznawiana; jeśli nie jest wczytana – wczytuje dane od czasu z pola
    /// (historia sprzed tego momentu i odtwarzanie od niego).
    /// </summary>
    private async Task PlayAsync()
    {
        if (_simNavigating)
        {
            return;
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

        if (!TryGetSimStart(out var start))
        {
            return false;
        }

        var loaded = await LoadSimulationAsync(start, paused, keepView: true);
        SetViewCutoff(null);
        return loaded;
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

    /// <summary>⏹ Reset – pauza i płynny powrót do momentu, od którego wczytano symulację.</summary>
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
        Status = "Symulacja wstrzymana na początku – ▶ odtwarza dalej";
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
        await StopAsync("Wczytywanie symulacji...");
        RefreshSimDataRange(); // np. w międzyczasie pobrano pierwsze dane od brokera – wtedy one mają pierwszeństwo

        if (SimDataRangeOf(_settings.InstrumentSymbol) is not { } range)
        {
            Status = NoSimDataMessage();
            return false;
        }

        // Bieżący profil (sesja, w której jest start) – w całości z ticków z bazy; wcześniejsze profile – ze świec 1-min.
        var ticksFrom = SessionBoundaries.CurrentSessionStart(_settings.ToProfileSettings(), start);
        var feed = new DatabaseReplayFeed(GetStore(), start, range.To.AddMinutes(1), SimSpeed, _settings.ExchangeTimeZone, ticksFrom);
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
            Status = NoSimDataMessage();
            return;
        }

        var end = at is { } a && a < range.To ? a : range.To;
        var from = end - TimeSpan.FromDays(_settings.RequiredHistoryDays());
        var settings = _settings.ToProfileSettings();
        var store = GetStore();
        var version = ++_previewVersion;
        Status = "Wczytywanie podglądu symulacji...";

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
        Status = at is null
            ? "Symulacja – profile jak na żywo. Ustaw datę i godzinę (Enter), potem ▶"
            : "Podgląd na wybraną chwilę – ▶ uruchamia odtwarzanie (bieżący profil z ticków z bazy)";
    }

    /// <summary>
    /// Zmiana czasu przed uruchomieniem symulacji: profile po tym momencie płynnie znikają (wstecz) albo wracają (w przód),
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
            foreach (var step in TimeSteps.Between(current, target, TimeSpan.FromMinutes(_settings.TpoMinutes), SimAnimationMaxFrames))
            {
                SetViewCutoff(step >= _previewEnd ? null : step);
                ShowSimTime(step);
                await Task.Delay(SimAnimationFrame);
            }

            Status = "Podgląd na wybraną chwilę – ▶ uruchamia odtwarzanie (bieżący profil z ticków z bazy)";
        }
        finally
        {
            _simNavigating = false;
        }
    }

    /// <summary>
    /// Płynne przejście symulacji do podanego momentu; stan Play/Pauza zostaje zachowany.
    ///  - w przód: odtwarzanie przeskakuje okres po okresie TPO – części profili dorysowują się po kolei,
    ///  - wstecz: części profili znikają okres po okresie (odcięcie widoku), a potem symulacja jest wczytywana
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
                foreach (var step in steps)
                {
                    SetViewCutoff(step);
                    ShowSimTime(step);
                    await Task.Delay(SimAnimationFrame);
                }

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
    /// ⏩ Profil w przód – płynne przejście do początku następnego profilu, w którym są dane.
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
            Status = "To ostatni profil w bazie – brak dalszych danych";
            return;
        }

        await MoveSimTimeAsync(target);
    }

    /// <summary>
    /// ⏮ Profil wstecz – płynny powrót do początku bieżącego profilu, a jeśli jesteśmy już na jego początku –
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
            Status = "To pierwszy profil w bazie – brak wcześniejszych danych";
            return;
        }

        await MoveSimTimeAsync(target);
    }

    /// <summary>
    /// Szuka początku profilu z danymi: w przód – pierwszy początek sesji po <paramref name="from"/>;
    /// wstecz – ostatni początek sesji przed <paramref name="from"/> (z marginesem 1 s, żeby z samego początku
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

            if (store.HasData(symbol, s, end, source))
            {
                // Wstecz nie schodzimy przed początek danych (pierwszy profil może zaczynać się w środku sesji).
                return !forward && s < limit ? limit : s;
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
    /// Wyraźne ostrzeżenia o danych na wykresie (pusty tekst = dane od brokera, bez zastrzeżeń):
    /// dane testowe, odtwarzanie okresu bez ticków (tylko świece 1-min), niezgodny tick instrumentu z IB.
    /// </summary>
    public string DataWarning
    {
        get
        {
            var warnings = new List<string>();
            if (IsUsingTestData)
            {
                warnings.Add(IsSimulationMode
                    ? $"⚠  DANE TESTOWE – brak danych {_settings.InstrumentSymbol} od brokera w lokalnej bazie. Symulacja odtwarza losowe ceny " +
                      "z generatora, NIE prawdziwy rynek. Uruchom TWS – prawdziwe dane pobiorą się w tle do bazy (także w trakcie " +
                      "symulacji) i będą używane zamiast testowych."
                    : "⚠  DANE TESTOWE – brak dostępu do danych brokera: źródłem jest generator losowych cen, NIE prawdziwy rynek. " +
                      "Wybierz Interactive Brokers w Ustawieniach → Źródło danych.");
            }

            if (IsSimulationMode && _replayingBarsOnly && !IsUsingTestData)
            {
                warnings.Add("⚠  BRAK TICKÓW dla tego okresu – odtwarzane są świece 1-min: każda minuta trafia do profilu w całości, " +
                             "bez przebiegu ceny wewnątrz minuty. Prawdziwe ticki pobierają się w tle, gdy działa TWS (także w trakcie symulacji).");
            }

            if (IsSimulationMode && _simSource is null)
            {
                warnings.Add($"⚠  {NoSimDataMessage()}");
            }

            if (_tickSizeWarning is { } tick)
            {
                warnings.Add($"⚠  {tick}");
            }

            return string.Join(Environment.NewLine, warnings);
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
                   "w Ustawieniach → Dane (pobieranie ruszy w tle, gdy działa TWS).";
        }

        return _settings.Instrument.DownloadData
            ? $"Brak danych {symbol} w bazie – pobierają się w tle, gdy działa TWS (świece w kilka minut, ticki dłużej); postęp na pasku na dole."
            : $"Brak danych {symbol} w bazie – zaznacz „Pobieraj dane w tle” dla {symbol} w Ustawieniach → Instrumenty i połącz się z brokerem.";
    }

    /// <summary>Czy pokazać pasek ostrzeżeń o danych.</summary>
    public bool HasDataWarning => DataWarning.Length > 0;

    /// <summary>Odświeża w widoku nazwę źródła danych i ostrzeżenia o danych.</summary>
    private void NotifyDataSource()
    {
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(IsUsingTestData));
        OnPropertyChanged(nameof(DataWarning));
        OnPropertyChanged(nameof(HasDataWarning));
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
            var message = Status; // komunikat "Symulacja zakończona..." od źródła danych
            await StopAsync(message);
            _loadedSimStart = null;
        });

    /// <summary>Odczytuje moment z pól daty i godziny.</summary>
    private bool TryGetSimStart(out DateTimeOffset start)
    {
        start = default;
        if (SimStartDate is not { } date ||
            !_settings.TryParseExchangeTime($"{date:yyyy-MM-dd} {SimStartTime?.Trim()}", out start))
        {
            Status = "Ustaw datę i godzinę symulacji (godzina w formacie GG:MM, w strefie czasowej instrumentu).";
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
