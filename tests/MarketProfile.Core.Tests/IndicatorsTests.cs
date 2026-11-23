using MarketProfile.Core;

namespace MarketProfile.Core.Tests;

/// <summary>Wskaźniki na świecach (średnie kroczące).</summary>
public sealed class IndicatorsTests
{
    private static IReadOnlyList<Bar> Closes(params decimal[] closes) =>
        closes.Select((c, i) => new Bar(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i), c, c, c, c, 0)).ToList();

    [Fact]
    public void Sma_SredniaZamkniec_BrakWartosciNaPoczatku()
    {
        var sma = Indicators.Sma(Closes(10, 11, 12, 13, 14.5m), 3);

        Assert.Equal(5, sma.Length);
        Assert.Null(sma[0]);
        Assert.Null(sma[1]);
        Assert.Equal(11m, sma[2]);
        Assert.Equal(12m, sma[3]);
        Assert.Equal(13.166666666666666666666666667m, sma[4]!.Value, 20);
    }

    [Fact]
    public void Sma_Okres1_ToZamkniecia_ZaMaloSwiec_SameNulle()
    {
        Assert.Equal([10m, 11m], Indicators.Sma(Closes(10, 11), 1).Select(v => v!.Value));
        Assert.All(Indicators.Sma(Closes(10, 11), 5), v => Assert.Null(v));
        Assert.Throws<ArgumentOutOfRangeException>(() => Indicators.Sma(Closes(10), 0));
    }

    [Fact]
    public void Ema_StartZSma_PotemWygladzanieWykladnicze()
    {
        // Okres 3: k = 0,5. Start: SMA(10, 11, 12) = 11; potem 11 + 0,5 × (13 − 11) = 12; 12 + 0,5 × (16 − 12) = 14.
        var ema = Indicators.Ema(Closes(10, 11, 12, 13, 16), 3);

        Assert.Equal(5, ema.Length);
        Assert.Null(ema[0]);
        Assert.Null(ema[1]);
        Assert.Equal(11m, ema[2]);
        Assert.Equal(12m, ema[3]);
        Assert.Equal(14m, ema[4]);
    }

    [Fact]
    public void Ema_Okres1_ToZamkniecia_ZaMaloSwiec_SameNulle()
    {
        Assert.Equal([10m, 11m, 9m], Indicators.Ema(Closes(10, 11, 9), 1).Select(v => v!.Value));
        Assert.All(Indicators.Ema(Closes(10, 11), 5), v => Assert.Null(v));
        Assert.Empty(Indicators.Ema(Closes(), 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Indicators.Ema(Closes(10), 0));
    }

    [Fact]
    public void MovingAverage_WybieraRodzaj()
    {
        var bars = Closes(10, 11, 12, 13, 16);
        Assert.Equal(Indicators.Sma(bars, 3), Indicators.MovingAverage(bars, MovingAverageKind.Sma, 3));
        Assert.Equal(Indicators.Ema(bars, 3), Indicators.MovingAverage(bars, MovingAverageKind.Ema, 3));
    }

    [Fact]
    public void SmaToday_PoprzednieZamknieciaPlusBiezacaCena_BezDzisiejszejIPozniejszych()
    {
        // Świece 1–5.01.2026 (zamknięcia 10..14); "dziś" = 4.01 → świece 4.01 i 5.01 pomijane.
        var bars = Closes(10, 11, 12, 13, 14);
        var today = new DateOnly(2026, 1, 4);

        Assert.Equal((11m + 12m + 20m) / 3, Indicators.SmaToday(bars, today, 3, 20m));
        Assert.Equal(20m, Indicators.SmaToday(bars, today, 1, 20m));
        Assert.Equal((10m + 11m + 12m + 20m) / 4, Indicators.SmaToday(bars, today, 4, 20m));
        Assert.Null(Indicators.SmaToday(bars, today, 5, 20m)); // tylko 3 świece przed dziś
    }
}

/// <summary>Rozpoznawanie niepełnych świec (sama cena zamknięcia, bez wolumenu).</summary>
public sealed class BarIncompleteTests
{
    private static readonly DateTimeOffset T = new(2023, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NiepelnaSwieca_TylkoGdyBezZakresuIBezWolumenu()
    {
        Assert.True(new Bar(T, 4000m, 4000m, 4000m, 4000m, 0).IsIncomplete());
        Assert.False(new Bar(T, 4000m, 4010m, 3990m, 4005m, 0).IsIncomplete());   // indeks: bez wolumenu, ale z zakresem
        Assert.False(new Bar(T, 4000m, 4000m, 4000m, 4000m, 12).IsIncomplete()); // płaska, ale z handlem
    }

    [Fact]
    public void PelneSwiece_SeriaZWolumenem_PomijaSwieceBezWolumenu_IndeksBezZakresu()
    {
        IReadOnlyList<Bar> futures =
        [
            new(T, 4540.54m, 4540.54m, 4540.27m, 4540.54m, 0),          // minimalny zakres, bez wolumenu – niepełna
            new(T.AddMonths(1), 4600m, 4700m, 4550m, 4650m, 1000),
        ];
        Assert.Equal([T.AddMonths(1)], BarExtensions.CompleteOnly(futures).Select(b => b.Time));

        // Odległy, mało płynny kontrakt: kilkadziesiąt kontraktów zamiast milionów – niepełna.
        IReadOnlyList<Bar> thin =
        [
            new(T, 4808m, 5110m, 4808m, 5097m, 28),
            new(T.AddMonths(1), 6145m, 6265m, 5858m, 6248m, 13_653_313),
            new(T.AddMonths(2), 6240m, 6369m, 6151m, 6166m, 27_225_986),
            new(T.AddMonths(3), 6170m, 6512m, 6151m, 6503m, 9_000_000),   // niższy wolumen (np. święta) – pełna
        ];
        Assert.Equal(3, BarExtensions.CompleteOnly(thin).Count);
        Assert.DoesNotContain(BarExtensions.CompleteOnly(thin), b => b.Volume == 28);

        IReadOnlyList<Bar> index =
        [
            new(T, 4000m, 4000m, 4000m, 4000m, 0),
            new(T.AddDays(1), 4000m, 4010m, 3990m, 4005m, 0),
        ];
        Assert.Equal([T.AddDays(1)], BarExtensions.CompleteOnly(index).Select(b => b.Time));
    }
}
