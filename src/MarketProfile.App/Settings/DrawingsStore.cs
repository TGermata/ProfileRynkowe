using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketProfile.Core;

namespace MarketProfile.App.Settings;

/// <summary>
/// Elementy dodane przez użytkownika dla jednego instrumentu: linie cenowe i punkty rozbicia profili.
/// </summary>
public sealed record InstrumentDrawings
{
    public List<PriceLine> Lines { get; init; } = [];
    public List<DateTime> Splits { get; init; } = [];

    /// <summary>Profile wyświetlane w trybie Split/Expanded (identyfikowane czasem rozpoczęcia profilu/części).</summary>
    public List<DateTime> ExpandedProfiles { get; init; } = [];
}

/// <summary>
/// Odczyt i zapis linii oraz rozbić profili do pliku drawings.json (w %AppData%\MarketProfile).
/// Dane są trzymane osobno dla każdego instrumentu (linie dla ES nie pokazują się na NQ).
/// </summary>
public static class DrawingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarketProfile", "drawings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }, // styl linii zapisujemy jako tekst, np. "Dashed"
    };

    /// <summary>Wczytuje elementy wszystkich instrumentów (symbol -> elementy).</summary>
    /// <remarks>Uszkodzony plik: kopia zapasowa + komunikat i pusta lista (patrz <see cref="JsonFileStore"/>).</remarks>
    public static Dictionary<string, InstrumentDrawings> Load() =>
        JsonFileStore.Read<Dictionary<string, InstrumentDrawings>>(FilePath, JsonOptions) ?? [];

    /// <summary>Zapisuje elementy wszystkich instrumentów (atomowo; błąd zapisu jest zgłaszany, nie rzucany).</summary>
    public static void Save(Dictionary<string, InstrumentDrawings> drawings) =>
        JsonFileStore.Write(FilePath, drawings, JsonOptions);
}
