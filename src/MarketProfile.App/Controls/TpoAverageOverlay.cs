using System.Windows.Media;
using MarketProfile.App.Infrastructure;
using MarketProfile.App.Settings;
using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>Poziom średniej do narysowania na wykresie TPO: cena, opis i zamrożone pióro / pędzel.</summary>
/// <param name="BandHeight">Szerokość paska w px (0 = linia).</param>
/// <param name="BandBrush">Wypełnienie paska (z kryciem paska); null = linia.</param>
/// <param name="Blink">Cena jest blisko średniej – linia / pasek miga.</param>
public sealed record TpoAverageLine(int Period, decimal Price, string Label, Pen Pen, Brush Brush, Brush AxisBrush,
    double BandHeight = 0, Brush? BandBrush = null, bool Blink = false);

/// <summary>
/// Średnie kroczące z wykresu dziennego (wartości „na dziś”) rysowane jako linie poziome na wykresie TPO
/// razem ze sposobem opisu z ustawień.
/// </summary>
public sealed record TpoAverageOverlay(
    IReadOnlyList<TpoAverageLine> Lines,
    LabelPosition LabelPosition,
    bool ShowAxisLabel,
    double FontSize,
    int BlinkIntervalMs = 500)
{
    public static TpoAverageOverlay Empty { get; } = new([], LabelPosition.Left, false, 11);

    /// <summary>Czy którakolwiek linia miga.</summary>
    public bool AnyBlink => Lines.Any(l => l.Blink);

    /// <summary>
    /// Buduje linie dla podanych wartości średnich (okres → wartość; brak wartości = za mało świec – bez linii).
    /// </summary>
    public static TpoAverageOverlay Create(TpoAveragesStyle style, IReadOnlyList<MovingAverageStyle> averages,
        Func<int, decimal?> valueFor, decimal tickSize, decimal lastPrice)
    {
        if (!style.Enabled)
        {
            return Empty;
        }

        var opacity = Math.Clamp(style.Opacity, 10, 100);
        var lines = new List<TpoAverageLine>();
        var blinkDistance = Math.Clamp(style.BlinkDistanceTicks, 1, 400) * tickSize;
        foreach (var average in averages.Where(a => a.ShowOnTpo && a.Period >= 1))
        {
            if (valueFor(average.Period) is not { } value)
            {
                continue;
            }

            var color = average.EffectiveTpoColor;
            var brush = Frozen(ColorUtil.ToBrush(color, opacity, "#F0C030"));
            var pen = Frozen(new Pen(brush, Math.Clamp(average.TpoThickness, 1, 10))
            {
                DashStyle = ChartStyle.ToDashStyle(average.TpoLineStyle),
            });
            var label = average.EffectiveTpoLabel;
            if (style.ShowValueInLabel)
            {
                label += " " + PriceFormat.Format(value, tickSize);
            }

            var band = average.TpoShape == TpoAverageShape.Band;
            lines.Add(new TpoAverageLine(average.Period, value, label, pen, brush,
                Frozen(ColorUtil.ToBrush(color, 100, "#F0C030")),
                band ? Math.Clamp(average.TpoBandHeight, 2, 60) : 0,
                band ? Frozen(ColorUtil.ToBrush(color, Math.Clamp(average.TpoBandOpacity, 5, 100), "#F0C030")) : null,
                // Cena zbliżyła się do średniej – linia miga (jeśli włączone w ustawieniach).
                style.BlinkEnabled && average.TpoBlink && Math.Abs(lastPrice - value) <= blinkDistance));
        }

        return new TpoAverageOverlay(lines, style.LabelPosition, style.ShowAxisLabel, Math.Clamp(style.FontSize, 7, 24),
            Math.Clamp(style.BlinkIntervalMs, 150, 2000));
    }

    /// <summary>Czy dwie nakładki rysują to samo (żeby nie odświeżać wykresu bez potrzeby).</summary>
    public bool SameAs(TpoAverageOverlay other) =>
        LabelPosition == other.LabelPosition && ShowAxisLabel == other.ShowAxisLabel && FontSize == other.FontSize &&
        BlinkIntervalMs == other.BlinkIntervalMs &&
        Lines.Count == other.Lines.Count &&
        Lines.Zip(other.Lines).All(p => p.First.Price == p.Second.Price && p.First.Label == p.Second.Label &&
                                         p.First.Pen.Thickness == p.Second.Pen.Thickness &&
                                         p.First.BandHeight == p.Second.BandHeight && p.First.Blink == p.Second.Blink &&
                                         p.First.BandBrush?.ToString() == p.Second.BandBrush?.ToString() &&
                                         p.First.Brush.ToString() == p.Second.Brush.ToString() &&
                                         p.First.Pen.DashStyle.Dashes.SequenceEqual(p.Second.Pen.DashStyle.Dashes));

    private static T Frozen<T>(T freezable) where T : System.Windows.Freezable
    {
        if (freezable.CanFreeze)
        {
            freezable.Freeze();
        }

        return freezable;
    }
}
