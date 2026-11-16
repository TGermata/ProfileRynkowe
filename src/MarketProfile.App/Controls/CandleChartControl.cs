using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MarketProfile.App.Settings;
using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>
/// Wykres szerszej perspektywy (świece dzienne, tygodniowe albo miesięczne) rysowany ręcznie w <see cref="OnRender"/> –
/// w tym samym stylu co wykres profili.
///  - świece japońskie albo słupki OHLC, wolumen w dolnej części, średnie kroczące (SMA) z ustawień,
///  - oś cen po prawej, oś dat na dole (podziałka dobierana do szerokości świec),
///  - skala pozioma: kółko (wokół kursora) albo przyciski −/+; skala pionowa: automatyczna (do widocznych świec)
///    albo ręczna – przeciągnięcie osi cen, Shift+kółko lub kółko nad osią cen; 2× klik na osi cen = skala automatyczna,
///  - przeciągnięcie wykresu – przesuwanie (przy ręcznej skali także w pionie), 2× klik – najnowsze świece,
///  - krzyż kursora i opis świecy pod kursorem (OHLC, zmiana, wolumen, wartości SMA) u góry wykresu.
/// </summary>
public sealed class CandleChartControl : FrameworkElement
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(IReadOnlyList<Bar>), typeof(CandleChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) =>
            ((CandleChartControl)d).OnBarsChanged((IReadOnlyList<Bar>?)e.OldValue, (IReadOnlyList<Bar>?)e.NewValue)));

    /// <summary>Świece w kolejności czasu.</summary>
    public IReadOnlyList<Bar>? Bars
    {
        get => (IReadOnlyList<Bar>?)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public static readonly DependencyProperty IntervalProperty = DependencyProperty.Register(
        nameof(Interval), typeof(BarInterval), typeof(CandleChartControl),
        new FrameworkPropertyMetadata(BarInterval.Day, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Interwał świec (podziałka osi dat i opis świecy).</summary>
    public BarInterval Interval
    {
        get => (BarInterval)GetValue(IntervalProperty);
        set => SetValue(IntervalProperty, value);
    }

    public static readonly DependencyProperty TickSizeProperty = DependencyProperty.Register(
        nameof(TickSize), typeof(decimal), typeof(CandleChartControl),
        new FrameworkPropertyMetadata(0.25m, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Wielkość ticka instrumentu – liczba miejsc po przecinku cen.</summary>
    public decimal TickSize
    {
        get => (decimal)GetValue(TickSizeProperty);
        set => SetValue(TickSizeProperty, value);
    }

    public static readonly DependencyProperty AppearanceProperty = DependencyProperty.Register(
        nameof(Appearance), typeof(CandleChartAppearance), typeof(CandleChartControl),
        new FrameworkPropertyMetadata(CandleChartAppearance.Default, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Rodzaj prezentacji, kolory, wolumen i średnie kroczące (z ustawień).</summary>
    public CandleChartAppearance Appearance
    {
        get => (CandleChartAppearance)GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(CandleChartControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Tekst na środku wykresu, gdy nie ma świec.</summary>
    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    // --- Układ (piksele) ---
    private const double AxisWidth = 75;
    private const double InfoHeight = 26;
    private const double DateAxisHeight = 22;
    private const double VolumeShare = 0.18;
    private const double RightPadSlots = 3;      // wolne miejsce za najnowszą świecą
    private const double MinSlot = 2, MaxSlot = 80, DefaultSlot = 9;
    private const double ZoomStep = 1.15;

    // --- Kolory interfejsu (jak wykres profili); kolory świec i SMA – z Appearance ---
    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)));
    private static readonly Brush HeaderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x29)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)));
    private static readonly Brush DimTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x88, 0x94)));
    private static readonly Brush ManualScaleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x30)));
    private static readonly Brush CrosshairLabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x60, 0x6A)));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x2E)), 1));
    private static readonly Pen SeparatorPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3A)), 1));
    private static readonly Pen CrosshairPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x70, 0x78, 0x84)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Typeface MonoTypeface = new("Consolas");

    // --- Stan widoku ---
    private double _slot = DefaultSlot;   // szerokość miejsca na jedną świecę
    private double _endIndex;             // indeks świecy (ułamkowy) przy prawej krawędzi obszaru świec
    private bool _pinned = true;          // najnowsza świeca przy prawej krawędzi (nowe świece przesuwają widok)
    private bool _fitPending = true;      // nowa seria – przy małej liczbie świec rozciągnąć je na szerokość wykresu

    // Skala pionowa: automatyczna (do widocznych świec) albo ręczna (_top/_bottom ustawione przez użytkownika).
    private bool _manualScale;
    private double _top, _bottom;

    // Geometria z ostatniego rysowania – do przeliczeń myszy.
    private double _shownTop = 1, _shownBottom;
    private double _plotTop, _priceBottom = 1;

    // Przeciąganie: wykresu (przesuwanie) albo osi cen (skala pionowa).
    private enum DragMode { None, Pan, Axis }
    private DragMode _drag;
    private Point _dragStart;
    private double _dragEndIndex, _dragTop, _dragBottom;
    private Point? _mouse;

    // Średnie kroczące policzone dla bieżących świec (okres → wartości).
    private readonly Dictionary<int, decimal?[]> _smaCache = [];

    public CandleChartControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Najnowsze świece przy prawej krawędzi, domyślna szerokość świec i automatyczna skala pionowa.</summary>
    public void FitLatest()
    {
        _slot = DefaultSlot;
        _pinned = true;
        _manualScale = false;
        _fitPending = true;
        InvalidateVisual();
    }

    /// <summary>Szersze świece (mniej świec na ekranie).</summary>
    public void ZoomIn() => ZoomHorizontal(ZoomStep, PlotRight);

    /// <summary>Węższe świece (więcej świec na ekranie).</summary>
    public void ZoomOut() => ZoomHorizontal(1 / ZoomStep, PlotRight);

    /// <summary>Automatyczna skala pionowa (dopasowana do widocznych świec).</summary>
    public void AutoScale()
    {
        _manualScale = false;
        InvalidateVisual();
    }

    private void OnBarsChanged(IReadOnlyList<Bar>? oldBars, IReadOnlyList<Bar>? newBars)
    {
        _smaCache.Clear();

        // Inny zestaw świec (interwał, instrument) – od najnowszych, skala automatyczna; dopisanie świec – widok zostaje.
        var sameSeries = oldBars is { Count: > 0 } && newBars is { Count: > 0 } && newBars[0].Time == oldBars[0].Time;
        if (!sameSeries)
        {
            _pinned = true;
            _manualScale = false;
            _fitPending = true;
        }
    }

    private double PlotRight => Math.Max(0, ActualWidth - AxisWidth);

    private decimal?[] Sma(IReadOnlyList<Bar> bars, int period)
    {
        if (!_smaCache.TryGetValue(period, out var values))
        {
            values = Indicators.Sma(bars, period);
            _smaCache[period] = values;
        }

        return values;
    }

    // =====================================================================
    // Rysowanie
    // =====================================================================

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        var bars = Bars;
        var plotRight = PlotRight;
        if (bars is null || bars.Count == 0 || plotRight < 50 || height < InfoHeight + DateAxisHeight + 40)
        {
            var empty = CreateText(EmptyText ?? string.Empty, 14, DimTextBrush, UiTypeface);
            dc.DrawText(empty, new Point((width - empty.Width) / 2, (height - empty.Height) / 2));
            return;
        }

        var style = Appearance ?? CandleChartAppearance.Default;
        if (_fitPending)
        {
            // Mało świec (np. miesięczne) – rozciągnięte na całą szerokość zamiast skupione przy prawej krawędzi.
            _fitPending = false;
            _slot = Math.Clamp(plotRight / (bars.Count + RightPadSlots + 1), DefaultSlot, 30);
        }

        if (_pinned)
        {
            _endIndex = bars.Count - 1 + RightPadSlots;
        }

        // Obszary: opis u góry, świece, wolumen, oś dat na dole.
        var plotTop = InfoHeight;
        var plotBottom = height - DateAxisHeight;
        var hasVolume = style.ShowVolume && bars.Any(b => b.Volume > 0);
        var volumeHeight = hasVolume ? (plotBottom - plotTop) * VolumeShare : 0;
        var priceBottom = plotBottom - volumeHeight - (hasVolume ? 6 : 0);

        // Widoczne świece.
        var first = Math.Max(0, (int)Math.Floor(IndexAt(0)));
        var last = Math.Min(bars.Count - 1, (int)Math.Ceiling(IndexAt(plotRight)));
        if (first > last)
        {
            first = last = Math.Clamp(first, 0, bars.Count - 1);
        }

        var averages = style.MovingAverages.Select(m => (Line: m, Values: Sma(bars, m.Period))).ToList();

        // Skala pionowa: automatyczna – widoczne świece i średnie z marginesem; ręczna – ustawiona przez użytkownika.
        double top, bottom;
        long maxVolume = 1;
        for (var i = first; i <= last; i++)
        {
            maxVolume = Math.Max(maxVolume, bars[i].Volume);
        }

        if (_manualScale)
        {
            (top, bottom) = (_top, _bottom);
        }
        else
        {
            decimal low = decimal.MaxValue, high = decimal.MinValue;
            for (var i = first; i <= last; i++)
            {
                low = Math.Min(low, bars[i].Low);
                high = Math.Max(high, bars[i].High);
                foreach (var (_, values) in averages)
                {
                    if (values[i] is { } v)
                    {
                        low = Math.Min(low, v);
                        high = Math.Max(high, v);
                    }
                }
            }

            if (high <= low)
            {
                high = low + Math.Max(TickSize, 1m);
            }

            var pad = (high - low) * 0.06m;
            top = (double)(high + pad);
            bottom = (double)(low - pad);
        }

        (_shownTop, _shownBottom, _plotTop, _priceBottom) = (top, bottom, plotTop, priceBottom);
        double PriceY(decimal price) => plotTop + ((top - (double)price) / (top - bottom)) * (priceBottom - plotTop);

        // Siatka i oś cen.
        dc.DrawRectangle(HeaderBrush, null, new Rect(plotRight, 0, AxisWidth, height));
        DrawPriceGrid(dc, plotRight, plotTop, priceBottom, bottom, top);
        var scaleLabel = CreateText(_manualScale ? "skala ręczna" : "skala auto", 10,
            _manualScale ? ManualScaleBrush : DimTextBrush, UiTypeface);
        dc.DrawText(scaleLabel, new Point(plotRight + (AxisWidth - scaleLabel.Width) / 2, (InfoHeight - scaleLabel.Height) / 2));

        // Oś dat (linie podziałki pod świecami).
        dc.DrawRectangle(HeaderBrush, null, new Rect(0, plotBottom, width, DateAxisHeight));
        DrawDateAxis(dc, bars, first, last, plotTop, plotBottom);

        // Wolumen.
        if (hasVolume)
        {
            dc.DrawLine(SeparatorPen, new Point(0, priceBottom + 3), new Point(plotRight, priceBottom + 3));
            for (var i = first; i <= last; i++)
            {
                var bar = bars[i];
                var h = volumeHeight * bar.Volume / maxVolume;
                var (x, w) = BodySpan(i);
                dc.DrawRectangle(bar.Close >= bar.Open ? style.UpVolumeBrush : style.DownVolumeBrush, null, new Rect(x, plotBottom - h, w, h));
            }
        }

        // Świece / słupki OHLC – przycięte do obszaru cen (przy ręcznej skali mogą wychodzić poza niego).
        dc.PushClip(new RectangleGeometry(new Rect(0, plotTop, plotRight, priceBottom - plotTop)));
        var thick = style.Type == CandleChartType.OhlcBars && _slot >= 14;
        for (var i = first; i <= last; i++)
        {
            var bar = bars[i];
            var up = bar.Close >= bar.Open;
            var center = Math.Round(XOf(i)) + 0.5;

            if (style.Type == CandleChartType.OhlcBars)
            {
                var pen = up ? (thick ? style.UpThickPen : style.UpPen) : (thick ? style.DownThickPen : style.DownPen);
                var tick = Math.Max(2, Math.Round(_slot * 0.35));
                dc.DrawLine(pen, new Point(center, PriceY(bar.High)), new Point(center, PriceY(bar.Low)));
                var yOpen = Math.Round(PriceY(bar.Open)) + 0.5;
                var yClose = Math.Round(PriceY(bar.Close)) + 0.5;
                dc.DrawLine(pen, new Point(center - tick, yOpen), new Point(center, yOpen));
                dc.DrawLine(pen, new Point(center, yClose), new Point(center + tick, yClose));
            }
            else
            {
                dc.DrawLine(up ? style.UpPen : style.DownPen, new Point(center, PriceY(bar.High)), new Point(center, PriceY(bar.Low)));
                var (x, w) = BodySpan(i);
                var yOpen = PriceY(bar.Open);
                var yClose = PriceY(bar.Close);
                dc.DrawRectangle(up ? style.UpBrush : style.DownBrush, null,
                    new Rect(x, Math.Min(yOpen, yClose), w, Math.Max(1, Math.Abs(yClose - yOpen))));
            }
        }

        // Średnie kroczące – linie przez środki świec (z jedną świecą zapasu po obu stronach, żeby linia dochodziła do krawędzi).
        foreach (var (line, values) in averages)
        {
            DrawLine(dc, line.Pen, values, Math.Max(0, first - 1), Math.Min(bars.Count - 1, last + 1), PriceY);
        }

        dc.Pop();

        // Ostatnia cena.
        var lastBar = bars[^1];
        var lastY = PriceY(lastBar.Close);
        var lastBrush = lastBar.Close >= lastBar.Open ? style.UpBrush : style.DownBrush;
        if (lastY >= plotTop && lastY <= priceBottom)
        {
            dc.DrawLine(new Pen(lastBrush, 1) { DashStyle = DashStyles.Dash }, new Point(0, lastY), new Point(plotRight, lastY));
            DrawAxisLabel(dc, plotRight, lastY, FormatPrice(lastBar.Close), lastBrush);
        }

        // Krzyż kursora i opis świecy.
        var hovered = bars.Count - 1;
        if (_mouse is { } m && m.X < plotRight && m.Y >= plotTop && m.Y < plotBottom && _drag == DragMode.None)
        {
            hovered = Math.Clamp((int)Math.Round(IndexAt(m.X)), 0, bars.Count - 1);
            var cx = Math.Round(XOf(hovered)) + 0.5;
            dc.DrawLine(CrosshairPen, new Point(cx, plotTop), new Point(cx, plotBottom));
            dc.DrawLine(CrosshairPen, new Point(0, Math.Round(m.Y) + 0.5), new Point(plotRight, Math.Round(m.Y) + 0.5));
            if (m.Y <= priceBottom)
            {
                DrawAxisLabel(dc, plotRight, m.Y, FormatPrice((decimal)PriceAt(m.Y)), CrosshairLabelBrush, TextBrush);
            }

            var dateText = CreateText(FormatDate(bars[hovered].Time, Interval), 11, Brushes.Black, UiTypeface);
            var dx = Math.Clamp(cx - dateText.Width / 2 - 4, 0, Math.Max(0, plotRight - dateText.Width - 8));
            dc.DrawRectangle(TextBrush, null, new Rect(dx, plotBottom + 2, dateText.Width + 8, DateAxisHeight - 4));
            dc.DrawText(dateText, new Point(dx + 4, plotBottom + (DateAxisHeight - dateText.Height) / 2));
        }

        DrawInfo(dc, bars, hovered, style, averages, plotRight);
    }

    /// <summary>Linia wartości (np. SMA) od świecy <paramref name="from"/> do <paramref name="to"/>; brak wartości przerywa linię.</summary>
    private void DrawLine(DrawingContext dc, Pen pen, decimal?[] values, int from, int to, Func<decimal, double> priceY)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var open = false;
            for (var i = from; i <= to; i++)
            {
                if (values[i] is not { } value)
                {
                    open = false;
                    continue;
                }

                var point = new Point(XOf(i), priceY(value));
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

    /// <summary>Linie siatki i podpisy cen co „okrągłą” wartość (~co 50 px).</summary>
    private void DrawPriceGrid(DrawingContext dc, double plotRight, double plotTop, double priceBottom, double bottom, double top)
    {
        var step = NiceStep((top - bottom) / Math.Max(1, (priceBottom - plotTop) / 50));
        var tick = (double)Math.Max(TickSize, 0.00000001m);
        step = Math.Max(tick, Math.Round(step / tick) * tick);

        for (var price = Math.Ceiling(bottom / step) * step; price <= top; price += step)
        {
            var y = Math.Round(plotTop + (top - price) / (top - bottom) * (priceBottom - plotTop)) + 0.5;
            if (y < plotTop || y > priceBottom)
            {
                continue;
            }

            dc.DrawLine(GridPen, new Point(0, y), new Point(plotRight, y));
            dc.DrawLine(SeparatorPen, new Point(plotRight, y), new Point(plotRight + 4, y));
            var label = CreateText(FormatPrice((decimal)price), 11, DimTextBrush, UiTypeface);
            dc.DrawText(label, new Point(plotRight + 8, y - label.Height / 2));
        }
    }

    /// <summary>„Okrągły” krok podziałki: 1, 2, 2,5 albo 5 × 10ⁿ, nie mniejszy niż <paramref name="raw"/>.</summary>
    internal static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw))
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var factor in new[] { 1, 2, 2.5, 5, 10 })
        {
            if (factor * magnitude >= raw)
            {
                return factor * magnitude;
            }
        }

        return 10 * magnitude;
    }

    /// <summary>
    /// Oś dat: podpis przy pierwszej świecy nowego okresu (miesiąc, kwartał, rok albo 5 lat) –
    /// najkrótszy okres, przy którym podpisy są co najmniej ~70 px od siebie.
    /// </summary>
    private void DrawDateAxis(DrawingContext dc, IReadOnlyList<Bar> bars, int first, int last, double plotTop, double plotBottom)
    {
        // Przybliżona liczba świec w miesiącu dla interwału.
        var barsPerMonth = Interval switch
        {
            BarInterval.Day => 21.0,
            BarInterval.Week => 4.35,
            _ => 1.0,
        };

        var months = new[] { 1, 3, 12, 60 }.FirstOrDefault(n => n * barsPerMonth * _slot >= 70, 120);
        var previous = first > 0 ? (int?)PeriodKey(bars[first - 1].Time, months) : null;

        for (var i = first; i <= last; i++)
        {
            var key = PeriodKey(bars[i].Time, months);
            if (previous is { } p && key == p)
            {
                continue;
            }

            var isFirstVisible = previous is null;
            previous = key;
            if (isFirstVisible)
            {
                continue; // pierwsza widoczna świeca nie musi zaczynać okresu
            }

            var x = Math.Round(XOf(i)) + 0.5;
            dc.DrawLine(GridPen, new Point(x, plotTop), new Point(x, plotBottom));
            var date = bars[i].Time.UtcDateTime;
            var text = months >= 12 || date.Month == 1
                ? date.ToString("yyyy", CultureInfo.CurrentCulture)
                : date.ToString("MMM", CultureInfo.CurrentCulture);
            var label = CreateText(text, 11, date.Month == 1 ? TextBrush : DimTextBrush, UiTypeface);
            dc.DrawText(label, new Point(x - label.Width / 2, plotBottom + (DateAxisHeight - label.Height) / 2));
        }
    }

    /// <summary>Numer okresu daty (miesiące od roku 0 podzielone przez długość okresu).</summary>
    private static int PeriodKey(DateTimeOffset time, int months)
    {
        var date = time.UtcDateTime;
        return (date.Year * 12 + date.Month - 1) / months;
    }

    /// <summary>Opis świecy u góry: data, OHLC, zmiana względem poprzedniego zamknięcia, wolumen, wartości SMA.</summary>
    private void DrawInfo(DrawingContext dc, IReadOnlyList<Bar> bars, int index, CandleChartAppearance style,
        IReadOnlyList<(MovingAverageLine Line, decimal?[] Values)> averages, double plotRight)
    {
        var bar = bars[index];
        var change = index > 0 && bars[index - 1].Close != 0
            ? (bar.Close - bars[index - 1].Close) / bars[index - 1].Close * 100m
            : (decimal?)null;

        var x = 8.0;
        void Part(string text, Brush brush, double gap = 14)
        {
            var formatted = CreateText(text, 12, brush, MonoTypeface);
            if (x + formatted.Width > plotRight)
            {
                return; // brak miejsca – reszta opisu się nie mieści
            }

            dc.DrawText(formatted, new Point(x, (InfoHeight - formatted.Height) / 2));
            x += formatted.Width + gap;
        }

        var color = bar.Close >= bar.Open ? style.UpBrush : style.DownBrush;
        Part(FormatDate(bar.Time, Interval), TextBrush, 18);
        Part($"O {FormatPrice(bar.Open)}", color);
        Part($"H {FormatPrice(bar.High)}", color);
        Part($"L {FormatPrice(bar.Low)}", color);
        Part($"C {FormatPrice(bar.Close)}", color);
        if (change is { } c)
        {
            Part((c >= 0 ? "+" : "−") + Math.Abs(c).ToString("0.00", CultureInfo.CurrentCulture) + "%", c >= 0 ? style.UpBrush : style.DownBrush);
        }

        if (bar.Volume > 0)
        {
            Part($"Wol {bar.Volume.ToString("N0", CultureInfo.CurrentCulture)}", DimTextBrush, 18);
        }

        foreach (var (line, values) in averages)
        {
            Part($"SMA{line.Period} {(values[index] is { } v ? FormatPrice(RoundToTick(v)) : "–")}", line.Brush);
        }
    }

    private decimal RoundToTick(decimal price) =>
        TickSize > 0 ? Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize : price;

    private static string FormatDate(DateTimeOffset time, BarInterval interval)
    {
        var date = time.UtcDateTime;
        return interval switch
        {
            BarInterval.Month => date.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            BarInterval.Week => "tydz. " + date.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture),
            _ => date.ToString("ddd dd.MM.yyyy", CultureInfo.CurrentCulture),
        };
    }

    private void DrawAxisLabel(DrawingContext dc, double plotRight, double y, string text, Brush background, Brush? foreground = null)
    {
        var formatted = CreateText(text, 11, foreground ?? Brushes.Black, UiTypeface);
        dc.DrawRectangle(background, null, new Rect(plotRight + 2, y - formatted.Height / 2 - 1, AxisWidth - 4, formatted.Height + 2));
        dc.DrawText(formatted, new Point(plotRight + 8, y - formatted.Height / 2));
    }

    // =====================================================================
    // Indeks świecy <-> x, cena <-> y
    // =====================================================================

    /// <summary>Środek świecy o indeksie <paramref name="index"/>.</summary>
    private double XOf(double index) => PlotRight - (_endIndex - index) * _slot;

    /// <summary>Indeks (ułamkowy) świecy w punkcie x.</summary>
    private double IndexAt(double x) => _endIndex - (PlotRight - x) / _slot;

    /// <summary>Cena w punkcie y (według ostatniego rysowania).</summary>
    private double PriceAt(double y) =>
        _shownTop - (y - _plotTop) / Math.Max(1, _priceBottom - _plotTop) * (_shownTop - _shownBottom);

    /// <summary>Lewa krawędź i szerokość korpusu świecy (ok. 70% miejsca, co najmniej 1 px).</summary>
    private (double X, double Width) BodySpan(int index)
    {
        var width = Math.Max(1, Math.Floor(_slot * 0.7));
        if (width >= 3 && (int)width % 2 == 0)
        {
            width -= 1; // nieparzysta szerokość – knot dokładnie na środku
        }

        return (Math.Round(XOf(index)) + 0.5 - width / 2, width);
    }

    // =====================================================================
    // Skalowanie
    // =====================================================================

    /// <summary>Zmiana szerokości świec; świeca w punkcie <paramref name="x"/> zostaje w miejscu.</summary>
    private void ZoomHorizontal(double factor, double x)
    {
        if (Bars is not { Count: > 0 } bars)
        {
            return;
        }

        x = Math.Min(x, PlotRight);
        var anchor = IndexAt(x);
        var slot = Math.Clamp(_slot * factor, MinSlot, MaxSlot);
        if (slot == _slot)
        {
            return;
        }

        _slot = slot;
        if (!_pinned)
        {
            _endIndex = ClampEnd(anchor + (PlotRight - x) / _slot, bars.Count);
        }

        InvalidateVisual();
    }

    /// <summary>Zmiana skali pionowej (przejście na skalę ręczną); cena w punkcie <paramref name="y"/> zostaje w miejscu.</summary>
    private void ZoomVertical(double factor, double y)
    {
        var anchor = PriceAt(y);
        _top = anchor + (_shownTop - anchor) * factor;
        _bottom = anchor - (anchor - _shownBottom) * factor;
        _manualScale = true;
        InvalidateVisual();
    }

    // =====================================================================
    // Mysz
    // =====================================================================

    private bool OverPriceAxis(Point p) => p.X >= PlotRight && p.Y >= InfoHeight && p.Y <= ActualHeight - DateAxisHeight;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var position = e.GetPosition(this);

        if (e.ClickCount == 2)
        {
            // 2× klik na osi cen – skala automatyczna; na wykresie – najnowsze świece.
            if (OverPriceAxis(position))
            {
                AutoScale();
            }
            else
            {
                FitLatest();
            }

            e.Handled = true;
            return;
        }

        _drag = OverPriceAxis(position) ? DragMode.Axis : DragMode.Pan;
        _dragStart = position;
        _dragEndIndex = _endIndex;
        (_dragTop, _dragBottom) = (_shownTop, _shownBottom);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        _mouse = position;
        Cursor = _drag == DragMode.Axis || (_drag == DragMode.None && OverPriceAxis(position)) ? Cursors.SizeNS : null;

        if (_drag == DragMode.Pan && Bars is { Count: > 0 } bars)
        {
            var dx = position.X - _dragStart.X;
            if (Math.Abs(dx) >= 1)
            {
                _pinned = false;
                _endIndex = ClampEnd(_dragEndIndex - dx / _slot, bars.Count);
            }

            // Przy ręcznej skali przeciąganie przesuwa też w pionie.
            if (_manualScale)
            {
                var shift = (position.Y - _dragStart.Y) / Math.Max(1, _priceBottom - _plotTop) * (_dragTop - _dragBottom);
                _top = _dragTop + shift;
                _bottom = _dragBottom + shift;
            }
        }
        else if (_drag == DragMode.Axis)
        {
            // Oś cen: w dół – większy zakres cen (oddalenie), w górę – mniejszy (przybliżenie); środek zostaje.
            var factor = Math.Exp((position.Y - _dragStart.Y) * 0.008);
            var middle = (_dragTop + _dragBottom) / 2;
            var half = (_dragTop - _dragBottom) / 2 * factor;
            _top = middle + half;
            _bottom = middle - half;
            _manualScale = true;
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            return;
        }

        var wasPan = _drag == DragMode.Pan;
        _drag = DragMode.None;
        ReleaseMouseCapture();
        if (wasPan && Bars is { Count: > 0 } bars && _endIndex >= bars.Count - 1 + RightPadSlots - 0.5)
        {
            _pinned = true; // przeciągnięte z powrotem do najnowszych świec
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _mouse = null;
        InvalidateVisual();
    }

    /// <summary>
    /// Kółko: szerokość świec wokół kursora. Shift+kółko albo kółko nad osią cen: skala pionowa wokół ceny pod kursorem.
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Bars is not { Count: > 0 })
        {
            return;
        }

        var position = e.GetPosition(this);
        if (OverPriceAxis(position) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ZoomVertical(e.Delta > 0 ? 1 / ZoomStep : ZoomStep, Math.Clamp(position.Y, _plotTop, _priceBottom));
        }
        else
        {
            ZoomHorizontal(e.Delta > 0 ? ZoomStep : 1 / ZoomStep, position.X);
        }

        e.Handled = true;
    }

    /// <summary>Ogranicza przesunięcie: co najmniej kilka świec widocznych, nie dalej niż wolne miejsce za najnowszą.</summary>
    private double ClampEnd(double end, int count)
    {
        var visible = PlotRight / _slot;
        return Math.Clamp(end, Math.Min(visible * 0.3, count - 1), count - 1 + RightPadSlots);
    }

    // =====================================================================
    // Pomocnicze
    // =====================================================================

    private string FormatPrice(decimal price) => PriceFormat.Format(price, TickSize);

    private FormattedText CreateText(string text, double size, Brush brush, Typeface typeface) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
