namespace MarketProfile.Core;

/// <summary>Poziom alarmowy, który zadziałał.</summary>
/// <param name="Line">Linia z alarmem.</param>
/// <param name="Upward">true = przebicie w górę, false = w dół.</param>
public sealed record PriceAlertHit(PriceLine Line, bool Upward);

/// <summary>
/// Wykrywa przebicia poziomów alarmowych. Dostaje zakres cen od poprzedniego sprawdzenia (najniższa, najwyższa i ostatnia
/// cena), więc nie przeoczy poziomu, przez który cena „przeskoczyła” między sprawdzeniami.
/// <list type="bullet">
/// <item>przebicie w górę: poprzednia cena &lt; poziom ≤ najwyższa cena;</item>
/// <item>przebicie w dół: poprzednia cena &gt; poziom ≥ najniższa cena;</item>
/// <item>pierwsze sprawdzenie (po <see cref="Reset"/>) tylko ustala punkt odniesienia – historia nie uruchamia alarmów;</item>
/// <item>alarm „przy każdym przebiciu” po zadziałaniu czeka, aż cena odsunie się od poziomu o <see cref="PriceAlert.RearmTicks"/>.</item>
/// </list>
/// Alarm jednokrotny wyłącza wywołujący (ustawia <see cref="PriceAlert.IsTriggered"/>).
/// </summary>
public sealed class PriceAlertMonitor
{
    private decimal? _reference;
    private readonly HashSet<Guid> _waitingForDistance = [];

    /// <summary>Czy jest punkt odniesienia (poprzednia cena).</summary>
    public bool HasReference => _reference is not null;

    /// <summary>Zapomina poprzednią cenę (start, zmiana instrumentu, skok w symulacji) – następne sprawdzenie nie alarmuje.</summary>
    public void Reset()
    {
        _reference = null;
        _waitingForDistance.Clear();
    }

    /// <summary>Uzbraja ponownie poziom (po edycji / przesunięciu) – nie czeka już na odsunięcie ceny.</summary>
    public void Rearm(Guid lineId) => _waitingForDistance.Remove(lineId);

    /// <summary>Sprawdza poziomy dla zakresu cen od poprzedniego sprawdzenia.</summary>
    public IReadOnlyList<PriceAlertHit> Check(IEnumerable<PriceLine> lines, decimal low, decimal high, decimal last, decimal tickSize)
    {
        if (_reference is not { } reference)
        {
            _reference = last;
            return [];
        }

        var hits = new List<PriceAlertHit>();
        foreach (var line in lines)
        {
            if (line.Alert is not { IsArmed: true } alert)
            {
                continue;
            }

            var level = line.Price;
            if (_waitingForDistance.Contains(line.Id))
            {
                // Uzbrojenie dopiero po odsunięciu ceny od poziomu (sprawdzane na ostatniej cenie).
                if (Math.Abs(last - level) >= Math.Max(0, alert.RearmTicks) * tickSize && last != level)
                {
                    _waitingForDistance.Remove(line.Id);
                }

                continue;
            }

            bool? upward = reference < level && high >= level ? true
                : reference > level && low <= level ? false
                : null;

            if (upward is not { } up ||
                alert.Direction == AlertDirection.Up && !up ||
                alert.Direction == AlertDirection.Down && up)
            {
                continue;
            }

            hits.Add(new PriceAlertHit(line, up));
            if (alert.Repeat == AlertRepeat.EveryCross)
            {
                _waitingForDistance.Add(line.Id);
            }
        }

        _reference = last;
        return hits;
    }
}
