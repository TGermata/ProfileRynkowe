using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MarketProfile.App.ViewModels;

namespace MarketProfile.App.Controls;

/// <summary>
/// Wskaźnik „spółki rosnące / spadające” (NYSE, NASDAQ) w kolumnie wiersza nad wykresem „Szeroki rynek”, w stylu
/// <see cref="MarketBreathControl"/>: u góry liczby „▲ rosnące  ▼ spadające” – na zielono, gdy więcej spółek rośnie,
/// na czerwono, gdy więcej spada; pod nimi – opcjonalnie – pasek z zerem na środku (przewaga jako część wszystkich
/// spółek rosnących i spadających: w prawo zielony, w lewo czerwony, pełny przy 100%), na dole opis.
/// </summary>
public sealed class AdvanceDeclineControl : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(AdvanceDeclineView), typeof(AdvanceDeclineControl),
        new FrameworkPropertyMetadata(AdvanceDeclineView.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Liczby spółek i opis.</summary>
    public AdvanceDeclineView Data
    {
        get => (AdvanceDeclineView)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AdvanceDeclineControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Podpis w lewym górnym rogu (np. „SPÓŁKI ↑↓ · NYSE”).</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty ShowGaugeProperty = DependencyProperty.Register(
        nameof(ShowGauge), typeof(bool), typeof(AdvanceDeclineControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Pasek pod liczbami – Ustawienia → Szeroki rynek (wspólne z „Oddechem rynku”).</summary>
    public bool ShowGauge
    {
        get => (bool)GetValue(ShowGaugeProperty);
        set => SetValue(ShowGaugeProperty, value);
    }

    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)));
    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x29)));
    private static readonly Brush TitleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x88, 0x94)));
    private static readonly Brush DimTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x6E)));
    private static readonly Brush NeutralBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)));
    private static readonly Brush GreenBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0xE0, 0x6E)));
    private static readonly Brush RedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3C, 0x3C)));
    private static readonly Brush GreenFillBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x5C)));
    private static readonly Brush RedFillBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD8, 0x3A, 0x3A)));
    private static readonly Pen TrackPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3A)), 1));
    private static readonly Pen ZeroPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB0)), 1));
    private static readonly Pen TickPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4A)), 1));
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Typeface NumberTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public AdvanceDeclineControl()
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
        var data = Data ?? AdvanceDeclineView.Empty;
        var hasData = data is { Advancing: not null, Declining: not null };
        var advancing = data.Advancing ?? 0;
        var declining = data.Declining ?? 0;
        var brush = !hasData || advancing == declining ? NeutralBrush : advancing > declining ? GreenBrush : RedBrush;

        // Na górze: liczby wyśrodkowane; mały tytuł w lewym górnym rogu (gdy się mieści).
        var text = hasData
            ? $"▲ {advancing.ToString("N0", CultureInfo.CurrentCulture)}   ▼ {declining.ToString("N0", CultureInfo.CurrentCulture)}"
            : "–";
        var number = CreateText(text, 18, brush, NumberTypeface);
        var numberLeft = Math.Max(margin, (width - number.Width) / 2);
        dc.DrawText(number, new Point(numberLeft, 1));
        var title = CreateText(Title ?? string.Empty, 9, TitleBrush, UiTypeface);
        if (margin + title.Width + 6 <= numberLeft)
        {
            dc.DrawText(title, new Point(margin, 3));
        }

        var detail = CreateText(data.Detail ?? string.Empty, 9, DimTextBrush, UiTypeface, Math.Max(1, inner));
        dc.DrawText(detail, new Point(margin, height - detail.Height - 3));

        if (!ShowGauge || inner < 40)
        {
            return;
        }

        // Pasek: zero (po równo) na środku; przewaga = (rosnące − spadające) / (rosnące + spadające).
        const double barHeight = 12;
        var top = Math.Round(number.Height + 4) + 0.5;
        dc.DrawRoundedRectangle(TrackBrush, TrackPen, new Rect(margin, top, inner, barHeight), 2, 2);
        var center = Math.Round(margin + inner / 2) + 0.5;
        var half = inner / 2;

        // Podziałka: 75% rosnących (prawo) / 75% spadających (lewo) – przewaga 50%.
        foreach (var x in new[] { center - half / 2, center + half / 2 })
        {
            var xx = Math.Round(x) + 0.5;
            dc.DrawLine(TickPen, new Point(xx, top), new Point(xx, top + barHeight));
        }

        DrawScaleLabel(dc, center + half / 2, top + barHeight, "75% ▲");
        DrawScaleLabel(dc, center - half / 2, top + barHeight, "75% ▼");
        DrawScaleLabel(dc, center, top + barHeight, "50/50");

        var total = advancing + declining;
        if (hasData && total > 0 && advancing != declining)
        {
            var edge = (double)(advancing - declining) / total;
            var length = Math.Max(2, Math.Abs(edge) * half);
            var fill = edge > 0
                ? new Rect(center, top + 2, length, barHeight - 4)
                : new Rect(center - length, top + 2, length, barHeight - 4);
            dc.DrawRoundedRectangle(edge > 0 ? GreenFillBrush : RedFillBrush, null, fill, 1.5, 1.5);
        }

        dc.DrawLine(ZeroPen, new Point(center, top - 2), new Point(center, top + barHeight + 2));
    }

    private void DrawScaleLabel(DrawingContext dc, double x, double y, string text)
    {
        var label = CreateText(text, 8, DimTextBrush, UiTypeface);
        dc.DrawText(label, new Point(x - label.Width / 2, y + 1));
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
