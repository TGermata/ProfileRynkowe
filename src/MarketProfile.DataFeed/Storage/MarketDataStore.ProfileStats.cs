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
        row_height, TPO_minutes,
        open, high, low, close, range, total_TPO, volume, POC, VPOC, VAH, VAL, VA_width, POC_position,
        IBH, IBL, IB_width, IB_ext_up, IB_ext_up_letter, IB_ext_down, IB_ext_down_letter, IB_ext,
        shape, TPO_above_POC, TPO_below_POC, single_prints, excess_high, excess_low, poor_high, poor_low,
        prev_trading_date, open_vs_prev_VA, open_type, open_direction, value_migration, VA_overlap,
        naked_POCs, naked_VPOCs,
        HB, SETTLE, SETTLE_source, prev_close, prev_SETTLE, prev_HB, GAP, open_vs_prev_HB,
        ONH, ONL, ONPOC, open_vs_overnight,
        inventory, inventory_size, inventory_corrected, inventory_corrected_letter,
        trend_direction, is_trend_day, trend_from, trend_to, trend_periods, PBL, PBH, PB_letter
        """;

    /// <summary>Kolumny dodane w późniejszych wersjach – w starych bazach dopisywane przez ALTER TABLE.</summary>
    private static readonly (string Name, string Type)[] ProfileStatsAddedColumns =
    [
        ("HB", "REAL"), ("SETTLE", "REAL"), ("SETTLE_source", "TEXT"), ("prev_close", "REAL"), ("prev_SETTLE", "REAL"),
        ("prev_HB", "REAL"), ("GAP", "REAL"), ("open_vs_prev_HB", "INTEGER"),
        ("ONH", "REAL"), ("ONL", "REAL"), ("ONPOC", "REAL"), ("open_vs_overnight", "TEXT"),
        ("inventory", "TEXT"), ("inventory_size", "REAL"), ("inventory_corrected", "INTEGER"),
        ("inventory_corrected_letter", "TEXT"),
        ("trend_direction", "INTEGER"), ("is_trend_day", "INTEGER"), ("trend_from", "TEXT"), ("trend_to", "TEXT"),
        ("trend_periods", "INTEGER"), ("PBL", "REAL"), ("PBH", "REAL"), ("PB_letter", "TEXT"),
    ];

    /// <summary>
    /// Kolumny, których nazwy ujednolicono ze skrótami z aplikacji (stara → nowa). W starych bazach zmieniane
    /// przez ALTER TABLE … RENAME COLUMN – zapisane dane zostają. Kolumny różniące się tylko wielkością liter
    /// (np. hb → HB) są poprawiane osobno, na podstawie <see cref="ProfileStatsColumns"/>.
    /// </summary>
    private static readonly (string Old, string New)[] ProfileStatsRenamedColumns =
    [
        ("previous_trading_date", "prev_trading_date"), ("open_location", "open_vs_prev_VA"), ("range_extension", "IB_ext"),
        ("ib_high", "IBH"), ("ib_low", "IBL"),
        ("halfback", "HB"), ("prev_halfback", "prev_HB"), ("open_vs_prev_halfback", "open_vs_prev_HB"),
        ("on_high", "ONH"), ("on_low", "ONL"), ("on_poc", "ONPOC"), ("opening_inventory", "inventory"),
        ("interval_from", "trend_from"), ("interval_to", "trend_to"), ("interval_periods", "trend_periods"),
        ("pullback_low", "PBL"), ("pullback_high", "PBH"), ("pullback_letter", "PB_letter"),
    ];

    /// <summary>Wartości położenia otwarcia względem nocy zapisane w starszych wersjach (stara → nowa).</summary>
    private static readonly (string Old, string New)[] OvernightLocationRenamedValues =
    [
        ("AboveHigh", nameof(OvernightLocation.AboveOnh)), ("InRange", nameof(OvernightLocation.InOvernight)),
        ("BelowLow", nameof(OvernightLocation.BelowOnl)),
    ];

    /// <summary>Nazwy kolumn tabeli w docelowej postaci (ze skrótami dużymi literami).</summary>
    public static IReadOnlyList<string> ProfileStatsColumnNames { get; } =
        ProfileStatsColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void EnsureProfileStatsTable(SqliteConnection connection)
    {
        CreateProfileStatsTable(connection);

        // Migracja baz z wcześniejszych wersji. SQLite porównuje nazwy kolumn bez wielkości liter.
        var existing = ExistingProfileStatsColumns(connection);
        string? Find(string name) => existing.FirstOrDefault(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));

        void Rename(string from, string to)
        {
            Execute(connection, $"ALTER TABLE profile_stats RENAME COLUMN \"{from}\" TO \"{to}\"");
            existing.Remove(from);
            existing.Add(to);
        }

        // 1. Stare nazwy → nowe.
        foreach (var (oldName, newName) in ProfileStatsRenamedColumns)
        {
            if (Find(oldName) is { } actual && Find(newName) is null)
            {
                Rename(actual, newName);
            }
        }

        // 2. Ta sama nazwa, inna wielkość liter (np. hb → HB, prev_hb → prev_HB).
        foreach (var name in ProfileStatsColumnNames)
        {
            if (Find(name) is { } actual && actual != name)
            {
                Rename(actual, name);
            }
        }

        // 3. Brakujące kolumny.
        foreach (var (name, type) in ProfileStatsAddedColumns.Where(c => Find(c.Name) is null))
        {
            Execute(connection, $"ALTER TABLE profile_stats ADD COLUMN {name} {type}");
            existing.Add(name);
        }

        // 4. Stare nazwy wartości.
        foreach (var (oldValue, newValue) in OvernightLocationRenamedValues)
        {
            Execute(connection, $"UPDATE profile_stats SET open_vs_overnight = '{newValue}' WHERE open_vs_overnight = '{oldValue}'");
        }
    }

    private static List<string> ExistingProfileStatsColumns(SqliteConnection connection)
    {
        var names = new List<string>();
        using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(profile_stats)";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names;
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
            TPO_minutes           INTEGER NOT NULL,
            open REAL, high REAL, low REAL, close REAL, range REAL,
            total_TPO             INTEGER,
            volume                INTEGER,
            POC REAL, VPOC REAL, VAH REAL, VAL REAL,
            VA_width              REAL,               -- VAH − VAL + wysokość wiersza
            POC_position          REAL,               -- POC w zakresie: 0 = dół, 1 = góra
            IBH REAL, IBL REAL, IB_width REAL, -- IBH, IBL, szerokość IB
            IB_ext_up REAL, IB_ext_up_letter TEXT, IB_ext_down REAL, IB_ext_down_letter TEXT, -- wybicie z IB: o ile i pierwsza litera
            IB_ext                TEXT,               -- kierunek wybicia z IB: None / Up / Down / Both
            shape                 TEXT,               -- D / P / b / B / Trend
            TPO_above_POC INTEGER, TPO_below_POC INTEGER,
            single_prints         TEXT,               -- "od-do;od-do"
            excess_high INTEGER, excess_low INTEGER,  -- ogony: liczba wierszy z jedną literą na krańcu (0 = brak)
            poor_high INTEGER, poor_low INTEGER,      -- 1 = płaski szczyt / dołek (bez ogona)
            -- relacje z poprzednim dniem (tylko sesje dzienne)
            prev_trading_date     TEXT,
            open_vs_prev_VA       TEXT,               -- otwarcie vs VA poprz.: AboveRange / AboveValue / InValue / BelowValue / BelowRange
            open_type             TEXT,               -- OpenDrive / OpenTestDrive / OpenRejectionReverse / OpenAuction
            open_direction        INTEGER,            -- 1 w górę, -1 w dół, 0 brak
            value_migration       TEXT,               -- migracja VA: Higher / OverlappingHigher / OverlappingLower / Lower / Inside / Outside / TrendDay
            VA_overlap            REAL,               -- nakładanie VA z poprzednią: 0–1
            naked_POCs            TEXT,               -- nietknięte POC: "cena;cena"
            naked_VPOCs           TEXT,               -- nietknięte VPOC
            HB                    REAL,               -- HB (halfback): (High + Low) / 2
            SETTLE                REAL,               -- SETTLE (sesja dzienna), NULL = brak
            SETTLE_source         TEXT,               -- Close / ręcznie / CME
            prev_close REAL, prev_SETTLE REAL, prev_HB REAL,
            GAP                   REAL,               -- GAP: Open − High (nad) / Open − Low (pod) poprzedniego dnia; NULL = brak
            open_vs_prev_HB       INTEGER,            -- otwarcie vs HB poprz.: 1 nad, -1 pod, 0 na poziomie
            ONH REAL, ONL REAL, ONPOC REAL,           -- ONH / ONL / ONPOC – sesja nocna przed dniem
            open_vs_overnight     TEXT,               -- otwarcie vs noc: AboveOnh / InOvernight / BelowOnl
            inventory             TEXT,               -- inwentarz: Long / Short / Neutral – względem SETTLE poprzedniego dnia
            inventory_size        REAL,               -- Open − SETTLE poprzedniego dnia
            inventory_corrected   INTEGER,            -- 1 = cena wróciła do SETTLE, 0 = nie (jeszcze), NULL = brak inwentarza
            inventory_corrected_letter TEXT,
            -- trend (interwałowanie)
            trend_direction       INTEGER,            -- 1 w górę, -1 w dół, 0 brak
            is_trend_day          INTEGER,            -- 1 = dzień trendowy (interwałowanie przez większą część sesji)
            trend_from TEXT, trend_to TEXT,           -- litery początku i końca serii
            trend_periods         INTEGER,
            PBL REAL, PBH REAL,                       -- PBL (trend w górę) / PBH (trend w dół)
            PB_letter             TEXT,
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
        var names = ProfileStatsColumnNames;
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
            Set("TPO_minutes", s.TpoMinutes);
            Set("open", (double)s.Open);
            Set("high", (double)s.High);
            Set("low", (double)s.Low);
            Set("close", (double)s.Close);
            Set("range", (double)s.Range);
            Set("total_TPO", s.TotalTpo);
            Set("volume", s.Volume);
            Set("POC", (double)s.Poc);
            Set("VPOC", (double?)s.VolumePoc);
            Set("VAH", (double)s.ValueAreaHigh);
            Set("VAL", (double)s.ValueAreaLow);
            Set("VA_width", (double)s.ValueAreaWidth);
            Set("POC_position", s.PocPosition);
            Set("IBH", (double?)s.IbHigh);
            Set("IBL", (double?)s.IbLow);
            Set("IB_width", (double?)s.IbWidth);
            Set("IB_ext_up", (double)s.IbExtensionUp);
            Set("IB_ext_up_letter", s.IbExtensionUpLetter?.ToString());
            Set("IB_ext_down", (double)s.IbExtensionDown);
            Set("IB_ext_down_letter", s.IbExtensionDownLetter?.ToString());
            Set("IB_ext", s.IbExtension.ToString());
            Set("shape", s.ShapeCode);
            Set("TPO_above_POC", s.TpoAbovePoc);
            Set("TPO_below_POC", s.TpoBelowPoc);
            Set("single_prints", string.Join(";", s.SinglePrints.Select(r => $"{Price(r.Low)}-{Price(r.High)}")));
            Set("excess_high", s.ExcessHigh);
            Set("excess_low", s.ExcessLow);
            Set("poor_high", s.PoorHigh ? 1 : 0);
            Set("poor_low", s.PoorLow ? 1 : 0);
            Set("prev_trading_date", s.PreviousTradingDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Set("open_vs_prev_VA", s.OpenVsPreviousValue.ToString());
            Set("open_type", s.OpenType.ToString());
            Set("open_direction", s.OpenDirection);
            Set("value_migration", s.ValueMigration.ToString());
            Set("VA_overlap", s.ValueAreaOverlap);
            Set("naked_POCs", string.Join(";", s.NakedPocs.Select(Price)));
            Set("naked_VPOCs", string.Join(";", s.NakedVolumePocs.Select(Price)));
            Set("HB", (double)s.Halfback);
            Set("SETTLE", (double?)s.Settle);
            Set("SETTLE_source", s.SettleSource);
            Set("prev_close", (double?)s.PreviousClose);
            Set("prev_SETTLE", (double?)s.PreviousSettle);
            Set("prev_HB", (double?)s.PreviousHalfback);
            Set("GAP", (double?)s.Gap);
            Set("open_vs_prev_HB", s.OpenVsPreviousHb);
            Set("ONH", (double?)s.OvernightHigh);
            Set("ONL", (double?)s.OvernightLow);
            Set("ONPOC", (double?)s.OvernightPoc);
            Set("open_vs_overnight", s.OpenVsOvernight.ToString());
            Set("inventory", s.Inventory.ToString());
            Set("inventory_size", (double?)s.InventorySize);
            Set("inventory_corrected", s.InventoryCorrected is { } corrected ? corrected ? 1 : 0 : null);
            Set("inventory_corrected_letter", s.InventoryCorrectedLetter?.ToString());
            Set("trend_direction", s.TrendDirection);
            Set("is_trend_day", s.IsTrendDay ? 1 : 0);
            Set("trend_from", s.TrendFrom?.ToString());
            Set("trend_to", s.TrendTo?.ToString());
            Set("trend_periods", s.TrendPeriods);
            Set("PBL", (double?)s.PullbackLow);
            Set("PBH", (double?)s.PullbackHigh);
            Set("PB_letter", s.PullbackLetter?.ToString());
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
                TpoMinutes = N("TPO_minutes"),
                Open = D("open"),
                High = D("high"),
                Low = D("low"),
                Close = D("close"),
                Range = D("range"),
                TotalTpo = N("total_TPO"),
                Volume = reader.IsDBNull(i("volume")) ? 0 : reader.GetInt64(i("volume")),
                Poc = D("POC"),
                VolumePoc = Dn("VPOC"),
                ValueAreaHigh = D("VAH"),
                ValueAreaLow = D("VAL"),
                ValueAreaWidth = D("VA_width"),
                PocPosition = reader.GetDouble(i("POC_position")),
                IbHigh = Dn("IBH"),
                IbLow = Dn("IBL"),
                IbWidth = Dn("IB_width"),
                IbExtensionUp = D("IB_ext_up"),
                IbExtensionUpLetter = C("IB_ext_up_letter"),
                IbExtensionDown = D("IB_ext_down"),
                IbExtensionDownLetter = C("IB_ext_down_letter"),
                IbExtension = E<IbExtensionDirection>("IB_ext"),
                Shape = ShapeFromCode(T("shape")),
                TpoAbovePoc = N("TPO_above_POC"),
                TpoBelowPoc = N("TPO_below_POC"),
                SinglePrints = ParseRanges(T("single_prints")),
                ExcessHigh = N("excess_high"),
                ExcessLow = N("excess_low"),
                PoorHigh = N("poor_high") == 1,
                PoorLow = N("poor_low") == 1,
                PreviousTradingDate = T("prev_trading_date") is { } prev
                    ? DateOnly.ParseExact(prev, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null,
                OpenVsPreviousValue = E<ValueLocation>("open_vs_prev_VA"),
                OpenType = E<OpenType>("open_type"),
                OpenDirection = N("open_direction"),
                ValueMigration = E<ValueMigration>("value_migration"),
                ValueAreaOverlap = reader.IsDBNull(i("VA_overlap")) ? null : reader.GetDouble(i("VA_overlap")),
                NakedPocs = ParsePrices(T("naked_POCs")),
                NakedVolumePocs = ParsePrices(T("naked_VPOCs")),
                Halfback = Dn("HB") ?? 0,
                Settle = Dn("SETTLE"),
                SettleSource = T("SETTLE_source"),
                PreviousClose = Dn("prev_close"),
                PreviousSettle = Dn("prev_SETTLE"),
                PreviousHalfback = Dn("prev_HB"),
                Gap = Dn("GAP"),
                OpenVsPreviousHb = N("open_vs_prev_HB"),
                OvernightHigh = Dn("ONH"),
                OvernightLow = Dn("ONL"),
                OvernightPoc = Dn("ONPOC"),
                OpenVsOvernight = E<OvernightLocation>("open_vs_overnight"),
                Inventory = E<OpeningInventory>("inventory"),
                InventorySize = Dn("inventory_size"),
                InventoryCorrected = reader.IsDBNull(i("inventory_corrected")) ? null : N("inventory_corrected") == 1,
                InventoryCorrectedLetter = C("inventory_corrected_letter"),
                TrendDirection = N("trend_direction"),
                IsTrendDay = N("is_trend_day") == 1,
                TrendFrom = C("trend_from"),
                TrendTo = C("trend_to"),
                TrendPeriods = N("trend_periods"),
                PullbackLow = Dn("PBL"),
                PullbackHigh = Dn("PBH"),
                PullbackLetter = C("PB_letter"),
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
