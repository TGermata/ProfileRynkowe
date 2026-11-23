using System.Runtime.CompilerServices;
using MarketProfile.App.Settings;
using MarketProfile.Core;
using MarketProfile.DataFeed.IB;
using MarketProfile.DataFeed.IBWeb;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Jedyne miejsce w aplikacji, które bezpośrednio tworzy źródła danych Interactive Brokers – przez TWS API
/// (<see cref="IbDataFeed"/>) albo przez IBKR Web API (<see cref="IbWebDataFeed"/>, gdy <see cref="IbConnectionSettings.UseWebApi"/>).
///
/// Dlaczego osobna klasa: <see cref="IbDataFeed"/> korzysta z biblioteki TWS API, którą ładujemy ze ścieżki
/// z ustawień dopiero w chwili użycia (<see cref="IbApiLoader.Use"/>). .NET przygotowuje kod metody tuż przed
/// jej pierwszym wywołaniem – metody TWS poniżej (NoInlining) są więc przygotowywane dopiero po wskazaniu biblioteki,
/// a ścieżka Web API w ogóle ich nie dotyka (działa bez biblioteki TWS API).
/// </summary>
public static class IbFeeds
{
    /// <summary>
    /// Przygotowanie przed użyciem IB: dla TWS API wskazuje bibliotekę ze ścieżki z ustawień (wyjątek, gdy jej brak);
    /// dla Web API nic nie robi.
    /// </summary>
    public static void Prepare(AppSettings settings)
    {
        if (settings.LiveSource != LiveSourceKind.InteractiveBrokersWebApi)
        {
            IbApiLoader.Use(string.IsNullOrWhiteSpace(settings.IbApiPath) ? IbApiLoader.AutoDetect() : settings.IbApiPath);
        }
    }

    /// <summary>Tworzy źródło danych IB (dla TWS – wywoływać po <see cref="IbApiLoader.Use"/>).</summary>
    public static IMarketDataFeed Create(IbConnectionSettings settings) =>
        settings.UseWebApi ? new IbWebDataFeed(settings) : Tws.Create(settings);

    /// <summary>Nazwa wybranego kontraktu (np. "ESZ6") albo null, gdy źródło nie jest IB.</summary>
    public static string? ContractName(IMarketDataFeed? feed) => feed switch
    {
        IbWebDataFeed web => web.ContractName,
        null => null,
        _ => Tws.ContractName(feed),
    };

    /// <summary>Ostrzeżenie o niezgodnym ticku instrumentu (null = zgodny albo źródło nie jest IB).</summary>
    public static string? TickSizeWarning(IMarketDataFeed? feed) => feed switch
    {
        IbWebDataFeed web => web.TickSizeWarning,
        null => null,
        _ => Tws.TickSizeWarning(feed),
    };

    /// <summary>Wyszukanie instrumentu w IB – „Sprawdź w IB”.</summary>
    public static Task<IbInstrumentInfo> LookupAsync(IbConnectionSettings settings, Instrument instrument) =>
        settings.UseWebApi ? IbWebDataFeed.LookupAsync(settings, instrument) : Tws.LookupAsync(settings, instrument);

    /// <summary>Sprawdzenie, ile dni wstecz IB udostępnia ticki (tylko TWS API – Web API nie ma historii ticków).</summary>
    public static Task<IbTickProbeResult> ProbeTickHistoryAsync(IbConnectionSettings settings, Instrument instrument, IProgress<string> progress,
        CancellationToken cancellationToken = default) =>
        settings.UseWebApi
            ? Task.FromResult(new IbTickProbeResult(instrument.Symbol, null, null, null, IbWebDataFeed.NoTickHistory, null))
            : Tws.ProbeTickHistoryAsync(settings, instrument, progress, cancellationToken);

    /// <summary>Jedna strona historycznych transakcji (maks. ok. 1000) od podanego momentu – źródło z <see cref="Create"/>.</summary>
    public static Task<TickPage> GetTicksPageAsync(IMarketDataFeed feed, DateTimeOffset start, CancellationToken token) =>
        feed is IbWebDataFeed web ? web.GetTicksPageAsync(start, token) : Tws.GetTicksPageAsync(feed, start, token);

    /// <summary>Świece D/W/M za okres wstecz od teraz (format IB, np. "5 Y") – źródło z <see cref="Create"/>.</summary>
    public static Task<OhlcPage> GetOhlcBarsAsync(IMarketDataFeed feed, BarInterval interval, string duration, CancellationToken token) =>
        feed is IbWebDataFeed web ? web.GetOhlcBarsAsync(interval, duration, token) : Tws.GetOhlcBarsAsync(feed, interval, duration, token);

    /// <summary>Świece 30 min (tylko sesja kasowa) za okres kończący się w <paramref name="end"/> (null = teraz) – źródło z <see cref="Create"/>.</summary>
    public static Task<OhlcPage> GetIntradayBarsAsync(IMarketDataFeed feed, DateTimeOffset? end, string duration, CancellationToken token) =>
        feed is IbWebDataFeed web ? web.GetIntradayBarsAsync(end, duration, token) : Tws.GetIntradayBarsAsync(feed, end, duration, token);

    /// <summary>Test połączenia.</summary>
    public static Task<string> TestConnectionAsync(IbConnectionSettings settings, Instrument instrument) =>
        settings.UseWebApi
            ? IbWebDataFeed.TestConnectionAsync(settings, instrument, TimeSpan.FromSeconds(20))
            : Tws.TestConnectionAsync(settings, instrument);

    /// <summary>Wywołania TWS API – każde w osobnej metodzie (NoInlining), przygotowywanej dopiero przy użyciu.</summary>
    private static class Tws
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static IMarketDataFeed Create(IbConnectionSettings settings) => new IbDataFeed(settings);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string? ContractName(IMarketDataFeed feed) => (feed as IbDataFeed)?.ContractName;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string? TickSizeWarning(IMarketDataFeed feed) => (feed as IbDataFeed)?.TickSizeWarning;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Task<IbInstrumentInfo> LookupAsync(IbConnectionSettings settings, Instrument instrument) =>
            IbDataFeed.LookupAsync(settings, instrument);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Task<IbTickProbeResult> ProbeTickHistoryAsync(IbConnectionSettings settings, Instrument instrument, IProgress<string> progress,
            CancellationToken cancellationToken) =>
            IbDataFeed.ProbeTickHistoryAsync(settings, instrument, progress, cancellationToken);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Task<TickPage> GetTicksPageAsync(IMarketDataFeed feed, DateTimeOffset start, CancellationToken token) =>
            ((IbDataFeed)feed).GetTicksPageAsync(start, token);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Task<OhlcPage> GetOhlcBarsAsync(IMarketDataFeed feed, BarInterval interval, string duration, CancellationToken token) =>
            ((IbDataFeed)feed).GetOhlcBarsAsync(interval, duration, token);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Task<OhlcPage> GetIntradayBarsAsync(IMarketDataFeed feed, DateTimeOffset? end, string duration, CancellationToken token) =>
            ((IbDataFeed)feed).GetIntradayBarsAsync(end, duration, token);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Task<string> TestConnectionAsync(IbConnectionSettings settings, Instrument instrument) =>
            IbDataFeed.TestConnectionAsync(settings, instrument, TimeSpan.FromSeconds(20));
    }
}
