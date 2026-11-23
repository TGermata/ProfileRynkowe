using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>Sposób rysowania serii wskaźnika.</summary>
public enum IndicatorSeriesKind
{
    /// <summary>Linia przez środki świec.</summary>
    Line,

    /// <summary>Słupki od zera (np. oscylator, wolumen, przewaga wzrostów).</summary>
    Histogram,
}

/// <summary>
/// Seria wskaźnika do narysowania w wierszu: wartości dla kolejnych świec wykresu (ten sam indeks co świeca;
/// null – brak wartości), kolor i sposób rysowania.
/// </summary>
public sealed record IndicatorSeries(string Name, IReadOnlyList<decimal?> Values, Brush Brush, IndicatorSeriesKind Kind = IndicatorSeriesKind.Line,
    double Thickness = 1.5, DashStyle? DashStyle = null);

/// <summary>
/// Wiersz wskaźnika nad albo pod wykresem świecowym (zakładka „Szeroki rynek”). Kolumny świec pokrywają się z wykresem:
/// widok (przewinięcie, zoom, świeca pod kursorem) przychodzi z <see cref="CandleChartControl.ViewportChanged"/>.
/// Wiersz bez serii pokazuje tylko tytuł i siatkę – miejsce przygotowane na przyszły wskaźnik.
/// Najniższy wiersz może rysować oś dat (<see cref="ShowDateAxis"/>) zamiast wykresu.
/// </summary>
public sealed class IndicatorPaneControl : FrameworkElement
{
    public static readonly DependencyProperty ViewportProperty = DependencyProperty.Register(
        nameof(Viewport), typeof(CandleViewport), typeof(IndicatorPaneControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Widok wykresu świecowego, z którym wiersz jest zgrany (null – brak świec).</summary>
    public CandleViewport? Viewport
    {
        get => (CandleViewport?)GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(IndicatorPaneControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Nazwa wiersza (lewy górny róg).</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<IndicatorSeries>), typeof(IndicatorPaneControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Serie wskaźnika (puste – wiersz wolny).</summary>
    public IReadOnlyList<IndicatorSeries>? Series
    {
        get => (IReadOnlyList<IndicatorSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public static readonly DependencyProperty ShowDateAxisProperty = DependencyProperty.Register(
        nameof(ShowDateAxis), typeof(bool), typeof(IndicatorPaneControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Oś dat na dole wiersza (najniższy wiersz pod wykresem).</summary>
    public bool ShowDateAxis
    {
        get => (bool)GetValue(ShowDateAxisProperty);
        set => SetValue(ShowDateAxisProperty, value);
    }

    private const double AxisWidth = CandleViewport.AxisWidth;
    private const double DateAxisBand = 22;
    private const double TitleHeight = 18;

    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)));
    private static readonly Brush HeaderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x29)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)));
    private static readonly Brush DimTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x88, 0x94)));
    private static readonly Brush FaintTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0x50, 0x5A)));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x2E)), 1));
    private static readonly Pen SeparatorPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3A)), 1));
    private static readonly Pen ZeroPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x50, 0x58, 0x64)), 1));
    private static readonly Pen CrosshairPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x70, 0x78, 0x84)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Typeface UiTypeface = new("Segoe UI");

    public IndicatorPaneControl()
    {
        ClipToBounds = true;
        IsHitTestVisible = false; // przewijanie i zoom – na wykresie świecowym
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        var plotRight = Math.Max(0, width - AxisWidth);
        var dateBand = ShowDateAxis ? DateAxisBand : 0;
        var plotBottom = Math.Max(0, height - dateBand);
        dc.DrawRectangle(HeaderBrush, null, new Rect(plotRight, 0, AxisWidth, height));
        dc.DrawLine(SeparatorPen, new Point(0, 0.5), new Point(width, 0.5));
        if (ShowDateAxis)
        {
            dc.DrawRectangle(HeaderBrush, null, new Rect(0, plotBottom, width, dateBand));
        }

        var viewport = Viewport;
        var series = Series ?? [];
        var hasData = viewport is { Bars.Count: > 0 } && series.Count > 0;

        // Wartości osi: zakres widocznych wartości wszystkich serii (słupki – od zera).
        double top = 1, bottom = 0;
        var valueTop = TitleHeight + 2;
        var valueBottom = plotBottom - 4;
        if (hasData)
        {
            (top, bottom) = VisibleRange(viewport!, series);
        }

        double ValueY(decimal value) => valueTop + (top - (double)value) / (top - bottom) * Math.Max(1, valueBottom - valueTop);

        if (viewport is { Bars.Count: > 0 } vp)
        {
            // Pionowa siatka dat – te same linie co na wykresie świecowym.
            CandleDateAxis.Draw(dc, vp.Bars, vp.First, vp.Last, vp.Interval, vp.Slot, i => vp.XOf(i, plotRight), 1, plotBottom,
                ShowDateAxis ? plotBottom : null, dateBand, GridPen, TextBrush, DimTextBrush,
                (text, brush) => CreateText(text, 11, brush), vp.Zone);
        }

        if (hasData)
        {
            DrawValueGrid(dc, plotRight, valueTop, valueBottom, bottom, top);
            if (bottom < 0 && top > 0)
            {
                var zero = Math.Round(ValueY(0)) + 0.5;
                dc.DrawLine(ZeroPen, new Point(0, zero), new Point(plotRight, zero));
            }

            dc.PushClip(new RectangleGeometry(new Rect(0, TitleHeight, plotRight, Math.Max(0, plotBottom - TitleHeight))));
            foreach (var s in series)
            {
                DrawSeries(dc, viewport!, s, plotRight, ValueY);
            }

            dc.Pop();
        }
        else
        {
            var free = CreateText("miejsce na wskaźnik", 11, FaintTextBrush);
            if (plotBottom - TitleHeight > free.Height)
            {
                dc.DrawText(free, new Point((plotRight - free.Width) / 2, TitleHeight + (plotBottom - TitleHeight - free.Height) / 2));
            }
        }

        // Pionowa linia kursora (świeca pod kursorem na wykresie) i data na osi dat.
        if (viewport is { Hover: { } hover } hv)
        {
            var cx = Math.Round(hv.XOf(hover, plotRight)) + 0.5;
            dc.DrawLine(CrosshairPen, new Point(cx, 1), new Point(cx, plotBottom));
            if (ShowDateAxis)
            {
                var dateText = CreateText(CandleDateAxis.FormatDate(hv.Bars[hover].Time, hv.Interval, hv.Zone), 11, Brushes.Black);
                var dx = Math.Clamp(cx - dateText.Width / 2 - 4, 0, Math.Max(0, plotRight - dateText.Width - 8));
                dc.DrawRectangle(TextBrush, null, new Rect(dx, plotBottom + 2, dateText.Width + 8, dateBand - 4));
                dc.DrawText(dateText, new Point(dx + 4, plotBottom + (dateBand - dateText.Height) / 2));
            }
        }

        DrawTitle(dc, viewport, series, plotRight);
    }

    /// <summary>Tytuł wiersza i wartości serii dla świecy pod kursorem (albo ostatniej).</summary>
    private void DrawTitle(DrawingContext dc, CandleViewport? viewport, IReadOnlyList<IndicatorSeries> series, double plotRight)
    {
        var x = 8.0;
        var title = CreateText(string.IsNullOrWhiteSpace(Title) ? "Wskaźnik (wolny)" : Title, 11, DimTextBrush);
        dc.DrawText(title, new Point(x, 2));
        x += title.Width + 14;

        if (viewport is not { Bars.Count: > 0 } vp)
        {
            return;
        }

        var index = vp.Hover ?? vp.Bars.Count - 1;
        foreach (var s in series)
        {
            var value = index < s.Values.Count ? s.Values[index] : null;
            var text = CreateText($"{s.Name} {(value is { } v ? FormatValue(v) : "–")}", 11, s.Brush);
            if (x + text.Width > plotRight)
            {
                return;
            }

            dc.DrawText(text, new Point(x, 2));
            x += text.Width + 12;
        }
    }

    private static (double Top, double Bottom) VisibleRange(CandleViewport viewport, IReadOnlyList<IndicatorSeries> series)
    {
        decimal low = decimal.MaxValue, high = decimal.MinValue;
        var histogram = false;
        foreach (var s in series)
        {
            histogram |= s.Kind == IndicatorSeriesKind.Histogram;
            for (var i = Math.Max(0, viewport.First); i <= viewport.Last && i < s.Values.Count; i++)
            {
                if (s.Values[i] is { } v)
                {
                    low = Math.Min(low, v);
                    high = Math.Max(high, v);
                }
            }
        }

        if (low > high)
        {
            return (1, 0);
        }

        if (histogram)
        {
            low = Math.Min(low, 0);
            high = Math.Max(high, 0);
        }

        if (high <= low)
        {
            high = low + 1;
        }

        var pad = (high - low) * 0.08m;
        return ((double)(high + pad), (double)(low - pad));
    }

    private static void DrawSeries(DrawingContext dc, CandleViewport viewport, IndicatorSeries series, double plotRight, Func<decimal, double> valueY)
    {
        var from = Math.Max(0, viewport.First - 1);
        var to = Math.Min(Math.Min(viewport.Bars.Count, series.Values.Count) - 1, viewport.Last + 1);
        if (series.Kind == IndicatorSeriesKind.Histogram)
        {
            var width = Math.Max(1, Math.Floor(viewport.Slot * 0.7));
            var zero = valueY(0);
            for (var i = from; i <= to; i++)
            {
                if (series.Values[i] is not { } v)
                {
                    continue;
                }

                var y = valueY(v);
                var x = Math.Round(viewport.XOf(i, plotRight)) + 0.5 - width / 2;
                dc.DrawRectangle(series.Brush, null, new Rect(x, Math.Min(y, zero), width, Math.Max(1, Math.Abs(zero - y))));
            }

            return;
        }

        var pen = new Pen(series.Brush, series.Thickness) { DashStyle = series.DashStyle ?? DashStyles.Solid, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var open = false;
            for (var i = from; i <= to; i++)
            {
                if (series.Values[i] is not { } v)
                {
                    open = false;
                    continue;
                }

                var point = new Point(viewport.XOf(i, plotRight), valueY(v));
                if (!open)
                {
                    context.BeginFigure(point, false, false);
                    open = true;
                }
                else
                {
                    context.LineTo(point, true, true);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawValueGrid(DrawingContext dc, double plotRight, double valueTop, double valueBottom, double bottom, double top)
    {
        var step = CandleChartControl.NiceStep((top - bottom) / Math.Max(1, (valueBottom - valueTop) / 28));
        for (var value = Math.Ceiling(bottom / step) * step; value <= top; value += step)
        {
            var y = Math.Round(valueTop + (top - value) / (top - bottom) * (valueBottom - valueTop)) + 0.5;
            if (y < valueTop || y > valueBottom)
            {
                continue;
            }

            dc.DrawLine(GridPen, new Point(0, y), new Point(plotRight, y));
            var label = CreateText(FormatValue((decimal)value), 10, DimTextBrush);
            dc.DrawText(label, new Point(plotRight + 8, y - label.Height / 2));
        }
    }

    private static string FormatValue(decimal value) =>
        Math.Abs(value) >= 1000 ? value.ToString("N0", CultureInfo.CurrentCulture) : value.ToString("0.##", CultureInfo.CurrentCulture);

    private FormattedText CreateText(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, UiTypeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
