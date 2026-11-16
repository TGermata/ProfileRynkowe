using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>
/// Wolumen całkowity profilu (sesji): bieżąca sesja do teraz, poprzednia sesja do tej samej chwili i – jako tło –
/// cała poprzednia sesja (ile wolumenu zwykle przychodzi do końca). U góry różnica w sztukach i procentach.
/// Po zakończeniu sesji – dwa słupki: cała sesja dziś i cała poprzednia.
/// </summary>
public sealed class SessionVolumeChartControl : FrameworkElement
{
    public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(
        nameof(Result), typeof(VolumeComparisonResult), typeof(SessionVolumeChartControl),
        new FrameworkPropertyMetadata(VolumeComparisonResult.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Porównanie (sumy sesji).</summary>
    public VolumeComparisonResult Result
    {
        get => (VolumeComparisonResult)GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x17, 0x1A, 0x20)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)));
    private static readonly Brush DimTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x88, 0x94)));
    private static readonly Brush TodayBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xA3, 0xF7)));
    private static readonly Brush PreviousBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x6E)));
    private static readonly Brush FullPreviousBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x50, 0x5A, 0x62, 0x6E)));
    private static readonly Brush UpBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0xC0, 0x70)));
    private static readonly Brush DownBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)));
    private static readonly Pen BorderPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3A)), 1));
    private static readonly Pen FullPreviousPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x6E)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Typeface BoldTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));
        dc.DrawLine(BorderPen, new Point(0.5, 0), new Point(0.5, height));

        var result = Result ?? VolumeComparisonResult.Empty;
        var title = Text("Wolumen sesji", 13, TextBrush, BoldTypeface);
        dc.DrawText(title, new Point((width - title.Width) / 2, 8));
        if (result.Rows.Count == 0 || width < 120 || height < 160)
        {
            return;
        }

        // Różnica (dziś vs poprzednia do tej samej chwili).
        var y = 8 + title.Height + 4;
        if (result.HasPrevious)
        {
            var diff = result.TotalDifference;
            var brush = diff >= 0 ? UpBrush : DownBrush;
            var percent = result.TotalPercent is { } p
                ? (p > 0 ? "+" : p < 0 ? "−" : string.Empty) + Math.Abs(p).ToString("0.0", CultureInfo.CurrentCulture) + "%"
                : "–";
            var big = Text(percent, 22, brush, BoldTypeface);
            dc.DrawText(big, new Point((width - big.Width) / 2, y));
            y += big.Height;
            var sub = Text((diff > 0 ? "+" : diff < 0 ? "−" : string.Empty) + VolumeFormat.Long(Math.Abs(diff)), 12, brush);
            dc.DrawText(sub, new Point((width - sub.Width) / 2, y));
            y += sub.Height + 2;
        }
        else
        {
            var none = Text("brak poprzedniej sesji", 11, DimTextBrush);
            dc.DrawText(none, new Point((width - none.Width) / 2, y + 6));
            y += none.Height + 14;
        }

        var cutoff = result.CutoffLocal is { } c ? $"do {c:HH:mm}" : "cała sesja";
        var bars = new List<(string Label, string Sub, long Value, Brush Fill, Pen? Outline)>
        {
            ("Dziś", cutoff, result.TodayTotal, TodayBrush, null),
        };
        if (result.HasPrevious)
        {
            bars.Add(("Poprzednia", cutoff, result.PreviousTotal, PreviousBrush, null));
            if (result.CutoffLocal is not null)
            {
                bars.Add(("Poprzednia", "cała sesja", result.PreviousSessionTotal, FullPreviousBrush, FullPreviousPen));
            }
        }

        // Słupki od dołu, wspólna skala.
        var labelsHeight = 34.0;
        var valueHeight = 18.0;
        var plotTop = y + valueHeight + 6;
        var plotBottom = height - labelsHeight - 6;
        if (plotBottom - plotTop < 30)
        {
            return;
        }

        var max = Math.Max(1, bars.Max(b => b.Value));
        var slot = (width - 16) / bars.Count;
        for (var i = 0; i < bars.Count; i++)
        {
            var (label, sub, value, fill, outline) = bars[i];
            var x0 = 8 + i * slot;
            var w = Math.Max(8, Math.Floor(slot * 0.62));
            var x = x0 + (slot - w) / 2;
            var h = (plotBottom - plotTop) * value / max;
            dc.DrawRectangle(fill, outline, new Rect(x, plotBottom - h, w, Math.Max(h, 0.5)));

            var valueText = Text(VolumeFormat.Short(value), 11, i == 0 ? TodayBrush : TextBrush);
            dc.DrawText(valueText, new Point(x0 + (slot - valueText.Width) / 2, plotBottom - h - valueText.Height - 2));

            var labelText = Text(label, 11, i == 0 ? TodayBrush : TextBrush);
            dc.DrawText(labelText, new Point(x0 + (slot - labelText.Width) / 2, plotBottom + 4));
            var subText = Text(sub, 10, DimTextBrush);
            dc.DrawText(subText, new Point(x0 + (slot - subText.Width) / 2, plotBottom + 4 + labelText.Height));
        }
    }

    private FormattedText Text(string text, double size, Brush brush, Typeface? typeface = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface ?? UiTypeface, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
