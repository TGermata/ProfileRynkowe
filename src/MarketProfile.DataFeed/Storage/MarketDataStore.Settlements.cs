using System.Globalization;
using MarketProfile.Core;
using Microsoft.Data.Sqlite;

namespace MarketProfile.DataFeed.Storage;

/// <summary>
/// Ceny rozliczeniowe (SETTLE) sesji dziennych – tabela <c>settlements</c>, jeden wiersz na instrument i dzień handlowy.
/// Źródło: strona CME albo wpis ręczny. Wpis ręczny ma pierwszeństwo – pobranie z CME go nie nadpisuje.
/// </summary>
public sealed partial class MarketDataStore
{
    private static void EnsureSettlementsTable(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE IF NOT EXISTS settlements (
            symbol       TEXT NOT NULL,
            trading_date TEXT NOT NULL,   -- yyyy-MM-dd
            settle       REAL NOT NULL,
            source       TEXT NOT NULL,   -- CME / ręcznie
            contract     TEXT,            -- miesiąc kontraktu (np. DEC 26)
            updated_at   TEXT NOT NULL,   -- UTC, ISO 8601
            PRIMARY KEY (symbol, trading_date)
        );
        """);

    /// <summary>
    /// Zapisuje cenę rozliczeniową. Cena z CME nie zastępuje wpisu ręcznego (zwraca wtedy false).
    /// </summary>
    public bool SaveSettlement(string symbol, Settlement settlement)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settlements (symbol, trading_date, settle, source, contract, updated_at)
            VALUES ($s, $d, $p, $src, $c, $u)
            ON CONFLICT (symbol, trading_date) DO UPDATE SET
                settle = excluded.settle, source = excluded.source, contract = excluded.contract, updated_at = excluded.updated_at
            WHERE excluded.source = $manual OR settlements.source <> $manual
            """;
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$d", settlement.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$p", (double)settlement.Price);
        command.Parameters.AddWithValue("$src", settlement.Source);
        command.Parameters.AddWithValue("$c", settlement.Contract);
        command.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$manual", Settlement.SourceManual);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Usuwa cenę rozliczeniową dnia (np. błędny wpis ręczny – potem można ją pobrać z CME ponownie).</summary>
    public void DeleteSettlement(string symbol, DateOnly tradingDate)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settlements WHERE symbol = $s AND trading_date = $d";
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$d", tradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>Ceny rozliczeniowe instrumentu (opcjonalnie z zakresu dat), wg daty handlowej.</summary>
    public IReadOnlyDictionary<DateOnly, Settlement> ReadSettlements(string symbol, DateOnly? from = null, DateOnly? to = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT trading_date, settle, source, contract FROM settlements
            WHERE symbol = $s AND trading_date >= $from AND trading_date <= $to
            """;
        command.Parameters.AddWithValue("$s", symbol);
        command.Parameters.AddWithValue("$from", (from ?? DateOnly.MinValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", (to ?? DateOnly.MaxValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var result = new Dictionary<DateOnly, Settlement>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            result[date] = new Settlement(date, (decimal)Math.Round(reader.GetDouble(1), 8), reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
        }

        return result;
    }
}
