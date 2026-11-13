using MarketProfile.Core;

namespace MarketProfile.DataFeed.Storage;

/// <summary>
/// Tworzy w bazie dane testowe (ticki z generatora cen), żeby można było sprawdzić odtwarzanie z bazy
/// zanim zostaną zapisane prawdziwe dane od brokera.
/// Dane testowe mają źródło <see cref="MarketDataStore.SourceTest"/> – ich ponowne wygenerowanie
/// usuwa tylko poprzednie dane testowe, a dane brokera pozostają nienaruszone.
/// </summary>
public static class TestDataGenerator
{
    /// <summary>
    /// Generuje ticki dla ostatnich <paramref name="days"/> dni (do teraz) i zapisuje je w bazie.
    /// </summary>
    /// <returns>Liczba zapisanych ticków.</returns>
    public static long Generate(MarketDataStore store, Instrument instrument, int days)
    {
        store.DeleteSource(instrument.Symbol, MarketDataStore.SourceTest);

        var to = BarAggregator.FloorToMinute(DateTimeOffset.UtcNow);
        var from = to.AddDays(-days);
        long count = 0;

        // Zapis paczkami po jednym dniu – nie trzymamy wszystkiego w pamięci naraz.
        var generator = new PriceGenerator(instrument);
        for (var dayStart = from; dayStart < to; dayStart = dayStart.AddDays(1))
        {
            var dayEnd = dayStart.AddDays(1) < to ? dayStart.AddDays(1) : to;
            var ticks = generator.GenerateTicks(dayStart, dayEnd).Where(t => t.Time < dayEnd).ToList();
            store.InsertTicks(instrument.Symbol, ticks, MarketDataStore.SourceTest);
            count += ticks.Count;
        }

        return count;
    }
}
