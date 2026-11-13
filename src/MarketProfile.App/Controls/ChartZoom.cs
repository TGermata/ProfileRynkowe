namespace MarketProfile.App.Controls;

/// <summary>Poziom szczegółów rysowania profili – zależy od powiększenia.</summary>
public enum ChartDetail
{
    /// <summary>Litery TPO (przybliżenie).</summary>
    Letters,

    /// <summary>Kolorowe bloki – po jednym na każdą literę (średnie oddalenie).</summary>
    Cells,

    /// <summary>Jeden pasek na wiersz (duże oddalenie – setki profili na ekranie).</summary>
    Rows,
}

/// <summary>
/// Obliczenia powiększenia wykresu (bez rysowania – łatwe do testów):
/// zakresy powiększenia, skalowanie stałych elementów kolumn, poziom szczegółów
/// i utrzymanie punktu pod kursorem w miejscu podczas powiększania.
/// </summary>
public static class ChartZoom
{
    /// <summary>Domyślna szerokość litery (przy niej stałe elementy kolumn mają pełną wielkość).</summary>
    public const double DefaultLetterPx = 10;

    public const double MinLetterPx = 0.3;
    public const double MaxLetterPx = 30;
    public const double MinRowPx = 1;
    public const double MaxRowPx = 40;

    /// <summary>Poniżej tych wielkości litery są nieczytelne – rysujemy bloki.</summary>
    public const double LettersMinLetterPx = 5;
    public const double LettersMinRowPx = 7;

    /// <summary>Poniżej tej szerokości nawet pojedyncze bloki są za małe – rysujemy jeden pasek na wiersz.</summary>
    public const double CellsMinLetterPx = 1.5;

    /// <summary>
    /// Współczynnik skalowania stałych elementów kolumny (odstępy, strzałki, profil wolumenowy, minimalna szerokość):
    /// 1 przy domyślnej szerokości litery i większej, proporcjonalnie mniej przy oddalaniu (najmniej 0.25).
    /// Dzięki temu oddalenie naprawdę mieści na ekranie dużo profili.
    /// </summary>
    public static double Compact(double letterPx) => Math.Clamp(letterPx / DefaultLetterPx, 0.25, 1);

    /// <summary>Poziom szczegółów dla danej szerokości litery i wysokości wiersza.</summary>
    public static ChartDetail Detail(double letterPx, double rowPx) =>
        letterPx >= LettersMinLetterPx && rowPx >= LettersMinRowPx ? ChartDetail.Letters
        : letterPx >= CellsMinLetterPx ? ChartDetail.Cells
        : ChartDetail.Rows;

    /// <summary>
    /// Nowe przesunięcie w poziomie, przy którym punkt kolumny (ułamek jej szerokości) wypada dokładnie
    /// pod kursorem – używane po zmianie powiększenia.
    /// </summary>
    public static double PanForAnchor(double currentPan, double cursorX, double columnX, double columnWidth, double fraction) =>
        currentPan + (cursorX - (columnX + fraction * columnWidth));

    /// <summary>
    /// Cena na środku ekranu, przy której cena spod kursora zostaje pod kursorem po zmianie wysokości wiersza.
    /// </summary>
    public static decimal CenterForAnchor(decimal priceUnderCursor, double cursorY, double middleY, double rowPx, decimal rowHeight) =>
        priceUnderCursor - (decimal)((middleY - cursorY) / rowPx) * rowHeight;
}
