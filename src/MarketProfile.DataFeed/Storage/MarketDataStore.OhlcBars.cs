using MarketProfile.Core;
using Microsoft.Data.Sqlite;

namespace MarketProfile.DataFeed.Storage;

/// <summary>
/// Świece szerszej perspektywy (dzienne, tygodniowe, miesięczne) – osobno dla każdego interwału.
/// Pobierane od brokera (dla kontraktów terminowych – kontrakt ciągły) i używane do wykresów D/W/M,
/// analiz i automatycznego tradingu. Czas świecy = początek okresu (<see cref="BarIntervals.PeriodStart"/>:
/// dzień, poniedziałek tygodnia, 1. dzień miesiąca; 00:00 UTC, bez strefy), ceny × 10⁸ jak w <c>bars</c>.
/// </summary>
public sealed partial class MarketDataStore
{
    private static void EnsureOhlcBarsTable(SqliteConnection connection)
    {
        CreateOhlcBarsTable(connection);
        NormalizeOhlcBarTimes(connection);
    }

    private static void CreateOhlcBarsTable(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE IF NOT EXISTS ohlc_bars (
            symbol   TEXT    NOT NULL,
            interval TEXT    NOT NULL,   -- D / W / M
            time     INTEGER NOT NULL,   -- data świecy (00:00 UTC), ms od 1970-01-01
            open     INTEGER NOT NULL,   -- cena × 10⁸
            high     INTEGER NOT NULL,
            low      INTEGER NOT NULL,
            close    INTEGER NOT NULL,
            volume   INTEGER NOT NULL,
            source   TEXT    NOT NULL,
            PRIMARY KEY (symbol, interval, time)
        );
        """);

    /// <summary>
    /// Świece tygodniowe / miesięczne zapisane pod datą inną niż początek okresu (pierwsza wersja zapisywała datę
    /// od brokera – koniec okresu, a bieżącą świecę pod dzisiejszą datą) – przenosi je na początek okresu.
    /// Gdy w okresie jest kilka świec, zostaje najnowsza (pełniejsza). Kilkaset wierszy – szybko przy każdym starcie.
    /// </summary>
    private static void NormalizeOhlcBarTimes(SqliteConnection connection)
    {
        var moves = new List<(string Symbol, string Interval, long From, long To)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT symbol, interval, time FROM ohlc_bars WHERE interval IN ('W', 'M') ORDER BY time";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var interval = reader.GetString(1) == "W" ? BarInterval.Week : BarInterval.Month;
                var time = reader.GetInt64(2);
                var start = interval.PeriodStart(DateTimeOffset.FromUnixTimeMilliseconds(time)).ToUnixTimeMilliseconds();
                if (start != time)
                {
                    moves.Add((reader.GetString(0), reader.GetString(1), time, start));
                }
            }
        }

        if (moves.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE OR REPLACE ohlc_bars SET time = $to WHERE symbol = $s AND interval = $i AND time = $from";
        var pSymbol = update.Parameters.Add("$s", SqliteType.Text);
        var pInterval = update.Parameters.Add("$i", SqliteType.Text);
        var pFrom = update.Parameters.Add("$from", SqliteType.Integer);
        var pTo = update.Parameters.Add("$to", SqliteType.Integer);
        foreach (var (symbol, interval, from, to) in moves)
        {
            pSymbol.Value = symbol;
            pInterval.Value = interval;
            pFrom.Value = from;
            pTo.Value = to;
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Przyrostek kodu interwału serii „tylko sesja kasowa” (świece D/W/M kontraktu terminowego bez notowań nocnych).</summary>
    public const string CashOnlySuffix = "-RTH";

    /// <summary>
    /// Kod interwału w kolumnie <c>interval</c>: "D" / "W" / "M" / "30m" – seria podstawowa (dla kontraktów terminowych
    /// cała doba), z przyrostkiem <see cref="CashOnlySuffix"/> – osobna seria tylko z sesji kasowej (np. "D-RTH").
    /// </summary>
    private static string StoredIntervalCode(BarInterval interval, bool cashOnly) =>
        cashOnly ? interval.Code() + CashOnlySuffix : interval.Code();

    /// <summary>
    /// Zapisuje świece interwału pod początkiem okresu (<see cref="BarIntervals.PeriodStart"/>). Świeca tego samego
    /// okresu jest zastępowana – bieżąca (niedomknięta) świeca jest aktualizowana przy każdym odświeżeniu.
    /// Zwraca liczbę zapisanych świec.
    /// </summary>
    public int SaveOhlcBars(string symbol, BarInterval interval, IEnumerable<Bar> bars, string source, bool cashOnly = false)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO ohlc_bars (symbol, interval, time, open, high, low, close, volume, source)
            VALUES ($s, $i, $t, $o, $h, $l, $c, $v, $src)
            """;

        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$i", StoredIntervalCode(interval, cashOnly));
        command.Parameters.AddWithValue("$src", source);
        var pTime = command.Parameters.Add("$t", SqliteType.Integer);
        var pOpen = command.Parameters.Add("$o", SqliteType.Integer);
        var pHigh = command.Parameters.Add("$h", SqliteType.Integer);
        var pLow = command.Parameters.Add("$l", SqliteType.Integer);
        var pClose = command.Parameters.Add("$c", SqliteType.Integer);
        var pVolume = command.Parameters.Add("$v", SqliteType.Integer);

        var count = 0;
        foreach (var bar in bars)
        {
            pTime.Value = interval.PeriodStart(bar.Time).ToUnixTimeMilliseconds();
            pOpen.Value = ToStored(bar.Open);
            pHigh.Value = ToStored(bar.High);
            pLow.Value = ToStored(bar.Low);
            pClose.Value = ToStored(bar.Close);
            pVolume.Value = bar.Volume;
            command.ExecuteNonQuery();
            count++;
        }

        transaction.Commit();
        return count;
    }

    /// <summary>
    /// Świece interwału w kolejności czasu, z zakresu [from, to) (null = bez ograniczenia).
    /// </summary>
    public IReadOnlyList<Bar> ReadOhlcBars(string symbol, BarInterval interval, DateTimeOffset? from = null, DateTimeOffset? to = null,
        bool cashOnly = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT time, open, high, low, close, volume FROM ohlc_bars
            WHERE symbol = $s AND interval = $i
              AND ($from IS NULL OR time >= $from) AND ($to IS NULL OR time < $to)
            ORDER BY time
            """;
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$i", StoredIntervalCode(interval, cashOnly));
        command.Parameters.AddWithValue("$from", (object?)from?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)to?.ToUnixTimeMilliseconds() ?? DBNull.Value);

        var bars = new List<Bar>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            bars.Add(new Bar(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                FromStored(reader.GetInt64(1)),
                FromStored(reader.GetInt64(2)),
                FromStored(reader.GetInt64(3)),
                FromStored(reader.GetInt64(4)),
                reader.GetInt64(5)));
        }

        return bars;
    }

    /// <summary>Data najstarszej i najnowszej świecy interwału oraz ich liczba; null = brak świec.</summary>
    public (DateTimeOffset Oldest, DateTimeOffset Newest, int Count)? GetOhlcRange(string symbol, BarInterval interval, bool cashOnly = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(time), MAX(time), COUNT(*) FROM ohlc_bars WHERE symbol = $s AND interval = $i";
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$i", StoredIntervalCode(interval, cashOnly));

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        return (DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                (int)reader.GetInt64(2));
    }
}
