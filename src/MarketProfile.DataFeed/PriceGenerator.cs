using MarketProfile.Core;

namespace MarketProfile.DataFeed;

/// <summary>
/// Generator realistycznie wyglądających cen: błądzenie losowe z powrotem do wolno wędrującej
/// "wartości godziwej" – daje rotacje i trendy podobne do prawdziwego rynku.
/// Używany przez symulator (dane na żywo) i do tworzenia danych testowych w bazie.
/// </summary>
public sealed class PriceGenerator
{
    private readonly Random _random;
    private readonly decimal _tickSize;
    private readonly double _sigma;   // zmienność na sekundę
    private double _price;            // bieżąca "prawdziwa" cena (przed zaokrągleniem do ticka)
    private double _anchor;           // wartość godziwa, wokół której oscyluje cena

    public PriceGenerator(Instrument instrument, int? seed = null)
    {
        _random = seed is { } s ? new Random(s) : new Random();
        _tickSize = instrument.TickSize;
        _price = (double)instrument.ReferencePrice;
        _anchor = _price;

        // Zmienność proporcjonalna do ceny – daje dzienny zakres ok. 1% (podobnie jak na prawdziwym rynku).
        _sigma = _price * 0.000025;
    }

    /// <summary>Bieżąca cena zaokrąglona do ticka.</summary>
    public decimal Price => Math.Round((decimal)_price / _tickSize) * _tickSize;

    /// <summary>Przesuwa cenę o krok trwający <paramref name="seconds"/> sekund.</summary>
    public void Step(double seconds)
    {
        var scale = Math.Sqrt(seconds);
        _anchor += NextGaussian() * _sigma * 0.4 * scale;
        _price += NextGaussian() * _sigma * scale + (_anchor - _price) * 0.002 * seconds;
    }

    /// <summary>Losowy wolumen transakcji z zakresu [min, max).</summary>
    public long NextVolume(int min, int max) => _random.Next(min, max);

    /// <summary>Losowa liczba całkowita z zakresu [min, max).</summary>
    public int Next(int min, int max) => _random.Next(min, max);

    /// <summary>
    /// Generuje ticki w zakresie czasu [from, to): średnio jedna transakcja co ~<paramref name="averageSeconds"/> s.
    /// </summary>
    public IEnumerable<Tick> GenerateTicks(DateTimeOffset from, DateTimeOffset to, double averageSeconds = 2)
    {
        var time = from;
        while (time < to)
        {
            var dt = averageSeconds * (0.2 + _random.NextDouble() * 1.6);
            Step(dt);
            time = time.AddSeconds(dt);
            yield return new Tick(time, Price, NextVolume(1, 25));
        }
    }

    /// <summary>Liczba losowa z rozkładu normalnego (transformacja Boxa-Mullera).</summary>
    private double NextGaussian()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
