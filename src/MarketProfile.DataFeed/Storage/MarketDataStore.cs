using MarketProfile.Core;
using Microsoft.Data.Sqlite;

namespace MarketProfile.DataFeed.Storage;

/// <summary>
/// Lokalna baza danych rynkowych (SQLite) – ticki i świece 1-minutowe, osobno dla każdego instrumentu.
/// Służy do zapisywania danych od brokera i późniejszego odtwarzania ich w symulatorze.
///
/// Format zapisu:
///  - czas: liczba milisekund od 1970-01-01 UTC (szybkie wyszukiwanie zakresów),
///  - cena: liczba całkowita = cena × 10 000 (dokładnie, bez błędów zaokrągleń liczb zmiennoprzecinkowych),
///  - źródło: skąd pochodzą dane, np. "IB" (broker) lub "TEST" (dane testowe z generatora).
/// </summary>
public sealed class MarketDataStore
{
    /// <summary>Źródło danych od brokera Interactive Brokers.</summary>
    public const string SourceBroker = "IB";

    /// <summary>Źródło danych testowych wygenerowanych przez symulator.</summary>
    public const string SourceTest = "TEST";

    private const decimal PriceScale = 10_000m;
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

    /// <summary>Domyślna lokalizacja bazy: %AppData%\MarketProfile\marketdata.db.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarketProfile", "marketdata.db");

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
            """);
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
    public IEnumerable<Tick> ReadTicks(string symbol, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT time, price, volume FROM ticks
            WHERE symbol = $s AND time >= $from AND time < $to
            ORDER BY time, rowid
            """;
        AddRange(command, symbol, from, to);

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
    public IEnumerable<Bar> ReadBars(string symbol, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT time, open, high, low, close, volume FROM bars
            WHERE symbol = $s AND time >= $from AND time < $to
            ORDER BY time
            """;
        AddRange(command, symbol, from, to);

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
    public (DateTimeOffset From, DateTimeOffset To, long Ticks, long Bars)? GetDataRange(string symbol)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(t), MAX(t), SUM(nt), SUM(nb) FROM (
                SELECT MIN(time) AS t, COUNT(*) AS nt, 0 AS nb FROM ticks WHERE symbol = $s
                UNION ALL SELECT MAX(time), 0, 0 FROM ticks WHERE symbol = $s
                UNION ALL SELECT MIN(time), 0, COUNT(*) FROM bars WHERE symbol = $s
                UNION ALL SELECT MAX(time) + 60000, 0, 0 FROM bars WHERE symbol = $s
            )
            """;
        command.Parameters.AddWithValue("$s", symbol);

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

    private static void AddRange(SqliteCommand command, string symbol, DateTimeOffset from, DateTimeOffset to)
    {
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
    }

    private static long ToStored(decimal price) => (long)Math.Round(price * PriceScale);

    private static decimal FromStored(long value) => value / PriceScale;
}
