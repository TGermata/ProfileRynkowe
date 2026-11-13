using System.Globalization;

namespace MarketProfile.DataFeed.IB;

/// <summary>Propozycja godzin sesji dziennej i nocnej wyliczona z godzin handlu podanych przez IB.</summary>
public sealed record IbSessionSuggestion(TimeOnly DayStart, TimeOnly DayEnd, bool HasNight, TimeOnly NightStart, TimeOnly NightEnd);

/// <summary>
/// Odczyt godzin handlu z danych kontraktu IB (pola TradingHours i LiquidHours) oraz zamiana strefy czasowej IB
/// na identyfikator IANA.
///
/// Format IB (API 9.72+): "20261113:0930-20261113:1600;20261114:CLOSED;20261115:1800-20261116:1700".
///  - LiquidHours – sesja główna (u nas: sesja dzienna),
///  - TradingHours – pełne godziny handlu; jeśli wykraczają poza sesję główną, czas poza nią to sesja nocna.
/// (Klasa nie używa typów biblioteki TWS API – można ją testować bez niej.)
/// </summary>
public static class IbTradingHours
{
    /// <summary>Odczytuje przedziały handlu (pomija dni zamknięte i wpisy nieczytelne).</summary>
    public static IReadOnlyList<(DateTime Start, DateTime End)> Parse(string? hours)
    {
        var result = new List<(DateTime, DateTime)>();
        if (string.IsNullOrWhiteSpace(hours))
        {
            return result;
        }

        foreach (var entry in hours.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.EndsWith("CLOSED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Nowy format: "20261113:0930-20261113:1600".
            var parts = entry.Split('-');
            if (parts.Length == 2 && TryParsePoint(parts[0], out var start) && TryParsePoint(parts[1], out var end) && end > start)
            {
                result.Add((start, end));
                continue;
            }

            // Stary format: "20261113:0930-1600,1700-1800" (godziny tylko z jednym dniem).
            var dayAndTimes = entry.Split(':', 2);
            if (dayAndTimes.Length == 2 &&
                DateTime.TryParseExact(dayAndTimes[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                foreach (var range in dayAndTimes[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var times = range.Split('-');
                    if (times.Length == 2 && TryParseTime(times[0], out var s) && TryParseTime(times[1], out var e))
                    {
                        var startTime = day + s.ToTimeSpan();
                        var endTime = day + e.ToTimeSpan();
                        if (endTime <= startTime)
                        {
                            endTime = endTime.AddDays(1); // przedział przez północ
                        }

                        result.Add((startTime, endTime));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Propozycja sesji: dzienna = pierwszy przedział sesji głównej (LiquidHours); nocna = od końca sesji głównej
    /// do jej początku następnego dnia – tylko jeśli pełne godziny handlu (TradingHours) wykraczają poza sesję główną.
    /// Null, gdy brak czytelnych godzin.
    /// </summary>
    public static IbSessionSuggestion? Suggest(string? liquidHours, string? tradingHours)
    {
        var liquid = Parse(liquidHours);
        var trading = Parse(tradingHours);

        var main = liquid.Count > 0 ? liquid[0] : trading.Count > 0 ? trading[0] : default;
        if (main == default)
        {
            return null;
        }

        // Czy handel trwa także poza sesją główną (np. futures prawie całą dobę, akcje przed i po sesji)?
        var covering = trading.FirstOrDefault(t => t.Start <= main.Start && t.End >= main.End);
        var hasNight = liquid.Count > 0 && covering != default && (covering.Start < main.Start || covering.End > main.End);

        var dayStart = TimeOnly.FromDateTime(main.Start);
        var dayEnd = TimeOnly.FromDateTime(main.End);
        return new IbSessionSuggestion(dayStart, dayEnd, hasNight, dayEnd, dayStart);
    }

    /// <summary>Zamienia nazwy stref czasowych używane przez IB na identyfikatory IANA.</summary>
    public static string ToIanaTimeZone(string? ibTimeZone)
    {
        var id = ibTimeZone?.Trim() ?? string.Empty;
        return id switch
        {
            "US/Eastern" or "EST" or "EST5EDT" => "America/New_York",
            "US/Central" or "CST" or "CST6CDT" => "America/Chicago",
            "US/Pacific" => "America/Los_Angeles",
            "MET" or "CET" or "MEZ" => "Europe/Berlin",
            "GB" or "GMT" or "Europe/London" => "Europe/London",
            "Japan" => "Asia/Tokyo",
            "Hongkong" => "Asia/Hong_Kong",
            "Australia/NSW" => "Australia/Sydney",
            "" => "America/New_York",
            _ => id,
        };
    }

    private static bool TryParsePoint(string text, out DateTime point) =>
        DateTime.TryParseExact(text.Trim(), "yyyyMMdd:HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out point);

    private static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), "HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
