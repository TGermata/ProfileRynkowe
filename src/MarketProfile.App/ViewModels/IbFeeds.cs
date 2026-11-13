using System.Runtime.CompilerServices;
using MarketProfile.Core;
using MarketProfile.DataFeed.IB;

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

    /// <summary>Wyszukanie instrumentu w IB – „Sprawdź w IB” (wywoływać po <see cref="IbApiLoader.Use"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<IbInstrumentInfo> LookupAsync(IbConnectionSettings settings, Instrument instrument) =>
        IbDataFeed.LookupAsync(settings, instrument);

    /// <summary>Test połączenia z TWS (wywoływać po <see cref="IbApiLoader.Use"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<string> TestConnectionAsync(IbConnectionSettings settings, Instrument instrument) =>
        IbDataFeed.TestConnectionAsync(settings, instrument, TimeSpan.FromSeconds(20));
}
