using MarketProfile.Core;
using MarketProfile.DataFeed.Settlements;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Parametry profili: podstawowe liczby, kształt (D/P/b/B/trend), ogony i single printy, wybicia z IB,
/// typ i miejsce otwarcia, migracja wartości i nietknięte POC.
/// </summary>
public sealed class ProfileStatsTests
{
    private static readonly ProfileSettings Settings = new() { TimeZone = TimeZoneInfo.Utc, TicksPerRow = 4 }; // wiersz = 1.00
    private static readonly SessionDefinition Night = Settings.Sessions[0];
    private static readonly SessionDefinition Day = Settings.Sessions[1];
    private static readonly DateTime Start = new(2026, 11, 12, 9, 30, 0);

    /// <summary>Profil z liter (Low, High, Close); pierwsza litera otwiera się w <paramref name="open"/>.</summary>
    private static ProfileSnapshot Profile(decimal open, params (decimal Low, decimal High, decimal Close)[] letters) =>
        Profile(Start, open, letters);

    private static ProfileSnapshot Profile(DateTime start, decimal open, params (decimal Low, decimal High, decimal Close)[] letters) =>
        Profile(Day, start, open, letters);

    private static ProfileSnapshot Profile(SessionDefinition session, DateTime start, decimal open,
        params (decimal Low, decimal High, decimal Close)[] letters)
    {
        var profile = new SessionProfile(session, start, Settings);
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
        Assert.Equal(IbExtensionDirection.Up, stats.IbExtension);
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
        Assert.Equal(ValueLocation.AboveValue, stats.OpenVsPreviousValue);
        Assert.Equal(ValueMigration.Higher, stats.ValueMigration);
        Assert.Equal(0, stats.ValueAreaOverlap);

        var overlapping = Stats(Profile(108, (104, 114, 108), (105, 114, 110), (105, 113, 109), (106, 114, 110)), previous);
        Assert.Equal(ValueLocation.InValue, overlapping.OpenVsPreviousValue);
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

    private static readonly DateTime NightStart = new(2026, 11, 11, 18, 0, 0); // noc przed dniem Start

    private static ProfileSnapshot PreviousDay() => Profile(Start.AddDays(-1), 105,
        (95, 115, 105), (100, 110, 105), (100, 110, 104), (100, 110, 106), (101, 109, 105), (102, 108, 105));

    [Fact]
    public void Noc_BezRelacjiZPoprzedniaNoca()
    {
        var night1 = Profile(Night, NightStart.AddDays(-1), 100, (95, 105, 100), (98, 102, 100), (99, 101, 100));
        var night2 = Profile(Night, NightStart, 120, (118, 125, 120), (119, 124, 121), (119, 123, 122));
        var stats = Stats(night2, night1);

        Assert.False(stats.IsDaySession);
        Assert.Null(stats.PreviousTradingDate);
        Assert.Equal(ValueMigration.None, stats.ValueMigration);
        Assert.Equal(ValueLocation.None, stats.OpenVsPreviousValue);
        Assert.Equal(OpenType.None, stats.OpenType);
        Assert.Empty(stats.NakedPocs);
        Assert.Null(stats.OvernightHigh);
        Assert.Equal(121.5m, stats.Halfback); // (125 + 118) / 2 – zaokrąglone do ticka 0.25
    }

    [Fact]
    public void Dzien_PoprzedniDzienTenSamTypPomimoNocyPomiedzy()
    {
        var previous = PreviousDay();
        var night = Profile(Night, NightStart, 112, (110, 121, 118), (112, 119, 117), (113, 118, 115));
        var day = Profile(116, (111, 118, 115), (111, 118, 116), (112, 117, 115), (113, 118, 114));
        var stats = Stats(day, previous, night);

        Assert.Equal(previous.TradingDate, stats.PreviousTradingDate);
        Assert.Equal(ValueMigration.Higher, stats.ValueMigration);
        Assert.Equal(121m, stats.OvernightHigh);
        Assert.Equal(110m, stats.OvernightLow);
        Assert.Equal(night.Poc, stats.OvernightPoc);
        Assert.Equal(OvernightLocation.InOvernight, stats.OpenVsOvernight);
        Assert.Equal(previous.LastPrice, stats.PreviousClose);
        Assert.Equal(105m, stats.PreviousHalfback);
        Assert.Equal(1, stats.OpenVsPreviousHb);
        Assert.Equal(1m, stats.Gap); // otwarcie 116 nad High poprzedniego dnia (115)
    }

    [Fact]
    public void Dzien_StaraNocNieJestNocaPrzedDniem()
    {
        var night = Profile(Night, NightStart.AddDays(-1), 112, (110, 121, 118), (112, 119, 117));
        var previous = PreviousDay(); // dzień po tej nocy
        var stats = Stats(Profile(116, (111, 118, 115), (111, 118, 116)), night, previous);

        Assert.Null(stats.OvernightHigh);
        Assert.Equal(OvernightLocation.None, stats.OpenVsOvernight);
    }

    [Fact]
    public void Dzien_TrendowyPomijaWartosc()
    {
        var trend = Profile(116, (116, 120, 120), (120, 124, 124), (124, 128, 128), (128, 132, 132), (132, 136, 136), (136, 140, 140));
        var stats = Stats(trend, PreviousDay());

        Assert.Equal(ProfileShape.Trend, stats.Shape);
        Assert.Equal(ValueMigration.TrendDay, stats.ValueMigration);
        Assert.Null(stats.ValueAreaOverlap);
    }

    [Fact]
    public void Wartosc_WewnatrzINizej()
    {
        var previous = PreviousDay();
        var inside = Stats(Profile(105, (103, 107, 105), (104, 106, 105), (104, 106, 104), (103, 107, 106)), previous);
        Assert.Equal(ValueMigration.Inside, inside.ValueMigration);

        var lower = Stats(Profile(95, (90, 97, 93), (91, 96, 92), (90, 96, 94), (91, 97, 93)), previous);
        Assert.Equal(ValueMigration.Lower, lower.ValueMigration);
    }

    [Fact]
    public void Settle_TylkoDlaZakonczonejSesjiILukaDoSettlePoprzedniego()
    {
        var previous = PreviousDay();
        var day = Profile(116, (111, 118, 115), (111, 118, 116));
        var settlements = new Dictionary<DateOnly, Settlement>
        {
            [previous.TradingDate] = new(previous.TradingDate, 106.25m, Settlement.SourceCme, "DEC 26"),
            [day.TradingDate] = new(day.TradingDate, 115.75m, Settlement.SourceManual),
        };

        var complete = ProfileAnalyzer.Analyze(day, [previous], isComplete: true, tpoMinutes: 30, settlements: settlements);
        Assert.Equal(115.75m, complete.Settle);
        Assert.Equal(Settlement.SourceManual, complete.SettleSource);
        Assert.Equal(106.25m, complete.PreviousSettle);
        Assert.Equal(1m, complete.Gap); // luka liczona od High poprzedniego dnia (115), nie od settle

        var running = ProfileAnalyzer.Analyze(day, [previous], isComplete: false, tpoMinutes: 30, settlements: settlements);
        Assert.Null(running.Settle); // sesja w toku – settle jeszcze nie istnieje
        Assert.Equal(106.25m, running.PreviousSettle);
    }

    [Fact]
    public void Baza_SettleRecznyMaPierwszenstwoIStaraTabelaDostajeKolumny()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mp-settle-{Guid.NewGuid():N}.db");
        try
        {
            // Stara baza: tabela profile_stats bez nowych kolumn.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE profile_stats (symbol TEXT NOT NULL, session TEXT NOT NULL, session_start TEXT NOT NULL, " +
                                      "trading_date TEXT NOT NULL, PRIMARY KEY (symbol, session, session_start))";
                command.ExecuteNonQuery();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var store = new MarketProfile.DataFeed.Storage.MarketDataStore(path);
            var date = new DateOnly(2026, 11, 16);

            Assert.True(store.SaveSettlement("ES", new Settlement(date, 6881.25m, Settlement.SourceManual)));
            Assert.False(store.SaveSettlement("ES", new Settlement(date, 6880m, Settlement.SourceCme, "DEC 26"))); // nie nadpisuje ręcznego
            Assert.Equal(6881.25m, store.ReadSettlements("ES")[date].Price);

            store.DeleteSettlement("ES", date);
            Assert.True(store.SaveSettlement("ES", new Settlement(date, 6880m, Settlement.SourceCme, "DEC 26")));
            Assert.True(store.SaveSettlement("ES", new Settlement(date, 6879.5m, Settlement.SourceManual)));
            var read = store.ReadSettlements("ES")[date];
            Assert.Equal(6879.5m, read.Price);
            Assert.True(read.IsManual);
            Assert.Empty(store.ReadSettlements("NQ"));
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
    public void Baza_NoweKolumnyParametrowWStarejBazie()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mp-stats-old-{Guid.NewGuid():N}.db");
        try
        {
            // Stara baza: tabela profile_stats w poprzednim formacie (bez nowych kolumn) – tworzy ją bieżąca wersja,
            // po czym usuwamy kolumny, odtwarzając tabelę ze starym zestawem pól.
            var store = new MarketProfile.DataFeed.Storage.MarketDataStore(path);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    ALTER TABLE profile_stats DROP COLUMN hb; ALTER TABLE profile_stats DROP COLUMN settle;
                    ALTER TABLE profile_stats DROP COLUMN settle_source; ALTER TABLE profile_stats DROP COLUMN onh;
                    ALTER TABLE profile_stats DROP COLUMN onpoc; ALTER TABLE profile_stats DROP COLUMN open_vs_overnight;
                    """;
                command.ExecuteNonQuery();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            store = new MarketProfile.DataFeed.Storage.MarketDataStore(path); // migracja dopisuje brakujące kolumny

            var previous = PreviousDay();
            var night = Profile(Night, NightStart, 112, (110, 121, 118), (112, 119, 117));
            var stats = Stats(Profile(116, (111, 118, 115), (111, 118, 116)), previous, night) with { Settle = 115.5m, SettleSource = "CME" };
            store.SaveProfileStats("ES", [stats]);
            var back = Assert.Single(store.ReadProfileStats("ES"));

            Assert.Equal(stats.Halfback, back.Halfback);
            Assert.Equal(115.5m, back.Settle);
            Assert.Equal("CME", back.SettleSource);
            Assert.Equal(stats.OvernightHigh, back.OvernightHigh);
            Assert.Equal(stats.OvernightPoc, back.OvernightPoc);
            Assert.Equal(stats.OpenVsOvernight, back.OpenVsOvernight);
            Assert.Equal(stats.Gap, back.Gap);
            Assert.Equal(stats.Inventory, back.Inventory);
            Assert.Equal(stats.InventorySize, back.InventorySize);
            Assert.Equal(stats.InventoryCorrected, back.InventoryCorrected);
            Assert.Equal(stats.InventoryCorrectedLetter, back.InventoryCorrectedLetter);
            Assert.Equal(stats.PreviousHalfback, back.PreviousHalfback);
            Assert.Equal(stats.TrendDirection, back.TrendDirection);
            Assert.Equal(stats.IsTrendDay, back.IsTrendDay);
            Assert.Equal(stats.TrendFrom, back.TrendFrom);
            Assert.Equal(stats.TrendTo, back.TrendTo);
            Assert.Equal(stats.TrendPeriods, back.TrendPeriods);
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
    public void Baza_StareNazwyKolumnSaZmienianeBezUtratyDanych()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mp-stats-rename-{Guid.NewGuid():N}.db");
        try
        {
            var previous = PreviousDay();
            var night = Profile(Night, NightStart, 112, (110, 121, 118), (112, 119, 117));
            var stats = Stats(Profile(116, (111, 118, 115), (111, 118, 116)), previous, night) with
            {
                OpenVsOvernight = OvernightLocation.AboveOnh,
                PullbackLow = 111.25m,
                PullbackLetter = 'D',
                TrendFrom = 'A',
                TrendTo = 'C',
                TrendPeriods = 3,
            };
            var store = new MarketProfile.DataFeed.Storage.MarketDataStore(path);
            store.SaveProfileStats("ES", [stats]);

            // Baza z poprzedniej wersji: stare nazwy kolumn i stara nazwa wartości położenia względem nocy.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    ALTER TABLE profile_stats RENAME COLUMN prev_trading_date TO previous_trading_date;
                    ALTER TABLE profile_stats RENAME COLUMN open_vs_prev_va TO open_location;
                    ALTER TABLE profile_stats RENAME COLUMN ib_ext TO range_extension;
                    ALTER TABLE profile_stats RENAME COLUMN hb TO halfback;
                    ALTER TABLE profile_stats RENAME COLUMN prev_hb TO prev_halfback;
                    ALTER TABLE profile_stats RENAME COLUMN open_vs_prev_hb TO open_vs_prev_halfback;
                    ALTER TABLE profile_stats RENAME COLUMN onh TO on_high;
                    ALTER TABLE profile_stats RENAME COLUMN onl TO on_low;
                    ALTER TABLE profile_stats RENAME COLUMN onpoc TO on_poc;
                    ALTER TABLE profile_stats RENAME COLUMN inventory TO opening_inventory;
                    ALTER TABLE profile_stats RENAME COLUMN trend_from TO interval_from;
                    ALTER TABLE profile_stats RENAME COLUMN trend_to TO interval_to;
                    ALTER TABLE profile_stats RENAME COLUMN trend_periods TO interval_periods;
                    ALTER TABLE profile_stats RENAME COLUMN pbl TO pullback_low;
                    ALTER TABLE profile_stats RENAME COLUMN pbh TO pullback_high;
                    ALTER TABLE profile_stats RENAME COLUMN pb_letter TO pullback_letter;
                    ALTER TABLE profile_stats RENAME COLUMN IBH TO ib_high;
                    ALTER TABLE profile_stats RENAME COLUMN IBL TO ib_low;
                    ALTER TABLE profile_stats RENAME COLUMN POC TO poc;
                    ALTER TABLE profile_stats RENAME COLUMN VAH TO vah;
                    ALTER TABLE profile_stats RENAME COLUMN SETTLE TO settle;
                    ALTER TABLE profile_stats RENAME COLUMN GAP TO gap;
                    ALTER TABLE profile_stats RENAME COLUMN TPO_minutes TO tpo_minutes;
                    ALTER TABLE profile_stats RENAME COLUMN naked_POCs TO naked_pocs;
                    ALTER TABLE profile_stats RENAME COLUMN VA_width TO va_width;
                    UPDATE profile_stats SET open_vs_overnight = 'AboveHigh';
                    """;
                command.ExecuteNonQuery();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            store = new MarketProfile.DataFeed.Storage.MarketDataStore(path); // migracja: nowe nazwy kolumn i wartości
            var back = Assert.Single(store.ReadProfileStats("ES"));

            Assert.Equal(stats.PreviousTradingDate, back.PreviousTradingDate);
            Assert.Equal(stats.OpenVsPreviousValue, back.OpenVsPreviousValue);
            Assert.Equal(stats.IbExtension, back.IbExtension);
            Assert.Equal(stats.Halfback, back.Halfback);
            Assert.Equal(stats.PreviousHalfback, back.PreviousHalfback);
            Assert.Equal(stats.OpenVsPreviousHb, back.OpenVsPreviousHb);
            Assert.Equal(stats.OvernightHigh, back.OvernightHigh);
            Assert.Equal(stats.OvernightLow, back.OvernightLow);
            Assert.Equal(stats.OvernightPoc, back.OvernightPoc);
            Assert.Equal(OvernightLocation.AboveOnh, back.OpenVsOvernight);
            Assert.Equal(stats.Inventory, back.Inventory);
            Assert.Equal('A', back.TrendFrom);
            Assert.Equal('C', back.TrendTo);
            Assert.Equal(3, back.TrendPeriods);
            Assert.Equal(111.25m, back.PullbackLow);
            Assert.Equal('D', back.PullbackLetter);
            Assert.Equal(stats.IbHigh, back.IbHigh);
            Assert.Equal(stats.Poc, back.Poc);
            Assert.Equal(stats.ValueAreaWidth, back.ValueAreaWidth);

            // Nazwy kolumn dokładnie jak w bieżącej wersji – skróty dużymi literami (np. POC, prev_HB, IBH).
            using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            check.Open();
            using var info = check.CreateCommand();
            info.CommandText = "PRAGMA table_info(profile_stats)";
            var columns = new List<string>();
            using (var reader = info.ExecuteReader())
            {
                while (reader.Read())
                {
                    columns.Add(reader.GetString(1));
                }
            }

            Assert.Equal(MarketProfile.DataFeed.Storage.MarketDataStore.ProfileStatsColumnNames.Order(StringComparer.Ordinal), columns.Order(StringComparer.Ordinal));
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
    public void Cme_OdczytOdpowiedzi()
    {
        const string json = """
            {"settlements":[
              {"month":"DEC 26","open":"6870.00","settle":"6,881.25","volume":"1,234,567"},
              {"month":"MAR 27","open":"-","settle":"6942.50","volume":"12,345"},
              {"month":"Total","settle":"","volume":"1,246,912"}],
             "tradeDate":"11/16/2026","empty":false}
            """;
        var date = new DateOnly(2026, 11, 16);

        var front = CmeSettlementClient.Parse(json, date, "");
        Assert.Equal(6881.25m, front.Settlement!.Price);
        Assert.Equal("DEC 26", front.Settlement.Contract);
        Assert.Equal(Settlement.SourceCme, front.Settlement.Source);

        Assert.Equal(6942.5m, CmeSettlementClient.Parse(json, date, "202703").Settlement!.Price);

        var missing = CmeSettlementClient.Parse(json, date, "202706");
        Assert.Null(missing.Settlement);
        Assert.True(missing.NotPublished);

        Assert.Null(CmeSettlementClient.Parse("<html>Access denied</html>", date, "").Settlement);
        Assert.Equal("DEC 26", CmeSettlementClient.MonthLabel("202612"));
        Assert.Null(CmeSettlementClient.ParsePrice("-"));
        Assert.Null(CmeSettlementClient.ParsePrice("110'16"));
    }

    /// <summary>Okresy TPO z podanych (Open, Low, High) – kolejno od A.</summary>
    private static List<TpoPeriod> Periods(params (decimal Open, decimal Low, decimal High)[] bars) =>
        [.. bars.Select((b, i) => new TpoPeriod(i, Start.AddMinutes(30 * i), Start.AddMinutes(30 * (i + 1)), b.High, b.Low) { Open = b.Open })];

    [Fact]
    public void Interwalowanie_WGoreDzienTrendowyIPullbackLow()
    {
        // A..H: początki coraz wyżej (A–H = 8 z 10 okresów), potem I i J – wybicie w dół (PULLBACK LOW w J).
        var trend = ProfileAnalyzer.TrendOf(Periods(
            (100, 99, 102), (101, 100, 103), (102, 101, 104), (103, 102, 105), (104, 103, 106),
            (105, 104, 107), (106, 105, 108), (107, 106, 109), (106, 101, 107), (102, 97, 103)), totalPeriods: 10);

        Assert.Equal(1, trend.Direction);
        Assert.Equal('A', trend.From);
        Assert.Equal('H', trend.To);
        Assert.Equal(8, trend.Periods);
        Assert.True(trend.IsTrendDay);
        Assert.Equal(97m, trend.PullbackPrice);
        Assert.Equal('J', trend.PullbackLetter);
    }

    [Fact]
    public void Interwalowanie_WDolPullbackHigh()
    {
        var trend = ProfileAnalyzer.TrendOf(Periods(
            (110, 108, 111), (109, 107, 110), (108, 106, 109), (107, 105, 108), (106, 104, 107), (105, 103, 106), (108, 104, 112)),
            totalPeriods: 7);

        Assert.Equal(-1, trend.Direction);
        Assert.Equal(6, trend.Periods); // A–F
        Assert.True(trend.IsTrendDay);
        Assert.Equal(112m, trend.PullbackPrice); // PULLBACK HIGH w G
        Assert.Equal('G', trend.PullbackLetter);
    }

    [Fact]
    public void Interwalowanie_NizszyPoczatekPrzerywaNawetBezWybiciaDolka()
    {
        // D otwiera się niżej niż C (ale nad dołkiem C) – koniec serii A–C, potem nowa seria D–F.
        var trend = ProfileAnalyzer.TrendOf(Periods(
            (100, 99, 102), (101, 100, 103), (103, 101, 105), (102.5m, 102, 104), (103.5m, 103, 105), (104.5m, 104, 106)), totalPeriods: 6);

        Assert.Equal(3, trend.Periods);
        Assert.False(trend.IsTrendDay); // 3 z 6 < 60%
        Assert.Null(trend.PullbackPrice);
    }

    [Fact]
    public void Interwalowanie_Prog60ProcentISesjaWToku()
    {
        var six = Periods((100, 99, 101), (101, 100, 102), (102, 101, 103), (103, 102, 104), (104, 103, 105), (105, 104, 106));

        Assert.True(ProfileAnalyzer.TrendOf(six, totalPeriods: 10).IsTrendDay);   // 6 z 10 = 60%
        Assert.False(ProfileAnalyzer.TrendOf(six, totalPeriods: 11).IsTrendDay);  // 6 z 11 < 60%

        // Sesja w toku: 5 rosnących okresów z planowanych 13 – jeszcze nie dzień trendowy.
        var day = Profile(100, (99, 101, 100), (100, 102, 101), (101, 103, 102), (102, 104, 103), (103, 105, 104));
        var running = ProfileAnalyzer.Analyze(day, [], isComplete: false, tpoMinutes: 30);
        Assert.Equal(1, running.TrendDirection);
        Assert.False(running.IsTrendDay);
        Assert.NotEqual(ProfileShape.Trend, running.Shape);

        // Ta sama sesja zakończona po 5 okresach (np. krótka sesja) – dzień trendowy.
        var complete = ProfileAnalyzer.Analyze(day, [], isComplete: true, tpoMinutes: 30);
        Assert.True(complete.IsTrendDay);
        Assert.Equal(ProfileShape.Trend, complete.Shape);
    }

    [Fact]
    public void Settle_BezWpisuToClose()
    {
        var previous = PreviousDay();
        var day = Profile(116, (111, 118, 115), (111, 118, 116));
        var stats = Stats(day, previous);

        Assert.Equal(day.LastPrice, stats.Settle);
        Assert.Equal(Settlement.SourceClose, stats.SettleSource);
        Assert.Equal(previous.LastPrice, stats.PreviousSettle);
        Assert.Equal(1m, stats.Gap);

        Assert.Null(Stats(Profile(Night, NightStart, 112, (110, 121, 118), (112, 119, 117))).Settle); // noc – bez settle
    }

    [Fact]
    public void Luka_OdHighLubLowPoprzedniegoDnia()
    {
        var previous = PreviousDay(); // zakres 95–115, Close 105

        Assert.Equal(3m, Stats(Profile(118, (117, 120, 119), (118, 121, 120)), previous).Gap);   // nad High: 118 − 115
        Assert.Equal(-4.5m, Stats(Profile(90.5m, (89, 92, 91), (90, 93, 92)), previous).Gap);   // pod Low: 90.5 − 95
        Assert.Null(Stats(Profile(110, (108, 112, 110), (109, 113, 111)), previous).Gap);       // w zakresie – brak luki
        Assert.Null(Stats(Profile(115, (114, 117, 116), (115, 118, 117)), previous).Gap);       // na samym High – brak luki
    }

    [Fact]
    public void Inwentarz_DlugiKrotkiIWyrownanie()
    {
        var previous = PreviousDay(); // SETTLE = Close 105 (brak wpisu)

        // Długi: otwarcie 108 nad SETTLE 105; C schodzi do 104 – wyrównany w C.
        var longCorrected = Stats(Profile(108, (107, 110, 109), (106, 109, 107), (104, 107, 105)), previous);
        Assert.Equal(OpeningInventory.Long, longCorrected.Inventory);
        Assert.Equal(3m, longCorrected.InventorySize);
        Assert.True(longCorrected.InventoryCorrected);
        Assert.Equal('C', longCorrected.InventoryCorrectedLetter);

        // Krótki: otwarcie 101 pod SETTLE 105; cena nie wraca – niewyrównany.
        var shortOpen = Profile(101, (99, 102, 100), (98, 101, 99));
        var shortStats = Stats(shortOpen, previous);
        Assert.Equal(OpeningInventory.Short, shortStats.Inventory);
        Assert.Equal(-4m, shortStats.InventorySize);
        Assert.False(shortStats.InventoryCorrected);
        Assert.Null(shortStats.InventoryCorrectedLetter);

        // Wyrównanie już w A: otwarcie 106, A schodzi do 105.
        var inA = Stats(Profile(106, (105, 107, 106), (106, 108, 107)), previous);
        Assert.True(inA.InventoryCorrected);
        Assert.Equal('A', inA.InventoryCorrectedLetter);

        // Otwarcie dokładnie na SETTLE – neutralny, bez wyrównania.
        var neutral = Stats(Profile(105, (104, 106, 105), (104, 107, 106)), previous);
        Assert.Equal(OpeningInventory.Neutral, neutral.Inventory);
        Assert.Null(neutral.InventoryCorrected);

        // Wpis ręczny SETTLE poprzedniego dnia (100) zmienia inwentarz krótki na długi.
        var settlements = new Dictionary<DateOnly, Settlement>
        {
            [previous.TradingDate] = new(previous.TradingDate, 100m, Settlement.SourceManual),
        };
        var manual = ProfileAnalyzer.Analyze(shortOpen, [previous], isComplete: true, tpoMinutes: 30, settlements: settlements);
        Assert.Equal(OpeningInventory.Long, manual.Inventory);
        Assert.True(manual.InventoryCorrected); // A schodzi do 99 ≤ 100

        // Noc – bez inwentarza.
        Assert.Equal(OpeningInventory.None, Stats(Profile(Night, NightStart, 112, (110, 121, 118), (112, 119, 117)), previous).Inventory);
    }
}
