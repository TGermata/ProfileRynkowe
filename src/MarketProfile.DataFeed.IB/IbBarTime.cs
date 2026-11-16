using System.Globalization;

namespace MarketProfile.DataFeed.IB;

/// <summary>Odczyt czasu świecy historycznej z TWS (bez typów biblioteki TWS API – testowalne).</summary>
public static class IbBarTime
{
    /// <summary>
    /// Czas świecy w jednym z formatów TWS:
    ///  - sekundy od 1970 (formatDate = 2, świece śróddzienne),
    ///  - „yyyyMMdd” (świece dzienne / tygodniowe / miesięczne) → data handlowa 00:00 UTC,
    ///  - „yyyyMMdd HH:mm:ss Strefa/Czasowa” (formatDate = 1, świece śróddzienne).
    /// </summary>
    public static bool TryParse(string text, out DateTimeOffset time)
    {
        text = text.Trim();
        if (text.Length == 8 &&
            DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            time = new DateTimeOffset(date, TimeSpan.Zero);
            return true;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            time = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            DateTime.TryParseExact($"{parts[0]} {parts[1]}", "yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            var zone = parts.Length >= 3 ? TryFindZone(parts[2]) : null;
            time = zone is null ? new DateTimeOffset(local, TimeSpan.Zero) : new DateTimeOffset(local, zone.GetUtcOffset(local));
            return true;
        }

        time = default;
        return false;
    }

    private static TimeZoneInfo? TryFindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
