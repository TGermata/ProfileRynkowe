namespace MarketProfile.Core;

/// <summary>Kształt profilu.</summary>
public enum ProfileShape
{
    /// <summary>Za mało danych, żeby ocenić kształt.</summary>
    None,

    /// <summary>„D” – równowaga: wartość i POC w środku zakresu, rozkład symetryczny.</summary>
    D,

    /// <summary>„P” – wartość i POC w górnej części zakresu, cienki ogon na dole (zwykle zamykanie krótkich pozycji).</summary>
    P,

    /// <summary>„b” – wartość i POC w dolnej części zakresu, cienki ogon u góry (zwykle likwidacja długich pozycji).</summary>
    LowerB,

    /// <summary>„B” – podwójny rozkład: dwa skupiska TPO rozdzielone przewężeniem.</summary>
    DoubleB,

    /// <summary>Trend – profil wąski i wydłużony (mało TPO na poziom ceny).</summary>
    Trend,
}

/// <summary>Gdzie otworzyła się sesja względem poprzedniego profilu tego samego typu.</summary>
public enum OpenLocation
{
    None,
    AboveRange,
    AboveValue,
    InValue,
    BelowValue,
    BelowRange,
}

/// <summary>Typ otwarcia (wg Dalton – na podstawie dwóch pierwszych liter).</summary>
public enum OpenType
{
    /// <summary>Jeszcze nie wiadomo (mniej niż dwie litery).</summary>
    None,

    /// <summary>Open-Drive – cena od otwarcia zdecydowanie w jedną stronę, bez powrotu do ceny otwarcia.</summary>
    OpenDrive,

    /// <summary>Open-Test-Drive – najpierw test w jedną stronę, potem zdecydowany ruch w drugą.</summary>
    OpenTestDrive,

    /// <summary>Open-Rejection-Reverse – ruch w jedną stronę odrzucony, cena wraca przez otwarcie na drugą stronę.</summary>
    OpenRejectionReverse,

    /// <summary>Open-Auction – rotacja wokół ceny otwarcia, bez wyraźnego kierunku.</summary>
    OpenAuction,
}

/// <summary>Migracja obszaru wartości względem poprzedniego profilu tego samego typu.</summary>
public enum ValueMigration
{
    None,
    Higher,
    OverlappingHigher,
    OverlappingLower,
    Lower,
    Inside,

    /// <summary>Wartość szersza – obejmuje całą wartość dnia poprzedniego (pokrywająca).</summary>
    Outside,

    /// <summary>Dzień trendowy – wartości nie bierze się pod uwagę.</summary>
    TrendDay,
}

/// <summary>Inwentarz na otwarciu sesji dziennej – względem SETTLE poprzedniego dnia.</summary>
public enum OpeningInventory
{
    /// <summary>Brak (sesja nocna albo brak poprzedniego dnia).</summary>
    None,

    /// <summary>Otwarcie powyżej SETTLE poprzedniego dnia – inwentarz długi.</summary>
    Long,

    /// <summary>Otwarcie poniżej SETTLE poprzedniego dnia – inwentarz krótki.</summary>
    Short,

    /// <summary>Otwarcie dokładnie na SETTLE – bez inwentarza.</summary>
    Neutral,
}

/// <summary>Otwarcie sesji dziennej względem zakresu poprzedzającej ją sesji nocnej.</summary>
public enum OvernightLocation
{
    None,
    AboveHigh,
    InRange,
    BelowLow,
}

/// <summary>Wybicie z Initial Balance.</summary>
public enum RangeExtension
{
    None,
    Up,
    Down,
    Both,
}

/// <summary>
/// Interwałowanie: najdłuższa seria okresów TPO, w której początek (cena otwarcia) każdego okresu jest wyżej
/// (trend w górę) albo niżej (trend w dół) niż początek poprzedniego. Pierwszy początek nie wyżej (nie niżej) kończy serię.
/// </summary>
/// <param name="Direction">1 = w górę, −1 = w dół, 0 = brak serii (mniej niż 2 okresy).</param>
/// <param name="From">Pierwsza litera serii.</param>
/// <param name="To">Ostatnia litera serii.</param>
/// <param name="Periods">Liczba okresów serii (od pierwszego do ostatniego, np. B–E = 4).</param>
/// <param name="IsTrendDay">Seria trwa przez większą część sesji (≥ <see cref="ProfileAnalysisOptions.TrendDayShare"/>).</param>
/// <param name="PullbackPrice">
/// PULLBACK LOW (trend w górę – najniższy Low po końcu serii) albo PULLBACK HIGH (w dół – najwyższy High);
/// null = nie dzień trendowy albo seria trwa do końca.
/// </param>
/// <param name="PullbackLetter">Litera okresu, w którym wypadł pullback.</param>
public sealed record TrendInfo(int Direction, char? From, char? To, int Periods, bool IsTrendDay, decimal? PullbackPrice, char? PullbackLetter)
{
    public static TrendInfo None { get; } = new(0, null, null, 0, false, null, null);
}

/// <summary>Zakres cen (np. single printy).</summary>
public sealed record PriceRange(decimal Low, decimal High);

/// <summary>
/// Parametry jednego profilu (sesji) do decyzji handlowych i automatycznego tradingu.
/// Ceny poziomów (POC, VAH, VAL, single printy) to dolne granice wierszy TPO – tak jak na wykresie.
/// </summary>
public sealed record ProfileStats
{
    // --- Identyfikacja ---
    public required string Session { get; init; }
    public required DateOnly TradingDate { get; init; }
    public required DateTime SessionStart { get; init; }
    public required DateTime SessionEnd { get; init; }

    /// <summary>Czy sesja jest zakończona (parametry są ostateczne).</summary>
    public bool IsComplete { get; init; }

    /// <summary>Liczba liter (okresów TPO) i ostatnia litera.</summary>
    public int Periods { get; init; }
    public char? LastLetter { get; init; }

    public decimal RowHeight { get; init; }
    public int TpoMinutes { get; init; }

    // --- 1. Podstawowe ---
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal Range { get; init; }
    public int TotalTpo { get; init; }
    public long Volume { get; init; }
    public decimal Poc { get; init; }
    public decimal? VolumePoc { get; init; }
    public decimal ValueAreaHigh { get; init; }
    public decimal ValueAreaLow { get; init; }

    /// <summary>Szerokość obszaru wartości (VAH − VAL + wysokość wiersza).</summary>
    public decimal ValueAreaWidth { get; init; }

    /// <summary>Położenie POC w zakresie sesji: 0 = na dole, 1 = na górze.</summary>
    public double PocPosition { get; init; }

    public decimal? IbHigh { get; init; }
    public decimal? IbLow { get; init; }
    public decimal? IbWidth { get; init; }

    /// <summary>O ile cena wyszła ponad IBH (0 = brak wybicia) i w której literze po raz pierwszy.</summary>
    public decimal IbExtensionUp { get; init; }
    public char? IbExtensionUpLetter { get; init; }

    /// <summary>O ile cena zeszła poniżej IBL (0 = brak wybicia) i w której literze po raz pierwszy.</summary>
    public decimal IbExtensionDown { get; init; }
    public char? IbExtensionDownLetter { get; init; }

    public RangeExtension RangeExtension { get; init; }

    /// <summary>Halfback – połowa zakresu profilu ((High + Low) / 2, zaokrąglona do ticka).</summary>
    public decimal Halfback { get; init; }

    /// <summary>Cena rozliczeniowa (SETTLE) sesji dziennej; null = jeszcze brak (lub sesja nocna).</summary>
    public decimal? Settle { get; init; }

    /// <summary>Źródło ceny rozliczeniowej: "CME" / "ręcznie" (null = brak).</summary>
    public string? SettleSource { get; init; }

    /// <summary>Czy to sesja dzienna (tylko ona bierze udział w relacjach między profilami).</summary>
    public bool IsDaySession => SessionNames.IsDay(Session);

    // --- 2. Struktura ---
    public ProfileShape Shape { get; init; }

    /// <summary>Kształt jako krótki kod: D, P, b, B, Trend (pusty = brak oceny).</summary>
    public string ShapeCode => Shape switch
    {
        ProfileShape.D => "D",
        ProfileShape.P => "P",
        ProfileShape.LowerB => "b",
        ProfileShape.DoubleB => "B",
        ProfileShape.Trend => "Trend",
        _ => string.Empty,
    };

    public int TpoAbovePoc { get; init; }
    public int TpoBelowPoc { get; init; }

    /// <summary>Single printy wewnątrz profilu (co najmniej 2 wiersze z jedną literą, bez ogonów na krańcach).</summary>
    public IReadOnlyList<PriceRange> SinglePrints { get; init; } = [];

    /// <summary>Excess (ogon) u góry / na dole – liczba wierszy z jedną literą na krańcu (0 = brak, ogon to min. 2 wiersze).</summary>
    public int ExcessHigh { get; init; }
    public int ExcessLow { get; init; }

    /// <summary>Poor high / poor low – płaski szczyt / dołek (co najmniej 2 litery na skrajnym wierszu, bez ogona).</summary>
    public bool PoorHigh { get; init; }
    public bool PoorLow { get; init; }

    // --- 3. Relacje z poprzednim profilem tego samego typu ---
    public DateOnly? PreviousTradingDate { get; init; }
    public OpenLocation OpenLocation { get; init; }
    public OpenType OpenType { get; init; }

    /// <summary>Kierunek otwarcia dla OpenDrive / OpenTestDrive / OpenRejectionReverse: 1 = w górę, −1 = w dół, 0 = brak.</summary>
    public int OpenDirection { get; init; }

    public ValueMigration ValueMigration { get; init; }

    /// <summary>Punkty charakterystyczne poprzedniej sesji dziennej.</summary>
    public decimal? PreviousClose { get; init; }
    public decimal? PreviousSettle { get; init; }
    public decimal? PreviousHalfback { get; init; }

    /// <summary>
    /// Luka (GAP) otwarcia względem zakresu poprzedniego dnia: otwarcie nad jego High → Open − High (dodatnia),
    /// pod jego Low → Open − Low (ujemna); otwarcie w zakresie poprzedniego dnia → null (brak luki).
    /// </summary>
    public decimal? Gap { get; init; }

    /// <summary>Otwarcie względem halfback poprzedniego dnia: 1 = nad, −1 = pod, 0 = na poziomie / brak.</summary>
    public int OpenVsPreviousHalfback { get; init; }

    /// <summary>Inwentarz na otwarciu: długi (otwarcie nad SETTLE poprzedniego dnia) / krótki (pod) / neutralny.</summary>
    public OpeningInventory OpeningInventory { get; init; }

    /// <summary>Wielkość inwentarza: Open − SETTLE poprzedniego dnia (dodatnia = długi, ujemna = krótki).</summary>
    public decimal? InventorySize { get; init; }

    /// <summary>
    /// Czy inwentarz został wyrównany – cena wróciła w tej sesji do SETTLE poprzedniego dnia
    /// (w sesji w toku false = jeszcze nie); null = brak inwentarza.
    /// </summary>
    public bool? InventoryCorrected { get; init; }

    /// <summary>Litera okresu, w którym inwentarz został wyrównany.</summary>
    public char? InventoryCorrectedLetter { get; init; }

    /// <summary>Sesja nocna bezpośrednio przed tym dniem – jej High, Low i POC (null = brak nocy).</summary>
    public decimal? OvernightHigh { get; init; }
    public decimal? OvernightLow { get; init; }
    public decimal? OvernightPoc { get; init; }

    /// <summary>Otwarcie dnia względem zakresu nocy.</summary>
    public OvernightLocation OpenVsOvernight { get; init; }

    // --- 4. Trend (interwałowanie) ---

    /// <summary>Kierunek najdłuższego interwałowania: 1 = w górę, −1 = w dół, 0 = brak.</summary>
    public int TrendDirection { get; init; }

    /// <summary>Dzień trendowy – interwałowanie przez większą część sesji (wtedy wartości nie bierze się pod uwagę).</summary>
    public bool IsTrendDay { get; init; }

    /// <summary>Litery początku i końca najdłuższego interwałowania oraz liczba jego okresów.</summary>
    public char? IntervalFrom { get; init; }
    public char? IntervalTo { get; init; }
    public int IntervalPeriods { get; init; }

    /// <summary>PULLBACK LOW (dzień trendowy w górę) – wybicie w drugą stronę po końcu interwałowania.</summary>
    public decimal? PullbackLow { get; init; }

    /// <summary>PULLBACK HIGH (dzień trendowy w dół).</summary>
    public decimal? PullbackHigh { get; init; }

    /// <summary>Litera okresu pullbacku.</summary>
    public char? PullbackLetter { get; init; }

    /// <summary>Jaka część obszaru wartości tej sesji pokrywa się z poprzednim (0–1; null = brak poprzedniego).</summary>
    public double? ValueAreaOverlap { get; init; }

    /// <summary>
    /// Nietknięte (naked) POC i VPOC poprzednich sesji tego samego typu – poziomy, do których cena (w żadnej
    /// późniejszej sesji, łącznie z tą) jeszcze nie wróciła. Najnowsze na końcu.
    /// </summary>
    public IReadOnlyList<decimal> NakedPocs { get; init; } = [];
    public IReadOnlyList<decimal> NakedVolumePocs { get; init; } = [];
}

/// <summary>Progi analizy profili.</summary>
public sealed record ProfileAnalysisOptions
{
    /// <summary>Ile poprzednich sesji przeszukiwać przy nietkniętych POC.</summary>
    public int NakedPocLookback { get; init; } = 20;

    /// <summary>Dzień trendowy: interwałowanie obejmuje co najmniej taką część okresów sesji.</summary>
    public double TrendDayShare { get; init; } = 0.6;

    /// <summary>Najkrótsze interwałowanie, które może oznaczać dzień trendowy (liczba okresów).</summary>
    public int TrendDayMinPeriods { get; init; } = 4;

    /// <summary>Szansa wyrównania inwentarza z otwarcia (opisowo – do podpowiedzi), ok. 65%.</summary>
    public const double InventoryCorrectionChance = 0.65;

    public static ProfileAnalysisOptions Default { get; } = new();
}

/// <summary>
/// Wylicza <see cref="ProfileStats"/> z migawki profilu sesji i wcześniejszych profili.
/// Kształt, typ otwarcia i migracja wartości to reguły heurystyczne (opisane przy metodach) – powtarzalne, bez zgadywania.
/// </summary>
public static class ProfileAnalyzer
{
    /// <param name="profile">Pełny profil sesji (nie część rozbitego profilu).</param>
    /// <param name="history">Wcześniejsze profile (wszystkie typy sesji) w kolejności chronologicznej.</param>
    /// <param name="isComplete">Czy sesja jest zakończona.</param>
    /// <param name="tpoMinutes">Długość okresu TPO w minutach (zapisywana razem z parametrami).</param>
    /// <param name="settlements">Ceny rozliczeniowe sesji dziennych wg daty handlowej (null = brak).</param>
    public static ProfileStats Analyze(ProfileSnapshot profile, IReadOnlyList<ProfileSnapshot> history, bool isComplete,
        int tpoMinutes, ProfileAnalysisOptions? options = null, IReadOnlyDictionary<DateOnly, Settlement>? settlements = null)
    {
        options ??= ProfileAnalysisOptions.Default;
        var row = profile.RowHeight;
        var levels = profile.Levels;
        var pocIndex = IndexOf(levels, profile.Poc);
        var range = profile.High - profile.Low;

        // Relacje między profilami liczymy tylko dla sesji dziennych (porównanie z poprzednim dniem).
        // Sesja nocna nie bierze w nich udziału – z nocy liczą się tylko jej POC, High i Low (pola Overnight* dnia).
        var isDay = SessionNames.IsDay(profile.Session.Name);
        var previous = isDay
            ? history.LastOrDefault(h => h.Session.Name == profile.Session.Name && !h.IsEmpty && h.SessionStart < profile.SessionStart)
            : null;
        // SETTLE: wpis ręczny, a gdy go brak – cena zamknięcia sesji dziennej (sesja w toku – jeszcze bez settle).
        var settle = isDay && isComplete && !profile.IsEmpty
            ? settlements?.GetValueOrDefault(profile.TradingDate) ??
              new Settlement(profile.TradingDate, profile.LastPrice, Settlement.SourceClose)
            : null;

        // Interwałowanie – planowana liczba okresów sesji w toku (trend tylko, gdy seria obejmuje większość całej sesji).
        var plannedPeriods = tpoMinutes > 0 ? (int)Math.Ceiling((profile.SessionEnd - profile.SessionStart).TotalMinutes / tpoMinutes) : 0;
        var trend = TrendOf(profile.Periods, isComplete ? profile.Periods.Count : Math.Max(profile.Periods.Count, plannedPeriods), options);

        var stats = new ProfileStats
        {
            Session = profile.Session.Name,
            TradingDate = profile.TradingDate,
            SessionStart = profile.SessionStart,
            SessionEnd = profile.SessionEnd,
            IsComplete = isComplete,
            Periods = profile.Periods.Count,
            LastLetter = profile.Periods.Count > 0 ? profile.Periods[^1].Letter : null,
            RowHeight = row,
            TpoMinutes = tpoMinutes,
            Open = profile.Open,
            High = profile.High,
            Low = profile.Low,
            Close = profile.LastPrice,
            Range = range,
            TotalTpo = profile.TotalTpo,
            Volume = profile.TotalVolume,
            Poc = profile.Poc,
            VolumePoc = profile.VolumePoc,
            ValueAreaHigh = profile.ValueAreaHigh,
            ValueAreaLow = profile.ValueAreaLow,
            ValueAreaWidth = profile.IsEmpty ? 0 : profile.ValueAreaHigh - profile.ValueAreaLow + row,
            PocPosition = levels.Count > 1 && pocIndex >= 0 ? Math.Round((double)pocIndex / (levels.Count - 1), 3) : 0.5,
            IbHigh = profile.InitialBalanceHigh,
            IbLow = profile.InitialBalanceLow,
            IbWidth = profile.InitialBalanceHigh - profile.InitialBalanceLow,
            Shape = ShapeOf(levels, pocIndex, trend.IsTrendDay),
            TpoAbovePoc = pocIndex >= 0 ? levels.Skip(pocIndex + 1).Sum(l => l.TpoCount) : 0,
            TpoBelowPoc = pocIndex >= 0 ? levels.Take(pocIndex).Sum(l => l.TpoCount) : 0,
            PreviousTradingDate = previous?.TradingDate,
            Halfback = profile.IsEmpty ? 0 : RoundToTick((profile.High + profile.Low) / 2, profile.TickSize),
            Settle = settle?.Price,
            SettleSource = settle?.Source,
            TrendDirection = trend.Direction,
            IsTrendDay = trend.IsTrendDay,
            IntervalFrom = trend.From,
            IntervalTo = trend.To,
            IntervalPeriods = trend.Periods,
            PullbackLow = trend.Direction > 0 ? trend.PullbackPrice : null,
            PullbackHigh = trend.Direction < 0 ? trend.PullbackPrice : null,
            PullbackLetter = trend.PullbackLetter,
        };

        stats = WithIbExtension(stats, profile);
        stats = WithTails(stats, levels);
        if (!isDay)
        {
            return stats; // noc: tylko parametry własne profilu
        }

        stats = WithOpen(stats, profile, previous);
        stats = WithValueMigration(stats, profile, previous);
        stats = WithPreviousDay(stats, profile, previous, settlements);
        stats = WithOvernight(stats, profile, history);
        return WithNakedPocs(stats, profile, history, options);
    }

    private static decimal RoundToTick(decimal price, decimal tick) =>
        tick > 0 ? Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick : price;

    /// <summary>Punkty charakterystyczne poprzedniego dnia (Close, SETTLE, Halfback) i położenie otwarcia względem nich.</summary>
    private static ProfileStats WithPreviousDay(ProfileStats stats, ProfileSnapshot profile, ProfileSnapshot? previous,
        IReadOnlyDictionary<DateOnly, Settlement>? settlements)
    {
        if (previous is null || profile.IsEmpty)
        {
            return stats;
        }

        var previousSettle = settlements?.GetValueOrDefault(previous.TradingDate)?.Price ?? previous.LastPrice; // brak wpisu – Close
        var previousHalfback = RoundToTick((previous.High + previous.Low) / 2, previous.TickSize);

        // Inwentarz na otwarciu względem SETTLE poprzedniego dnia i jego wyrównanie (powrót ceny do SETTLE).
        var inventory = profile.Open > previousSettle ? OpeningInventory.Long
            : profile.Open < previousSettle ? OpeningInventory.Short
            : OpeningInventory.Neutral;
        var correctedIn = inventory switch
        {
            OpeningInventory.Long => profile.Periods.FirstOrDefault(p => p.Low <= previousSettle),
            OpeningInventory.Short => profile.Periods.FirstOrDefault(p => p.High >= previousSettle),
            _ => null,
        };

        return stats with
        {
            OpeningInventory = inventory,
            InventorySize = profile.Open - previousSettle,
            InventoryCorrected = inventory == OpeningInventory.Neutral ? null : correctedIn is not null,
            InventoryCorrectedLetter = correctedIn?.Letter,
            PreviousClose = previous.LastPrice,
            PreviousSettle = previousSettle,
            PreviousHalfback = previousHalfback,
            Gap = profile.Open > previous.High ? profile.Open - previous.High
                : profile.Open < previous.Low ? profile.Open - previous.Low
                : null,
            OpenVsPreviousHalfback = Math.Sign(profile.Open - previousHalfback),
        };
    }

    /// <summary>
    /// Noc bezpośrednio poprzedzająca dzień: ostatnia sesja nocna przed początkiem dnia, bez innego dnia pomiędzy nimi.
    /// </summary>
    private static ProfileStats WithOvernight(ProfileStats stats, ProfileSnapshot profile, IReadOnlyList<ProfileSnapshot> history)
    {
        var night = history.LastOrDefault(h => !SessionNames.IsDay(h.Session.Name) && !h.IsEmpty && h.SessionStart < profile.SessionStart);
        if (night is null || profile.IsEmpty || history.Any(h => SessionNames.IsDay(h.Session.Name) && !h.IsEmpty &&
                                                                h.SessionStart > night.SessionStart && h.SessionStart < profile.SessionStart))
        {
            return stats;
        }

        var o = profile.Open;
        return stats with
        {
            OvernightHigh = night.High,
            OvernightLow = night.Low,
            OvernightPoc = night.Poc,
            OpenVsOvernight = o > night.High ? OvernightLocation.AboveHigh
                : o < night.Low ? OvernightLocation.BelowLow
                : OvernightLocation.InRange,
        };
    }

    private static int IndexOf(IReadOnlyList<PriceLevel> levels, decimal price)
    {
        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i].Price == price)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Wybicia z IB: o ile ponad IBH / poniżej IBL i pierwsza litera, która wyszła poza IB.</summary>
    private static ProfileStats WithIbExtension(ProfileStats stats, ProfileSnapshot profile)
    {
        if (profile.InitialBalanceHigh is not { } ibh || profile.InitialBalanceLow is not { } ibl)
        {
            return stats;
        }

        var up = Math.Max(0, profile.High - ibh);
        var down = Math.Max(0, ibl - profile.Low);
        return stats with
        {
            IbExtensionUp = up,
            IbExtensionUpLetter = profile.Periods.FirstOrDefault(p => p.High > ibh)?.Letter,
            IbExtensionDown = down,
            IbExtensionDownLetter = profile.Periods.FirstOrDefault(p => p.Low < ibl)?.Letter,
            RangeExtension = (up > 0, down > 0) switch
            {
                (true, true) => RangeExtension.Both,
                (true, false) => RangeExtension.Up,
                (false, true) => RangeExtension.Down,
                _ => RangeExtension.None,
            },
        };
    }

    /// <summary>
    /// Kształt (kolejność sprawdzania):
    ///  - B: dwa wyraźne skupiska TPO (wygładzone liczby TPO), przewężenie między nimi ≤ 50% mniejszego szczytu,
    ///    każde skupisko ma co najmniej 25% TPO;
    ///  - Trend: dzień trendowy wg interwałowania (<see cref="TrendOf"/>) – sprawdzany najpierw;
    ///  - P / b: środek ciężkości TPO i POC w górnych / dolnych ~40% zakresu;
    ///  - w pozostałych przypadkach D.
    /// </summary>
    internal static ProfileShape ShapeOf(IReadOnlyList<PriceLevel> levels, int pocIndex, bool isTrendDay)
    {
        if (isTrendDay)
        {
            return ProfileShape.Trend;
        }

        var n = levels.Count;
        var total = levels.Sum(l => l.TpoCount);
        if (n < 5 || total < 5 || pocIndex < 0)
        {
            return ProfileShape.None;
        }

        var counts = levels.Select(l => (double)l.TpoCount).ToArray();
        if (IsDoubleDistribution(counts, total))
        {
            return ProfileShape.DoubleB;
        }

        var centre = counts.Select((c, i) => c * i).Sum() / total / (n - 1);
        var pocPosition = (double)pocIndex / (n - 1);
        if (centre >= 0.58 && pocPosition >= 0.6)
        {
            return ProfileShape.P;
        }

        if (centre <= 0.42 && pocPosition <= 0.4)
        {
            return ProfileShape.LowerB;
        }

        return ProfileShape.D;
    }

    /// <summary>
    /// Interwałowanie i dzień trendowy:
    ///  - seria w górę: początek każdego kolejnego okresu wyżej niż początek poprzedniego (np. C &gt; B, D &gt; C, E &gt; D…);
    ///    pierwszy początek nie wyżej kończy serię; w dół – analogicznie (coraz niżej);
    ///  - dzień trendowy: najdłuższa seria obejmuje ≥ <see cref="ProfileAnalysisOptions.TrendDayShare"/> z
    ///    <paramref name="totalPeriods"/> okresów (i co najmniej <see cref="ProfileAnalysisOptions.TrendDayMinPeriods"/>);
    ///  - pullback (dzień trendowy, seria skończyła się przed ostatnim okresem): przy trendzie w górę najniższy Low
    ///    okresów po końcu serii (PULLBACK LOW), przy trendzie w dół najwyższy High (PULLBACK HIGH).
    /// </summary>
    /// <param name="periods">Okresy sesji w kolejności.</param>
    /// <param name="totalPeriods">Liczba okresów sesji (dla sesji w toku – planowana liczba okresów całej sesji).</param>
    public static TrendInfo TrendOf(IReadOnlyList<TpoPeriod> periods, int totalPeriods, ProfileAnalysisOptions? options = null)
    {
        options ??= ProfileAnalysisOptions.Default;
        int bestStart = 0, bestEnd = 0, bestDirection = 0;
        for (var direction = 1; direction >= -1; direction -= 2)
        {
            var start = 0;
            for (var i = 1; i <= periods.Count; i++)
            {
                var continues = i < periods.Count && periods[i].Open > 0 && periods[i - 1].Open > 0 &&
                                Math.Sign(periods[i].Open - periods[i - 1].Open) == direction;
                if (continues)
                {
                    continue;
                }

                // Seria start..i-1 (co najmniej dwa okresy = jeden wzrost/spadek).
                if (i - 1 > start && i - 1 - start > bestEnd - bestStart)
                {
                    (bestStart, bestEnd, bestDirection) = (start, i - 1, direction);
                }

                start = i;
            }
        }

        if (bestDirection == 0)
        {
            return TrendInfo.None;
        }

        var length = bestEnd - bestStart + 1;
        var isTrendDay = length >= options.TrendDayMinPeriods && length >= Math.Ceiling(options.TrendDayShare * totalPeriods);
        decimal? pullback = null;
        char? pullbackLetter = null;
        if (isTrendDay && bestEnd < periods.Count - 1)
        {
            var after = periods.Skip(bestEnd + 1).ToList();
            var extreme = bestDirection > 0 ? after.MinBy(p => p.Low)! : after.MaxBy(p => p.High)!;
            pullback = bestDirection > 0 ? extreme.Low : extreme.High;
            pullbackLetter = extreme.Letter;
        }

        return new TrendInfo(bestDirection, periods[bestStart].Letter, periods[bestEnd].Letter, length, isTrendDay, pullback, pullbackLetter);
    }

    private static bool IsDoubleDistribution(double[] counts, int total)
    {
        var n = counts.Length;
        var smooth = new double[n];
        for (var i = 0; i < n; i++)
        {
            var from = Math.Max(0, i - 1);
            var to = Math.Min(n - 1, i + 1);
            smooth[i] = counts[from..(to + 1)].Average();
        }

        // Maksima wygładzonych liczb TPO od dołu i od góry – szczyt po każdej stronie każdego wiersza.
        var maxBelow = new double[n];
        var maxAbove = new double[n];
        for (var i = 0; i < n; i++)
        {
            maxBelow[i] = Math.Max(i > 0 ? maxBelow[i - 1] : 0, smooth[i]);
        }

        for (var i = n - 1; i >= 0; i--)
        {
            maxAbove[i] = Math.Max(i < n - 1 ? maxAbove[i + 1] : 0, smooth[i]);
        }

        // Przewężenie w wierszu v: oba szczyty co najmniej 2 TPO, przewężenie ≤ 50% mniejszego z nich,
        // a po każdej stronie co najmniej 25% wszystkich TPO.
        var below = counts[0];
        for (var v = 2; v < n - 2; v++)
        {
            below += counts[v - 1];
            var smallerPeak = Math.Min(maxBelow[v - 2], maxAbove[v + 2]);
            if (smallerPeak < 2 || smooth[v] > 0.5 * smallerPeak)
            {
                continue;
            }

            var above = total - below - counts[v];
            if (below >= 0.25 * total && above >= 0.25 * total)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ogony (excess), poor high/low i single printy wewnątrz profilu.</summary>
    private static ProfileStats WithTails(ProfileStats stats, IReadOnlyList<PriceLevel> levels)
    {
        var n = levels.Count;
        if (n == 0)
        {
            return stats;
        }

        var topRun = 0;
        while (topRun < n && levels[n - 1 - topRun].TpoCount == 1)
        {
            topRun++;
        }

        var bottomRun = 0;
        while (bottomRun < n - topRun && levels[bottomRun].TpoCount == 1)
        {
            bottomRun++;
        }

        // Single printy: odcinki ≥ 2 wierszy z jedną literą między ogonami.
        var singles = new List<PriceRange>();
        var runStart = -1;
        for (var i = bottomRun; i <= n - topRun; i++)
        {
            if (i < n - topRun && levels[i].TpoCount == 1)
            {
                if (runStart < 0)
                {
                    runStart = i;
                }

                continue;
            }

            if (runStart >= 0 && i - runStart >= 2)
            {
                singles.Add(new PriceRange(levels[runStart].Price, levels[i - 1].Price));
            }

            runStart = -1;
        }

        return stats with
        {
            ExcessHigh = topRun >= 2 ? topRun : 0,
            ExcessLow = bottomRun >= 2 ? bottomRun : 0,
            PoorHigh = topRun == 0 && levels[n - 1].TpoCount >= 2,
            PoorLow = bottomRun == 0 && levels[0].TpoCount >= 2,
            SinglePrints = singles,
        };
    }

    /// <summary>
    /// Otwarcie względem poprzedniego profilu i typ otwarcia (A = pierwsza litera, B = druga, O = cena otwarcia):
    ///  - Open-Drive: O przy skraju A (≤ 15% zakresu A), A zamyka się po drugiej stronie, B nie wraca do O;
    ///  - Open-Test-Drive: A testuje stronę przeciwną (15–50% zakresu A), zamyka się po stronie ruchu,
    ///    a B rozszerza zakres w kierunku ruchu (wyższe High i Low przy ruchu w górę);
    ///  - Open-Rejection-Reverse: A jednostronna (ruch od O ≥ 40% zakresu IB i ≥ 2× ruchu w drugą stronę),
    ///    a B zamyka się po drugiej stronie O;
    ///  - Open-Auction: pozostałe przypadki.
    /// </summary>
    private static ProfileStats WithOpen(ProfileStats stats, ProfileSnapshot profile, ProfileSnapshot? previous)
    {
        var o = profile.Open;
        var location = OpenLocation.None;
        if (previous is not null && !profile.IsEmpty)
        {
            var vahTop = previous.ValueAreaHigh + previous.RowHeight;
            location = o > previous.High ? OpenLocation.AboveRange
                : o >= vahTop ? OpenLocation.AboveValue
                : o >= previous.ValueAreaLow ? OpenLocation.InValue
                : o >= previous.Low ? OpenLocation.BelowValue
                : OpenLocation.BelowRange;
        }

        var (type, direction) = OpenTypeOf(profile);
        return stats with { OpenLocation = location, OpenType = type, OpenDirection = direction };
    }

    internal static (OpenType Type, int Direction) OpenTypeOf(ProfileSnapshot profile)
    {
        var periods = profile.Periods;
        if (periods.Count < 2 || periods[0].Index != profile.FirstPeriod)
        {
            return (OpenType.None, 0);
        }

        var a = periods[0];
        var b = periods[1];
        var o = profile.Open;
        var rangeA = a.High - a.Low;
        var ibRange = Math.Max(a.High, b.High) - Math.Min(a.Low, b.Low);
        if (rangeA <= 0 || ibRange <= 0)
        {
            return (OpenType.OpenAuction, 0);
        }

        var fromLow = (o - a.Low) / rangeA;   // 0 = otwarcie na dole A
        var fromHigh = (a.High - o) / rangeA; // 0 = otwarcie na górze A

        if (fromLow <= 0.15m && a.Close > o && b.Low >= o)
        {
            return (OpenType.OpenDrive, 1);
        }

        if (fromHigh <= 0.15m && a.Close < o && b.High <= o)
        {
            return (OpenType.OpenDrive, -1);
        }

        if (fromLow > 0.15m && fromLow <= 0.5m && a.Close > o && b.High > a.High && b.Low > a.Low)
        {
            return (OpenType.OpenTestDrive, 1);
        }

        if (fromHigh > 0.15m && fromHigh <= 0.5m && a.Close < o && b.Low < a.Low && b.High < a.High)
        {
            return (OpenType.OpenTestDrive, -1);
        }

        // Litera A wyraźnie jednostronna (ruch od otwarcia co najmniej 2× większy niż w drugą stronę), potem B zamyka się
        // po drugiej stronie otwarcia. Rotacja wokół otwarcia (A po równo w obie strony) to Open-Auction.
        var aUp = a.High - o;
        var aDown = o - a.Low;
        if (aUp >= 0.4m * ibRange && aUp >= 2 * aDown && b.Close < o)
        {
            return (OpenType.OpenRejectionReverse, -1); // ruch w górę odrzucony – w dół
        }

        if (aDown >= 0.4m * ibRange && aDown >= 2 * aUp && b.Close > o)
        {
            return (OpenType.OpenRejectionReverse, 1); // ruch w dół odrzucony – w górę
        }

        return (OpenType.OpenAuction, 0);
    }

    /// <summary>Migracja obszaru wartości i jego nakładanie się z poprzednim profilem tego samego typu.</summary>
    private static ProfileStats WithValueMigration(ProfileStats stats, ProfileSnapshot profile, ProfileSnapshot? previous)
    {
        if (previous is null || profile.IsEmpty)
        {
            return stats;
        }

        // Dzień trendowy – wartości nie bierze się pod uwagę.
        if (stats.IsTrendDay)
        {
            return stats with { ValueMigration = ValueMigration.TrendDay, ValueAreaOverlap = null };
        }

        var low = profile.ValueAreaLow;
        var top = profile.ValueAreaHigh + profile.RowHeight;
        var prevLow = previous.ValueAreaLow;
        var prevTop = previous.ValueAreaHigh + previous.RowHeight;

        var overlap = Math.Max(0, Math.Min(top, prevTop) - Math.Max(low, prevLow));
        var migration = low >= prevTop ? ValueMigration.Higher
            : top <= prevLow ? ValueMigration.Lower
            : low >= prevLow && top <= prevTop ? ValueMigration.Inside
            : low <= prevLow && top >= prevTop ? ValueMigration.Outside
            : low + top >= prevLow + prevTop ? ValueMigration.OverlappingHigher
            : ValueMigration.OverlappingLower;

        return stats with
        {
            ValueMigration = migration,
            ValueAreaOverlap = top > low ? Math.Round((double)(overlap / (top - low)), 3) : null,
        };
    }

    /// <summary>
    /// Nietknięte POC/VPOC: poziomy z ostatnich sesji tego samego typu, których wiersza nie dotknęła cena
    /// w żadnym późniejszym profilu (dowolnego typu sesji), łącznie z bieżącym.
    /// </summary>
    private static ProfileStats WithNakedPocs(ProfileStats stats, ProfileSnapshot profile, IReadOnlyList<ProfileSnapshot> history,
        ProfileAnalysisOptions options)
    {
        var earlier = history.Where(h => !h.IsEmpty && h.SessionStart < profile.SessionStart).ToList();
        var candidates = earlier
            .Select((h, i) => (Profile: h, Index: i))
            .Where(x => x.Profile.Session.Name == profile.Session.Name)
            .TakeLast(options.NakedPocLookback)
            .ToList();

        bool Touched(decimal level, decimal rowHeight, int index)
        {
            foreach (var later in earlier.Skip(index + 1).Append(profile))
            {
                if (!later.IsEmpty && level <= later.High && level + rowHeight > later.Low)
                {
                    return true;
                }
            }

            return false;
        }

        var pocs = new List<decimal>();
        var vpocs = new List<decimal>();
        foreach (var (candidate, index) in candidates)
        {
            if (!Touched(candidate.Poc, candidate.RowHeight, index))
            {
                pocs.Add(candidate.Poc);
            }

            if (candidate.VolumePoc is { } vpoc && !Touched(vpoc, candidate.RowHeight, index))
            {
                vpocs.Add(vpoc);
            }
        }

        return stats with { NakedPocs = pocs, NakedVolumePocs = vpocs };
    }
}
