using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MarketProfile.App.Controls;

/// <summary>
/// Kolory tempa budowania TPO jak temperatura: małe tempo – niebieski, zwykłe – zielony, duże – w stronę czerwonego.
/// Kolor zależy od wielkości tempa (bez znaku); kierunek pokazuje strzałka.
/// </summary>
public static class TempoColors
{
    /// <summary>Koniec skali wskaźnika (× normy) – większe tempo jest pokazywane na końcu skali („30+”).</summary>
    public const double ScaleMax = 30.0;

    /// <summary>Podziałka wskaźnika (skala logarytmiczna – małe wartości dokładnie, duże się mieszczą).</summary>
    public static IReadOnlyList<double> ScaleMarks { get; } = [0, 1, 2, 5, 10, 20, 30];

    // Punkty gradientu: (tempo × normy, kolor).
    private static readonly (double At, Color Color)[] Stops =
    [
        (0.0, Color.FromRgb(0x3A, 0x6E, 0xF0)),  // niebieski – stoi
        (0.5, Color.FromRgb(0x2E, 0xC4, 0xE6)),  // turkus – wolno
        (1.0, Color.FromRgb(0x4C, 0xD9, 0x64)),  // zielony – zwykłe tempo
        (3.0, Color.FromRgb(0xF2, 0xD1, 0x3E)),  // żółty – szybko
        (10.0, Color.FromRgb(0xFF, 0x8C, 0x2E)), // pomarańczowy – bardzo szybko
        (30.0, Color.FromRgb(0xFF, 0x3B, 0x3B)), // czerwony – wyjątkowo szybko
    ];

    private static readonly Dictionary<int, SolidColorBrush> BrushCache = [];

    /// <summary>Położenie tempa na skali wskaźnika: 0 = początek, 1 = koniec (logarytmicznie: ln(1+v) / ln(1+30)).</summary>
    public static double Position(double tempo)
    {
        var m = double.IsNaN(tempo) ? 0 : Math.Min(Math.Abs(tempo), ScaleMax);
        return Math.Log(1 + m) / Math.Log(1 + ScaleMax);
    }

    /// <summary>Wartość tempa w danym położeniu na skali (odwrotność <see cref="Position"/>).</summary>
    public static double ValueAt(double position) =>
        Math.Exp(Math.Clamp(position, 0, 1) * Math.Log(1 + ScaleMax)) - 1;

    /// <summary>Kolor dla tempa (liczy się wielkość, znak pomijany).</summary>
    public static Color ColorFor(double tempo)
    {
        var m = double.IsNaN(tempo) ? 0 : Math.Min(Math.Abs(tempo), ScaleMax);
        for (var i = 1; i < Stops.Length; i++)
        {
            if (m <= Stops[i].At)
            {
                var (fromAt, from) = Stops[i - 1];
                var (toAt, to) = Stops[i];
                var t = (m - fromAt) / (toAt - fromAt);
                return Color.FromRgb(Lerp(from.R, to.R, t), Lerp(from.G, to.G, t), Lerp(from.B, to.B, t));
            }
        }

        return Stops[^1].Color;
    }

    /// <summary>Zamrożony pędzel dla tempa (pamięć podręczna co 1/200 długości skali).</summary>
    public static SolidColorBrush BrushFor(double tempo)
    {
        var key = (int)Math.Round(Position(tempo) * 200);
        if (!BrushCache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(ColorFor(ValueAt(key / 200.0)));
            brush.Freeze();
            BrushCache[key] = brush;
        }

        return brush;
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
}

/// <summary>Konwerter dla XAML: tempo (double) → pędzel w kolorze „temperatury”.</summary>
public sealed class TempoBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double tempo && !double.IsNaN(tempo) ? TempoColors.BrushFor(tempo) : System.Windows.DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
