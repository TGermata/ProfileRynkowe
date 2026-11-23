using System.Globalization;
using System.Text.Json;

namespace MarketProfile.DataFeed.IBWeb;

/// <summary>Kontrakt futures z odpowiedzi <c>/trsrv/futures</c>.</summary>
public sealed record IbWebFutureContract(long Conid, string Symbol, int ExpirationDate);

/// <summary>Zamiany formatów między aplikacją (konwencje TWS) a IBKR Web API – bez sieci, testowalne.</summary>
public static class IbWebFormats
{
    /// <summary>
    /// Okres w formacie TWS („5 Y”, „10 D”, „2 W”, „6 M”) → parametr <c>period</c> Web API („5y”, „10d”, „2w”, „6m”).
    /// </summary>
    public static string ToPeriod(string duration)
    {
        var parts = duration.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count <= 0)
        {
            throw new ArgumentException($"Nieznany format okresu: „{duration}”.", nameof(duration));
        }

        var unit = parts[1].ToUpperInvariant() switch
        {
            "S" => "s",
            "D" => "d",
            "W" => "w",
            "M" => "m",
            "Y" => "y",
            _ => throw new ArgumentException($"Nieznana jednostka okresu: „{duration}”.", nameof(duration)),
        };
        return $"{count}{unit}";
    }

    /// <summary>Czas do parametru <c>startTime</c> (UTC, „yyyyMMdd-HH:mm:ss”).</summary>
    public static string ToApiTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Kontrakty z odpowiedzi <c>/trsrv/futures?symbols=ES</c>: <c>{"ES":[{"conid":…,"expirationDate":20251219,…}]}</c>.</summary>
    public static IReadOnlyList<IbWebFutureContract> ParseFutures(JsonElement root, string symbol)
    {
        var list = new List<IbWebFutureContract>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, symbol, StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in property.Value.EnumerateArray())
            {
                if (ReadLong(item, "conid") is { } conid && ReadLong(item, "expirationDate") is { } expiry)
                {
                    list.Add(new IbWebFutureContract(conid, ReadString(item, "symbol") ?? symbol, (int)expiry));
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Wybór kontraktu: najbliższy niewygasły (front month); gdy podano miesiąc RRRRMM – tylko kontrakty z datą wygaśnięcia
    /// w tym miesiącu (dla większości kontraktów to miesiąc kontraktu). Kolejność: od najbliższego.
    /// </summary>
    public static IReadOnlyList<IbWebFutureContract> OrderCandidates(IEnumerable<IbWebFutureContract> contracts, string contractMonth, DateTime today)
    {
        var todayNumber = int.Parse(today.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var month = contractMonth.Trim();
        return contracts
            .Where(c => c.ExpirationDate >= todayNumber)
            .Where(c => month.Length == 0 || c.ExpirationDate.ToString(CultureInfo.InvariantCulture).StartsWith(month, StringComparison.Ordinal))
            .OrderBy(c => c.ExpirationDate)
            .ToList();
    }

    /// <summary>
    /// Data handlowa świecy D/W/M z czasu Web API (ms od 1970): w strefie instrumentu; świeca zaczynająca się wieczorem
    /// (od 17:00 – otwarcie sesji nocnej) należy do następnego dnia handlowego. Wynik: data 00:00 UTC (jak w TWS).
    /// </summary>
    public static DateTimeOffset TradingDate(long milliseconds, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), zone);
        var date = local.Hour >= 17 ? local.Date.AddDays(1) : local.Date;
        return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.Zero);
    }

    /// <summary>Liczba z pola JSON (liczba albo tekst); null, gdy brak.</summary>
    public static long? ReadLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.Number => (long)value.GetDouble(),
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    /// <summary>Liczba dziesiętna z pola JSON – dokładnie jak w tekście odpowiedzi (bez błędów zaokrągleń double).</summary>
    public static decimal? ReadDecimal(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Tekst z pola JSON; null, gdy brak.</summary>
    public static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
}
