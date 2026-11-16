using MarketProfile.Core;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Parametry profili: podstawowe liczby, kształt (D/P/b/B/trend), ogony i single printy, wybicia z IB,
/// typ i miejsce otwarcia, migracja wartości i nietknięte POC.
/// </summary>
public sealed class ProfileStatsTests
{
    private static readonly ProfileSettings Settings = new() { TimeZone = TimeZoneInfo.Utc, TicksPerRow = 4 }; // wiersz = 1.00
    private static readonly SessionDefinition Day = Settings.Sessions[1];
    private static readonly DateTime Start = new(2026, 11, 12, 9, 30, 0);

    /// <summary>Profil z liter (Low, High, Close); pierwsza litera otwiera się w <paramref name="open"/>.</summary>
    private static ProfileSnapshot Profile(decimal open, params (decimal Low, decimal High, decimal Close)[] letters) =>
        Profile(Start, open, letters);

    private static ProfileSnapshot Profile(DateTime start, decimal open, params (decimal Low, decimal High, decimal Close)[] letters)
    {
        var profile = new SessionProfile(Day, start, Settings);
        for (var i = 0; i < letters.Length; i++)
        {
            var (low, high, close) = letters[i];
            profile.AddPriceRange(i, low, high, close, 10, openPrice: i == 0 ? open : null);
        }

        return profile.GetSnapshot();
    }

    private static ProfileStats Stats(ProfileSnapshot profile, params ProfileSnapshot[] history) =>
        ProfileAnalyzer.Analyze(profile, history, isComplete: true, tpoMinutes: 30);

    [Fact]
    public void Podstawowe_ZakresIbIWybicie()
    {
        var stats = Stats(Profile(100, (100, 104, 103), (101, 105, 104), (105, 108, 107)));

        Assert.Equal(100m, stats.Open);
        Assert.Equal(108m, stats.High);
        Assert.Equal(100m, stats.Low);
        Assert.Equal(107m, stats.Close);
        Assert.Equal(8m, stats.Range);
        Assert.Equal(105m, stats.IbHigh);
        Assert.Equal(100m, stats.IbLow);
        Assert.Equal(3m, stats.IbExtensionUp);
        Assert.Equal('C', stats.IbExtensionUpLetter);
        Assert.Equal(RangeExtension.Up, stats.RangeExtension);
        Assert.Equal(3, stats.Periods);
        Assert.Equal('C', stats.LastLetter);
        Assert.True(stats.TpoAbovePoc + stats.TpoBelowPoc < stats.TotalTpo); // reszta to wiersz POC
    }

    [Fact]
    public void Ksztalt_D_Symetryczny()
    {
        var shape = Stats(Profile(100, (95, 105, 100), (96, 104, 100), (97, 103, 100), (98, 102, 100), (99, 101, 100), (98, 102, 100), (97, 103, 100))).Shape;
        Assert.Equal(ProfileShape.D, shape);
    }

    [Fact]
    public void Ksztalt_P_I_b()
    {
        var p = Stats(Profile(90, (90, 110, 108), (105, 110, 107), (105, 110, 108), (105, 110, 106), (105, 110, 107), (106, 110, 108), (105, 109, 107)));
        Assert.Equal(ProfileShape.P, p.Shape);
        Assert.Equal("P", p.ShapeCode);

        var b = Stats(Profile(110, (90, 110, 92), (90, 95, 93), (90, 95, 92), (90, 95, 94), (90, 95, 93), (90, 94, 92), (91, 95, 93)));
        Assert.Equal(ProfileShape.LowerB, b.Shape);
        Assert.Equal("b", b.ShapeCode);
    }

    [Fact]
    public void Ksztalt_B_DwaSkupiska()
    {
        var stats = Stats(Profile(92, (90, 94, 92), (90, 94, 93), (90, 94, 92), (94, 106, 105), (102, 106, 104), (102, 106, 103), (102, 106, 104)));
        Assert.Equal(ProfileShape.DoubleB, stats.Shape);
        Assert.Contains(stats.SinglePrints, r => r.Low == 95m && r.High == 101m);
    }

    [Fact]
    public void Ksztalt_Trend_WaskiIWydluzony()
    {
        var stats = Stats(Profile(100, (100, 104, 104), (104, 108, 108), (108, 112, 112), (112, 116, 116), (116, 120, 120), (120, 124, 124)));
        Assert.Equal(ProfileShape.Trend, stats.Shape);
    }

    [Fact]
    public void Ogony_ExcessIPoorLow()
    {
        // A sięga 112 sama (ogon 110–112), dół 100 odwiedzony przez kilka liter (płaski dołek).
        var stats = Stats(Profile(105, (100, 112, 104), (100, 109, 103), (100, 109, 105), (101, 108, 104)));

        Assert.Equal(3, stats.ExcessHigh);
        Assert.False(stats.PoorHigh);
        Assert.Equal(0, stats.ExcessLow);
        Assert.True(stats.PoorLow);
    }

    [Fact]
    public void TypOtwarcia_OpenDrive_ORR_OpenAuction()
    {
        var drive = Stats(Profile(100, (100, 106, 106), (105, 110, 109)));
        Assert.Equal(OpenType.OpenDrive, drive.OpenType);
        Assert.Equal(1, drive.OpenDirection);

        var reverse = Stats(Profile(100, (100, 108, 104), (96, 103, 97)));
        Assert.Equal(OpenType.OpenRejectionReverse, reverse.OpenType);
        Assert.Equal(-1, reverse.OpenDirection);

        var auction = Stats(Profile(100, (97, 103, 100.5m), (98, 102, 99)));
        Assert.Equal(OpenType.OpenAuction, auction.OpenType);

        Assert.Equal(OpenType.None, Stats(Profile(100, (99, 101, 100))).OpenType); // tylko litera A
    }

    [Fact]
    public void Relacje_OtwarcieMigracjaNakladanie()
    {
        // Poprzedni dzień: zakres 95–115, VA ok. 100–110.
        var previous = Profile(Start.AddDays(-1), 105,
            (95, 115, 105), (100, 110, 105), (100, 110, 104), (100, 110, 106), (101, 109, 105), (102, 108, 105));
        var higher = Profile(112, (111, 118, 115), (111, 118, 116), (112, 117, 115), (113, 118, 114));
        var stats = Stats(higher, previous);

        Assert.Equal(previous.TradingDate, stats.PreviousTradingDate);
        Assert.Equal(OpenLocation.AboveValue, stats.OpenLocation);
        Assert.Equal(ValueMigration.Higher, stats.ValueMigration);
        Assert.Equal(0, stats.ValueAreaOverlap);

        var overlapping = Stats(Profile(108, (104, 114, 108), (105, 114, 110), (105, 113, 109), (106, 114, 110)), previous);
        Assert.Equal(OpenLocation.InValue, overlapping.OpenLocation);
        Assert.Equal(ValueMigration.OverlappingHigher, overlapping.ValueMigration);
        Assert.InRange(overlapping.ValueAreaOverlap!.Value, 0.01, 0.99);
    }

    [Fact]
    public void Baza_ZapisIOdczytBezStrat()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mp-stats-{Guid.NewGuid():N}.db");
        try
        {
            var store = new MarketProfile.DataFeed.Storage.MarketDataStore(path);
            var previous = Profile(Start.AddDays(-1), 105, (95, 115, 105), (100, 110, 105), (100, 110, 104));
            var stats = Stats(Profile(92, (90, 94, 92), (90, 94, 93), (90, 94, 92), (94, 106, 105), (102, 106, 104), (102, 106, 103), (102, 106, 104)), previous);

            store.SaveProfileStats("ES", [stats]);
            store.SaveProfileStats("ES", [stats with { Close = 104.25m }]); // zastąpienie, nie duplikat
            var read = Assert.Single(store.ReadProfileStats("ES"));

            Assert.Equal(104.25m, read.Close);
            Assert.Equal(stats.Shape, read.Shape);
            Assert.Equal(stats.SinglePrints, read.SinglePrints);
            Assert.Equal(stats.NakedPocs, read.NakedPocs);
            Assert.Equal(stats.OpenType, read.OpenType);
            Assert.Equal(stats.ValueMigration, read.ValueMigration);
            Assert.Equal(stats.SessionStart, read.SessionStart);
            Assert.Equal(stats.PreviousTradingDate, read.PreviousTradingDate);
            Assert.Empty(store.ReadProfileStats("NQ"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void NietknietePoc_PoziomyDoKtorychCenaNieWrocila()
    {
        var day1 = Profile(Start.AddDays(-2), 100, (95, 105, 100), (98, 102, 100), (99, 101, 100)); // POC ok. 100
        var day2 = Profile(Start.AddDays(-1), 110, (106, 115, 110), (108, 112, 110), (109, 111, 110)); // POC ok. 110

        var away = Stats(Profile(116, (112, 120, 118), (113, 119, 117)), day1, day2);
        Assert.Equal([day1.Poc, day2.Poc], away.NakedPocs);

        var back = Stats(Profile(112, (108, 120, 118), (109, 119, 117)), day1, day2);
        Assert.Equal([day1.Poc], back.NakedPocs); // POC dnia 2 dotknięty
    }
}
