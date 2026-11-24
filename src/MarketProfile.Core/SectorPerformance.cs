namespace MarketProfile.Core;

/// <summary>
/// Sektor S&amp;P 500 (klasyfikacja GICS): klucz, nazwy, ETF sektorowy SPDR (źródło notowań) i symbol graficzny.
/// </summary>
public sealed record SectorInfo(string Key, string Name, string ShortName, string Etf, string Icon, string EnglishName);

/// <summary>Waga sektora w S&amp;P 500 (%) – wpisywana w ustawieniach wg comiesięcznych danych S&amp;P (spglobal.com).</summary>
public sealed record SectorWeight(string Key, decimal Weight);

/// <summary>Poziom zmiany ceny sektora od otwarcia sesji kasowej – wyznacza kolor wskaźnika.</summary>
public enum SectorChangeLevel
{
    /// <summary>Brak danych.</summary>
    None,

    /// <summary>Powyżej +1% – ciemnozielony.</summary>
    StrongUp,

    /// <summary>Od +0,5% do +1% – zielony (pośredni).</summary>
    Up,

    /// <summary>Od 0 do +0,5% – jasnozielony.</summary>
    SlightUp,

    /// <summary>Od 0 do −0,5% – jasnoczerwony.</summary>
    SlightDown,

    /// <summary>Od −0,5% do −1% – czerwony (pośredni).</summary>
    Down,

    /// <summary>Poniżej −1% – czerwony (ciemny).</summary>
    StrongDown,
}

/// <summary>11 sektorów S&amp;P 500, zmiana od otwarcia sesji kasowej i jej poziom (kolor).</summary>
public static class SectorPerformance
{
    /// <summary>Sektory GICS w S&amp;P 500 z ETF-ami SPDR (Select Sector) – notowania sektora.</summary>
    public static IReadOnlyList<SectorInfo> Sectors { get; } =
    [
        new("IT", "Technologia informacyjna", "Technologia", "XLK", "💻", "Information Technology"),
        new("FIN", "Finanse", "Finanse", "XLF", "🏦", "Financials"),
        new("DISC", "Dobra konsumpcyjne cykliczne", "Konsumpcja cykl.", "XLY", "🛍", "Consumer Discretionary"),
        new("COMM", "Usługi komunikacyjne", "Komunikacja", "XLC", "📡", "Communication Services"),
        new("HLTH", "Ochrona zdrowia", "Zdrowie", "XLV", "⚕", "Health Care"),
        new("IND", "Przemysł", "Przemysł", "XLI", "🏭", "Industrials"),
        new("STPL", "Dobra konsumpcyjne podstawowe", "Konsumpcja podst.", "XLP", "🛒", "Consumer Staples"),
        new("ENRG", "Energetyka", "Energetyka", "XLE", "⛽", "Energy"),
        new("UTIL", "Użyteczność publiczna", "Użyteczność publ.", "XLU", "💡", "Utilities"),
        new("RE", "Nieruchomości", "Nieruchomości", "XLRE", "🏠", "Real Estate"),
        new("MAT", "Surowce", "Surowce", "XLB", "⛏", "Materials"),
    ];

    /// <summary>
    /// Wagi domyślne – przybliżone, z ostatniego znanego zestawienia S&amp;P (do sprawdzenia i poprawienia w ustawieniach
    /// wg spglobal.com; data <see cref="DefaultWeightsDate"/> jest celowo stara, żeby pokazać ostrzeżenie o aktualizacji).
    /// </summary>
    public static IReadOnlyList<SectorWeight> DefaultWeights { get; } =
    [
        new("IT", 34.9m), new("FIN", 13.4m), new("DISC", 10.5m), new("COMM", 10.1m), new("HLTH", 8.9m),
        new("IND", 8.3m), new("STPL", 4.9m), new("ENRG", 2.9m), new("UTIL", 2.4m), new("RE", 1.9m), new("MAT", 1.8m),
    ];

    /// <summary>Data wag domyślnych (przybliżonych).</summary>
    public static DateOnly DefaultWeightsDate { get; } = new(2025, 10, 31);

    /// <summary>Po ilu dniach wagi uznajemy za nieaktualne (S&amp;P publikuje je co miesiąc).</summary>
    public const int WeightsMaxAgeDays = 35;

    /// <summary>Sektor po kluczu (null – nieznany).</summary>
    public static SectorInfo? Find(string key) => Sectors.FirstOrDefault(s => s.Key == key);

    /// <summary>
    /// Sektory z wagami, od największej wagi; sektor bez wpisu w <paramref name="weights"/> – z wagą domyślną.
    /// </summary>
    public static IReadOnlyList<(SectorInfo Sector, decimal Weight)> OrderedByWeight(IReadOnlyList<SectorWeight>? weights) =>
        Sectors
            .Select(s => (Sector: s, Weight: weights?.FirstOrDefault(w => w.Key == s.Key)?.Weight
                                               ?? DefaultWeights.First(w => w.Key == s.Key).Weight))
            .OrderByDescending(x => x.Weight)
            .ThenBy(x => Sectors.ToList().IndexOf(x.Sector))
            .ToList();

    /// <summary>Zmiana od otwarcia w procentach: (ostatnia − otwarcie) / otwarcie × 100; null – brak danych.</summary>
    public static decimal? ChangePercent(decimal? open, decimal? last) =>
        open is > 0 && last is > 0 ? (last.Value - open.Value) / open.Value * 100m : null;

    /// <summary>
    /// Poziom (kolor) zmiany: &gt; +1% ciemnozielony, (+0,5; +1] zielony, [0; +0,5] jasnozielony, [−0,5; 0) jasnoczerwony,
    /// [−1; −0,5) czerwony, &lt; −1% ciemnoczerwony.
    /// </summary>
    public static SectorChangeLevel Classify(decimal? changePercent) => changePercent switch
    {
        null => SectorChangeLevel.None,
        > 1m => SectorChangeLevel.StrongUp,
        > 0.5m => SectorChangeLevel.Up,
        >= 0m => SectorChangeLevel.SlightUp,
        >= -0.5m => SectorChangeLevel.SlightDown,
        >= -1m => SectorChangeLevel.Down,
        _ => SectorChangeLevel.StrongDown,
    };
}
