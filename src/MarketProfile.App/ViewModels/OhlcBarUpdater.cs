using MarketProfile.Core;
using MarketProfile.DataFeed.IB;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Uzupełnianie w bazie świec dziennych, tygodniowych i miesięcznych z IB – w pobieraniu w tle
/// (połączenie pobierania w tle) albo jednorazowo, na żądanie z zakładki „Wykres D/W/M” (własne połączenie).
/// </summary>
public static class OhlcBarUpdater
{
    /// <summary>Uzupełnia świece D/W/M instrumentu przez już połączone źródło IB.</summary>
    public static Task<IReadOnlyList<OhlcDownloadResult>> UpdateAsync(
        IMarketDataFeed feed, string symbol, MarketDataStore store, CancellationToken token)
    {
        var downloader = new OhlcBarDownloader(store, (interval, duration, ct) => IbFeeds.GetOhlcBarsAsync(feed, interval, duration, ct));
        return downloader.UpdateAsync(symbol, MarketDataStore.SourceBroker, DateTimeOffset.UtcNow, token);
    }

    /// <summary>Uzupełnia świece 30 min (sesja kasowa) – rynek szeroki, wykres „Szeroki rynek”.</summary>
    public static Task<OhlcDownloadResult> UpdateIntradayAsync(
        IMarketDataFeed feed, string symbol, MarketDataStore store, CancellationToken token)
    {
        var downloader = new OhlcBarDownloader(store, (interval, duration, ct) => IbFeeds.GetOhlcBarsAsync(feed, interval, duration, ct));
        return downloader.UpdateIntradayAsync(symbol, MarketDataStore.SourceBroker, DateTimeOffset.UtcNow,
            (end, duration, ct) => IbFeeds.GetIntradayBarsAsync(feed, end, duration, ct), token);
    }

    /// <summary>
    /// Jednorazowe uzupełnienie: łączy się z TWS (Client ID o 2 większy niż główny – nie koliduje z danymi
    /// na żywo ani z pobieraniem w tle), pobiera świece D/W/M i rozłącza się.
    /// </summary>
    /// <param name="intraday">Także świece 30 min (sesja kasowa) – dla rynku szerokiego.</param>
    public static async Task<IReadOnlyList<OhlcDownloadResult>> ConnectAndUpdateAsync(
        Instrument instrument, IbConnectionSettings mainConnection, MarketDataStore store, CancellationToken token, bool intraday = false)
    {
        await using var feed = IbFeeds.Create(mainConnection with { ClientId = mainConnection.ClientId + 2 });
        await feed.ConnectAsync(instrument, token).ConfigureAwait(false);
        var results = (await UpdateAsync(feed, instrument.Symbol, store, token).ConfigureAwait(false)).ToList();
        if (intraday)
        {
            results.Add(await UpdateIntradayAsync(feed, instrument.Symbol, store, token).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Opis błędów interwałów (null = bez błędów).</summary>
    public static string? Errors(IReadOnlyList<OhlcDownloadResult> results)
    {
        var errors = results.Where(r => r.Error is not null).Select(r => $"{r.Interval.DisplayName()}: {r.Error}").ToList();
        return errors.Count > 0 ? string.Join("; ", errors) : null;
    }
}
