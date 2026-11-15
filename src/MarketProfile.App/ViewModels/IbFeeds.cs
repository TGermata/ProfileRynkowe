using System.Runtime.CompilerServices;
using MarketProfile.Core;
using MarketProfile.DataFeed.IB;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Jedyne miejsce w aplikacji, które bezpośrednio używa klasy <see cref="IbDataFeed"/>.
///
/// Dlaczego osobna klasa: <see cref="IbDataFeed"/> korzysta z biblioteki TWS API, którą ładujemy ze ścieżki
/// z ustawień dopiero w chwili użycia (<see cref="IbApiLoader.Use"/>). .NET przygotowuje kod metody tuż przed
/// jej pierwszym wywołaniem – metody poniżej (NoInlining) są więc przygotowywane dopiero po wskazaniu biblioteki.
/// </summary>
public static class IbFeeds
{
    /// <summary>Tworzy źródło danych IB (wywoływać po <see cref="IbApiLoader.Use"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IMarketDataFeed Create(IbConnectionSettings settings) => new IbDataFeed(settings);

    /// <summary>Nazwa wybranego kontraktu (np. "ESZ6") albo null, gdy źródło nie jest IB.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string? ContractName(IMarketDataFeed? feed) => (feed as IbDataFeed)?.ContractName;

    /// <summary>Ostrzeżenie o niezgodnym ticku instrumentu (null = zgodny albo źródło nie jest IB).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string? TickSizeWarning(IMarketDataFeed? feed) => (feed as IbDataFeed)?.TickSizeWarning;

    /// <summary>Wyszukanie instrumentu w IB – „Sprawdź w IB” (wywoływać po <see cref="IbApiLoader.Use"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<IbInstrumentInfo> LookupAsync(IbConnectionSettings settings, Instrument instrument) =>
        IbDataFeed.LookupAsync(settings, instrument);

    /// <summary>Sprawdzenie, ile dni wstecz IB udostępnia ticki (wywoływać po <see cref="IbApiLoader.Use"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<IbTickProbeResult> ProbeTickHistoryAsync(IbConnectionSettings settings, Instrument instrument, IProgress<string> progress) =>
        IbDataFeed.ProbeTickHistoryAsync(settings, instrument, progress);

    /// <summary>
    /// Jedna strona historycznych transakcji (maks. ok. 1000) od podanego momentu – źródło musi być połączonym
    /// źródłem IB z <see cref="Create"/> (wywoływać po <see cref="IbApiLoader.Use"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<TickPage> GetTicksPageAsync(IMarketDataFeed feed, DateTimeOffset start, CancellationToken token) =>
        ((IbDataFeed)feed).GetTicksPageAsync(start, token);

    /// <summary>Test połączenia z TWS (wywoływać po <see cref="IbApiLoader.Use"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<string> TestConnectionAsync(IbConnectionSettings settings, Instrument instrument) =>
        IbDataFeed.TestConnectionAsync(settings, instrument, TimeSpan.FromSeconds(20));
}
