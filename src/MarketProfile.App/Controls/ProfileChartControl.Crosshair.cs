using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>
/// Kursor z liniami (włączany z panelu narzędzi albo klawiszem C): pozioma linia z ceną na osi cen i pionowa linia
/// z profilem oraz słupkiem TPO (litera i godziny) pod kursorem – etykieta na dole wykresu.
///
/// Kursor jest rysowany na osobnej warstwie (<see cref="DrawingVisual"/> nad wykresem), więc ruch myszy przerysowuje
/// tylko dwie linie i etykiety, a nie cały wykres z profilami.
/// </summary>
public sealed partial class ProfileChartControl
{
    public static readonly DependencyProperty CrosshairEnabledProperty = DependencyProperty.Register(
        nameof(CrosshairEnabled), typeof(bool), typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((ProfileChartControl)d).OnCrosshairEnabledChanged()));

    /// <summary>Czy pokazywać kursor z liniami ceny i czasu.</summary>
    public bool CrosshairEnabled
    {
        get => (bool)GetValue(CrosshairEnabledProperty);
        set => SetValue(CrosshairEnabledProperty, value);
    }

    private static readonly Brush CrosshairBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0xC8, 0xCD, 0xD4)));
    private static readonly Pen CrosshairPen = Frozen(new Pen(CrosshairBrush, 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush CrosshairLabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)));

    private DrawingVisual? _crosshairLayer;
    private Point? _cursorPosition;   // położenie myszy nad wykresem (null = mysz poza wykresem)

    /// <summary>Warstwa kursora – tworzona przy pierwszym użyciu, rysowana nad wykresem.</summary>
    private DrawingVisual CrosshairLayer
    {
        get
        {
            if (_crosshairLayer is null)
            {
                _crosshairLayer = new DrawingVisual();
                AddVisualChild(_crosshairLayer);
            }

            return _crosshairLayer;
        }
    }

    protected override int VisualChildrenCount => _crosshairLayer is null ? 0 : 1;

    protected override Visual GetVisualChild(int index) =>
        _crosshairLayer ?? throw new ArgumentOutOfRangeException(nameof(index));

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _cursorPosition = null;
        UpdateCrosshair();
    }

    private void OnCrosshairEnabledChanged()
    {
        Cursor = CrosshairEnabled ? Cursors.Cross : null;
        UpdateCrosshair();
    }

    /// <summary>Zapamiętuje położenie myszy i przerysowuje kursor (wywoływane przy ruchu myszy).</summary>
    private void TrackCursor(Point position)
    {
        _cursorPosition = position;
        UpdateCrosshair();
    }

    /// <summary>Przerysowuje warstwę kursora (po ruchu myszy i po każdym przerysowaniu wykresu).</summary>
    private void UpdateCrosshair()
    {
        if (_crosshairLayer is null && !CrosshairEnabled)
        {
            return; // kursora nigdy nie włączono – nie tworzymy warstwy
        }

        using var dc = CrosshairLayer.RenderOpen();
        var chartRight = ActualWidth - AxisWidth;
        if (!CrosshairEnabled || !_hasData || _cursorPosition is not { } p ||
            p.X < 0 || p.X > chartRight || p.Y < HeaderHeight || p.Y > ActualHeight)
        {
            return;
        }

        // Linie na pełnych pikselach (ostre).
        var x = Math.Round(p.X) + 0.5;
        var y = Math.Round(p.Y) + 0.5;
        dc.DrawLine(CrosshairPen, new Point(0, y), new Point(chartRight, y));
        dc.DrawLine(CrosshairPen, new Point(x, HeaderHeight), new Point(x, ActualHeight));

        // Cena pod kursorem (z dokładnością do ticka) – etykieta na osi cen.
        DrawAxisLabel(dc, chartRight, y, YToTickPrice(p.Y), CrosshairLabelBrush);

        // Profil i słupek TPO pod kursorem – etykieta na dole, przy pionowej linii.
        if (CrosshairTimeText(p) is { } text)
        {
            var label = CreateText(text, 11, Brushes.Black, UiTypeface);
            var width = label.Width + 10;
            var left = Math.Clamp(x - width / 2, 0, Math.Max(0, chartRight - width));
            var top = ActualHeight - label.Height - 4;
            dc.DrawRectangle(CrosshairLabelBrush, null, new Rect(left, top - 1, width, label.Height + 2));
            dc.DrawText(label, new Point(left + 5, top));
        }
    }

    /// <summary>
    /// Opis miejsca pod kursorem: profil (sesja i dzień) oraz – gdy kursor wskazuje literę (albo kolumnę okresu w trybie
    /// Split) – słupek TPO z godzinami, np. „Dzienna 16.11  J 14:00–14:30”. Null = poza profilami.
    /// </summary>
    private string? CrosshairTimeText(Point point)
    {
        if (HitTestColumn(point) is not { } column)
        {
            return null;
        }

        var profile = column.Profile;
        TpoPeriod? period = null;
        if (HitTestLetter(point) is { } hit)
        {
            period = profile.Periods.FirstOrDefault(p => p.Index == hit.Period);
        }
        else if (column.IsExpanded && _letterPx > 0)
        {
            // Tryb Split: kolumna liter = okres, także między literami.
            var index = profile.FirstPeriod + (int)Math.Floor((point.X - column.LettersX) / _letterPx);
            period = profile.Periods.FirstOrDefault(p => p.Index == index);
        }

        var name = $"{profile.Session.Name} {profile.TradingDate:dd.MM}";
        return period is null ? name : $"{name}   {period.Letter}  {period.Start:HH:mm}–{period.End:HH:mm}";
    }
}
