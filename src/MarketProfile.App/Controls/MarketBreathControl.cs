using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MarketProfile.App.Controls;

/// <summary>
/// Wskaźnik „ODDECH RYNKU” w kolumnie wiersza nad wykresem „Szeroki rynek” (wszystko mieści się w szerokości kontrolki):
/// u góry wartość liczbowa (stosunek kupno/sprzedaż ze znakiem, np. +1,25 / −1,25), pod nią – opcjonalnie – poziomy
/// pasek z zerem na środku, na dole opis: wartość dodatnia wypełnia go w prawo
/// na zielono, ujemna w lewo na czerwono. Długość i nasycenie koloru (gradient) rosną z przewagą:
/// ±1,00 = równowaga (pusty pasek), ±3,00 i więcej = pełny pasek w pełnym kolorze.
/// </summary>
public sealed class MarketBreathControl : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(decimal?), typeof(MarketBreathControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Wartość wskaźnika (null – brak danych).</summary>
    public decimal? Value
    {
        get => (decimal?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(MarketBreathControl),
        new FrameworkPropertyMetadata("ODDECH RYNKU", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Podpis w lewym górnym rogu (np. „ODDECH RYNKU · SPY”).</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(MarketBreathControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Opis pod wartością (instrument, wolumen kupna i sprzedaży, sesja).</summary>
    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public static readonly DependencyProperty ShowGaugeProperty = DependencyProperty.Register(
        nameof(ShowGauge), typeof(bool), typeof(MarketBreathControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Wskaźnik graficzny (pasek) obok liczby – Ustawienia → Szeroki rynek.</summary>
    public bool ShowGauge
    {
        get => (bool)GetValue(ShowGaugeProperty);
        set => SetValue(ShowGaugeProperty, value);
    }

    /// <summary>Wartość (|v|), przy której pasek jest pełny.</summary>
    private const double FullScale = 3;

    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)));
    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x29)));
    private static readonly Brush TitleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x88, 0x94)));
    private static readonly Brush DimTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x6E)));
    private static readonly Brush NeutralBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)));
    private static readonly Pen TrackPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3A)), 1));
    private static readonly Pen ZeroPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB0)), 1));
    private static readonly Pen TickPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4A)), 1));
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Typeface NumberTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // Kolory: przy równowadze – przygaszone, przy pełnej przewadze – pełne.
    private static readonly Color GreenDim = Color.FromRgb(0x24, 0x4A, 0x36);
    private static readonly Color GreenFull = Color.FromRgb(0x2E, 0xE0, 0x6E);
    private static readonly Color RedDim = Color.FromRgb(0x4E, 0x26, 0x26);
    private static readonly Color RedFull = Color.FromRgb(0xFF, 0x3C, 0x3C);

    public MarketBreathControl()
    {
        ClipToBounds = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        const double margin = 8;
        var inner = Math.Max(0, width - 2 * margin);
        var value = Value;
        var strength = value is { } v ? Math.Clamp((Math.Abs((double)v) - 1) / (FullScale - 1), 0, 1) : 0;
        var positive = value is >= 0;
        var valueBrush = value is null
            ? NeutralBrush
            : Frozen(new SolidColorBrush(Lerp(positive ? GreenDim : RedDim, positive ? GreenFull : RedFull, 0.45 + 0.55 * strength)));

        // Na górze: wartość liczbowa wyśrodkowana nad paskiem; mały tytuł w lewym górnym rogu (gdy się mieści).
        var number = CreateText(value is { } n ? (n >= 0 ? "+" : "−") + Math.Abs(n).ToString("0.00", CultureInfo.CurrentCulture) : "–",
            20, valueBrush, NumberTypeface);
        var numberLeft = Math.Max(margin, (width - number.Width) / 2);
        dc.DrawText(number, new Point(numberLeft, 0));
        var title = CreateText(Title ?? string.Empty, 9, TitleBrush, UiTypeface);
        if (margin + title.Width + 6 <= numberLeft)
        {
            dc.DrawText(title, new Point(margin, 3));
        }

        // Dół: opis przycięty do szerokości.
        var detail = CreateText(Detail ?? string.Empty, 9, DimTextBrush, UiTypeface, Math.Max(1, inner));
        dc.DrawText(detail, new Point(margin, height - detail.Height - 3));

        if (!ShowGauge || inner < 40)
        {
            return;
        }

        // Środek: pasek na całą szerokość kolumny, zero na środku.
        const double barHeight = 12;
        var top = Math.Round(number.Height + 3) + 0.5;
        var track = new Rect(margin, top, inner, barHeight);
        dc.DrawRoundedRectangle(TrackBrush, TrackPen, track, 2, 2);

        var center = Math.Round(margin + inner / 2) + 0.5;
        var half = inner / 2;

        // Podziałka: ±1 (środek – równowaga), ±2, ±3.
        foreach (var mark in new[] { 2.0, 3.0 })
        {
            var offset = (mark - 1) / (FullScale - 1) * half;
            foreach (var x in new[] { center - offset, center + offset })
            {
                var xx = Math.Round(x) + 0.5;
                dc.DrawLine(TickPen, new Point(xx, top), new Point(xx, top + barHeight));
            }

            if (mark < FullScale)
            {
                DrawScaleLabel(dc, center + offset, top + barHeight, "+" + mark.ToString("0", CultureInfo.CurrentCulture));
                DrawScaleLabel(dc, center - offset, top + barHeight, "−" + mark.ToString("0", CultureInfo.CurrentCulture));
            }
        }

        DrawScaleLabel(dc, center, top + barHeight, "±1");

        // Wypełnienie: od środka w stronę przewagi, gradient od przygaszonego do koloru zależnego od siły.
        if (value is not null && strength > 0)
        {
            var length = Math.Max(2, strength * half);
            var end = Lerp(positive ? GreenDim : RedDim, positive ? GreenFull : RedFull, strength);
            var start = positive ? GreenDim : RedDim;
            var fillRect = positive
                ? new Rect(center, top + 2, length, barHeight - 4)
                : new Rect(center - length, top + 2, length, barHeight - 4);
            var gradient = new LinearGradientBrush(positive ? start : end, positive ? end : start, 0);
            gradient.Freeze();
            dc.DrawRoundedRectangle(gradient, null, fillRect, 1.5, 1.5);
        }

        dc.DrawLine(ZeroPen, new Point(center, top - 2), new Point(center, top + barHeight + 2));
    }

    private void DrawScaleLabel(DrawingContext dc, double x, double y, string text)
    {
        var label = CreateText(text, 8, DimTextBrush, UiTypeface);
        dc.DrawText(label, new Point(x - label.Width / 2, y + 1));
    }

    private static Color Lerp(Color from, Color to, double t) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * t),
        (byte)Math.Round(from.G + (to.G - from.G) * t),
        (byte)Math.Round(from.B + (to.B - from.B) * t));

    /// <param name="maxWidth">Najszerszy tekst (dłuższy jest przycinany wielokropkiem); null – bez ograniczenia.</param>
    private FormattedText CreateText(string text, double size, Brush brush, Typeface typeface, double? maxWidth = null)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth is { } max)
        {
            formatted.MaxTextWidth = max;
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }

        return formatted;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
