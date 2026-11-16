using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketProfile.Core;
using MarketProfile.DataFeed.IB;

namespace MarketProfile.App.Settings;

/// <summary>
/// Ustawienia aplikacji zapisywane do pliku settings.json (w %AppData%\MarketProfile).
/// Godziny przechowujemy jako tekst "HH:mm", żeby plik był czytelny i łatwy do ręcznej edycji.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Symbol wybranego instrumentu (klucz z listy <see cref="Instruments"/>).</summary>
    public string InstrumentSymbol { get; init; } = "ES";

    /// <summary>
    /// Lista instrumentów do wyboru – każdy z własnymi godzinami sesji, strefą czasową i wielkością ticka.
    /// Przy pierwszym uruchomieniu wypełniana gotową listą popularnych instrumentów (patrz <see cref="WithInstrumentsInitialized"/>).
    /// </summary>
    public List<Instrument> Instruments { get; init; } = [];

    // --- Przestarzałe: wspólne godziny sesji z wcześniejszych wersji (czas Nowego Jorku).
    //     Używane tylko jednorazowo – do przeniesienia ustawień użytkownika do ES i NQ. ---
    public string DaySessionStart { get; init; } = "09:30";
    public string DaySessionEnd { get; init; } = "16:00";
    public string NightSessionStart { get; init; } = "18:00";
    public string NightSessionEnd { get; init; } = "09:30";

    /// <summary>Wysokość wiersza TPO w tickach.</summary>
    public int TicksPerRow { get; init; } = 4;

    /// <summary>Długość okresu TPO w minutach.</summary>
    public int TpoMinutes { get; init; } = 30;

    /// <summary>Procent TPO w obszarze wartości (Value Area).</summary>
    public int ValueAreaPercent { get; init; } = 70;

    /// <summary>Liczba pierwszych okresów TPO tworzących Initial Balance (standardowo 2 = pierwsza godzina przy 30 min).</summary>
    public int InitialBalancePeriods { get; init; } = 2;

    /// <summary>Reguła wyboru POC, gdy dwa wiersze mają tyle samo TPO i są równo oddalone od środka profilu.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PocTieBreak>))]
    public PocTieBreak PocTieBreak { get; init; } = PocTieBreak.MoreTpoSide;

    /// <summary>Ile dni historii pobierać przy starcie.</summary>
    public int HistoryDays { get; init; } = 3;

    /// <summary>Przyspieszenie czasu w symulatorze (60 = 1 s rzeczywista to 1 minuta).</summary>
    public double SimulatorSpeed { get; init; } = 60;

    /// <summary>
    /// Ostatnio ustawiony początek symulacji – "yyyy-MM-dd HH:mm", czas Nowego Jorku
    /// (pusty = zostanie zaproponowany na podstawie danych w bazie).
    /// </summary>
    public string SimStart { get; init; } = string.Empty;

    /// <summary>Ostatnio ustawiona prędkość symulacji (60 = 1 s to 1 minuta danych).</summary>
    public double SimSpeed { get; init; } = 60;

    /// <summary>
    /// Czas końca symulacji – "yyyy-MM-dd HH:mm" w strefie instrumentu. Pusty = odtwarzanie bez ograniczeń
    /// (do końca danych w bazie). Po dojściu do tego momentu symulacja się wstrzymuje.
    /// </summary>
    public string SimEnd { get; init; } = string.Empty;

    /// <summary>
    /// Dokładny moment (format ISO 8601 „o”), w którym symulację wstrzymano przy przejściu do trybu „Na żywo” –
    /// po powrocie do symulacji wykres wraca do tego miejsca (w pauzie). Pusty = brak zapamiętanego punktu.
    /// </summary>
    public string SimResumeAt { get; init; } = string.Empty;

    /// <summary>Instrument, którego dotyczy <see cref="SimResumeAt"/>.</summary>
    public string SimResumeSymbol { get; init; } = string.Empty;

    /// <summary>Moment z <see cref="SimResumeAt"/>, jeśli jest zapisany poprawnie.</summary>
    public DateTimeOffset? ParseSimResumeAt() =>
        DateTimeOffset.TryParseExact(SimResumeAt, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    // --- Interactive Brokers ---

    /// <summary>Źródło danych w trybie „Na żywo”: generator losowych cen albo Interactive Brokers.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LiveSourceKind>))]
    public LiveSourceKind LiveSource { get; init; } = LiveSourceKind.Generator;

    /// <summary>
    /// Ścieżka do biblioteki klienta TWS API: plik CSharpAPI.dll albo folder, w którym go szukamy
    /// (puste = wyszukiwanie automatyczne, np. folder „TWS API” obok aplikacji).
    /// </summary>
    public string IbApiPath { get; init; } = string.Empty;

    /// <summary>Adres komputera z TWS / IB Gateway.</summary>
    public string IbHost { get; init; } = "127.0.0.1";

    /// <summary>Port API TWS (7497 – konto demo, 7496 – rzeczywiste, 4002/4001 – IB Gateway).</summary>
    public int IbPort { get; init; } = 7497;

    /// <summary>Identyfikator klienta API.</summary>
    public int IbClientId { get; init; } = 1;

    /// <summary>Przestarzałe: wspólny miesiąc kontraktu – przenoszony do ES i NQ (teraz jest przy instrumencie).</summary>
    public string IbContractMonth { get; init; } = string.Empty;

    /// <summary>Rodzaj danych z IB: na żywo (wymaga subskrypcji) / opóźnione / opóźnione zamrożone.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<IbDataType>))]
    public IbDataType IbDataType { get; init; } = IbDataType.Delayed;

    /// <summary>Parametry połączenia z IB w postaci używanej przez źródło danych.</summary>
    public IbConnectionSettings ToIbSettings() => new()
    {
        Host = IbHost,
        Port = IbPort,
        ClientId = IbClientId,
        DataType = IbDataType,
    };

    /// <summary>Czy zapisywać dane od brokera (ticki i świece) w lokalnej bazie do późniejszych symulacji.</summary>
    public bool RecordToDatabase { get; init; } = true;

    /// <summary>
    /// Ile dni historii (świece 1-min) trzymać w bazie dla instrumentów z zaznaczonym „Pobieraj dane”.
    /// Brakujące dni są pobierane w tle po połączeniu z brokerem.
    /// </summary>
    public int BackgroundHistoryDays { get; init; } = 20;

    /// <summary>
    /// Ile ostatnich dni kompletnych ticków (każda transakcja) pobierać w tle dla instrumentów z „Pobieraj dane”
    /// – symulacja odtwarza wtedy rynek dokładnie tak, jak wyglądał. 0 = tylko świece 1-min.
    /// Pobieranie ticków jest powolne (limity zapytań IB: ok. 40 min na dzień handlowy ES).
    /// </summary>
    public int BackgroundTickDays { get; init; } = 5;

    /// <summary>Czy pokazywać na wykresie wskaźnik tempa budowania TPO (ze wskazówką).</summary>
    public bool TempoGauge { get; init; } = true;

    /// <summary>Położenie wskaźnika tempa na wykresie.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TempoGaugePosition>))]
    public TempoGaugePosition TempoGaugePosition { get; init; } = TempoGaugePosition.UnderTimer;

    /// <summary>Czy pokazywać linię z tempem (wartość, stan, ostatnie zatrzymanie) w pasku na dole okna.</summary>
    public bool TempoInStatusBar { get; init; } = true;

    /// <summary>Czy pokazywać pod paskiem narzędzi pasek stanu pobierania ticków (postęp i komplet dni).</summary>
    public bool ShowTickDownloadBar { get; init; } = true;

    /// <summary>
    /// Czy wykonano jednorazowe zaznaczenie „Pobieraj dane” dla ES i NQ w ustawieniach zapisanych przez
    /// wcześniejsze wersje (wtedy tej opcji nie było).
    /// </summary>
    public bool DownloadDefaultsApplied { get; init; }

    /// <summary>Instrumenty, których historia jest pobierana w tle.</summary>
    [JsonIgnore]
    public IReadOnlyList<Instrument> DownloadInstruments => Instruments.Where(i => i.DownloadData).ToList();

    /// <summary>
    /// Czy zmiana ustawień wymaga ponownego uruchomienia pobierania w tle
    /// (inne instrumenty, liczba dni albo parametry połączenia z brokerem).
    /// </summary>
    public bool RequiresDownloadRestart(AppSettings other) =>
        BackgroundHistoryDays != other.BackgroundHistoryDays || BackgroundTickDays != other.BackgroundTickDays ||
        RecordToDatabase != other.RecordToDatabase ||
        LiveSource != other.LiveSource || IbApiPath != other.IbApiPath || IbHost != other.IbHost ||
        IbPort != other.IbPort || IbClientId != other.IbClientId || IbDataType != other.IbDataType ||
        !DownloadInstruments.SequenceEqual(other.DownloadInstruments) ||
        !Equals(BroadMarketInstrument, other.BroadMarketInstrument);

    /// <summary>Ile dni danych testowych generować do bazy.</summary>
    public int TestDataDays { get; init; } = 5;

    /// <summary>
    /// Ile ostatnich profili ma się mieścić na ekranie (szerokość liter jest dopasowywana automatycznie).
    /// Aplikacja pobiera też tyle historii, ile potrzeba na tę liczbę profili.
    /// </summary>
    public int ProfilesOnScreen { get; init; } = 6;

    /// <summary>Nazwy sesji, których profile są ukryte (np. "Nocna").</summary>
    public List<string> HiddenSessions { get; init; } = [];

    /// <summary>Dla których profili rysować profil wolumenowy.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<VolumeProfileMode>))]
    public VolumeProfileMode VolumeProfile { get; init; } = VolumeProfileMode.Current;

    /// <summary>Wygląd profilu wolumenowego (odległość, długość, kierunek, kolory).</summary>
    public VolumeProfileStyle VolumeStyle { get; init; } = new();

    /// <summary>Profil wolumenowy aktualnie budowanego słupka TPO (bieżącego okresu).</summary>
    public PeriodVolumeStyle PeriodVolume { get; init; } = new();

    /// <summary>Liczby wolumenu przy słupkach profili wolumenowych.</summary>
    public VolumeLabelStyle VolumeLabels { get; init; } = new();

    /// <summary>Schemat kolorów profili sesji dziennej.</summary>
    public SessionColorScheme DayColors { get; init; } = SessionColorScheme.DefaultDay;

    /// <summary>Schemat kolorów profili sesji nocnej.</summary>
    public SessionColorScheme NightColors { get; init; } = SessionColorScheme.DefaultNight;

    /// <summary>Czy pokazywać na profilach pasek Initial Balance.</summary>
    public bool ShowInitialBalance { get; init; } = true;

    /// <summary>Czy pokazywać na profilach POC (ramka wiersza, wyróżnione litery, wartość w nagłówku).</summary>
    public bool ShowPoc { get; init; } = true;

    /// <summary>Strzałki początku (otwarcia) i końca (zamknięcia) rysowania profilu.</summary>
    public ProfileArrowsStyle Arrows { get; init; } = new();

    /// <summary>Kreska połowy zakończonego okresu TPO w trybie Split (włączenie, kolor, grubość).</summary>
    public SplitMidlineStyle SplitMidline { get; init; } = new();

    /// <summary>Linia HalfBack – połowa wysokości profilu (włączenie, styl linii, kolor, grubość, krycie).</summary>
    public HalfBackStyle HalfBack { get; init; } = new();

    /// <summary>Podświetlenie tła profilu wybranego w tabeli „Parametry profili” (kolor, krycie).</summary>
    public ProfileHighlightStyle SelectedProfileHighlight { get; init; } = new();

    /// <summary>Zegar bieżącego okresu TPO na wykresie (położenie, czcionka, kolor).</summary>
    public PeriodTimerStyle PeriodTimer { get; init; } = new();

    /// <summary>Wykres D/W/M: rodzaj prezentacji, kolory, wolumen i średnie kroczące.</summary>
    public CandleChartStyle CandleChart { get; init; } = new();

    /// <summary>Średnie dzienne (wartości „na dziś”) jako linie na wykresie TPO.</summary>
    public TpoAveragesStyle TpoAverages { get; init; } = new();

    /// <summary>
    /// Symbol „rynku szerokiego” (domyślnie SPX) – na wykresie D/W/M można przełączyć się z instrumentu na ten
    /// instrument; jego świece D/W/M są pobierane w tle i zapisywane w bazie (do analiz).
    /// </summary>
    public string BroadMarketSymbol { get; init; } = Instrument.DefaultBroadMarketSymbol;

    /// <summary>Instrument rynku szerokiego (z listy, a gdy go tam nie ma – z listy wbudowanej); null = nieznany symbol.</summary>
    [JsonIgnore]
    public Instrument? BroadMarketInstrument => Instrument.Find(Instruments, BroadMarketSymbol);

    /// <summary>
    /// Czy zmiana ustawień wymaga przebudowania profili od nowa (np. inna wysokość wiersza albo godziny sesji).
    /// Zmiany samego wyglądu (kolory, profil wolumenowy) nie wymagają restartu pobierania danych.
    /// </summary>
    public bool RequiresRebuild(AppSettings other) =>
        InstrumentSymbol != other.InstrumentSymbol ||
        // Zmieniono definicję wybranego instrumentu (sesje, tick...); samo „Pobieraj dane” nie przebudowuje profili.
        !Equals(Instrument with { DownloadData = false }, other.Instrument with { DownloadData = false }) ||
        TicksPerRow != other.TicksPerRow || TpoMinutes != other.TpoMinutes ||
        InitialBalancePeriods != other.InitialBalancePeriods || PocTieBreak != other.PocTieBreak ||
        ValueAreaPercent != other.ValueAreaPercent || HistoryDays != other.HistoryDays ||
        SimulatorSpeed != other.SimulatorSpeed || RecordToDatabase != other.RecordToDatabase ||
        LiveSource != other.LiveSource || IbApiPath != other.IbApiPath || IbHost != other.IbHost ||
        IbPort != other.IbPort || IbClientId != other.IbClientId || IbDataType != other.IbDataType;

    /// <summary>
    /// Ile dni historii trzeba wczytać, żeby było co najmniej <see cref="ProfilesOnScreen"/> profili:
    /// liczba dni handlowych (profile ÷ widoczne sesje na dzień) przeliczona na dni kalendarzowe
    /// z zapasem na weekendy. Nigdy mniej niż ustawienie „Historia (dni)”.
    /// </summary>
    public int RequiredHistoryDays()
    {
        var sessionsPerDay = Math.Max(1, Instrument.Sessions.Count(s => !HiddenSessions.Contains(s.Name)));
        var tradingDays = (int)Math.Ceiling(ProfilesOnScreen / (double)sessionsPerDay);
        var calendarDays = (int)Math.Ceiling(tradingDays * 7 / 5.0) + 1;
        return Math.Max(HistoryDays, calendarDays);
    }

    /// <summary>Strefa czasowa wybranego instrumentu – w niej podawane są godziny sesji i czas symulacji.</summary>
    [JsonIgnore]
    public TimeZoneInfo ExchangeTimeZone => Instrument.TimeZone;

    /// <summary>
    /// Zamienia tekst "yyyy-MM-dd HH:mm" (czas Nowego Jorku) na dokładny moment w czasie.
    /// </summary>
    public bool TryParseExchangeTime(string text, out DateTimeOffset time)
    {
        time = default;
        if (!DateTime.TryParseExact(text?.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return false;
        }

        var zone = ExchangeTimeZone;
        time = new DateTimeOffset(local, zone.GetUtcOffset(local));
        return true;
    }

    /// <summary>Wybrany instrument (z listy <see cref="Instruments"/>; gdy go nie ma – pierwszy z listy albo ES).</summary>
    [JsonIgnore]
    public Instrument Instrument =>
        Instruments.FirstOrDefault(i => i.Symbol == InstrumentSymbol) ?? Instruments.FirstOrDefault() ?? Instrument.ES;

    /// <summary>
    /// Uzupełnia pustą listę instrumentów gotową listą popularnych. Jednorazowo przenosi do ES i NQ
    /// dotychczasowe wspólne godziny sesji i miesiąc kontraktu, żeby wcześniejsze profile się nie zmieniły.
    /// </summary>
    public AppSettings WithInstrumentsInitialized()
    {
        if (Instruments.Count > 0)
        {
            return WithDownloadDefaults();
        }

        TimeOnly Parse(string text, TimeOnly fallback) =>
            TimeOnly.TryParseExact(text?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : fallback;

        var instruments = Instrument.BuiltIn
            .Select(i => i.Symbol is "ES" or "NQ"
                ? i with
                {
                    DayStart = Parse(DaySessionStart, i.DayStart),
                    DayEnd = Parse(DaySessionEnd, i.DayEnd),
                    NightStart = Parse(NightSessionStart, i.NightStart),
                    NightEnd = Parse(NightSessionEnd, i.NightEnd),
                    ContractMonth = IbContractMonth,
                    TicksPerRow = 0, // ES/NQ korzystały dotąd ze wspólnej wysokości wiersza – zostaje bez zmian
                }
                : i)
            .ToList();

        return this with { Instruments = instruments, DownloadDefaultsApplied = true };
    }

    /// <summary>
    /// Jednorazowo zaznacza „Pobieraj dane” dla ES i NQ w liście instrumentów zapisanej przez wcześniejszą wersję.
    /// </summary>
    private AppSettings WithDownloadDefaults()
    {
        if (DownloadDefaultsApplied)
        {
            return this;
        }

        var instruments = Instruments
            .Select(i => i.Symbol is "ES" or "NQ" ? i with { DownloadData = true } : i)
            .ToList();
        return this with { Instruments = instruments, DownloadDefaultsApplied = true };
    }

    /// <summary>
    /// Zamienia ustawienia aplikacji na ustawienia modelu profilu (warstwa Core nie zna pliku ustawień).
    /// </summary>
    public ProfileSettings ToProfileSettings() => new()
    {
        TickSize = Instrument.TickSize,
        TicksPerRow = Instrument.TicksPerRow > 0 ? Instrument.TicksPerRow : TicksPerRow,
        TimeZone = Instrument.TimeZone,
        TpoPeriod = TimeSpan.FromMinutes(TpoMinutes),
        ValueAreaPercent = ValueAreaPercent / 100m,
        InitialBalancePeriods = InitialBalancePeriods,
        PocTieBreak = PocTieBreak,
        Sessions = Instrument.Sessions, // godziny sesji (i strefa czasowa) są osobne dla każdego instrumentu
    };

    /// <summary>Zamienia tekst "HH:mm" na godzinę.</summary>
    public static TimeOnly ParseTime(string text) =>
        TimeOnly.ParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>Tryb wyświetlania profilu wolumenowego.</summary>
public enum VolumeProfileMode
{
    /// <summary>Profil wolumenowy wyłączony.</summary>
    None,

    /// <summary>Tylko dla bieżącego (tworzonego na żywo) profilu.</summary>
    Current,

    /// <summary>Dla wszystkich profili.</summary>
    All,
}

/// <summary>Skala długości słupków profilu wolumenowego.</summary>
public enum VolumeScaleMode
{
    /// <summary>Liniowa – długość proporcjonalna do wolumenu.</summary>
    Linear,

    /// <summary>Pierwiastkowa – łagodnie spłaszcza duże wolumeny.</summary>
    SquareRoot,

    /// <summary>Logarytmiczna – mocno spłaszcza duże wolumeny, małe są lepiej widoczne.</summary>
    Logarithmic,
}

/// <summary>
/// Wygląd profilu wolumenowego.
/// </summary>
public sealed record VolumeProfileStyle
{
    /// <summary>Odległość (w pikselach) między literami TPO a profilem wolumenowym.</summary>
    public int Gap { get; init; } = 6;

    /// <summary>Długość najdłuższego słupka (w pikselach) – słupek wiersza z największym wolumenem.</summary>
    public int Width { get; init; } = 70;

    /// <summary>
    /// Profil odwrócony: słupki rosną od prawej krawędzi w stronę liter TPO
    /// (zamiast od liter w prawo).
    /// </summary>
    public bool Mirrored { get; init; }

    /// <summary>Kolor słupków wolumenu (#RRGGBB).</summary>
    public string BarColor { get; init; } = "#5A6E8C";

    /// <summary>Kolor słupka VPOC – wiersza z największym wolumenem (#RRGGBB).</summary>
    public string VpocColor { get; init; } = "#FF9E40";

    /// <summary>Krycie słupków w procentach (100 = pełny kolor, mniej = przezroczyste).</summary>
    public int Opacity { get; init; } = 60;

    /// <summary>Skala długości słupków (liniowa, pierwiastkowa, logarytmiczna).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<VolumeScaleMode>))]
    public VolumeScaleMode Scale { get; init; } = VolumeScaleMode.Linear;

    /// <summary>
    /// Limit w procentach największego wolumenu: wolumen powyżej limitu jest rysowany jako słupek
    /// o pełnej długości (obcięty). 100 = bez limitu. Np. 60 – słupki skalowane do 60% maksimum,
    /// dzięki czemu kilka bardzo dużych wolumenów nie "spłaszcza" reszty profilu.
    /// </summary>
    public int CapPercent { get; init; } = 100;
}

/// <summary>
/// Profil wolumenowy aktualnie budowanego słupka TPO (bieżącego okresu, np. 30 min).
/// Rysowany przy ostatnim widocznym profilu: w trybie Split tuż obok kolumny bieżącego słupka,
/// w trybie zwykłym – między literami TPO a profilem wolumenowym całej sesji.
/// </summary>
public sealed record PeriodVolumeStyle
{
    /// <summary>Czy rysować profil wolumenowy bieżącego słupka.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Odstęp (px) od liter TPO (strzałki zamknięcia) do profilu wolumenowego bieżącego słupka.</summary>
    public int GapFromTpo { get; init; } = 6;

    /// <summary>Odstęp (px) między profilem bieżącego słupka a profilem wolumenowym całej sesji.</summary>
    public int GapToSessionVolume { get; init; } = 10;

    /// <summary>Długość najdłuższego słupka (px).</summary>
    public int Width { get; init; } = 40;

    /// <summary>Kolor słupków (#RRGGBB).</summary>
    public string BarColor { get; init; } = "#4FC3F7";

    /// <summary>Krycie słupków (%).</summary>
    public int Opacity { get; init; } = 60;

    /// <summary>Skala długości słupków (względem poprzednich słupków albo własnego maksimum).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PeriodVolumeScaleMode>))]
    public PeriodVolumeScaleMode ScaleMode { get; init; } = PeriodVolumeScaleMode.Reference;

    /// <summary>Z ilu ostatnich zakończonych słupków tej samej sesji liczyć skalę odniesienia (mediana).</summary>
    public int ReferencePeriods { get; init; } = 10;
}

/// <summary>Skala profilu wolumenowego bieżącego słupka TPO.</summary>
public enum PeriodVolumeScaleMode
{
    /// <summary>
    /// Względem poprzednich słupków: pełna długość = typowy (mediana) największy wolumen wiersza z ostatnich
    /// zakończonych słupków tej samej sesji – na początku okresu słupki są krótkie i rosną z wolumenem.
    /// </summary>
    Reference,

    /// <summary>Własne maksimum: najdłuższy słupek zawsze ma pełną długość (nawet przy kilku kontraktach).</summary>
    OwnMaximum,
}

/// <summary>Gdzie rysować liczbę wolumenu względem słupka.</summary>
public enum VolumeLabelPosition
{
    /// <summary>Na słupku (od strony jego początku).</summary>
    OnBar,

    /// <summary>Na prawo od słupka.</summary>
    Right,

    /// <summary>Na lewo od słupka.</summary>
    Left,
}

/// <summary>
/// Liczby wolumenu przy słupkach profilu wolumenowego – aktualnie zliczony wolumen wiersza.
/// </summary>
public sealed record VolumeLabelStyle
{
    /// <summary>Czy pokazywać liczby przy profilu wolumenowym sesji.</summary>
    public bool ShowOnSessionVolume { get; init; } = true;

    /// <summary>Czy pokazywać liczby przy profilu wolumenowym bieżącego słupka TPO.</summary>
    public bool ShowOnPeriodVolume { get; init; } = true;

    /// <summary>Położenie liczby względem słupka.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<VolumeLabelPosition>))]
    public VolumeLabelPosition Position { get; init; } = VolumeLabelPosition.OnBar;

    /// <summary>Kolor tekstu (#RRGGBB).</summary>
    public string Color { get; init; } = "#E6E6E6";

    /// <summary>Krycie tekstu (%).</summary>
    public int Opacity { get; init; } = 85;

    /// <summary>Wielkość czcionki (pt).</summary>
    public int FontSize { get; init; } = 9;

    /// <summary>Grubość czcionki.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<FontWeightKind>))]
    public FontWeightKind FontWeight { get; init; } = FontWeightKind.Normal;
}

/// <summary>
/// Kreska połowy okresu w trybie Split: dla każdego zakończonego słupka (okresu TPO) rysowana jest
/// pozioma kreska na wysokości połowy jego zakresu, o szerokości jednej kolumny TPO.
/// </summary>
public sealed record SplitMidlineStyle
{
    /// <summary>Czy rysować kreskę.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Kolor kreski (#RRGGBB).</summary>
    public string Color { get; init; } = "#FFFFFF";

    /// <summary>Grubość kreski w pikselach.</summary>
    public int Thickness { get; init; } = 2;

    /// <summary>Krycie kreski w procentach (100 = pełny kolor, mniej = bardziej przezroczysta).</summary>
    public int Opacity { get; init; } = 70;
}

/// <summary>
/// HalfBack – pozioma linia na połowie wysokości profilu (środek między najwyższym a najniższym wierszem),
/// rysowana przez całą szerokość liter TPO.
/// </summary>
public sealed record HalfBackStyle
{
    /// <summary>Czy rysować linię HalfBack.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Styl linii (ciągła, kreskowana, kropkowana).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LineStyle>))]
    public LineStyle Style { get; init; } = LineStyle.Dashed;

    /// <summary>Kolor linii (#RRGGBB).</summary>
    public string Color { get; init; } = "#E0E0E0";

    /// <summary>Grubość linii w pikselach.</summary>
    public int Thickness { get; init; } = 1;

    /// <summary>Krycie linii w procentach (100 = pełny kolor, mniej = bardziej przezroczysta).</summary>
    public int Opacity { get; init; } = 70;
}

/// <summary>Podświetlenie tła profilu wybranego w tabeli „Parametry profili”.</summary>
public sealed record ProfileHighlightStyle
{
    /// <summary>Kolor tła (#RRGGBB).</summary>
    public string Color { get; init; } = "#FFD54F";

    /// <summary>Krycie tła w procentach (100 = pełny kolor, mniej = bardziej przezroczyste).</summary>
    public int Opacity { get; init; } = 15;
}

/// <summary>Źródło danych w trybie „Na żywo”.</summary>
public enum LiveSourceKind
{
    /// <summary>Generator losowych cen (do testów).</summary>
    Generator,

    /// <summary>Interactive Brokers (przez TWS / IB Gateway).</summary>
    InteractiveBrokers,
}

/// <summary>
/// Strzałki przy profilu: otwarcia (początek rysowania – pierwsza cena profilu, po lewej stronie)
/// i zamknięcia (koniec rysowania – ostatnia cena, po prawej stronie). Obie są skierowane w stronę profilu.
/// </summary>
public sealed record ProfileArrowsStyle
{
    /// <summary>Czy pokazywać strzałkę otwarcia (początku rysowania profilu).</summary>
    public bool ShowOpen { get; init; } = true;

    /// <summary>Czy pokazywać strzałkę zamknięcia (końca rysowania profilu).</summary>
    public bool ShowClose { get; init; } = true;

    /// <summary>Kolor strzałki otwarcia (#RRGGBB).</summary>
    public string OpenColor { get; init; } = "#40C4FF";

    /// <summary>Kolor strzałki zamknięcia (#RRGGBB).</summary>
    public string CloseColor { get; init; } = "#FF9E40";

    /// <summary>Grubość linii strzałki w pikselach.</summary>
    public int Thickness { get; init; } = 2;

    /// <summary>Wielkość grotu strzałki w pikselach.</summary>
    public int HeadSize { get; init; } = 7;

    /// <summary>Całkowita długość strzałki w pikselach.</summary>
    public int Length { get; init; } = 14;
}

/// <summary>Położenie zegara bieżącego okresu TPO na wykresie.</summary>
public enum TimerPosition
{
    /// <summary>Lewy górny róg wykresu.</summary>
    TopLeft,

    /// <summary>Góra, na środku wykresu.</summary>
    TopCenter,

    /// <summary>Prawy górny róg wykresu (obok osi cen).</summary>
    TopRight,
}

/// <summary>Rodzaj prezentacji świec na wykresie D/W/M.</summary>
public enum CandleChartType
{
    /// <summary>Świece japońskie (korpus od otwarcia do zamknięcia, knoty do maksimum i minimum).</summary>
    Candles,

    /// <summary>Słupki OHLC (pionowa kreska maksimum–minimum, otwarcie w lewo, zamknięcie w prawo).</summary>
    OhlcBars,
}

/// <summary>Wygląd wykresu D/W/M.</summary>
public sealed record CandleChartStyle
{
    /// <summary>Świece japońskie albo słupki OHLC.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<CandleChartType>))]
    public CandleChartType Type { get; init; } = CandleChartType.Candles;

    /// <summary>Kolor świecy wzrostowej (zamknięcie ≥ otwarcie), #RRGGBB.</summary>
    public string UpColor { get; init; } = "#30C070";

    /// <summary>Kolor świecy spadkowej, #RRGGBB.</summary>
    public string DownColor { get; init; } = "#E05050";

    /// <summary>Czy pokazywać wolumen w dolnej części wykresu.</summary>
    public bool ShowVolume { get; init; } = true;

    /// <summary>Krycie słupków wolumenu w procentach.</summary>
    public int VolumeOpacity { get; init; } = 45;

    /// <summary>
    /// Co ile minut odświeżać świece D/W/M z IB (instrument i rynek szeroki) przy połączeniu z brokerem.
    /// Ostatnia świeca instrumentu i tak zmienia się na żywo z bieżącymi profilami TPO.
    /// </summary>
    public int IbRefreshMinutes { get; init; } = 5;

    /// <summary>Średnie kroczące rysowane na wykresie (w kolejności z listy).</summary>
    public List<MovingAverageStyle> MovingAverages { get; init; } =
    [
        new() { Period = 20, Color = "#F0C030", Thickness = 1 },
        new() { Period = 50, Color = "#4FA3F7", Thickness = 2 },
        new() { Period = 200, Color = "#E040FB", Thickness = 2, Enabled = false },
    ];
}

/// <summary>Prosta średnia krocząca (SMA) cen zamknięcia na wykresie D/W/M.</summary>
public sealed record MovingAverageStyle
{
    /// <summary>Czy rysować tę średnią.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Liczba świec średniej (np. 20 = średnia z 20 ostatnich zamknięć).</summary>
    public int Period { get; init; } = 20;

    /// <summary>Kolor linii, #RRGGBB.</summary>
    public string Color { get; init; } = "#F0C030";

    /// <summary>Grubość linii w pikselach.</summary>
    public int Thickness { get; init; } = 1;

    /// <summary>Czy rysować poziom tej średniej (z wykresu dziennego, wartość „na dziś”) na wykresie profili TPO.</summary>
    public bool ShowOnTpo { get; init; } = true;

    /// <summary>Opis linii na wykresie TPO; pusty = automatyczny (np. „SMA20 D”).</summary>
    public string TpoLabel { get; init; } = string.Empty;

    /// <summary>Styl linii na wykresie TPO.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LineStyle>))]
    public LineStyle TpoLineStyle { get; init; } = LineStyle.Dashed;

    /// <summary>Grubość linii na wykresie TPO (px).</summary>
    public int TpoThickness { get; init; } = 1;

    /// <summary>Na wykresie TPO: linia albo wąski pasek (strefa wokół średniej).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TpoAverageShape>))]
    public TpoAverageShape TpoShape { get; init; } = TpoAverageShape.Line;

    /// <summary>Szerokość (wysokość) paska na wykresie TPO w pikselach.</summary>
    public int TpoBandHeight { get; init; } = 6;

    /// <summary>Krycie paska na wykresie TPO w procentach.</summary>
    public int TpoBandOpacity { get; init; } = 30;

    /// <summary>Czy linia / pasek na wykresie TPO ma migać, gdy cena zbliży się do średniej.</summary>
    public bool TpoBlink { get; init; } = true;

    /// <summary>Kolor na wykresie TPO (#RRGGBB); pusty = kolor średniej z wykresu dziennego.</summary>
    public string TpoColor { get; init; } = string.Empty;

    /// <summary>Kolor na wykresie TPO (własny albo z wykresu dziennego).</summary>
    public string EffectiveTpoColor => string.IsNullOrWhiteSpace(TpoColor) ? Color : TpoColor;

    /// <summary>Opis linii na wykresie TPO (własny albo automatyczny).</summary>
    public string EffectiveTpoLabel => string.IsNullOrWhiteSpace(TpoLabel) ? DefaultTpoLabel(Period) : TpoLabel.Trim();

    /// <summary>Automatyczny opis średniej dziennej, np. „SMA20 D”.</summary>
    public static string DefaultTpoLabel(int period) => $"SMA{period} D";
}

/// <summary>Sposób rysowania średniej na wykresie TPO.</summary>
public enum TpoAverageShape
{
    /// <summary>Linia (styl i grubość z ustawień).</summary>
    Line,

    /// <summary>Wąski półprzezroczysty pasek wyśrodkowany na średniej (szerokość w px i krycie z ustawień).</summary>
    Band,
}

/// <summary>
/// Poziomy średnich kroczących z wykresu dziennego (wartości „na dziś”) rysowane jako linie poziome na wykresie TPO –
/// widać, że cena zbliża się do znaczącej średniej. Które średnie, styl i opis – przy średnich wykresu D/W/M.
/// </summary>
public sealed record TpoAveragesStyle
{
    /// <summary>Czy rysować średnie na wykresie TPO.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Po której stronie wykresu opis linii.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LabelPosition>))]
    public LabelPosition LabelPosition { get; init; } = LabelPosition.Left;

    /// <summary>Czy dopisywać do opisu wartość średniej (np. „SMA20 D 6712.40”).</summary>
    public bool ShowValueInLabel { get; init; } = true;

    /// <summary>Czy pokazywać cenę średniej na osi cen (w kolorze średniej).</summary>
    public bool ShowAxisLabel { get; init; } = true;

    /// <summary>Krycie linii i opisu (%).</summary>
    public int Opacity { get; init; } = 85;

    /// <summary>Wielkość czcionki opisu (pt).</summary>
    public int FontSize { get; init; } = 11;

    /// <summary>Czy linie / paski średnich migają, gdy cena zbliży się do nich (włącznik główny; każda średnia – własny).</summary>
    public bool BlinkEnabled { get; init; } = true;

    /// <summary>Od jakiej odległości ceny od średniej (w tickach) linia zaczyna migać.</summary>
    public int BlinkDistanceTicks { get; init; } = 8;

    /// <summary>Tempo migania – czas jednej fazy (widoczna / wygaszona) w milisekundach.</summary>
    public int BlinkIntervalMs { get; init; } = 500;
}

/// <summary>Położenie wskaźnika tempa budowania TPO na wykresie.</summary>
public enum TempoGaugePosition
{
    /// <summary>Pod zegarem TPO (wyrównany do niego); bez zegara – w miejscu zegara.</summary>
    UnderTimer,

    /// <summary>Lewy górny róg wykresu.</summary>
    TopLeft,

    /// <summary>Góra, na środku wykresu.</summary>
    TopCenter,

    /// <summary>Prawy górny róg wykresu.</summary>
    TopRight,

    /// <summary>Lewy dolny róg wykresu.</summary>
    BottomLeft,

    /// <summary>Prawy dolny róg wykresu.</summary>
    BottomRight,
}

/// <summary>Grubość czcionki.</summary>
public enum FontWeightKind
{
    /// <summary>Zwykła.</summary>
    Normal,

    /// <summary>Półgruba.</summary>
    SemiBold,

    /// <summary>Gruba (pogrubiona).</summary>
    Bold,
}

/// <summary>
/// Zegar bieżącego okresu TPO: litera rysowanego okresu, jego przedział czasu
/// i czas pozostały do końca okresu – wyświetlany na wykresie podczas pracy na żywo i w symulacji.
/// </summary>
public sealed record PeriodTimerStyle
{
    /// <summary>Czy wyświetlać zegar.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Położenie zegara na wykresie.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TimerPosition>))]
    public TimerPosition Position { get; init; } = TimerPosition.TopRight;

    /// <summary>Wielkość czcionki (w punktach).</summary>
    public int FontSize { get; init; } = 16;

    /// <summary>Grubość czcionki.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<FontWeightKind>))]
    public FontWeightKind FontWeight { get; init; } = FontWeightKind.Bold;

    /// <summary>Kolor czcionki (#RRGGBB).</summary>
    public string Color { get; init; } = "#30C070";
}

/// <summary>Sposób kolorowania liter TPO.</summary>
public enum LetterColorMode
{
    /// <summary>Każdy okres TPO w innym kolorze (kolejne barwy koła kolorów).</summary>
    Rainbow,

    /// <summary>Wszystkie litery w jednym kolorze.</summary>
    Single,
}

/// <summary>
/// Schemat kolorów profilu – osobny dla sesji dziennej i nocnej.
/// </summary>
public sealed record SessionColorScheme
{
    /// <summary>Sposób kolorowania liter TPO.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LetterColorMode>))]
    public LetterColorMode LetterMode { get; init; } = LetterColorMode.Rainbow;

    /// <summary>Kolor liter w trybie jednego koloru (#RRGGBB).</summary>
    public string LetterColor { get; init; } = "#C8CDD4";

    /// <summary>Kolor wiersza POC – litery i ramka (#RRGGBB).</summary>
    public string PocColor { get; init; } = "#FF5050";

    /// <summary>Kolor tła obszaru wartości (Value Area) (#RRGGBB).</summary>
    public string ValueAreaColor { get; init; } = "#4090FF";

    /// <summary>Krycie tła Value Area w procentach.</summary>
    public int ValueAreaOpacity { get; init; } = 19;

    /// <summary>Kolor paska Initial Balance (#RRGGBB).</summary>
    public string IbColor { get; init; } = "#F0C030";

    /// <summary>Domyślne kolory sesji dziennej – kolorowe litery.</summary>
    public static SessionColorScheme DefaultDay { get; } = new();

    /// <summary>Domyślne kolory sesji nocnej – stonowane, jednokolorowe litery.</summary>
    public static SessionColorScheme DefaultNight { get; } = new()
    {
        LetterMode = LetterColorMode.Single,
        LetterColor = "#7A8CA8",
        ValueAreaColor = "#8090A0",
        ValueAreaOpacity = 15,
    };
}

/// <summary>
/// Odczyt i zapis ustawień do pliku JSON.
/// </summary>
public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarketProfile", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Wczytuje ustawienia; jeśli plik nie istnieje lub jest uszkodzony – zwraca domyślne.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                // Lista instrumentów: uzupełniana przy pierwszym uruchomieniu nowej wersji (z przeniesieniem starych godzin sesji).
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings())
                    .WithInstrumentsInitialized();
            }
        }
        catch (Exception)
        {
            // Uszkodzony plik – wracamy do ustawień domyślnych.
        }

        return new AppSettings().WithInstrumentsInitialized();
    }

    /// <summary>Zapisuje ustawienia do pliku.</summary>
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
