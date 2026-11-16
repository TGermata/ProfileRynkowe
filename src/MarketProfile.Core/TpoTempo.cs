namespace MarketProfile.Core;

/// <summary>Stan tempa budowania TPO.</summary>
public enum TempoState
{
    /// <summary>Brak danych (sesja jeszcze nie ma liter).</summary>
    None,

    /// <summary>Litery dokładają nowe poziomy w jednym kierunku co najmniej <see cref="TempoOptions.FastFactor"/>× szybciej niż zwykle.</summary>
    Fast,

    /// <summary>Rynek dokłada nowe poziomy w zwykłym tempie.</summary>
    Normal,

    /// <summary>Rynek dalej dokłada nowe poziomy w tym samym kierunku, ale wolniej niż poprzednia litera i niż zwykle.</summary>
    Slowing,

    /// <summary>Po ruchu kierunkowym litera nie dokłada nowych poziomów – rynek stoi przy osiągniętym ekstremum.</summary>
    Stopped,

    /// <summary>Kilka liter z rzędu bez nowych poziomów, na tych samych wierszach – rynek kręci się w zakresie.</summary>
    Balance,

    /// <summary>W trakcie sesji od końca ostatniej litery nie ma żadnych transakcji (np. przerwa w handlu) – tempo 0.</summary>
    NoTrading,

    /// <summary>Sesja się zakończyła, a nowa jeszcze nie ruszyła (rynek zamknięty) – tempo 0.</summary>
    SessionEnded,

    /// <summary>
    /// Początek sesji – litery rozgrzewki (<see cref="TempoOptions.WarmupPeriods"/>, domyślnie cała litera A) oraz
    /// pierwsze <see cref="TempoOptions.MinElapsedMinutes"/> minut następnej litery: za wcześnie na pomiar tempa;
    /// tempo 0, wskaźnik pozostaje widoczny.
    /// </summary>
    Starting,
}

/// <summary>Rodzaj zdarzenia tempa.</summary>
public enum TempoEventKind
{
    /// <summary>Ruch kierunkowy się zatrzymał – litera nie dołożyła nowych poziomów.</summary>
    Stopped,
}

/// <summary>Progi analizy tempa.</summary>
public sealed record TempoOptions
{
    /// <summary>Tempo (× normy), od którego ruch jest „szybki”.</summary>
    public double FastFactor { get; init; } = 1.5;

    /// <summary>Ile ostatnich sesji tego samego typu tworzy normę (średnią nowych wierszy na literę).</summary>
    public int HistorySessions { get; init; } = 20;

    /// <summary>Po ilu minutach bieżącej litery jej tempo jest rzutowane na pełny okres (wcześniej – tempo poprzedniej litery).</summary>
    public double MinElapsedMinutes { get; init; } = 5;

    /// <summary>Ile liter z rzędu bez nowych poziomów (z dużym nakładaniem) to równowaga.</summary>
    public int BalancePeriods { get; init; } = 2;

    /// <summary>Najmniejsza część wierszy litery pokrywająca się z wcześniejszymi literami, przy której to równowaga.</summary>
    public double BalanceOverlap { get; init; } = 0.6;

    /// <summary>
    /// Ile pierwszych liter sesji tylko buduje zakres odniesienia (bez tempa). Litera A jest porównywana z jednym
    /// wierszem ceny otwarcia, więc jej „nowe wiersze” to cały jej zakres – tempo od niej byłoby mylące.
    /// </summary>
    public int WarmupPeriods { get; init; } = 1;

    /// <summary>Najmniejsza norma (wiersze na literę) – chroni przed dzieleniem przez bardzo małe liczby.</summary>
    public double MinReference { get; init; } = 0.5;

    public static TempoOptions Default { get; } = new();
}

/// <summary>
/// Dane sesji potrzebne do analizy tempa: cena otwarcia i zakres (High/Low) każdej litery.
/// </summary>
/// <param name="SessionName">Nazwa sesji (Dzienna/Nocna) – norma jest liczona z sesji tego samego typu.</param>
/// <param name="SessionStart">Początek sesji (czas lokalny giełdy).</param>
/// <param name="PeriodLength">Długość okresu TPO.</param>
/// <param name="RowHeight">Wysokość wiersza profilu.</param>
/// <param name="Open">Cena otwarcia sesji (pierwsza cena litery A) – punkt odniesienia dla pierwszej litery.</param>
/// <param name="Periods">Litery sesji w kolejności.</param>
public sealed record TempoSession(
    string SessionName, DateTime SessionStart, TimeSpan PeriodLength, decimal RowHeight, decimal Open, IReadOnlyList<TpoPeriod> Periods)
{
    /// <summary>Koniec sesji (czas lokalny giełdy); null = nieznany (bez rozpoznawania końca sesji).</summary>
    public DateTime? SessionEnd { get; init; }
}

/// <summary>
/// Tempo jednej litery.
/// </summary>
/// <param name="Index">Numer okresu (0 = A).</param>
/// <param name="Letter">Litera okresu.</param>
/// <param name="NewUp">Nowe wiersze powyżej dotychczasowego High sesji.</param>
/// <param name="NewDown">Nowe wiersze poniżej dotychczasowego Low sesji.</param>
/// <param name="Rows">Liczba wierszy pokrytych przez literę.</param>
/// <param name="Overlap">Część wierszy litery (0–1) pokrywająca się z wcześniejszymi literami.</param>
/// <param name="Tempo">Tempo litery w „× normy” (ze znakiem: + w górę, − w dół, 0 = brak nowych poziomów).</param>
public sealed record PeriodTempo(int Index, char Letter, int NewUp, int NewDown, int Rows, double Overlap, double Tempo)
{
    /// <summary>Nowe wiersze netto (ze znakiem).</summary>
    public int NetNewRows => NewUp - NewDown;
}

/// <summary>
/// Bieżący odczyt tempa – liczby do odwołań w kodzie (np. automatyczny trading); opis tekstowy wynika z tych samych liczb.
/// </summary>
/// <param name="Time">Moment odczytu (czas lokalny giełdy).</param>
/// <param name="Letter">Litera, której dotyczy odczyt.</param>
/// <param name="Tempo">Tempo w „× normy” (+2,0 = w górę dwa razy szybciej niż zwykle, −1,0 = w dół w zwykłym tempie, 0 = stoi).</param>
/// <param name="TempoChange">Zmiana tempa względem poprzedniej litery (przy wzroście ujemna = zwalnia).</param>
/// <param name="NewRowsPerPeriod">Nowe wiersze netto na literę (ze znakiem, dla bieżącej litery – rzut na pełny okres).</param>
/// <param name="IsProjected">Czy to rzut niedokończonej litery na pełny okres.</param>
/// <param name="State">Stan tempa.</param>
/// <param name="Direction">Kierunek ostatniego ruchu: 1 = w górę, −1 = w dół, 0 = brak.</param>
/// <param name="StopPrice">Poziom zatrzymania (ekstremum ruchu) – przy stanie <see cref="TempoState.Stopped"/>.</param>
/// <param name="BalanceLow">Dół zakresu równowagi – przy stanie <see cref="TempoState.Balance"/>.</param>
/// <param name="BalanceHigh">Góra zakresu równowagi – przy stanie <see cref="TempoState.Balance"/>.</param>
public sealed record TempoReading(
    DateTime Time,
    char Letter,
    double Tempo,
    double TempoChange,
    double NewRowsPerPeriod,
    bool IsProjected,
    TempoState State,
    int Direction,
    decimal? StopPrice,
    decimal? BalanceLow,
    decimal? BalanceHigh)
{
    /// <summary>Odczyt bez danych.</summary>
    public static TempoReading Empty { get; } = new(default, ' ', 0, 0, 0, false, TempoState.None, 0, null, null, null);
}

/// <summary>
/// Zdarzenie tempa, np. „10:40 zatrzymanie przy 7655,50 po ruchu +38,00 w 70 min”.
/// </summary>
/// <param name="Time">Początek litery, w której ruch się zatrzymał (czas lokalny giełdy).</param>
/// <param name="Kind">Rodzaj zdarzenia.</param>
/// <param name="Price">Poziom zatrzymania (ekstremum ruchu).</param>
/// <param name="Move">Ruch od początku serii liter w tym kierunku do ekstremum (ze znakiem, w jednostkach ceny).</param>
/// <param name="Duration">Czas trwania ruchu (od początku serii do litery zatrzymania).</param>
public sealed record TempoEvent(DateTime Time, TempoEventKind Kind, decimal Price, decimal Move, TimeSpan Duration);

/// <summary>Wynik analizy tempa sesji.</summary>
public sealed record TempoAnalysis(IReadOnlyList<PeriodTempo> Periods, TempoReading Current, IReadOnlyList<TempoEvent> Events)
{
    public static TempoAnalysis Empty { get; } = new([], TempoReading.Empty, []);
}

/// <summary>
/// Norma tempa: średnia liczba nowych wierszy na literę (osobno w górę i w dół) dla każdego numeru litery,
/// liczona z poprzednich sesji tego samego typu.
/// </summary>
public sealed class TempoReference
{
    private readonly double[] _upByIndex;
    private readonly double[] _downByIndex;
    private readonly int[] _samplesByIndex;

    private TempoReference(double[] up, double[] down, int[] samples, double overallUp, double overallDown, int sessions)
    {
        _upByIndex = up;
        _downByIndex = down;
        _samplesByIndex = samples;
        OverallUp = overallUp;
        OverallDown = overallDown;
        Sessions = sessions;
    }

    /// <summary>Liczba sesji, z których policzono normę.</summary>
    public int Sessions { get; }

    /// <summary>Średnia nowych wierszy w górę na literę (wszystkie litery).</summary>
    public double OverallUp { get; }

    /// <summary>Średnia nowych wierszy w dół na literę (wszystkie litery).</summary>
    public double OverallDown { get; }

    /// <summary>
    /// Norma z poprzednich sesji (najnowsze <paramref name="maxSessions"/>); litery rozgrzewki
    /// (<paramref name="warmupPeriods"/> pierwszych) nie wchodzą do średnich.
    /// </summary>
    public static TempoReference FromHistory(IEnumerable<TempoSession> history, int maxSessions, int? warmupPeriods = null)
    {
        var warmup = warmupPeriods ?? TempoOptions.Default.WarmupPeriods;
        var sessions = history.Where(s => s.Periods.Count > 0).TakeLast(Math.Max(0, maxSessions)).ToList();
        var maxIndex = sessions.Count > 0 ? sessions.Max(s => s.Periods[^1].Index) : -1;
        var up = new double[maxIndex + 1];
        var down = new double[maxIndex + 1];
        var samples = new int[maxIndex + 1];
        double allUp = 0, allDown = 0;
        var all = 0;

        foreach (var session in sessions)
        {
            foreach (var (period, newUp, newDown, _, _) in TpoTempo.Extensions(session, warmup).Skip(warmup))
            {
                up[period.Index] += newUp;
                down[period.Index] += newDown;
                samples[period.Index]++;
                allUp += newUp;
                allDown += newDown;
                all++;
            }
        }

        for (var i = 0; i <= maxIndex; i++)
        {
            if (samples[i] > 0)
            {
                up[i] /= samples[i];
                down[i] /= samples[i];
            }
        }

        return new TempoReference(up, down, samples, all > 0 ? allUp / all : 0, all > 0 ? allDown / all : 0, sessions.Count);
    }

    /// <summary>Norma bez historii.</summary>
    public static TempoReference None { get; } = new([], [], [], 0, 0, 0);

    /// <summary>
    /// Norma dla litery o numerze <paramref name="index"/>: średnia tej samej litery, gdy wystąpiła w co najmniej
    /// 3 sesjach; w przeciwnym razie średnia wszystkich liter. Null = brak historii.
    /// </summary>
    public (double Up, double Down)? For(int index)
    {
        if (Sessions == 0)
        {
            return null;
        }

        return index < _samplesByIndex.Length && _samplesByIndex[index] >= 3
            ? (_upByIndex[index], _downByIndex[index])
            : (OverallUp, OverallDown);
    }
}

/// <summary>
/// Tempo budowania TPO: ile nowych poziomów ceny (wierszy profilu) dokłada każda litera i w którą stronę,
/// w porównaniu z normą (średnią z poprzednich sesji). Szybki ruch = litery dokładają dużo nowych wierszy;
/// zwalnianie = coraz mniej; zatrzymanie = litera nie dokłada nowych wierszy, tylko litery na tych samych poziomach.
/// </summary>
public static class TpoTempo
{
    /// <summary>
    /// Nowe wiersze każdej litery sesji: (litera, nowe w górę, nowe w dół, pokryte wiersze, nakładanie 0–1).
    /// Zakres odniesienia startuje od ceny otwarcia; litery rozgrzewki (<paramref name="warmupPeriods"/> pierwszych)
    /// tylko go rozszerzają – zwracane bez nowych wierszy i z pełnym nakładaniem.
    /// </summary>
    internal static IEnumerable<(TpoPeriod Period, int NewUp, int NewDown, int Rows, double Overlap)> Extensions(
        TempoSession session, int warmupPeriods)
    {
        if (session.Periods.Count == 0 || session.RowHeight <= 0)
        {
            yield break;
        }

        var high = Row(session.Open, session.RowHeight);
        var low = high;

        for (var i = 0; i < session.Periods.Count; i++)
        {
            var period = session.Periods[i];
            var periodHigh = Row(period.High, session.RowHeight);
            var periodLow = Row(period.Low, session.RowHeight);
            var rows = periodHigh - periodLow + 1;

            if (i < warmupPeriods)
            {
                yield return (period, 0, 0, (int)rows, 1.0);
            }
            else
            {
                var overlapRows = Math.Max(0, Math.Min(periodHigh, high) - Math.Max(periodLow, low) + 1);
                var newUp = Math.Max(0, periodHigh - high);
                var newDown = Math.Max(0, low - periodLow);

                yield return (period, (int)newUp, (int)newDown, (int)rows, rows > 0 ? (double)overlapRows / rows : 0);
            }

            high = Math.Max(high, periodHigh);
            low = Math.Min(low, periodLow);
        }
    }

    private static long Row(decimal price, decimal rowHeight) => (long)Math.Floor(price / rowHeight);

    /// <summary>
    /// Analiza tempa sesji.
    /// </summary>
    /// <param name="session">Sesja (litery do bieżącego momentu).</param>
    /// <param name="reference">Norma z poprzednich sesji tego samego typu (<see cref="TempoReference.None"/> = brak historii).</param>
    /// <param name="nowLocal">
    /// „Teraz” (czas lokalny giełdy) – gdy wypada w trakcie ostatniej litery, jej tempo jest rzutowane na pełny okres.
    /// Null = wszystkie litery traktowane jako zakończone.
    /// </param>
    /// <param name="options">Progi analizy.</param>
    public static TempoAnalysis Analyze(TempoSession session, TempoReference reference, DateTime? nowLocal, TempoOptions? options = null)
    {
        options ??= TempoOptions.Default;
        var warmup = Math.Max(0, options.WarmupPeriods);
        var extensions = Extensions(session, warmup).ToList();
        if (extensions.Count == 0)
        {
            return TempoAnalysis.Empty;
        }

        // 1. Tempo każdej litery (bez rzutu) – historia wartości. Litery rozgrzewki mają tempo 0.
        var periods = new List<PeriodTempo>(extensions.Count);
        double sumUp = 0, sumDown = 0;
        for (var i = 0; i < extensions.Count; i++)
        {
            var (period, up, down, rows, overlap) = extensions[i];
            var norm = Norm(reference, period.Index, i - warmup, sumUp, sumDown, options);
            periods.Add(new PeriodTempo(period.Index, period.Letter, up, down, rows, overlap, TempoOf(up - down, norm)));
            sumUp += up;
            sumDown += down;
        }

        // 2. Bieżąca litera: rzut na pełny okres albo (na początku litery) tempo poprzedniej.
        var last = periods[^1];
        var lastStart = session.SessionStart + last.Index * session.PeriodLength;
        var lastEnd = lastStart + session.PeriodLength;
        var inProgress = nowLocal is { } now && now >= lastStart && now < lastEnd;
        var elapsed = inProgress ? nowLocal!.Value - lastStart : session.PeriodLength;
        var tooEarly = inProgress && elapsed.TotalMinutes < options.MinElapsedMinutes;

        // „Teraz” po końcu ostatniej litery, a nowej litery nie ma – od tego czasu nie było żadnej transakcji
        // (koniec sesji, rynek zamknięty albo przerwa w handlu). Tempo = 0: rynek nic nie buduje.
        // Krótko po końcu litery (mniej niż MinElapsedMinutes) – jeszcze zwykły odczyt ostatniej litery.
        if (nowLocal is { } idleNow && idleNow >= lastEnd &&
            ((session.SessionEnd is { } end && idleNow >= end) || (idleNow - lastEnd).TotalMinutes >= options.MinElapsedMinutes))
        {
            var ended = session.SessionEnd is { } sessionEnd && idleNow >= sessionEnd;
            var idle = new TempoReading(
                Time: idleNow,
                Letter: last.Letter,
                Tempo: 0,
                TempoChange: Math.Round(-last.Tempo, 2),
                NewRowsPerPeriod: 0,
                IsProjected: false,
                State: ended ? TempoState.SessionEnded : TempoState.NoTrading,
                Direction: LastDirection(periods),
                StopPrice: null,
                BalanceLow: null,
                BalanceHigh: null);
            return new TempoAnalysis(periods, idle, StopEvents(session, periods));
        }

        // Litery uwzględniane w stanie i zdarzeniach (bieżąca – dopiero po kilku minutach).
        var settled = tooEarly ? periods.Take(periods.Count - 1).ToList() : periods;
        if (settled.Count <= warmup)
        {
            // Litery rozgrzewki (A) albo pierwsze minuty następnej litery – nie ma jeszcze z czym porównać ani czego rzutować.
            var starting = TempoReading.Empty with { Time = nowLocal ?? lastStart, Letter = last.Letter, State = TempoState.Starting };
            return new TempoAnalysis(periods, starting, []);
        }

        var current = settled[^1];
        var factor = inProgress && !tooEarly ? session.PeriodLength.TotalMinutes / elapsed.TotalMinutes : 1;
        var projectedNet = current.NetNewRows * factor;
        var currentNorm = Norm(reference, current.Index, settled.Count - 1 - warmup,
            settled.Take(settled.Count - 1).Sum(p => (double)p.NewUp), settled.Take(settled.Count - 1).Sum(p => (double)p.NewDown), options);
        var tempo = TempoOf(projectedNet, currentNorm);
        var previousTempo = settled.Count > 1 ? settled[^2].Tempo : 0;

        // 3. Kierunek ostatniego ruchu i stan.
        var direction = LastDirection(settled);
        var (state, stopPrice, balanceLow, balanceHigh) = StateOf(session, settled, tempo, previousTempo, direction, options);

        var reading = new TempoReading(
            Time: nowLocal ?? lastEnd,
            Letter: current.Letter,
            Tempo: Math.Round(tempo, 2),
            TempoChange: Math.Round(tempo - previousTempo, 2),
            NewRowsPerPeriod: Math.Round(projectedNet, 2),
            IsProjected: inProgress && !tooEarly && factor > 1,
            State: state,
            Direction: direction,
            StopPrice: stopPrice,
            BalanceLow: balanceLow,
            BalanceHigh: balanceHigh);

        return new TempoAnalysis(periods, reading, StopEvents(session, settled));
    }

    /// <summary>
    /// Norma (wiersze na literę) dla litery – z historii, a bez niej ze średniej wcześniejszych mierzonych liter bieżącej sesji.
    /// <paramref name="position"/> = numer litery liczony od pierwszej mierzonej (po rozgrzewce).
    /// </summary>
    private static (double Up, double Down) Norm(
        TempoReference reference, int index, int position, double sumUpBefore, double sumDownBefore, TempoOptions options)
    {
        var norm = reference.For(index) ?? (position > 0
            ? (sumUpBefore / position, sumDownBefore / position)
            : (1.0, 1.0)); // pierwsza litera bez historii – surowa liczba wierszy

        var both = Math.Max(options.MinReference, (norm.Item1 + norm.Item2) / 2);
        return (Math.Max(norm.Item1, both / 2), Math.Max(norm.Item2, both / 2));
    }

    private static double TempoOf(double net, (double Up, double Down) norm) =>
        net > 0 ? net / norm.Up : net < 0 ? net / norm.Down : 0;

    /// <summary>Kierunek ostatniej litery, która dołożyła nowe wiersze (1, −1 albo 0).</summary>
    private static int LastDirection(IReadOnlyList<PeriodTempo> periods)
    {
        for (var i = periods.Count - 1; i >= 0; i--)
        {
            if (periods[i].NetNewRows != 0)
            {
                return Math.Sign(periods[i].NetNewRows);
            }
        }

        return 0;
    }

    private static (TempoState State, decimal? Stop, decimal? BalanceLow, decimal? BalanceHigh) StateOf(
        TempoSession session, IReadOnlyList<PeriodTempo> periods, double tempo, double previousTempo, int direction, TempoOptions options)
    {
        var current = periods[^1];

        if (current.NewUp == 0 && current.NewDown == 0)
        {
            // Litery bez nowych poziomów od końca – równowaga albo zatrzymanie po ruchu.
            var quiet = 0;
            for (var i = periods.Count - 1; i >= 0 && periods[i].NewUp == 0 && periods[i].NewDown == 0; i--)
            {
                quiet++;
            }

            var quietPeriods = session.Periods.Take(periods.Count).TakeLast(quiet).ToList();
            var averageOverlap = periods.TakeLast(quiet).Average(p => p.Overlap);
            if (direction == 0 || (quiet >= options.BalancePeriods && averageOverlap >= options.BalanceOverlap))
            {
                return (TempoState.Balance, null, quietPeriods.Min(p => p.Low), quietPeriods.Max(p => p.High));
            }

            var settledPeriods = session.Periods.Take(periods.Count).ToList();
            var stop = direction > 0 ? settledPeriods.Max(p => p.High) : settledPeriods.Min(p => p.Low);
            return (TempoState.Stopped, stop, null, null);
        }

        var magnitude = Math.Abs(tempo);
        if (magnitude >= options.FastFactor)
        {
            return (TempoState.Fast, null, null, null);
        }

        var sameDirection = Math.Sign(tempo) == Math.Sign(previousTempo) && previousTempo != 0;
        if (magnitude < 1 && (!sameDirection || magnitude < Math.Abs(previousTempo)))
        {
            return (TempoState.Slowing, null, null, null);
        }

        return (TempoState.Normal, null, null, null);
    }

    /// <summary>
    /// Zdarzenia zatrzymania: litera bez nowych poziomów po serii liter rozszerzających sesję w jednym kierunku.
    /// Ruch liczony od początku serii (Low/High jej pierwszej litery albo cena otwarcia) do ekstremum.
    /// </summary>
    private static List<TempoEvent> StopEvents(TempoSession session, IReadOnlyList<PeriodTempo> periods)
    {
        var events = new List<TempoEvent>();
        var legDirection = 0;
        var legStart = -1;

        for (var i = 0; i < periods.Count; i++)
        {
            var net = periods[i].NetNewRows;
            if (net != 0)
            {
                var direction = Math.Sign(net);
                if (direction != legDirection)
                {
                    legDirection = direction;
                    legStart = i;
                }

                continue;
            }

            if (periods[i].NewUp != 0 || periods[i].NewDown != 0 || legDirection == 0)
            {
                continue; // litera rozszerzyła sesję w obie strony po równo albo nie było jeszcze ruchu
            }

            // Pierwsza litera bez nowych poziomów po ruchu – zatrzymanie.
            var leg = session.Periods.Skip(legStart).Take(i - legStart).ToList();
            var startPrice = legStart == 0
                ? session.Open
                : legDirection > 0 ? leg[0].Low : leg[0].High;
            var extreme = legDirection > 0
                ? session.Periods.Take(i).Max(p => p.High)
                : session.Periods.Take(i).Min(p => p.Low);
            var legStartTime = session.SessionStart + periods[legStart].Index * session.PeriodLength;
            var stopTime = session.SessionStart + periods[i].Index * session.PeriodLength;

            events.Add(new TempoEvent(stopTime, TempoEventKind.Stopped, extreme, extreme - startPrice, stopTime - legStartTime));
            legDirection = 0; // kolejne litery bez ruchu to już ten sam postój
        }

        return events;
    }
}
