namespace MarketProfile.Core;

/// <summary>
/// Pojedyncza transakcja (tick) otrzymana od brokera.
/// </summary>
/// <param name="Time">Czas transakcji (z informacją o strefie czasowej).</param>
/// <param name="Price">Cena transakcji.</param>
/// <param name="Volume">Wolumen (liczba kontraktów).</param>
/// <param name="Side">
/// Strona inicjująca transakcję ustalona z notowań bid/ask w chwili transakcji (Unknown = nieznana – wtedy szacuje ją
/// reguła ticka przy budowie profilu, patrz <see cref="TickRule"/>).
/// </param>
public readonly record struct Tick(DateTimeOffset Time, decimal Price, long Volume, TradeSide Side = TradeSide.Unknown);

/// <summary>Strona inicjująca transakcję (agresor).</summary>
public enum TradeSide
{
    /// <summary>Nieznana (brak notowań bid/ask, cena w środku spreadu albo stare dane).</summary>
    Unknown = 0,

    /// <summary>Kupno – transakcja po cenie ask lub wyższej (kupujący „podnosi” ofertę).</summary>
    Buy = 1,

    /// <summary>Sprzedaż – transakcja po cenie bid lub niższej (sprzedający „uderza” w bid).</summary>
    Sell = 2,
}

/// <summary>
/// Świeca (bar) z danymi historycznymi: cena otwarcia, maksimum, minimum, zamknięcia i wolumen.
/// Dane historyczne pobieramy jako świece 1-minutowe, żeby szybko odbudować profile z przeszłości.
/// </summary>
/// <param name="Time">Czas rozpoczęcia świecy.</param>
/// <param name="Open">Cena otwarcia.</param>
/// <param name="High">Najwyższa cena w świecy.</param>
/// <param name="Low">Najniższa cena w świecy.</param>
/// <param name="Close">Cena zamknięcia.</param>
/// <param name="Volume">Wolumen w świecy.</param>
public readonly record struct Bar(DateTimeOffset Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

/// <summary>Pomocnicze dla <see cref="Bar"/>.</summary>
public static class BarExtensions
{
    /// <summary>
    /// Świeca niepełna: maksimum = minimum (czyli O = H = L = C) i zerowy wolumen. Tak broker podaje najstarsze świece
    /// kontraktu ciągłego (sama cena zamknięcia) – nie nadają się do wykresu ani analiz OHLC.
    /// </summary>
    public static bool IsIncomplete(this Bar bar) => bar.High == bar.Low && bar.Volume == 0;

    /// <summary>Świeca z wolumenem mniejszym niż ta część mediany wolumenu serii jest niepełna.</summary>
    public const decimal MinVolumeShare = 0.01m;

    /// <summary>
    /// Tylko pełne świece serii.
    ///  - Seria z wolumenem (futures, akcje): niepełna jest świeca bez wolumenu albo z wolumenem mniejszym niż 1% mediany
    ///    wolumenu serii. Tak wyglądają najstarsze świece kontraktu ciągłego IB: sama cena zamknięcia albo kilka
    ///    przypadkowych transakcji odległego, mało płynnego kontraktu (np. 28 kontraktów w miesiącu zamiast ~20 mln).
    ///  - Seria bez wolumenu (indeksy): niepełna jest świeca bez zakresu (<see cref="IsIncomplete"/>).
    /// </summary>
    public static List<Bar> CompleteOnly(IReadOnlyList<Bar> bars)
    {
        var volumes = bars.Where(b => b.Volume > 0).Select(b => b.Volume).Order().ToList();
        if (volumes.Count == 0)
        {
            return bars.Where(b => !b.IsIncomplete()).ToList();
        }

        var minVolume = Math.Max(1, (long)(volumes[volumes.Count / 2] * MinVolumeShare));
        return bars.Where(b => b.Volume >= minVolume).ToList();
    }
}

/// <summary>Interwał świec szerszej perspektywy (zapisywanych w bazie osobno dla każdego interwału).</summary>
public enum BarInterval
{
    /// <summary>Świeca dzienna (pełna doba handlowa, z sesją nocną).</summary>
    Day,

    /// <summary>Świeca tygodniowa.</summary>
    Week,

    /// <summary>Świeca miesięczna.</summary>
    Month,

    /// <summary>Świeca 30-minutowa (wewnątrzdzienna; zapisywana z rzeczywistym czasem początku, UTC).</summary>
    Minutes30,
}

/// <summary>Pomocnicze dla <see cref="BarInterval"/>.</summary>
public static class BarIntervals
{
    /// <summary>Interwały D/W/M, od najdłuższego (świece 30 min są pobierane osobno – tylko dla rynku szerokiego).</summary>
    public static IReadOnlyList<BarInterval> All { get; } = [BarInterval.Month, BarInterval.Week, BarInterval.Day];

    /// <summary>Czy interwał jest wewnątrzdzienny (świece z godziną, nie z samą datą).</summary>
    public static bool IsIntraday(this BarInterval interval) => interval == BarInterval.Minutes30;

    /// <summary>Kod interwału w bazie: "D", "W" albo "M".</summary>
    public static string Code(this BarInterval interval) => interval switch
    {
        BarInterval.Day => "D",
        BarInterval.Week => "W",
        BarInterval.Month => "M",
        BarInterval.Minutes30 => "30m",
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };

    /// <summary>
    /// Data, pod którą świeca jest zapisana: dzień – sama data, tydzień – poniedziałek, miesiąc – 1. dzień miesiąca.
    /// Broker podpisuje świecę tygodniową / miesięczną różnie (ostatnim dniem okresu, a bieżącą – dzisiejszą datą),
    /// więc bez tego bieżący tydzień / miesiąc dublowałby się przy każdym odświeżeniu.
    /// Niedziela należy do następnego tygodnia (sesja futures zaczyna się w niedzielę wieczorem).
    /// </summary>
    public static DateTimeOffset PeriodStart(this BarInterval interval, DateTimeOffset time)
    {
        if (interval == BarInterval.Minutes30)
        {
            // Początek półgodziny (UTC). Strefy giełd mają przesunięcia w pełnych godzinach, więc 9:30 ET to też pełna półgodzina UTC.
            var ticks = time.UtcTicks;
            return new DateTimeOffset(ticks - ticks % TimeSpan.FromMinutes(30).Ticks, TimeSpan.Zero);
        }

        var date = time.UtcDateTime.Date;
        var start = interval switch
        {
            BarInterval.Week => date.AddDays(date.DayOfWeek == DayOfWeek.Sunday ? 1 : -((int)date.DayOfWeek - 1)),
            BarInterval.Month => new DateTime(date.Year, date.Month, 1),
            _ => date,
        };

        return new DateTimeOffset(start, TimeSpan.Zero);
    }

    /// <summary>Nazwa interwału do opisów, np. „Dzienny”.</summary>
    public static string DisplayName(this BarInterval interval) => interval switch
    {
        BarInterval.Day => "Dzienny",
        BarInterval.Week => "Tygodniowy",
        BarInterval.Month => "Miesięczny",
        BarInterval.Minutes30 => "30 min",
        _ => interval.ToString(),
    };
}
