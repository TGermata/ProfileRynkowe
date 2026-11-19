using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MarketProfile.App.Controls;

/// <summary>
/// Dane mini-wykresu otwarcia: zakres, VA i POC poprzedniego dnia, zakres nocy (NaN = brak) i cena otwarcia.
/// VAH to dolna granica wiersza – <paramref name="ValueHigh"/> podajemy już z wysokością wiersza (górna krawędź VA).
/// </summary>
public sealed record OpeningGaugeData(
    double Low, double High, double ValueLow, double ValueHigh, double Poc,
    double OvernightLow, double OvernightHigh, double Open);

/// <summary>
/// Mały pionowy wykres (jak ceny na wykresie – góra = wyżej) pokazujący, w której części zakresu poprzedniego dnia
/// jest otwarcie: szary pasek Low–High, niebieski odcinek VA, pomarańczowa kreska POC, fioletowy pasek nocy (ONL–ONH)
/// i żółty znacznik ▶ otwarcia. Otwarcie poza zakresem rozszerza skalę, a odstęp od High / Low (GAP) jest przerywany.
/// </summary>
public sealed class OpeningRangeGauge : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(OpeningGaugeData), typeof(OpeningRangeGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush RangeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0x52, 0x60)));
    private static readonly Brush ValueBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3E, 0x7B, 0xD8)));
    private static readonly Brush NightBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB0, 0x8C, 0xF0)));
    private static readonly Brush OpenBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xD1, 0x3E)));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xAE)));
    private static readonly Pen PocPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x3C)), 2));
    private static readonly Pen OpenPen = Frozen(new Pen(OpenBrush, 1.5));
    private static readonly Pen GapPen = Frozen(new Pen(OpenBrush, 1) { DashStyle = DashStyles.Dash });
    private static readonly Typeface LabelFace = new("Segoe UI");

    public OpeningRangeGauge()
    {
        Width = 64;
        Height = 84;
        ToolTip = "Zakres poprzedniego dnia (szary), VA (niebieski), POC (pomarańczowy), noc ONL–ONH (fioletowy) i otwarcie (▶ żółty)";
    }

    public OpeningGaugeData? Data
    {
        get => (OpeningGaugeData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // podpowiedź także nad pustym miejscem
        if (Data is not { } d || !(d.High > d.Low))
        {
            return;
        }

        var hasNight = !double.IsNaN(d.OvernightLow) && !double.IsNaN(d.OvernightHigh);
        var min = Math.Min(d.Low, d.Open);
        var max = Math.Max(d.High, d.Open);
        if (hasNight)
        {
            min = Math.Min(min, d.OvernightLow);
            max = Math.Max(max, d.OvernightHigh);
        }

        var pad = (max - min) * 0.06;
        min -= pad;
        max += pad;
        const double top = 4;
        var bottom = ActualHeight - 4;
        double Y(double price) => bottom - (price - min) / (max - min) * (bottom - top);

        const double barLeft = 16, barRight = 28, nightLeft = 32, nightRight = 36;

        // Zakres poprzedniego dnia, VA i POC.
        dc.DrawRectangle(RangeBrush, null, new Rect(new Point(barLeft, Y(d.High)), new Point(barRight, Y(d.Low))));
        dc.DrawRectangle(ValueBrush, null, new Rect(new Point(barLeft, Y(d.ValueHigh)), new Point(barRight, Y(d.ValueLow))));
        dc.DrawLine(PocPen, new Point(barLeft - 2, Y(d.Poc)), new Point(barRight + 2, Y(d.Poc)));

        // Noc.
        if (hasNight)
        {
            dc.DrawRectangle(NightBrush, null,
                new Rect(new Point(nightLeft, Y(d.OvernightHigh)), new Point(nightRight, Y(d.OvernightLow))));
        }

        // Luka: otwarcie poza zakresem poprzedniego dnia.
        var center = (barLeft + barRight) / 2;
        if (d.Open > d.High)
        {
            dc.DrawLine(GapPen, new Point(center, Y(d.High)), new Point(center, Y(d.Open)));
        }
        else if (d.Open < d.Low)
        {
            dc.DrawLine(GapPen, new Point(center, Y(d.Low)), new Point(center, Y(d.Open)));
        }

        // Otwarcie: ▶ i kreska przez pasek.
        var yOpen = Y(d.Open);
        var triangle = new StreamGeometry();
        using (var g = triangle.Open())
        {
            g.BeginFigure(new Point(4, yOpen - 4), true, true);
            g.LineTo(new Point(11, yOpen), true, false);
            g.LineTo(new Point(4, yOpen + 4), true, false);
        }

        triangle.Freeze();
        dc.DrawGeometry(OpenBrush, null, triangle);
        dc.DrawLine(OpenPen, new Point(11, yOpen), new Point(nightRight + 2, yOpen));

        // Podpisy H / L przy krańcach zakresu.
        Label(dc, "H", Y(d.High));
        Label(dc, "L", Y(d.Low));
    }

    private void Label(DrawingContext dc, string text, double y)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 9, LabelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(42, y - formatted.Height / 2));
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
