using System.Globalization;
using MarketProfile.Core;
using MarketProfile.DataFeed.IB;

namespace MarketProfile.DataFeed.IBWeb;

/// <summary>
/// Składa transakcje (<see cref="Tick"/>) z wiadomości strumienia notowań Web API – tak samo jak ścieżka
/// „dane opóźnione” w <c>IbDataFeed</c>: Web API nie podaje każdej transakcji osobno (brak tick-by-tick).
///  - pole 31 (ostatnia cena) – nowa cena; prefiks „C” = zamknięcie poprzedniej sesji (nie transakcja), „H” = wstrzymanie,
///  - pole 7762 (wolumen dnia, narastający) – przyrost przypisujemy do ostatniej ceny (<see cref="CumulativeVolumeTracker"/>),
///  - pole 7059 (wielkość ostatniej transakcji) – tylko gdy brak wolumenu narastającego,
///  - pole 6509 (dostępność danych): „D”/„Y” = opóźnione → czas transakcji = czas aktualizacji − 15 min,
///  - pola 84 / 86 (bid / ask) – strona transakcji (kupno po ask, sprzedaż po bid), patrz <see cref="TradeSideClassifier"/>.
/// Ceny zapisujemy dokładnie tak, jak podał broker (bez zaokrąglania).
/// </summary>
public sealed class IbWebTickAssembler(bool isIndex)
{
    /// <summary>Opóźnienie danych opóźnionych IB (minuty).</summary>
    public const int DelayedMinutes = 15;

    private readonly CumulativeVolumeTracker _volume = new();
    private decimal? _lastPrice;
    private decimal? _bid;
    private decimal? _ask;

    /// <summary>Czy dane są opóźnione (wg pola 6509); null = jeszcze nie wiadomo.</summary>
    public bool? IsDelayed { get; private set; }

    /// <summary>Ostatnia cena (do testu połączenia i paska statusu).</summary>
    public decimal? LastPrice => _lastPrice;

    /// <summary>Ostatnie notowanie bid (pole 84).</summary>
    public decimal? Bid => _bid;

    /// <summary>Ostatnie notowanie ask (pole 86).</summary>
    public decimal? Ask => _ask;

    /// <summary>Przetwarza jedną wiadomość i zwraca powstałe transakcje (zwykle 0 albo 1).</summary>
    public IReadOnlyList<Tick> Process(IReadOnlyDictionary<string, string> fields, DateTimeOffset receivedAt)
    {
        if (fields.TryGetValue("6509", out var availability) && availability.Length > 0)
        {
            IsDelayed = availability.Contains('D') || availability.Contains('Y');
        }

        var time = fields.TryGetValue("_updated", out var updated) &&
                   long.TryParse(updated, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : receivedAt;
        if (IsDelayed == true)
        {
            time = time.AddMinutes(-DelayedMinutes);
        }

        // Bid / ask przed ceną transakcji – ta sama wiadomość może nieść nowe notowania i transakcję.
        if (fields.TryGetValue("84", out var bidText) && TryParsePrice(bidText, out var bid, out _))
        {
            _bid = bid;
        }

        if (fields.TryGetValue("86", out var askText) && TryParsePrice(askText, out var ask, out _))
        {
            _ask = ask;
        }

        var priceChanged = false;
        if (fields.TryGetValue("31", out var priceText) && TryParsePrice(priceText, out var price, out var isClose) && !isClose)
        {
            // Web API wysyła tylko pola, które się zmieniły – obecność pola 31 oznacza nowe notowanie.
            priceChanged = true;
            _lastPrice = price;
        }

        if (_lastPrice is not { } last)
        {
            return [];
        }

        // Indeks: brak transakcji i wolumenu – każda nowa wartość to „transakcja” z wolumenem 0.
        if (isIndex)
        {
            return priceChanged ? [new Tick(time, last, 0)] : [];
        }

        long delta = 0;
        if (fields.TryGetValue("7762", out var volumeText) && TryParseVolume(volumeText, out var cumulative))
        {
            delta = _volume.Update(cumulative);
        }

        if (delta > 0)
        {
            return [new Tick(time, last, delta, TradeSideClassifier.FromQuote(last, _bid, _ask))];
        }

        if (!priceChanged)
        {
            return [];
        }

        // Nowa cena bez przyrostu wolumenu: przy wolumenie narastającym – sama cena (wolumen 0, bez podwójnego liczenia);
        // bez niego – wielkość ostatniej transakcji.
        var size = !_volume.HasData && fields.TryGetValue("7059", out var sizeText) && TryParseVolume(sizeText, out var s) ? (long)s : 0;
        return [new Tick(time, last, _volume.HasData ? 0 : Math.Max(1, size), TradeSideClassifier.FromQuote(last, _bid, _ask))];
    }

    /// <summary>Cena z pola 31: „5012.25”, „C5012.25” (zamknięcie), „H5012.25” (wstrzymanie).</summary>
    public static bool TryParsePrice(string text, out decimal price, out bool isClose)
    {
        text = text.Trim();
        isClose = text.StartsWith('C');
        var digits = text.TrimStart('C', 'H').Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out price) && price > 0;
    }

    /// <summary>Wolumen: „123456”, „1,234”, „1.2K”, „3.4M”.</summary>
    public static bool TryParseVolume(string text, out decimal volume)
    {
        text = text.Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        var multiplier = 1m;
        if (text.EndsWith('K') || text.EndsWith('k'))
        {
            multiplier = 1_000m;
            text = text[..^1];
        }
        else if (text.EndsWith('M') || text.EndsWith('m'))
        {
            multiplier = 1_000_000m;
            text = text[..^1];
        }

        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0)
        {
            volume = value * multiplier;
            return true;
        }

        volume = 0;
        return false;
    }
}
