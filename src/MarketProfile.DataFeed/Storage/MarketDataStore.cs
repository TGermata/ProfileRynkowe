using MarketProfile.Core;
using Microsoft.Data.Sqlite;

namespace MarketProfile.DataFeed.Storage;

/// <summary>
/// Lokalna baza danych rynkowych (SQLite) – ticki i świece 1-minutowe, osobno dla każdego instrumentu.
/// Służy do zapisywania danych od brokera i późniejszego odtwarzania ich w symulatorze.
///
/// Format zapisu:
///  - czas: liczba milisekund od 1970-01-01 UTC (szybkie wyszukiwanie zakresów),
///  - cena: liczba całkowita = cena × 100 000 000 (dokładnie – także ceny walut z ticku 0,00005 – bez błędów
///    zaokrągleń liczb zmiennoprzecinkowych),
///  - źródło: skąd pochodzą dane, np. "IB" (broker) lub "TEST" (dane testowe z generatora).
/// </summary>
public sealed partial class MarketDataStore
{
    /// <summary>Źródło danych od brokera Interactive Brokers.</summary>
    public const string SourceBroker = "IB";

    /// <summary>Źródło danych testowych wygenerowanych przez symulator.</summary>
    public const string SourceTest = "TEST";

    private const decimal PriceScale = 100_000_000m;

    /// <summary>Wersja formatu bazy (PRAGMA user_version): 1 = ceny × 10⁸ (wcześniej × 10⁴).</summary>
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    /// <summary>Otwiera (lub tworzy) bazę w podanym pliku.</summary>
    public MarketDataStore(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = filePath, Pooling = false }.ToString();
        EnsureCreated();
    }

    /// <summary>Ścieżka pliku bazy.</summary>
    public string FilePath { get; }

    /// <summary>Domyślna lokalizacja bazy: podfolder „data” w folderze aplikacji (obok pliku .exe).</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "marketdata.db");

    /// <summary>Tworzy tabele i indeksy, jeśli jeszcze nie istnieją.</summary>
    private void EnsureCreated()
    {
        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS ticks (
                symbol TEXT    NOT NULL,
                time   INTEGER NOT NULL,   -- ms od 1970-01-01 UTC
                price  INTEGER NOT NULL,   -- cena × 10000
                volume INTEGER NOT NULL,
                source TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_ticks ON ticks (symbol, time);

            CREATE TABLE IF NOT EXISTS bars (
                symbol TEXT    NOT NULL,
                time   INTEGER NOT NULL,   -- początek minuty, ms od 1970-01-01 UTC
                open   INTEGER NOT NULL,
                high   INTEGER NOT NULL,
                low    INTEGER NOT NULL,
                close  INTEGER NOT NULL,
                volume INTEGER NOT NULL,
                source TEXT    NOT NULL,
                PRIMARY KEY (symbol, time)
            );

            -- Odcinki [from_time, to_time), dla których w tabeli ticks są kompletne historyczne ticki od brokera
            -- (wszystkie transakcje z taśmy giełdy) – tych odcinków nie trzeba już pobierać.
            CREATE TABLE IF NOT EXISTS tick_coverage (
                symbol    TEXT    NOT NULL,
                from_time INTEGER NOT NULL,   -- ms od 1970-01-01 UTC
                to_time   INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tick_coverage ON tick_coverage (symbol, from_time);
            """);

        EnsureProfileStatsTable(connection);
        EnsureOhlcBarsTable(connection);
        EnsureSettlementsTable(connection);

        // Migracja: bazy zapisane przez wcześniejsze wersje mają ceny × 10⁴ – przeliczamy je na × 10⁸ (bez strat).
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        if ((long)version.ExecuteScalar()! < SchemaVersion)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, $"""
                UPDATE ticks SET price = price * 10000;
                UPDATE bars  SET open = open * 10000, high = high * 10000, low = low * 10000, close = close * 10000;
                PRAGMA user_version = {SchemaVersion};
                """);
            transaction.Commit();
        }
    }

    // =====================================================================
    // Zapis
    // =====================================================================

    /// <summary>Zapisuje ticki (w jednej transakcji – szybko nawet dla setek tysięcy rekordów).</summary>
    public void InsertTicks(string symbol, IEnumerable<Tick> ticks, string source)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ticks (symbol, time, price, volume, source) VALUES ($s, $t, $p, $v, $src)";

        var pSymbol = command.Parameters.Add("$s", SqliteType.Text);
        var pTime = command.Parameters.Add("$t", SqliteType.Integer);
        var pPrice = command.Parameters.Add("$p", SqliteType.Integer);
        var pVolume = command.Parameters.Add("$v", SqliteType.Integer);
        var pSource = command.Parameters.Add("$src", SqliteType.Text);
        pSymbol.Value = symbol;
        pSource.Value = source;

        foreach (var tick in ticks)
        {
            pTime.Value = tick.Time.ToUnixTimeMilliseconds();
            pPrice.Value = ToStored(tick.Price);
            pVolume.Value = tick.Volume;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Zapisuje świece 1-minutowe. Jeśli świeca dla tej minuty już istnieje, jest zastępowana
    /// (np. broker przysłał poprawione dane).
    /// </summary>
    public void InsertBars(string symbol, IEnumerable<Bar> bars, string source)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO bars (symbol, time, open, high, low, close, volume, source)
            VALUES ($s, $t, $o, $h, $l, $c, $v, $src)
            """;

        var p = new[] { "$t", "$o", "$h", "$l", "$c", "$v" }
            .ToDictionary(n => n, n => command.Parameters.Add(n, SqliteType.Integer));
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$src", source);

        foreach (var bar in bars)
        {
            p["$t"].Value = bar.Time.ToUnixTimeMilliseconds();
            p["$o"].Value = ToStored(bar.Open);
            p["$h"].Value = ToStored(bar.High);
            p["$l"].Value = ToStored(bar.Low);
            p["$c"].Value = ToStored(bar.Close);
            p["$v"].Value = bar.Volume;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Zapisuje kompletne historyczne ticki od brokera z odcinka [from, to): w jednej transakcji usuwa ticki
    /// tego źródła zapisane wcześniej w tym odcinku (np. nagrane na żywo – mniej dokładne), wstawia nowe
    /// i oznacza odcinek jako pokryty (<see cref="GetTickCoverage"/>). Pusta lista ticków z
    /// <paramref name="replaceExisting"/> = false tylko oznacza odcinek (np. przerwa bez handlu).
    /// </summary>
    public void SaveHistoricalTicks(string symbol, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<Tick> ticks,
        string source, bool replaceExisting = true)
    {
        if (to <= from)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs = to.ToUnixTimeMilliseconds();

        if (replaceExisting)
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM ticks WHERE symbol = $s AND source = $src AND time >= $from AND time < $to";
            delete.Parameters.AddWithValue("$s", symbol);
            delete.Parameters.AddWithValue("$src", source);
            delete.Parameters.AddWithValue("$from", fromMs);
            delete.Parameters.AddWithValue("$to", toMs);
            delete.ExecuteNonQuery();
        }

        if (ticks.Count > 0)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO ticks (symbol, time, price, volume, source) VALUES ($s, $t, $p, $v, $src)";
            insert.Parameters.AddWithValue("$s", symbol);
            insert.Parameters.AddWithValue("$src", source);
            var pTime = insert.Parameters.Add("$t", SqliteType.Integer);
            var pPrice = insert.Parameters.Add("$p", SqliteType.Integer);
            var pVolume = insert.Parameters.Add("$v", SqliteType.Integer);

            foreach (var tick in ticks)
            {
                pTime.Value = tick.Time.ToUnixTimeMilliseconds();
                pPrice.Value = ToStored(tick.Price);
                pVolume.Value = tick.Volume;
                insert.ExecuteNonQuery();
            }
        }

        // Pokrycie: łączymy nowy odcinek z nakładającymi się i stykającymi – w tabeli zostaje jeden wiersz.
        using (var merge = connection.CreateCommand())
        {
            merge.CommandText = """
                SELECT MIN(from_time), MAX(to_time) FROM tick_coverage
                WHERE symbol = $s AND from_time <= $to AND to_time >= $from
                """;
            merge.Parameters.AddWithValue("$s", symbol);
            merge.Parameters.AddWithValue("$from", fromMs);
            merge.Parameters.AddWithValue("$to", toMs);
            using var reader = merge.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0))
            {
                fromMs = Math.Min(fromMs, reader.GetInt64(0));
                toMs = Math.Max(toMs, reader.GetInt64(1));
            }
        }

        using (var replace = connection.CreateCommand())
        {
            replace.CommandText = """
                DELETE FROM tick_coverage WHERE symbol = $s AND from_time >= $from AND to_time <= $to;
                INSERT INTO tick_coverage (symbol, from_time, to_time) VALUES ($s, $from, $to);
                """;
            replace.Parameters.AddWithValue("$s", symbol);
            replace.Parameters.AddWithValue("$from", fromMs);
            replace.Parameters.AddWithValue("$to", toMs);
            replace.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Odcinki, dla których w bazie są kompletne historyczne ticki od brokera (posortowane, rozłączne).
    /// </summary>
    public IReadOnlyList<TimeRange> GetTickCoverage(string symbol)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT from_time, to_time FROM tick_coverage WHERE symbol = $s ORDER BY from_time";
        command.Parameters.AddWithValue("$s", symbol);

        var result = new List<TimeRange>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TimeRange(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1))));
        }

        return result;
    }

    /// <summary>
    /// Początek pierwszej świecy instrumentu w zakresie [from, to) albo null – używane przy pobieraniu ticków,
    /// żeby przeskoczyć przerwę w handlu (noc, weekend) do miejsca, w którym znów były transakcje.
    /// </summary>
    public DateTimeOffset? NextBarTime(string symbol, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(time) FROM bars WHERE symbol = $s AND time >= $from AND time < $to";
        AddRange(command, symbol, from, to);
        var value = command.ExecuteScalar();
        return value is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }

    /// <summary>
    /// Czas ostatnich danych instrumentu ściśle przed <paramref name="before"/>: ostatni tick albo początek ostatniej
    /// świecy 1-min (null, gdy wcześniej nie ma danych). Używane przy kroku symulacji wstecz, żeby przeskoczyć
    /// przerwę w handlu (noc, weekend) do ostatniego okresu z transakcjami.
    /// </summary>
    /// <param name="source">Tylko dane z tego źródła (null = ze wszystkich).</param>
    public DateTimeOffset? LastDataTimeBefore(string symbol, DateTimeOffset before, string? source = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(t) FROM (
                SELECT MAX(time) AS t FROM ticks WHERE symbol = $s AND time < $before AND ($src IS NULL OR source = $src)
                UNION ALL SELECT MAX(time) FROM bars WHERE symbol = $s AND time < $before AND ($src IS NULL OR source = $src)
            )
            """;
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$before", before.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$src", (object?)source ?? DBNull.Value);
        var value = command.ExecuteScalar();
        return value is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }

    /// <summary>
    /// Usuwa dane instrumentu pochodzące z danego źródła (np. stare dane testowe).
    /// Dane z innych źródeł (np. od brokera) pozostają nienaruszone.
    /// </summary>
    public void DeleteSource(string symbol, string source)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM ticks WHERE symbol = $s AND source = $src;
            DELETE FROM bars  WHERE symbol = $s AND source = $src;
            DELETE FROM tick_coverage WHERE symbol = $s AND $src = 'IB';
            """;
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$src", source);
        command.ExecuteNonQuery();
    }

    // =====================================================================
    // Odczyt
    // =====================================================================

    /// <summary>
    /// Odczytuje ticki z zakresu [from, to) w kolejności czasu. Dane są czytane strumieniowo
    /// (po jednym rekordzie), więc nawet miliony ticków nie zajmują dużo pamięci.
    /// </summary>
    /// <param name="source">Tylko dane z tego źródła (null = ze wszystkich).</param>
    public IEnumerable<Tick> ReadTicks(string symbol, DateTimeOffset from, DateTimeOffset to, string? source = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT time, price, volume FROM ticks
            WHERE symbol = $s AND time >= $from AND time < $to AND ($src IS NULL OR source = $src)
            ORDER BY time, rowid
            """;
        AddRange(command, symbol, from, to, source);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new Tick(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                FromStored(reader.GetInt64(1)),
                reader.GetInt64(2));
        }
    }

    /// <summary>Odczytuje świece 1-minutowe z zakresu [from, to) w kolejności czasu.</summary>
    /// <param name="source">Tylko dane z tego źródła (null = ze wszystkich).</param>
    public IEnumerable<Bar> ReadBars(string symbol, DateTimeOffset from, DateTimeOffset to, string? source = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT time, open, high, low, close, volume FROM bars
            WHERE symbol = $s AND time >= $from AND time < $to AND ($src IS NULL OR source = $src)
            ORDER BY time
            """;
        AddRange(command, symbol, from, to, source);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new Bar(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                FromStored(reader.GetInt64(1)),
                FromStored(reader.GetInt64(2)),
                FromStored(reader.GetInt64(3)),
                FromStored(reader.GetInt64(4)),
                reader.GetInt64(5));
        }
    }

    /// <summary>Liczba świec 1-min instrumentu w zakresie [from, to) – liczba minut z handlem (np. do postępu pobierania ticków).</summary>
    public long CountBars(string symbol, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM bars WHERE symbol = $s AND time >= $from AND time < $to";
        AddRange(command, symbol, from, to);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Liczba ticków instrumentu w zakresie [from, to).</summary>
    public long CountTicks(string symbol, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ticks WHERE symbol = $s AND time >= $from AND time < $to";
        AddRange(command, symbol, from, to);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Zakres czasu dostępnych danych instrumentu (ticki i świece) oraz liczba ticków i świec.
    /// Null, gdy w bazie nie ma żadnych danych tego instrumentu.
    /// </summary>
    /// <param name="source">Tylko dane z tego źródła (null = ze wszystkich).</param>
    public (DateTimeOffset From, DateTimeOffset To, long Ticks, long Bars)? GetDataRange(string symbol, string? source = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(t), MAX(t), SUM(nt), SUM(nb) FROM (
                SELECT MIN(time) AS t, COUNT(*) AS nt, 0 AS nb FROM ticks WHERE symbol = $s AND ($src IS NULL OR source = $src)
                UNION ALL SELECT MAX(time), 0, 0 FROM ticks WHERE symbol = $s AND ($src IS NULL OR source = $src)
                UNION ALL SELECT MIN(time), 0, COUNT(*) FROM bars WHERE symbol = $s AND ($src IS NULL OR source = $src)
                UNION ALL SELECT MAX(time) + 60000, 0, 0 FROM bars WHERE symbol = $s AND ($src IS NULL OR source = $src)
            )
            """;
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$src", (object?)source ?? DBNull.Value);

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        return (DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetInt64(2),
                reader.GetInt64(3));
    }

    /// <summary>Czy w zakresie [from, to) są jakiekolwiek dane instrumentu (ticki lub świece).</summary>
    /// <param name="source">Tylko dane z tego źródła (null = ze wszystkich).</param>
    public bool HasData(string symbol, DateTimeOffset from, DateTimeOffset to, string? source = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM ticks WHERE symbol = $s AND time >= $from AND time < $to AND ($src IS NULL OR source = $src))
                OR EXISTS (SELECT 1 FROM bars  WHERE symbol = $s AND time >= $from AND time < $to AND ($src IS NULL OR source = $src))
            """;
        AddRange(command, symbol, from, to, source);
        return (long)command.ExecuteScalar()! != 0;
    }

    /// <summary>
    /// Źródło danych do symulacji instrumentu: dane od brokera (prawdziwy rynek), a dane testowe tylko wtedy,
    /// gdy od brokera nie ma nic. Źródeł nie mieszamy – losowe ceny z generatora wśród prawdziwych notowań
    /// wyglądałyby jak zupełnie inny instrument. Null = brak danych.
    /// </summary>
    public string? SimulationSource(string symbol)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM ticks WHERE symbol = $s AND source = $ib)
                OR EXISTS (SELECT 1 FROM bars  WHERE symbol = $s AND source = $ib),
                   EXISTS (SELECT 1 FROM ticks WHERE symbol = $s)
                OR EXISTS (SELECT 1 FROM bars  WHERE symbol = $s)
            """;
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$ib", SourceBroker);

        using var reader = command.ExecuteReader();
        reader.Read();
        if (reader.GetInt64(0) != 0)
        {
            return SourceBroker;
        }

        return reader.GetInt64(1) != 0 ? SourceTest : null;
    }

    /// <summary>
    /// Zakres świec instrumentu z danego źródła: czas najstarszej i najnowszej świecy (początek minuty).
    /// Null, gdy nie ma żadnej świecy z tego źródła.
    /// </summary>
    public (DateTimeOffset Oldest, DateTimeOffset Newest)? GetBarRange(string symbol, string source)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(time), MAX(time) FROM bars WHERE symbol = $s AND source = $src";
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$src", source);

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        return (DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)));
    }

    // =====================================================================
    // Pomocnicze
    // =====================================================================

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AddRange(SqliteCommand command, string symbol, DateTimeOffset from, DateTimeOffset to, string? source = null)
    {
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        if (command.CommandText.Contains("$src"))
        {
            command.Parameters.AddWithValue("$src", (object?)source ?? DBNull.Value);
        }
    }

    private static long ToStored(decimal price) => (long)Math.Round(price * PriceScale);

    private static decimal FromStored(long value) => value / PriceScale;
}
