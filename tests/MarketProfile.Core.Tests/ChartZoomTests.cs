using MarketProfile.App.Controls;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Testy obliczeń powiększania wykresu: skalowanie stałych elementów, poziom szczegółów
/// i utrzymanie punktu pod kursorem w miejscu.
/// </summary>
public class ChartZoomTests
{
    [Theory]
    [InlineData(10, 1)]      // domyślna szerokość litery – pełna wielkość elementów
    [InlineData(25, 1)]      // przybliżenie – nie powiększamy odstępów ponad normę
    [InlineData(5, 0.5)]     // oddalenie – elementy proporcjonalnie mniejsze
    [InlineData(0.5, 0.25)]  // duże oddalenie – nie mniej niż 25%
    public void Compact_SkalujeStaleElementyZPowiekszeniem(double letterPx, double expected) =>
        Assert.Equal(expected, ChartZoom.Compact(letterPx), 3);

    [Theory]
    [InlineData(10, 14, ChartDetail.Letters)]
    [InlineData(4, 14, ChartDetail.Cells)]    // wąskie litery → bloki
    [InlineData(10, 5, ChartDetail.Cells)]    // niskie wiersze → bloki
    [InlineData(1, 3, ChartDetail.Rows)]      // bardzo duże oddalenie → paski
    public void Detail_ZaleznyOdPowiekszenia(double letterPx, double rowPx, ChartDetail expected) =>
        Assert.Equal(expected, ChartZoom.Detail(letterPx, rowPx));

    [Fact]
    public void Kotwica_PoziomPunktZostajePodKursorem()
    {
        // Kursor na X=500 wskazywał środek kolumny (ułamek 0.5). Po powiększeniu kolumna ma X=420, szerokość 200,
        // więc jej środek jest na 520 – przesunięcie musi się zmniejszyć o 20 px.
        var pan = ChartZoom.PanForAnchor(currentPan: 100, cursorX: 500, columnX: 420, columnWidth: 200, fraction: 0.5);
        Assert.Equal(80, pan);
        Assert.Equal(500, (420 - 20) + 0.5 * 200); // po korekcie środek kolumny wypada pod kursorem
    }

    [Fact]
    public void Kotwica_PionCenaZostajePodKursorem()
    {
        // Wiersz = 1 punkt, kursor 100 px nad środkiem wykresu, pod kursorem cena 6010.
        // Przy wierszu 20 px środek ekranu musi mieć cenę 6010 − 100/20 = 6005.
        var center = ChartZoom.CenterForAnchor(6010m, cursorY: 200, middleY: 300, rowPx: 20, rowHeight: 1m);
        Assert.Equal(6005m, center);
    }
}
