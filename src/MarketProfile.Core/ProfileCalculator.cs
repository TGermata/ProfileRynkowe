namespace MarketProfile.Core;

/// <summary>
/// Obliczenia statystyk profilu: POC (Point of Control) i obszar wartości (Value Area).
/// Klasa jest bezstanowa – łatwo ją testować i podmieniać metodę liczenia w przyszłości.
/// </summary>
public static class ProfileCalculator
{
    /// <summary>
    /// Wyznacza POC: wiersz z największą liczbą TPO.
    /// Przy remisie wybieramy wiersz najbliższy środka zakresu profilu (standardowa reguła Market Profile).
    /// Gdy dwa wiersze są równo oddalone od środka (jeden poniżej, drugi powyżej), decyduje <paramref name="tieBreak"/>.
    /// </summary>
    /// <param name="levels">Wiersze posortowane rosnąco po cenie, bez przerw.</param>
    /// <param name="tieBreak">Reguła wyboru przy równej odległości od środka.</param>
    /// <returns>Indeks wiersza POC w liście lub -1, gdy lista jest pusta.</returns>
    public static int FindPocIndex(IReadOnlyList<PriceLevel> levels, PocTieBreak tieBreak = PocTieBreak.MoreTpoSide)
    {
        if (levels.Count == 0)
        {
            return -1;
        }

        var middle = (levels.Count - 1) / 2.0;
        var best = 0;
        bool? upperSideHasMore = null; // liczone leniwie – tylko gdy wystąpi remis odległości

        for (var i = 1; i < levels.Count; i++)
        {
            var count = levels[i].TpoCount;
            var bestCount = levels[best].TpoCount;

            if (count > bestCount)
            {
                best = i;
                continue;
            }

            if (count < bestCount)
            {
                continue;
            }

            var distance = Math.Abs(i - middle);
            var bestDistance = Math.Abs(best - middle);

            if (distance < bestDistance)
            {
                best = i;
            }
            else if (distance == bestDistance)
            {
                // best leży poniżej środka, i – powyżej (symetrycznie).
                var takeUpper = tieBreak switch
                {
                    PocTieBreak.Higher => true,
                    PocTieBreak.Lower => false,
                    _ => upperSideHasMore ??= UpperSideHasMoreTpo(levels, middle),
                };

                if (takeUpper)
                {
                    best = i;
                }
            }
        }

        return best;
    }

    /// <summary>Czy powyżej środka profilu jest więcej TPO niż poniżej (wiersz środkowy nie jest liczony).</summary>
    private static bool UpperSideHasMoreTpo(IReadOnlyList<PriceLevel> levels, double middle)
    {
        var below = 0;
        var above = 0;
        for (var i = 0; i < levels.Count; i++)
        {
            if (i < middle)
            {
                below += levels[i].TpoCount;
            }
            else if (i > middle)
            {
                above += levels[i].TpoCount;
            }
        }

        return above > below;
    }

    /// <summary>
    /// Wyznacza VPOC (Volume Point of Control): wiersz z największym wolumenem.
    /// Przy remisie – wiersz najbliższy środka zakresu.
    /// </summary>
    /// <returns>Indeks wiersza VPOC lub -1, gdy brak wolumenu.</returns>
    public static int FindVolumePocIndex(IReadOnlyList<PriceLevel> levels)
    {
        var middle = (levels.Count - 1) / 2.0;
        var best = -1;

        for (var i = 0; i < levels.Count; i++)
        {
            var volume = levels[i].Volume;
            if (volume <= 0)
            {
                continue;
            }

            if (best < 0 || volume > levels[best].Volume ||
                (volume == levels[best].Volume && Math.Abs(i - middle) < Math.Abs(best - middle)))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Wyznacza obszar wartości (Value Area):
    /// <list type="number">
    /// <item>Cel = zadany procent (np. 70%) wszystkich TPO profilu; od celu odejmujemy TPO wiersza POC.</item>
    /// <item>Sumujemy TPO dwóch wierszy powyżej i dwóch wierszy poniżej obecnego obszaru.</item>
    /// <item>Dołączamy parę z większą sumą i odejmujemy ją od pozostałych TPO.
    ///   Przy równych sumach – parę leżącą bliżej POC; gdy obie są równo daleko od POC – obie naraz.</item>
    /// <item>Powtarzamy, aż pozostałe TPO będą równe 0 lub lekko ujemne.</item>
    /// </list>
    /// Przy krawędzi profilu para może mieć 1 wiersz (lub 0 – wtedy rozszerzamy tylko w drugą stronę).
    /// </summary>
    /// <param name="levels">Wiersze posortowane rosnąco po cenie, bez przerw.</param>
    /// <param name="pocIndex">Indeks POC wyznaczony przez <see cref="FindPocIndex"/>.</param>
    /// <param name="valueAreaPercent">Udział TPO w obszarze wartości (0–1).</param>
    /// <returns>Indeksy dolnego i górnego wiersza obszaru wartości.</returns>
    public static (int LowIndex, int HighIndex) FindValueArea(
        IReadOnlyList<PriceLevel> levels, int pocIndex, decimal valueAreaPercent)
    {
        if (pocIndex < 0)
        {
            return (-1, -1);
        }

        // Ile TPO brakuje jeszcze do celu (np. 70% wszystkich TPO) – zaczynamy od odjęcia TPO wiersza POC.
        var total = levels.Sum(l => l.TpoCount);
        var remaining = total * valueAreaPercent - levels[pocIndex].TpoCount;

        var low = pocIndex;
        var high = pocIndex;

        while (remaining > 0)
        {
            // Ile wierszy możemy jeszcze dodać powyżej i poniżej obecnego obszaru (maksymalnie 2).
            var rowsAbove = Math.Min(2, levels.Count - 1 - high);
            var rowsBelow = Math.Min(2, low);

            if (rowsAbove == 0 && rowsBelow == 0)
            {
                break; // Cały profil jest już w obszarze wartości.
            }

            var sumAbove = SumTpo(levels, high + 1, rowsAbove);
            var sumBelow = SumTpo(levels, low - rowsBelow, rowsBelow);

            bool goUp;
            bool goDown;
            if (rowsBelow == 0 || rowsAbove == 0)
            {
                // Krawędź profilu – rozszerzamy tylko tam, gdzie są jeszcze wiersze.
                goUp = rowsAbove > 0;
                goDown = rowsBelow > 0;
            }
            else if (sumAbove != sumBelow)
            {
                // Para z większą sumą TPO.
                goUp = sumAbove > sumBelow;
                goDown = !goUp;
            }
            else
            {
                // Remis sum – wygrywa para bliższa POC (odległość najbliższego wiersza pary od POC).
                // Równa odległość – dołączamy obie pary naraz.
                var distanceAbove = high + 1 - pocIndex;
                var distanceBelow = pocIndex - (low - 1);
                goUp = distanceAbove <= distanceBelow;
                goDown = distanceBelow <= distanceAbove;
            }

            if (goUp)
            {
                high += rowsAbove;
                remaining -= sumAbove;
            }

            if (goDown)
            {
                low -= rowsBelow;
                remaining -= sumBelow;
            }
        }

        return (low, high);
    }

    /// <summary>Suma TPO w <paramref name="count"/> kolejnych wierszach począwszy od <paramref name="start"/>.</summary>
    private static int SumTpo(IReadOnlyList<PriceLevel> levels, int start, int count)
    {
        var sum = 0;
        for (var i = start; i < start + count; i++)
        {
            sum += levels[i].TpoCount;
        }

        return sum;
    }
}
