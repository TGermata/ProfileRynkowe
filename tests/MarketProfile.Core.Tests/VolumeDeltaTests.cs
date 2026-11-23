using MarketProfile.DataFeed.IBWeb;
using MarketProfile.DataFeed.Storage;
using Microsoft.Data.Sqlite;

namespace MarketProfile.Core.Tests;

/// <summary>Delta wolumenu: strona transakcji z bid/ask, reguła ticka, profil różnicowy i zapis strony w bazie.</summary>
public sealed class VolumeDeltaTests : IDisposable
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero); // 10:00 w Nowym Jorku
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mp-delta-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    private static Tick TickAt(int hour, int minute, decimal price, long volume, TradeSide side = TradeSide.Unknown)
    {
        var local = new DateTime(2026, 11, 12, hour, minute, 0, DateTimeKind.Unspecified);
        return new Tick(new DateTimeOffset(local, NewYork.GetUtcOffset(local)), price, volume, side);
    }

    private static ProfileSnapshot DaySnapshot(ProfileBuilder builder) =>
        builder.Profiles.Single(p => p.Session.Name == "Dzienna").GetSnapshot();

    [Theory]
    [InlineData(100.25, TradeSide.Buy)]     // po ask
    [InlineData(100.50, TradeSide.Buy)]     // powyżej ask
    [InlineData(100.00, TradeSide.Sell)]    // po bid
    [InlineData(99.75, TradeSide.Sell)]     // poniżej bid
    public void FromQuote_AskToKupnoBidToSprzedaz(double price, TradeSide expected) =>
        Assert.Equal(expected, TradeSideClassifier.FromQuote((decimal)price, 100.00m, 100.25m));

    [Fact]
    public void FromQuote_SrodekSpreaduIBrakNotowan_Nieznana()
    {
        Assert.Equal(TradeSide.Unknown, TradeSideClassifier.FromQuote(100.125m, 100.00m, 100.25m));
        Assert.Equal(TradeSide.Unknown, TradeSideClassifier.FromQuote(100m, null, 100.25m));
        Assert.Equal(TradeSide.Unknown, TradeSideClassifier.FromQuote(100m, 100.5m, 100.25m)); // odwrócone notowania
    }

    [Fact]
    public void RegulaTicka_WGoreWDolTaSamaCenaDziedziczy()
    {
        var rule = new TickRule();
        Assert.Equal(TradeSide.Unknown, rule.Classify(100m, TradeSide.Unknown)); // brak punktu odniesienia
        Assert.Equal(TradeSide.Buy, rule.Classify(100.25m, TradeSide.Unknown));
        Assert.Equal(TradeSide.Buy, rule.Classify(100.25m, TradeSide.Unknown));  // ta sama cena – jak poprzednia
        Assert.Equal(TradeSide.Sell, rule.Classify(100m, TradeSide.Unknown));
        Assert.Equal(TradeSide.Buy, rule.Classify(100m, TradeSide.Buy));          // zmierzona strona ma pierwszeństwo
        Assert.Equal(TradeSide.Buy, rule.Classify(100m, TradeSide.Unknown));      // … i jest punktem odniesienia
    }

    [Fact]
    public void Profil_DeltaWierszyISesji()
    {
        var builder = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4 }); // wiersz = 1 punkt
        builder.ProcessTick(TickAt(9, 35, 100m, 10, TradeSide.Buy));
        builder.ProcessTick(TickAt(9, 36, 100m, 4, TradeSide.Sell));
        builder.ProcessTick(TickAt(9, 37, 101m, 6, TradeSide.Sell));
        builder.ProcessTick(TickAt(9, 38, 101m, 3)); // nieznana – reguła ticka: ta sama cena → sprzedaż

        var snapshot = DaySnapshot(builder);
        var row100 = snapshot.Levels.Single(l => l.Price == 100m);
        var row101 = snapshot.Levels.Single(l => l.Price == 101m);

        Assert.Equal((10L, 4L, 6L), (row100.BuyVolume, row100.SellVolume, row100.Delta));
        Assert.Equal((0L, 9L, -9L), (row101.BuyVolume, row101.SellVolume, row101.Delta));
        Assert.Equal(-3, snapshot.TotalDelta);
        Assert.Equal(9, snapshot.MaxRowAbsDelta);
        Assert.Equal(1.0, snapshot.ClassifiedShare);
    }

    [Fact]
    public void Profil_SwiecaNieZmieniaDeltyAleObnizaPokrycie()
    {
        var builder = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4 });
        var local = new DateTime(2026, 11, 12, 9, 31, 0, DateTimeKind.Unspecified);
        builder.ProcessBar(new Bar(new DateTimeOffset(local, NewYork.GetUtcOffset(local)), 100m, 102m, 99m, 101m, 300));
        builder.ProcessTick(TickAt(9, 40, 101m, 100, TradeSide.Buy));

        var snapshot = DaySnapshot(builder);
        Assert.Equal(100, snapshot.TotalDelta);
        Assert.Equal(400, snapshot.TotalVolume);
        Assert.Equal(0.25, snapshot.ClassifiedShare, 3);

        // Pierwszy tick bez strony po świecy: porównanie z ceną zamknięcia świecy (101 → 101.25 = kupno).
        builder.ProcessTick(TickAt(9, 41, 101.25m, 5));
        Assert.Equal(105, DaySnapshot(builder).TotalDelta);
    }

    [Fact]
    public void Profil_DeltaBiezacegoSlupkaISumaCzesciRozbicia()
    {
        var builder = new ProfileBuilder(new ProfileSettings { TicksPerRow = 4 });
        builder.ProcessTick(TickAt(9, 35, 100m, 10, TradeSide.Buy));  // A
        builder.ProcessTick(TickAt(10, 5, 101m, 7, TradeSide.Sell));  // B
        builder.ProcessTick(TickAt(10, 6, 102m, 2, TradeSide.Buy));   // B

        var profile = builder.Profiles.Single(p => p.Session.Name == "Dzienna");
        var whole = profile.GetSnapshot();
        Assert.Equal(-7, whole.CurrentPeriodDelta[101m]);
        Assert.Equal(2, whole.CurrentPeriodDelta[102m]);
        Assert.False(whole.CurrentPeriodDelta.ContainsKey(100m));
        Assert.Equal(7, whole.MaxCurrentPeriodAbsDelta);

        var partA = profile.GetSnapshot(0, 0);
        var partB = profile.GetSnapshot(1);
        Assert.Equal(whole.TotalDelta, partA.TotalDelta + partB.TotalDelta);
    }

    [Fact]
    public void Baza_ZapisujeIOdczytujeStrone()
    {
        var store = new MarketDataStore(_dbPath);
        store.InsertTicks("ES",
            [new Tick(T0, 6000m, 1, TradeSide.Buy), new Tick(T0.AddSeconds(1), 6000m, 2, TradeSide.Sell), new Tick(T0.AddSeconds(2), 6000m, 3)],
            MarketDataStore.SourceBroker);

        var sides = store.ReadTicks("ES", T0, T0.AddMinutes(1)).Select(t => t.Side).ToList();
        Assert.Equal([TradeSide.Buy, TradeSide.Sell, TradeSide.Unknown], sides);
    }

    [Fact]
    public void Baza_StaraBezKolumnySide_Migruje()
    {
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ticks (symbol TEXT NOT NULL, time INTEGER NOT NULL, price INTEGER NOT NULL,
                                    volume INTEGER NOT NULL, source TEXT NOT NULL);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
            command.CommandText = $"INSERT INTO ticks VALUES ('ES', {T0.ToUnixTimeMilliseconds()}, 600000000000, 5, 'IB')";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var store = new MarketDataStore(_dbPath);
        var tick = Assert.Single(store.ReadTicks("ES", T0, T0.AddMinutes(1)));
        Assert.Equal(TradeSide.Unknown, tick.Side);
        Assert.Equal(5, tick.Volume);

        store.InsertTicks("ES", [new Tick(T0.AddSeconds(1), 6000m, 1, TradeSide.Sell)], "IB");
        Assert.Equal(TradeSide.Sell, store.ReadTicks("ES", T0, T0.AddMinutes(1)).Last().Side);
    }

    [Fact]
    public void Baza_ZastapienieOdcinkaHistoriaZachowujeZmierzonaStrone()
    {
        var store = new MarketDataStore(_dbPath);
        store.InsertTicks("ES",
            [new Tick(T0, 6000m, 1, TradeSide.Buy), new Tick(T0, 6000m, 1, TradeSide.Sell), new Tick(T0.AddSeconds(3), 6001m, 2, TradeSide.Buy)],
            MarketDataStore.SourceBroker);

        // Historyczne ticki IB (bez strony) zastępują odcinek – te same transakcje dostają zmierzoną stronę.
        store.SaveHistoricalTicks("ES", T0, T0.AddMinutes(1),
            [new Tick(T0, 6000m, 1), new Tick(T0, 6000m, 1), new Tick(T0.AddSeconds(3), 6001m, 2), new Tick(T0.AddSeconds(4), 6001m, 1)],
            MarketDataStore.SourceBroker);

        var sides = store.ReadTicks("ES", T0, T0.AddMinutes(1)).Select(t => t.Side).ToList();
        Assert.Equal([TradeSide.Buy, TradeSide.Sell, TradeSide.Buy, TradeSide.Unknown], sides);
    }

    [Fact]
    public void WebApi_BidAskUstalaStroneTransakcji()
    {
        var assembler = new IbWebTickAssembler(isIndex: false);
        var now = DateTimeOffset.UtcNow;
        Dictionary<string, string> Fields(params (string Key, string Value)[] f) => f.ToDictionary(x => x.Key, x => x.Value);

        assembler.Process(Fields(("31", "100.25"), ("7762", "1000"), ("84", "100.00"), ("86", "100.25")), now);
        var buy = Assert.Single(assembler.Process(Fields(("7762", "1010")), now));
        Assert.Equal((10L, TradeSide.Buy), (buy.Volume, buy.Side));

        var sell = Assert.Single(assembler.Process(Fields(("31", "100.00"), ("7762", "1013")), now));
        Assert.Equal((3L, TradeSide.Sell), (sell.Volume, sell.Side));
    }
}
