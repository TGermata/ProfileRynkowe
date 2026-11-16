using System.Globalization;

namespace MarketProfile.App.Controls;

/// <summary>Formatowanie wolumenu do wyświetlenia.</summary>
public static class VolumeFormat
{
    /// <summary>Pełna liczba z separatorem tysięcy, np. „1 245 300”.</summary>
    public static string Long(long volume) => volume.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Skrót do osi i podpisów słupków, np. „850”, „12,5 tys.”, „1,24 mln”.</summary>
    public static string Short(long volume)
    {
        var abs = Math.Abs(volume);
        var sign = volume < 0 ? "−" : string.Empty;
        return abs switch
        {
            >= 1_000_000 => sign + (abs / 1_000_000.0).ToString(abs >= 10_000_000 ? "0.#" : "0.##", CultureInfo.CurrentCulture) + " mln",
            >= 10_000 => sign + (abs / 1000.0).ToString(abs >= 100_000 ? "0" : "0.#", CultureInfo.CurrentCulture) + " tys.",
            _ => sign + abs.ToString("N0", CultureInfo.CurrentCulture),
        };
    }
}
