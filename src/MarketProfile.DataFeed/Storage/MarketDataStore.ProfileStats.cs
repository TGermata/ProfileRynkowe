using System.Globalization;
using MarketProfile.Core;
using Microsoft.Data.Sqlite;

namespace MarketProfile.DataFeed.Storage;

/// <summary>
/// Parametry profili (<see cref="ProfileStats"/>) – tabela <c>profile_stats</c>, jeden wiersz na profil sesji.
/// Tabela jest czytelna także dla innych programów (np. algorytm w Pythonie): ceny jako liczby (REAL),
/// typy jako tekst, listy cen jako tekst rozdzielony średnikami (single printy: "od-do;od-do").
/// </summary>
public sealed partial class MarketDataStore
{
    private const string ProfileStatsColumns = """
        symbol, session, trading_date, session_start, session_end, is_complete, updated_at, periods, last_letter,
        row_height, tpo_minutes,
        open, high, low, close, range, total_tpo, volume, poc, vpoc, vah, val, va_width, poc_position,
        ib_high, ib_low, ib_width, ib_ext_up, ib_ext_up_letter, ib_ext_down, ib_ext_down_letter, range_extension,
        shape, tpo_above_poc, tpo_below_poc, single_prints, excess_high, excess_low, poor_high, poor_low,
        previous_trading_date, open_location, open_type, open_direction, value_migration, va_overlap,
        naked_pocs, naked_vpocs,
        halfback, settle, settle_source, prev_close, prev_settle, prev_halfback, gap, open_vs_prev_halfback,
        on_high, on_low, on_poc, open_vs_overnight,
        opening_inventory, inventory_size, inventory_corrected, inventory_corrected_letter,
        trend_direction, is_trend_day, interval_from, interval_to, interval_periods, pullback_low, pullback_high, pullback_letter
        """;

    /// <summary>Kolumny dodane w późniejszych wersjach – w starych bazach dopisywane przez ALTER TABLE.</summary>
    private static readonly (string Name, string Type)[] ProfileStatsAddedColumns =
    [
        ("halfback", "REAL"), ("settle", "REAL"), ("settle_source", "TEXT"), ("prev_close", "REAL"), ("prev_settle", "REAL"),
        ("prev_halfback", "REAL"), ("gap", "REAL"), ("open_vs_prev_halfback", "INTEGER"),
        ("on_high", "REAL"), ("on_low", "REAL"), ("on_poc", "REAL"), ("open_vs_overnight", "TEXT"),
        ("opening_inventory", "TEXT"), ("inventory_size", "REAL"), ("inventory_corrected", "INTEGER"),
        ("inventory_corrected_letter", "TEXT"),
        ("trend_direction", "INTEGER"), ("is_trend_day", "INTEGER"), ("interval_from", "TEXT"), ("interval_to", "TEXT"),
        ("interval_periods", "INTEGER"), ("pullback_low", "REAL"), ("pullback_high", "REAL"), ("pullback_letter", "TEXT"),
    ];

    private static void EnsureProfileStatsTable(SqliteConnection connection)
    {
        CreateProfileStatsTable(connection);

        // Migracja: brakujące kolumny (bazy z wcześniejszych wersji).
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(profile_stats)";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var (name, type) in ProfileStatsAddedColumns.Where(c => !existing.Contains(c.Name)))
        {
            Execute(connection, $"ALTER TABLE profile_stats ADD COLUMN {name} {type}");
        }
    }

    private static void CreateProfileStatsTable(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE IF NOT EXISTS profile_stats (
            symbol                TEXT    NOT NULL,
            session               TEXT    NOT NULL,   -- Dzienna / Nocna
            trading_date          TEXT    NOT NULL,   -- yyyy-MM-dd
            session_start         TEXT    NOT NULL,   -- czas lokalny giełdy, yyyy-MM-ddTHH:mm
            session_end           TEXT    NOT NULL,
            is_complete           INTEGER NOT NULL,   -- 1 = sesja zakończona (wartości ostateczne)
            updated_at            TEXT    NOT NULL,   -- UTC, ISO 8601
            periods               INTEGER NOT NULL,   -- liczba liter TPO
            last_letter           TEXT,
            row_height            REAL    NOT NULL,
            tpo_minutes           INTEGER NOT NULL,
            open REAL, high REAL, low REAL, close REAL, range REAL,
            total_tpo             INTEGER,
            volume                INTEGER,
            poc REAL, vpoc REAL, vah REAL, val REAL, va_width REAL,
            poc_position          REAL,               -- 0 = dół zakresu, 1 = góra
            ib_high REAL, ib_low REAL, ib_width REAL,
            ib_ext_up REAL, ib_ext_up_letter TEXT, ib_ext_down REAL, ib_ext_down_letter TEXT,
            range_extension       TEXT,               -- None / Up / Down / Both
            shape                 TEXT,               -- D / P / b / B / Trend
            tpo_above_poc INTEGER, tpo_below_poc INTEGER,
            single_prints         TEXT,               -- "od-do;od-do"
            excess_high INTEGER, excess_low INTEGER, poor_high INTEGER, poor_low INTEGER,
            previous_trading_date TEXT,
            open_location         TEXT,               -- AboveRange / AboveValue / InValue / BelowValue / BelowRange
            open_type             TEXT,               -- OpenDrive / OpenTestDrive / OpenRejectionReverse / OpenAuction
            open_direction        INTEGER,            -- 1 w górę, -1 w dół, 0 brak
            value_migration       TEXT,               -- Higher / OverlappingHigher / OverlappingLower / Lower / Inside / Outside
            va_overlap            REAL,               -- 0–1
            naked_pocs            TEXT,               -- "cena;cena"
            naked_vpocs           TEXT,
            halfback REAL,                            -- (High + Low) / 2
            settle REAL,                              -- cena rozliczeniowa (sesja dzienna), NULL = brak
            settle_source         TEXT,               -- CME / ręcznie
            prev_close REAL, prev_settle REAL, prev_halfback REAL,
            gap                   REAL,               -- luka: Open − High (nad) / Open − Low (pod) poprzedniego dnia; NULL = brak
            open_vs_prev_halfback INTEGER,            -- 1 nad, -1 pod
            on_high REAL, on_low REAL, on_poc REAL,   -- sesja nocna przed dniem
            open_vs_overnight     TEXT,               -- AboveHigh / InRange / BelowLow
            opening_inventory     TEXT,               -- Long / Short / Neutral – względem SETTLE poprzedniego dnia
            inventory_size        REAL,               -- Open − SETTLE poprzedniego dnia
            inventory_corrected   INTEGER,            -- 1 = cena wróciła do SETTLE, 0 = nie (jeszcze), NULL = brak inwentarza
            inventory_corrected_letter TEXT,
            trend_direction       INTEGER,            -- interwałowanie: 1 w górę, -1 w dół, 0 brak
            is_trend_day          INTEGER,            -- 1 = dzień trendowy (interwałowanie przez większą część sesji)
            interval_from TEXT, interval_to TEXT,     -- litery początku i końca interwałowania
            interval_periods      INTEGER,
            pullback_low REAL, pullback_high REAL,    -- PULLBACK LOW (trend w górę) / PULLBACK HIGH (w dół)
            pullback_letter       TEXT,
            PRIMARY KEY (symbol, session, session_start)
        );
        CREATE INDEX IF NOT EXISTS ix_profile_stats_date ON profile_stats (symbol, trading_date);
        """);

    /// <summary>Zapisuje (lub zastępuje) parametry profili instrumentu – w jednej transakcji.</summary>
    public void SaveProfileStats(string symbol, IEnumerable<ProfileStats> stats)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        var names = ProfileStatsColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        command.CommandText = $"INSERT OR REPLACE INTO profile_stats ({string.Join(", ", names)}) " +
                              $"VALUES ({string.Join(", ", names.Select(n => "$" + n))})";
        var parameters = names.ToDictionary(n => n, n => command.Parameters.Add("$" + n, SqliteType.Text));
        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        foreach (var s in stats)
        {
            void Set(string name, object? value) => parameters[name].Value = value ?? DBNull.Value;

            Set("symbol", symbol);
            Set("session", s.Session);
            Set("trading_date", s.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Set("session_start", s.SessionStart.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));
            Set("session_end", s.SessionEnd.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));
            Set("is_complete", s.IsComplete ? 1 : 0);
            Set("updated_at", now);
            Set("periods", s.Periods);
            Set("last_letter", s.LastLetter?.ToString());
            Set("row_height", (double)s.RowHeight);
            Set("tpo_minutes", s.TpoMinutes);
            Set("open", (double)s.Open);
            Set("high", (double)s.High);
            Set("low", (double)s.Low);
            Set("close", (double)s.Close);
            Set("range", (double)s.Range);
            Set("total_tpo", s.TotalTpo);
            Set("volume", s.Volume);
            Set("poc", (double)s.Poc);
            Set("vpoc", (double?)s.VolumePoc);
            Set("vah", (double)s.ValueAreaHigh);
            Set("val", (double)s.ValueAreaLow);
            Set("va_width", (double)s.ValueAreaWidth);
            Set("poc_position", s.PocPosition);
            Set("ib_high", (double?)s.IbHigh);
            Set("ib_low", (double?)s.IbLow);
            Set("ib_width", (double?)s.IbWidth);
            Set("ib_ext_up", (double)s.IbExtensionUp);
            Set("ib_ext_up_letter", s.IbExtensionUpLetter?.ToString());
            Set("ib_ext_down", (double)s.IbExtensionDown);
            Set("ib_ext_down_letter", s.IbExtensionDownLetter?.ToString());
            Set("range_extension", s.RangeExtension.ToString());
            Set("shape", s.ShapeCode);
            Set("tpo_above_poc", s.TpoAbovePoc);
            Set("tpo_below_poc", s.TpoBelowPoc);
            Set("single_prints", string.Join(";", s.SinglePrints.Select(r => $"{Price(r.Low)}-{Price(r.High)}")));
            Set("excess_high", s.ExcessHigh);
            Set("excess_low", s.ExcessLow);
            Set("poor_high", s.PoorHigh ? 1 : 0);
            Set("poor_low", s.PoorLow ? 1 : 0);
            Set("previous_trading_date", s.PreviousTradingDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Set("open_location", s.OpenLocation.ToString());
            Set("open_type", s.OpenType.ToString());
            Set("open_direction", s.OpenDirection);
            Set("value_migration", s.ValueMigration.ToString());
            Set("va_overlap", s.ValueAreaOverlap);
            Set("naked_pocs", string.Join(";", s.NakedPocs.Select(Price)));
            Set("naked_vpocs", string.Join(";", s.NakedVolumePocs.Select(Price)));
            Set("halfback", (double)s.Halfback);
            Set("settle", (double?)s.Settle);
            Set("settle_source", s.SettleSource);
            Set("prev_close", (double?)s.PreviousClose);
            Set("prev_settle", (double?)s.PreviousSettle);
            Set("prev_halfback", (double?)s.PreviousHalfback);
            Set("gap", (double?)s.Gap);
            Set("open_vs_prev_halfback", s.OpenVsPreviousHalfback);
            Set("on_high", (double?)s.OvernightHigh);
            Set("on_low", (double?)s.OvernightLow);
            Set("on_poc", (double?)s.OvernightPoc);
            Set("open_vs_overnight", s.OpenVsOvernight.ToString());
            Set("opening_inventory", s.OpeningInventory.ToString());
            Set("inventory_size", (double?)s.InventorySize);
            Set("inventory_corrected", s.InventoryCorrected is { } corrected ? corrected ? 1 : 0 : null);
            Set("inventory_corrected_letter", s.InventoryCorrectedLetter?.ToString());
            Set("trend_direction", s.TrendDirection);
            Set("is_trend_day", s.IsTrendDay ? 1 : 0);
            Set("interval_from", s.IntervalFrom?.ToString());
            Set("interval_to", s.IntervalTo?.ToString());
            Set("interval_periods", s.IntervalPeriods);
            Set("pullback_low", (double?)s.PullbackLow);
            Set("pullback_high", (double?)s.PullbackHigh);
            Set("pullback_letter", s.PullbackLetter?.ToString());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Parametry profili instrumentu (opcjonalnie z zakresu dat handlowych), od najstarszego.</summary>
    public IReadOnlyList<ProfileStats> ReadProfileStats(string symbol, DateOnly? from = null, DateOnly? to = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ProfileStatsColumns} FROM profile_stats
            WHERE symbol = $s AND trading_date >= $from AND trading_date <= $to
            ORDER BY session_start
            """;
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$from", (from ?? DateOnly.MinValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", (to ?? DateOnly.MaxValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var result = new List<ProfileStats>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            int i(string name) => reader.GetOrdinal(name);
            decimal D(string name) => (decimal)Math.Round(reader.GetDouble(i(name)), 8);
            decimal? Dn(string name) => reader.IsDBNull(i(name)) ? null : D(name);
            string? T(string name) => reader.IsDBNull(i(name)) ? null : reader.GetString(i(name));
            char? C(string name) => T(name) is { Length: > 0 } t ? t[0] : null;
            int N(string name) => reader.IsDBNull(i(name)) ? 0 : reader.GetInt32(i(name));
            TEnum E<TEnum>(string name) where TEnum : struct, Enum => Enum.TryParse<TEnum>(T(name), out var v) ? v : default;
            DateTime Dt(string name) => DateTime.ParseExact(reader.GetString(i(name)), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
            DateOnly Do(string name) => DateOnly.ParseExact(reader.GetString(i(name)), "yyyy-MM-dd", CultureInfo.InvariantCulture);

            result.Add(new ProfileStats
            {
                Session = reader.GetString(i("session")),
                TradingDate = Do("trading_date"),
                SessionStart = Dt("session_start"),
                SessionEnd = Dt("session_end"),
                IsComplete = N("is_complete") == 1,
                Periods = N("periods"),
                LastLetter = C("last_letter"),
                RowHeight = D("row_height"),
                TpoMinutes = N("tpo_minutes"),
                Open = D("open"),
                High = D("high"),
                Low = D("low"),
                Close = D("close"),
                Range = D("range"),
                TotalTpo = N("total_tpo"),
                Volume = reader.IsDBNull(i("volume")) ? 0 : reader.GetInt64(i("volume")),
                Poc = D("poc"),
                VolumePoc = Dn("vpoc"),
                ValueAreaHigh = D("vah"),
                ValueAreaLow = D("val"),
                ValueAreaWidth = D("va_width"),
                PocPosition = reader.GetDouble(i("poc_position")),
                IbHigh = Dn("ib_high"),
                IbLow = Dn("ib_low"),
                IbWidth = Dn("ib_width"),
                IbExtensionUp = D("ib_ext_up"),
                IbExtensionUpLetter = C("ib_ext_up_letter"),
                IbExtensionDown = D("ib_ext_down"),
                IbExtensionDownLetter = C("ib_ext_down_letter"),
                RangeExtension = E<RangeExtension>("range_extension"),
                Shape = ShapeFromCode(T("shape")),
                TpoAbovePoc = N("tpo_above_poc"),
                TpoBelowPoc = N("tpo_below_poc"),
                SinglePrints = ParseRanges(T("single_prints")),
                ExcessHigh = N("excess_high"),
                ExcessLow = N("excess_low"),
                PoorHigh = N("poor_high") == 1,
                PoorLow = N("poor_low") == 1,
                PreviousTradingDate = T("previous_trading_date") is { } prev
                    ? DateOnly.ParseExact(prev, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null,
                OpenLocation = E<OpenLocation>("open_location"),
                OpenType = E<OpenType>("open_type"),
                OpenDirection = N("open_direction"),
                ValueMigration = E<ValueMigration>("value_migration"),
                ValueAreaOverlap = reader.IsDBNull(i("va_overlap")) ? null : reader.GetDouble(i("va_overlap")),
                NakedPocs = ParsePrices(T("naked_pocs")),
                NakedVolumePocs = ParsePrices(T("naked_vpocs")),
                Halfback = Dn("halfback") ?? 0,
                Settle = Dn("settle"),
                SettleSource = T("settle_source"),
                PreviousClose = Dn("prev_close"),
                PreviousSettle = Dn("prev_settle"),
                PreviousHalfback = Dn("prev_halfback"),
                Gap = Dn("gap"),
                OpenVsPreviousHalfback = N("open_vs_prev_halfback"),
                OvernightHigh = Dn("on_high"),
                OvernightLow = Dn("on_low"),
                OvernightPoc = Dn("on_poc"),
                OpenVsOvernight = E<OvernightLocation>("open_vs_overnight"),
                OpeningInventory = E<OpeningInventory>("opening_inventory"),
                InventorySize = Dn("inventory_size"),
                InventoryCorrected = reader.IsDBNull(i("inventory_corrected")) ? null : N("inventory_corrected") == 1,
                InventoryCorrectedLetter = C("inventory_corrected_letter"),
                TrendDirection = N("trend_direction"),
                IsTrendDay = N("is_trend_day") == 1,
                IntervalFrom = C("interval_from"),
                IntervalTo = C("interval_to"),
                IntervalPeriods = N("interval_periods"),
                PullbackLow = Dn("pullback_low"),
                PullbackHigh = Dn("pullback_high"),
                PullbackLetter = C("pullback_letter"),
            });
        }

        return result;
    }

    private static string Price(decimal price) => price.ToString("0.########", CultureInfo.InvariantCulture);

    private static ProfileShape ShapeFromCode(string? code) => code switch
    {
        "D" => ProfileShape.D,
        "P" => ProfileShape.P,
        "b" => ProfileShape.LowerB,
        "B" => ProfileShape.DoubleB,
        "Trend" => ProfileShape.Trend,
        _ => ProfileShape.None,
    };

    private static IReadOnlyList<decimal> ParsePrices(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : [.. text.Split(';').Select(p => decimal.Parse(p, CultureInfo.InvariantCulture))];

    private static IReadOnlyList<PriceRange> ParseRanges(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : [.. text.Split(';').Select(r =>
            {
                // Ceny mogą być ujemne (np. spready) – dzielimy na myślniku po pierwszym znaku.
                var dash = r.IndexOf('-', 1);
                return new PriceRange(decimal.Parse(r[..dash], CultureInfo.InvariantCulture),
                    decimal.Parse(r[(dash + 1)..], CultureInfo.InvariantCulture));
            })];
}
