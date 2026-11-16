using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>
/// Słupki porównania wolumenu okresów TPO (<see cref="VolumeComparisonResult"/>), rysowane w <see cref="OnRender"/>:
///  - górna część: dla każdej litery para słupków – bieżąca sesja (niebieski) i poprzednia sesja (szary);
///    okres w trakcie wyróżniony ramką, okresy przyszłe – tylko słupek poprzedniej sesji (co „przed nami”),
///  - dolna część: różnica dziś − poprzednia sesja od linii zera (zielona = więcej dziś, czerwona = mniej),
///  - najechanie myszą: opis okresu (godziny, wolumeny, różnica, %).
/// </summary>
public sealed class VolumeCompareChartControl : FrameworkElement
{
    public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(
        nameof(Result), typeof(VolumeComparisonResult), typeof(VolumeCompareChartControl),
        new FrameworkPropertyMetadata(VolumeComparisonResult.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Porównanie do narysowania.</summary>
    public VolumeComparisonResult Result
    {
        get => (VolumeComparisonResult)GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(VolumeCompareChartControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Zaznaczony okres (−1 = brak) – kliknięcie słupków zaznacza okres, zaznaczenie z tabeli jest wyróżnione.</summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    private const double LeftAxis = 70;
    private const double Top = 30;          // miejsce na legendę / opis najechanego okresu
    private const double LetterHeight = 36;   // litera + godzina okresu
    private const double DiffShare = 0.32;  // część wysokości na słupki różnicy
    private const double Gap = 14;

    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)));
    private static readonly Brush DimTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x88, 0x94)));
    private static readonly Brush TodayBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xA3, 0xF7)));
    private static readonly Brush PreviousBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x6E)));
    private static readonly Brush FuturePreviousBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x5A, 0x62, 0x6E)));
    private static readonly Brush UpBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0xC0, 0x70)));
    private static readonly Brush DownBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)));
    private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush SelectedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x4F, 0xA3, 0xF7)));
    private static readonly Pen SelectedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0xA3, 0xF7)), 1.5));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x2E)), 1));
    private static readonly Pen ZeroPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x50, 0x58, 0x64)), 1));
    private static readonly Pen CurrentPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x30)), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Typeface MonoTypeface = new("Consolas");

    private int? _hovered;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        var rows = Result?.Rows ?? [];
        if (rows.Count == 0 || width < 200 || height < 150)
        {
            var empty = Text("Brak danych do porównania", 14, DimTextBrush);
            dc.DrawText(empty, new Point((width - empty.Width) / 2, (height - empty.Height) / 2));
            return;
        }

        // Układ: [oś] słupki par | linia liter | słupki różnicy.
        var plotLeft = LeftAxis;
        var plotRight = width - 12;
        var barsBottom = Top + (height - Top - LetterHeight - Gap) * (1 - DiffShare);
        var lettersTop = barsBottom;
        var diffTop = lettersTop + LetterHeight + Gap;
        var diffBottom = height - 8;
        var slot = (plotRight - plotLeft) / rows.Count;

        var max = Math.Max(1, rows.Max(r => Math.Max(r.Today ?? 0, r.Previous)));
        var maxDiff = Math.Max(1, rows.Max(r => Math.Abs(r.Difference ?? 0)));

        // Siatka i oś wolumenu (górna część).
        var step = NiceStep(max / 4.0);
        for (var v = step; v <= max; v += step)
        {
            var y = Math.Round(barsBottom - (barsBottom - Top) * v / max) + 0.5;
            dc.DrawLine(GridPen, new Point(plotLeft, y), new Point(plotRight, y));
            var label = Text(VolumeFormat.Short((long)v), 11, DimTextBrush);
            dc.DrawText(label, new Point(plotLeft - label.Width - 6, y - label.Height / 2));
        }

        // Oś różnicy (dolna część): zero i ± maksimum.
        var zeroY = Math.Round((diffTop + diffBottom) / 2) + 0.5;
        var halfDiff = (diffBottom - diffTop) / 2;
        dc.DrawLine(ZeroPen, new Point(plotLeft, zeroY), new Point(plotRight, zeroY));
        foreach (var (value, y) in new[] { (maxDiff, diffTop), (0L, zeroY), (-maxDiff, diffBottom) })
        {
            var label = Text((value > 0 ? "+" : string.Empty) + VolumeFormat.Short(value), 11, DimTextBrush);
            dc.DrawText(label, new Point(plotLeft - label.Width - 6, y - label.Height / 2));
        }

        var diffTitle = Text("różnica dziś − poprzednia sesja", 11, DimTextBrush);
        dc.DrawText(diffTitle, new Point(plotLeft + 4, diffTop - diffTitle.Height - 1));

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var x0 = plotLeft + i * slot;
            if (i == SelectedIndex)
            {
                dc.DrawRectangle(SelectedBrush, SelectedPen, new Rect(x0 + 0.5, Top, slot - 1, diffBottom - Top));
            }
            else if (_hovered == i)
            {
                dc.DrawRectangle(HoverBrush, null, new Rect(x0, Top, slot, diffBottom - Top));
            }

            // Para słupków: poprzednia sesja (lewy) i dziś (prawy).
            var barWidth = Math.Max(1, Math.Floor(slot * 0.36));
            var pairLeft = x0 + (slot - 2 * barWidth - 2) / 2;
            var previousHeight = (barsBottom - Top) * row.Previous / max;
            dc.DrawRectangle(row.Today is null ? FuturePreviousBrush : PreviousBrush, null,
                new Rect(pairLeft, barsBottom - previousHeight, barWidth, previousHeight));
            if (row.Today is { } today)
            {
                var todayHeight = (barsBottom - Top) * today / max;
                dc.DrawRectangle(TodayBrush, null, new Rect(pairLeft + barWidth + 2, barsBottom - todayHeight, barWidth, todayHeight));
            }

            if (row.IsCurrent)
            {
                dc.DrawRectangle(null, CurrentPen, new Rect(x0 + 1, Top, slot - 2, diffBottom - Top));
            }

            // Litera okresu i pod nią godzina: początek okresu, a przy szerokich słupkach – cały przedział.
            // Przy wąskich słupkach godzina co drugi (co czwarty) okres, żeby podpisy się nie nakładały.
            var letter = Text(row.Letter.ToString(), 13, row.Today is null ? DimTextBrush : TextBrush, MonoTypeface);
            dc.DrawText(letter, new Point(x0 + (slot - letter.Width) / 2, lettersTop + 1));
            var every = slot >= 36 ? 1 : slot >= 18 ? 2 : 4;
            if (i % every == 0 || i == SelectedIndex)
            {
                var hourText = slot >= 84 ? $"{row.Start:HH:mm}–{row.End:HH:mm}" : $"{row.Start:HH:mm}";
                var hour = Text(hourText, 10, i == SelectedIndex ? TextBrush : DimTextBrush);
                dc.DrawText(hour, new Point(x0 + (slot - hour.Width) / 2, lettersTop + letter.Height + 1));
            }

            // Słupek różnicy.
            if (row.Difference is { } diff && diff != 0)
            {
                var h = halfDiff * Math.Abs(diff) / maxDiff;
                var w = Math.Max(1, Math.Floor(slot * 0.6));
                var x = x0 + (slot - w) / 2;
                dc.DrawRectangle(diff > 0 ? UpBrush : DownBrush, null,
                    diff > 0 ? new Rect(x, zeroY - h, w, h) : new Rect(x, zeroY, w, h));

                if (slot >= 46)
                {
                    var percent = row.DifferencePercent is { } p
                        ? (p > 0 ? "+" : "−") + Math.Abs(p).ToString("0", CultureInfo.CurrentCulture) + "%"
                        : string.Empty;
                    var label = Text(percent, 10, diff > 0 ? UpBrush : DownBrush);
                    var ly = diff > 0 ? zeroY - h - label.Height - 1 : zeroY + h + 1;
                    dc.DrawText(label, new Point(x0 + (slot - label.Width) / 2, Math.Clamp(ly, diffTop - 2, diffBottom - label.Height)));
                }
            }
        }

        DrawHeader(dc, rows, plotLeft);
    }

    /// <summary>Legenda albo opis najechanego okresu u góry.</summary>
    private void DrawHeader(DrawingContext dc, IReadOnlyList<VolumeComparisonRow> rows, double left)
    {
        var x = left;
        void Part(string text, Brush brush, double gap = 16)
        {
            var formatted = Text(text, 12, brush, MonoTypeface);
            dc.DrawText(formatted, new Point(x, (Top - formatted.Height) / 2));
            x += formatted.Width + gap;
        }

        if ((_hovered ?? (SelectedIndex >= 0 ? SelectedIndex : null)) is { } i && i < rows.Count)
        {
            var row = rows[i];
            Part($"{row.Letter}  {row.Start:HH:mm}–{row.End:HH:mm}" + (row.IsCurrent ? $" (do {row.Minutes} min)" : string.Empty), TextBrush, 22);
            Part($"dziś {(row.Today is { } t ? VolumeFormat.Long(t) : "–")}", TodayBrush);
            Part($"poprzednia {VolumeFormat.Long(row.Previous)}", TextBrush);
            if (row.Difference is { } d)
            {
                var brush = d >= 0 ? UpBrush : DownBrush;
                Part($"różnica {(d > 0 ? "+" : d < 0 ? "−" : string.Empty)}{VolumeFormat.Long(Math.Abs(d))}", brush);
                if (row.DifferencePercent is { } p)
                {
                    Part((p > 0 ? "+" : p < 0 ? "−" : string.Empty) + Math.Abs(p).ToString("0.0", CultureInfo.CurrentCulture) + "%", brush);
                }
            }

            return;
        }

        dc.DrawRectangle(TodayBrush, null, new Rect(x, Top / 2 - 5, 10, 10));
        x += 14;
        Part("bieżąca sesja", TextBrush);
        dc.DrawRectangle(PreviousBrush, null, new Rect(x, Top / 2 - 5, 10, 10));
        x += 14;
        Part("poprzednia sesja", TextBrush);
        Part("┆ okres w trakcie – porównanie do tej samej minuty", DimTextBrush);
    }

    /// <summary>Okres pod punktem x (null = poza słupkami).</summary>
    private int? IndexAt(double x)
    {
        var rows = Result?.Rows ?? [];
        var slot = (ActualWidth - 12 - LeftAxis) / Math.Max(1, rows.Count);
        return rows.Count > 0 && x >= LeftAxis && x < ActualWidth - 12 ? Math.Min(rows.Count - 1, (int)((x - LeftAxis) / slot)) : null;
    }

    /// <summary>Kliknięcie słupków zaznacza okres (także wiersz w tabeli); kliknięcie zaznaczonego – odznacza.</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IndexAt(e.GetPosition(this).X) is { } index)
        {
            SelectedIndex = index == SelectedIndex ? -1 : index;
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hovered = IndexAt(e.GetPosition(this).X);
        Cursor = hovered is null ? null : Cursors.Hand;
        if (hovered != _hovered)
        {
            _hovered = hovered;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hovered = null;
        InvalidateVisual();
    }

    private static double NiceStep(double raw) => CandleChartControl.NiceStep(raw);

    private FormattedText Text(string text, double size, Brush brush, Typeface? typeface = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface ?? UiTypeface, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
