using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketProfile.Core;

namespace MarketProfile.DataFeed.Settlements;

/// <summary>Wynik pobrania ceny rozliczeniowej: cena albo czytelny powód, dlaczego jej nie ma.</summary>
/// <param name="NotPublished">CME odpowiedziało, ale ceny jeszcze nie ma (np. tuż po zamknięciu albo dzień bez handlu).</param>
public sealed record SettlementResult(Settlement? Settlement, string? Error, bool NotPublished = false)
{
    public static SettlementResult Fail(string error) => new(null, error);
}

/// <summary>
/// Pobiera ceny rozliczeniowe (SETTLE) kontraktów futures ze strony CME Group (dane publiczne, te same, które pokazuje
/// strona „Settlements” produktu). CME może odrzucać zapytania programów – wtedy zwracany jest powód,
/// a cenę można wpisać ręcznie. Klasa nie rzuca wyjątków na błędy sieci / formatu.
/// </summary>
public sealed class CmeSettlementClient
{
    private static readonly HttpClient Shared = CreateClient();

    private readonly HttpClient _http;

    public CmeSettlementClient(HttpClient? http = null) => _http = http ?? Shared;

    /// <summary>Adres danych rozliczeniowych produktu z danego dnia handlowego.</summary>
    public static Uri UrlFor(int productId, DateOnly tradingDate) => new(
        $"https://www.cmegroup.com/CmeWS/mvc/Settlements/Futures/Settlements/{productId}/FUT" +
        $"?strategy=DEFAULT&tradeDate={tradingDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Cena rozliczeniowa kontraktu <paramref name="contractMonth"/> (RRRRMM; pusty = najbliższy miesiąc z listy CME).
    /// </summary>
    public async Task<SettlementResult> GetAsync(int productId, DateOnly tradingDate, string contractMonth, CancellationToken cancellationToken = default)
    {
        if (productId <= 0)
        {
            return SettlementResult.Fail("brak kodu produktu CME w ustawieniach instrumentu");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UrlFor(productId, tradingDate));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return SettlementResult.Fail(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                    ? $"CME odmówiło dostępu ({(int)response.StatusCode})"
                    : $"odpowiedź CME {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Parse(json, tradingDate, contractMonth);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SettlementResult.Fail("CME nie odpowiada (przekroczony czas)");
        }
        catch (HttpRequestException ex)
        {
            return SettlementResult.Fail($"brak połączenia z CME ({ex.Message})");
        }
    }

    /// <summary>
    /// Odczyt odpowiedzi CME: {"settlements":[{"month":"DEC 26","settle":"6881.25",...},...],"tradeDate":"11/16/2026"}.
    /// </summary>
    public static SettlementResult Parse(string json, DateOnly tradingDate, string contractMonth)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return SettlementResult.Fail("nieznany format danych CME");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("settlements", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return SettlementResult.Fail("nieznany format danych CME");
            }

            var wanted = MonthLabel(contractMonth);
            foreach (var row in rows.EnumerateArray())
            {
                var month = Text(row, "month");
                if (month is null || month.Equals("Total", StringComparison.OrdinalIgnoreCase) ||
                    wanted is not null && !month.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ParsePrice(Text(row, "settle")) is { } price)
                {
                    return new SettlementResult(new Settlement(tradingDate, price, Settlement.SourceCme, month), null);
                }

                if (wanted is not null)
                {
                    break; // wskazany kontrakt jest, ale bez ceny
                }
            }

            return new SettlementResult(null, wanted is null
                ? "CME nie podało jeszcze ceny rozliczeniowej"
                : $"CME nie podało ceny rozliczeniowej kontraktu {wanted}", NotPublished: true);
        }
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Cena z tekstu CME (np. "6881.25", "6,881.25", "6881.25A"); "-" lub ułamki (obligacje) = brak.</summary>
    public static decimal? ParsePrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\''))
        {
            return null;
        }

        var cleaned = new string([.. text.Where(c => char.IsDigit(c) || c is '.' or '-')]);
        return cleaned.Length > 0 && cleaned != "-" &&
               decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var price)
            ? price
            : null;
    }

    /// <summary>Miesiąc kontraktu RRRRMM → etykieta CME, np. "202612" → "DEC 26"; pusty / błędny = null.</summary>
    public static string? MonthLabel(string contractMonth)
    {
        if (contractMonth is not { Length: >= 6 } ||
            !DateTime.TryParseExact(contractMonth[..6], "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return null;
        }

        return month.ToString("MMM yy", CultureInfo.InvariantCulture).ToUpperInvariant();
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.cmegroup.com/");
        return client;
    }
}
