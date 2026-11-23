using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>
/// Bieżący widok wykresu świecowego (<see cref="CandleChartControl"/>): które świece są widoczne i gdzie leżą.
/// Przekazywany do wierszy wskaźników (<see cref="IndicatorPaneControl"/>), żeby ich słupki i linie leżały dokładnie
/// pod świecami – przewijanie i zoom wykresu przesuwają też wiersze.
/// </summary>
/// <param name="Bars">Świece wykresu.</param>
/// <param name="Interval">Interwał świec.</param>
/// <param name="Slot">Szerokość miejsca na jedną świecę (px).</param>
/// <param name="EndIndex">Indeks świecy (ułamkowy) przy prawej krawędzi obszaru świec.</param>
/// <param name="First">Pierwsza widoczna świeca.</param>
/// <param name="Last">Ostatnia widoczna świeca.</param>
/// <param name="Hover">Świeca pod kursorem (null – kursor poza wykresem).</param>
/// <param name="Zone">Strefa czasowa opisów świec wewnątrzdziennych (null – UTC).</param>
public sealed record CandleViewport(
    IReadOnlyList<Bar> Bars, BarInterval Interval, double Slot, double EndIndex, int First, int Last, int? Hover,
    TimeZoneInfo? Zone = null)
{
    /// <summary>Szerokość osi wartości po prawej – wspólna dla wykresu i wierszy wskaźników (kolumny świec się pokrywają).</summary>
    public const double AxisWidth = 75;

    /// <summary>Środek świecy o indeksie <paramref name="index"/> przy prawej krawędzi obszaru <paramref name="plotRight"/>.</summary>
    public double XOf(double index, double plotRight) => plotRight - (EndIndex - index) * Slot;
}

/// <summary>Oś dat wykresów świecowych (wspólna dla wykresu i dolnego wiersza wskaźników).</summary>
internal static class CandleDateAxis
{
    /// <summary>
    /// Pionowe linie siatki i podpisy przy pierwszej świecy nowego okresu (miesiąc, kwartał, rok albo 5 lat) –
    /// najkrótszy okres, przy którym podpisy są co najmniej ~70 px od siebie.
    /// </summary>
    /// <param name="labelTop">Górna krawędź paska podpisów (null – tylko linie siatki, bez podpisów).</param>
    public static void Draw(DrawingContext dc, IReadOnlyList<Bar> bars, int first, int last, BarInterval interval, double slot,
        Func<int, double> xOf, double gridTop, double gridBottom, double? labelTop, double labelHeight,
        Pen gridPen, Brush textBrush, Brush dimTextBrush, Func<string, Brush, FormattedText> createText, TimeZoneInfo? zone = null)
    {
        if (interval.IsIntraday())
        {
            DrawIntraday(dc, bars, first, last, slot, xOf, gridTop, gridBottom, labelTop, labelHeight, gridPen, textBrush,
                dimTextBrush, createText, zone ?? TimeZoneInfo.Utc);
            return;
        }

        var barsPerMonth = interval switch
        {
            BarInterval.Day => 21.0,
            BarInterval.Week => 4.35,
            _ => 1.0,
        };

        var months = new[] { 1, 3, 12, 60 }.FirstOrDefault(n => n * barsPerMonth * slot >= 70, 120);
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

            var x = Math.Round(xOf(i)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, gridTop), new Point(x, gridBottom));
            if (labelTop is not { } top)
            {
                continue;
            }

            var date = bars[i].Time.UtcDateTime;
            var text = months >= 12 || date.Month == 1
                ? date.ToString("yyyy", CultureInfo.CurrentCulture)
                : date.ToString("MMM", CultureInfo.CurrentCulture);
            var label = createText(text, date.Month == 1 ? textBrush : dimTextBrush);
            dc.DrawText(label, new Point(x - label.Width / 2, top + (labelHeight - label.Height) / 2));
        }
    }

    /// <summary>
    /// Oś dat świec wewnątrzdziennych (sesja kasowa: ok. 13 świec 30 min na dzień): podpis przy pierwszej świecy dnia,
    /// a przy gęstym widoku – tygodnia, miesiąca albo roku (najkrótszy okres z podpisami co najmniej ~70 px od siebie).
    /// </summary>
    private static void DrawIntraday(DrawingContext dc, IReadOnlyList<Bar> bars, int first, int last, double slot,
        Func<int, double> xOf, double gridTop, double gridBottom, double? labelTop, double labelHeight,
        Pen gridPen, Brush textBrush, Brush dimTextBrush, Func<string, Brush, FormattedText> createText, TimeZoneInfo zone)
    {
        const double barsPerDay = 13;
        var step = new[] { 1, 5, 21, 252 }.FirstOrDefault(days => days * barsPerDay * slot >= 70, 1260);

        DateTime LocalDate(int i) => TimeZoneInfo.ConvertTime(bars[i].Time, zone).Date;

        long Key(DateTime date) => step switch
        {
            1 => date.Ticks,
            5 => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)).Ticks,      // tydzień od poniedziałku
            21 => date.Year * 12 + date.Month,
            252 => date.Year,
            _ => date.Year / 5,
        };

        var previous = first > 0 ? (long?)Key(LocalDate(first - 1)) : null;
        for (var i = first; i <= last; i++)
        {
            var date = LocalDate(i);
            var key = Key(date);
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

            var x = Math.Round(xOf(i)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, gridTop), new Point(x, gridBottom));
            if (labelTop is not { } top)
            {
                continue;
            }

            var text = step switch
            {
                1 => date.ToString("ddd dd.MM", CultureInfo.CurrentCulture),
                5 => date.ToString("dd.MM", CultureInfo.CurrentCulture),
                21 => date.Month == 1 ? date.ToString("yyyy", CultureInfo.CurrentCulture) : date.ToString("MMM", CultureInfo.CurrentCulture),
                _ => date.ToString("yyyy", CultureInfo.CurrentCulture),
            };
            var strong = step == 1 ? date.DayOfWeek == DayOfWeek.Monday : step == 5 ? date.Day <= 7 : date.Month == 1;
            var label = createText(text, strong ? textBrush : dimTextBrush);
            dc.DrawText(label, new Point(x - label.Width / 2, top + (labelHeight - label.Height) / 2));
        }
    }

    /// <summary>Data świecy w opisie (dzień, tydzień albo miesiąc; świeca wewnątrzdzienna – z godziną w strefie <paramref name="zone"/>).</summary>
    public static string FormatDate(DateTimeOffset time, BarInterval interval, TimeZoneInfo? zone = null)
    {
        if (interval.IsIntraday())
        {
            return TimeZoneInfo.ConvertTime(time, zone ?? TimeZoneInfo.Utc).ToString("ddd dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
        }

        var date = time.UtcDateTime;
        return interval switch
        {
            BarInterval.Month => date.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            BarInterval.Week => "tydz. " + date.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture),
            _ => date.ToString("ddd dd.MM.yyyy", CultureInfo.CurrentCulture),
        };
    }

    /// <summary>Numer okresu daty (miesiące od roku 0 podzielone przez długość okresu).</summary>
    private static int PeriodKey(DateTimeOffset time, int months)
    {
        var date = time.UtcDateTime;
        return (date.Year * 12 + date.Month - 1) / months;
    }
}
