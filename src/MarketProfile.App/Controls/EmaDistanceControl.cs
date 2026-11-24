using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MarketProfile.App.Controls;

/// <summary>
/// Wskaźnik „cena wobec średniej” w górnym wierszu zakładki „Szeroki rynek” (na żółtym tle): o ile procent bieżąca cena
/// SPX jest nad (▲, ciemnozielony) albo pod (▼, ciemnoczerwony) średnią (EMA8 / EMA21 z wykresu 30 min).
/// U góry tytuł, w środku duża liczba, na dole opis (cena, średnia).
/// </summary>
public sealed class EmaDistanceControl : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(decimal?), typeof(EmaDistanceControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Odległość od średniej w % (null – brak danych).</summary>
    public decimal? Value
    {
        get => (decimal?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmaDistanceControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Podpis w lewym górnym rogu, np. „SPX VS EMA8”.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(EmaDistanceControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Opis na dole (cena, wartość średniej).</summary>
    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    private static readonly Brush YellowBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C)));
    private static readonly Brush TitleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0x3A, 0x0A)));
    private static readonly Brush DetailBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x30, 0x10)));
    private static readonly Brush UpBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x5E, 0x2A)));
    private static readonly Brush DownBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9C, 0x14, 0x14)));
    private static readonly Brush NeutralBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)));
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Typeface NumberTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public EmaDistanceControl()
    {
        ClipToBounds = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(YellowBrush, null, new Rect(0, 0, width, height));

        const double margin = 8;
        var inner = Math.Max(1, width - 2 * margin);

        var title = CreateText(Title ?? string.Empty, 9, TitleBrush, UiTypeface, inner);
        dc.DrawText(title, new Point(margin, 3));

        var value = Value;
        var (text, brush) = value switch
        {
            null => ("–", NeutralBrush),
            > 0 => ("▲ +" + value.Value.ToString("0.00", CultureInfo.CurrentCulture) + "%", UpBrush),
            < 0 => ("▼ −" + Math.Abs(value.Value).ToString("0.00", CultureInfo.CurrentCulture) + "%", DownBrush),
            _ => (0m.ToString("0.00", CultureInfo.CurrentCulture) + "%", NeutralBrush),
        };
        var number = CreateText(text, 22, brush, NumberTypeface);
        var numberTop = Math.Max(title.Height + 2, (height - number.Height) / 2 - 2);
        dc.DrawText(number, new Point(Math.Max(margin, (width - number.Width) / 2), numberTop));

        var detail = CreateText(Detail ?? string.Empty, 9, DetailBrush, UiTypeface, inner);
        dc.DrawText(detail, new Point(margin, height - detail.Height - 3));
    }

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
