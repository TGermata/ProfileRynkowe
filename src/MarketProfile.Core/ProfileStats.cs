namespace MarketProfile.Core;

/// <summary>Kształt profilu.</summary>
public enum ProfileShape
{
    /// <summary>Za mało danych, żeby ocenić kształt.</summary>
    None,

    /// <summary>„D” – równowaga: wartość i POC w środku zakresu, rozkład symetryczny.</summary>
    D,

    /// <summary>„P” – wartość i POC w górnej części zakresu, cienki ogon na dole (zwykle zamykanie krótkich pozycji).</summary>
    P,

    /// <summary>„b” – wartość i POC w dolnej części zakresu, cienki ogon u góry (zwykle likwidacja długich pozycji).</summary>
    LowerB,

    /// <summary>„B” – podwójny rozkład: dwa skupiska TPO rozdzielone przewężeniem.</summary>
    DoubleB,

    /// <summary>Trend – profil wąski i wydłużony (mało TPO na poziom ceny).</summary>
    Trend,
}

/// <summary>Gdzie otworzyła się sesja względem poprzedniego profilu tego samego typu.</summary>
public enum OpenLocation
{
    None,
    AboveRange,
    AboveValue,
    InValue,
    BelowValue,
    BelowRange,
}

/// <summary>Typ otwarcia (wg Dalton – na podstawie dwóch pierwszych liter).</summary>
public enum OpenType
{
    /// <summary>Jeszcze nie wiadomo (mniej niż dwie litery).</summary>
    None,

    /// <summary>Open-Drive – cena od otwarcia zdecydowanie w jedną stronę, bez powrotu do ceny otwarcia.</summary>
    OpenDrive,

    /// <summary>Open-Test-Drive – najpierw test w jedną stronę, potem zdecydowany ruch w drugą.</summary>
    OpenTestDrive,

    /// <summary>Open-Rejection-Reverse – ruch w jedną stronę odrzucony, cena wraca przez otwarcie na drugą stronę.</summary>
    OpenRejectionReverse,

    /// <summary>Open-Auction – rotacja wokół ceny otwarcia, bez wyraźnego kierunku.</summary>
    OpenAuction,
}

/// <summary>Migracja obszaru wartości względem poprzedniego profilu tego samego typu.</summary>
public enum ValueMigration
{
    None,
    Higher,
    OverlappingHigher,
    OverlappingLower,
    Lower,
    Inside,
    Outside,
}

/// <summary>Wybicie z Initial Balance.</summary>
public enum RangeExtension
{
    None,
    Up,
    Down,
    Both,
}

/// <summary>Zakres cen (np. single printy).</summary>
public sealed record PriceRange(decimal Low, decimal High);

/// <summary>
/// Parametry jednego profilu (sesji) do decyzji handlowych i automatycznego tradingu.
/// Ceny poziomów (POC, VAH, VAL, single printy) to dolne granice wierszy TPO – tak jak na wykresie.
/// </summary>
public sealed record ProfileStats
{
    // --- Identyfikacja ---
    public required string Session { get; init; }
    public required DateOnly TradingDate { get; init; }
    public required DateTime SessionStart { get; init; }
    public required DateTime SessionEnd { get; init; }

    /// <summary>Czy sesja jest zakończona (parametry są ostateczne).</summary>
    public bool IsComplete { get; init; }

    /// <summary>Liczba liter (okresów TPO) i ostatnia litera.</summary>
    public int Periods { get; init; }
    public char? LastLetter { get; init; }

    public decimal RowHeight { get; init; }
    public int TpoMinutes { get; init; }

    // --- 1. Podstawowe ---
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal Range { get; init; }
    public int TotalTpo { get; init; }
    public long Volume { get; init; }
    public decimal Poc { get; init; }
    public decimal? VolumePoc { get; init; }
    public decimal ValueAreaHigh { get; init; }
    public decimal ValueAreaLow { get; init; }

    /// <summary>Szerokość obszaru wartości (VAH − VAL + wysokość wiersza).</summary>
    public decimal ValueAreaWidth { get; init; }

    /// <summary>Położenie POC w zakresie sesji: 0 = na dole, 1 = na górze.</summary>
    public double PocPosition { get; init; }

    public decimal? IbHigh { get; init; }
    public decimal? IbLow { get; init; }
    public decimal? IbWidth { get; init; }

    /// <summary>O ile cena wyszła ponad IBH (0 = brak wybicia) i w której literze po raz pierwszy.</summary>
    public decimal IbExtensionUp { get; init; }
    public char? IbExtensionUpLetter { get; init; }

    /// <summary>O ile cena zeszła poniżej IBL (0 = brak wybicia) i w której literze po raz pierwszy.</summary>
    public decimal IbExtensionDown { get; init; }
    public char? IbExtensionDownLetter { get; init; }

    public RangeExtension RangeExtension { get; init; }

    // --- 2. Struktura ---
    public ProfileShape Shape { get; init; }

    /// <summary>Kształt jako krótki kod: D, P, b, B, Trend (pusty = brak oceny).</summary>
    public string ShapeCode => Shape switch
    {
        ProfileShape.D => "D",
        ProfileShape.P => "P",
        ProfileShape.LowerB => "b",
        ProfileShape.DoubleB => "B",
        ProfileShape.Trend => "Trend",
        _ => string.Empty,
    };

    public int TpoAbovePoc { get; init; }
    public int TpoBelowPoc { get; init; }

    /// <summary>Single printy wewnątrz profilu (co najmniej 2 wiersze z jedną literą, bez ogonów na krańcach).</summary>
    public IReadOnlyList<PriceRange> SinglePrints { get; init; } = [];

    /// <summary>Excess (ogon) u góry / na dole – liczba wierszy z jedną literą na krańcu (0 = brak, ogon to min. 2 wiersze).</summary>
    public int ExcessHigh { get; init; }
    public int ExcessLow { get; init; }

    /// <summary>Poor high / poor low – płaski szczyt / dołek (co najmniej 2 litery na skrajnym wierszu, bez ogona).</summary>
    public bool PoorHigh { get; init; }
    public bool PoorLow { get; init; }

    // --- 3. Relacje z poprzednim profilem tego samego typu ---
    public DateOnly? PreviousTradingDate { get; init; }
    public OpenLocation OpenLocation { get; init; }
    public OpenType OpenType { get; init; }

    /// <summary>Kierunek otwarcia dla OpenDrive / OpenTestDrive / OpenRejectionReverse: 1 = w górę, −1 = w dół, 0 = brak.</summary>
    public int OpenDirection { get; init; }

    public ValueMigration ValueMigration { get; init; }

    /// <summary>Jaka część obszaru wartości tej sesji pokrywa się z poprzednim (0–1; null = brak poprzedniego).</summary>
    public double? ValueAreaOverlap { get; init; }

    /// <summary>
    /// Nietknięte (naked) POC i VPOC poprzednich sesji tego samego typu – poziomy, do których cena (w żadnej
    /// późniejszej sesji, łącznie z tą) jeszcze nie wróciła. Najnowsze na końcu.
    /// </summary>
    public IReadOnlyList<decimal> NakedPocs { get; init; } = [];
    public IReadOnlyList<decimal> NakedVolumePocs { get; init; } = [];
}

/// <summary>Progi analizy profili.</summary>
public sealed record ProfileAnalysisOptions
{
    /// <summary>Ile poprzednich sesji przeszukiwać przy nietkniętych POC.</summary>
    public int NakedPocLookback { get; init; } = 20;

    public static ProfileAnalysisOptions Default { get; } = new();
}

/// <summary>
/// Wylicza <see cref="ProfileStats"/> z migawki profilu sesji i wcześniejszych profili.
/// Kształt, typ otwarcia i migracja wartości to reguły heurystyczne (opisane przy metodach) – powtarzalne, bez zgadywania.
/// </summary>
public static class ProfileAnalyzer
{
    /// <param name="profile">Pełny profil sesji (nie część rozbitego profilu).</param>
    /// <param name="history">Wcześniejsze profile (wszystkie typy sesji) w kolejności chronologicznej.</param>
    /// <param name="isComplete">Czy sesja jest zakończona.</param>
    /// <param name="tpoMinutes">Długość okresu TPO w minutach (zapisywana razem z parametrami).</param>
    public static ProfileStats Analyze(ProfileSnapshot profile, IReadOnlyList<ProfileSnapshot> history, bool isComplete,
        int tpoMinutes, ProfileAnalysisOptions? options = null)
    {
        options ??= ProfileAnalysisOptions.Default;
        var row = profile.RowHeight;
        var levels = profile.Levels;
        var pocIndex = IndexOf(levels, profile.Poc);
        var range = profile.High - profile.Low;

        var previous = history.LastOrDefault(h => h.Session.Name == profile.Session.Name && !h.IsEmpty && h.SessionStart < profile.SessionStart);

        var stats = new ProfileStats
        {
            Session = profile.Session.Name,
            TradingDate = profile.TradingDate,
            SessionStart = profile.SessionStart,
            SessionEnd = profile.SessionEnd,
            IsComplete = isComplete,
            Periods = profile.Periods.Count,
            LastLetter = profile.Periods.Count > 0 ? profile.Periods[^1].Letter : null,
            RowHeight = row,
            TpoMinutes = tpoMinutes,
            Open = profile.Open,
            High = profile.High,
            Low = profile.Low,
            Close = profile.LastPrice,
            Range = range,
            TotalTpo = profile.TotalTpo,
            Volume = profile.TotalVolume,
            Poc = profile.Poc,
            VolumePoc = profile.VolumePoc,
            ValueAreaHigh = profile.ValueAreaHigh,
            ValueAreaLow = profile.ValueAreaLow,
            ValueAreaWidth = profile.IsEmpty ? 0 : profile.ValueAreaHigh - profile.ValueAreaLow + row,
            PocPosition = levels.Count > 1 && pocIndex >= 0 ? Math.Round((double)pocIndex / (levels.Count - 1), 3) : 0.5,
            IbHigh = profile.InitialBalanceHigh,
            IbLow = profile.InitialBalanceLow,
            IbWidth = profile.InitialBalanceHigh - profile.InitialBalanceLow,
            Shape = ShapeOf(levels, pocIndex, profile.Periods.Count, range > 0 ? (double)((profile.LastPrice - profile.Low) / range) : 0.5),
            TpoAbovePoc = pocIndex >= 0 ? levels.Skip(pocIndex + 1).Sum(l => l.TpoCount) : 0,
            TpoBelowPoc = pocIndex >= 0 ? levels.Take(pocIndex).Sum(l => l.TpoCount) : 0,
            PreviousTradingDate = previous?.TradingDate,
        };

        stats = WithIbExtension(stats, profile);
        stats = WithTails(stats, levels);
        stats = WithOpen(stats, profile, previous);
        stats = WithValueMigration(stats, profile, previous);
        return WithNakedPocs(stats, profile, history, options);
    }

    private static int IndexOf(IReadOnlyList<PriceLevel> levels, decimal price)
    {
        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i].Price == price)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Wybicia z IB: o ile ponad IBH / poniżej IBL i pierwsza litera, która wyszła poza IB.</summary>
    private static ProfileStats WithIbExtension(ProfileStats stats, ProfileSnapshot profile)
    {
        if (profile.InitialBalanceHigh is not { } ibh || profile.InitialBalanceLow is not { } ibl)
        {
            return stats;
        }

        var up = Math.Max(0, profile.High - ibh);
        var down = Math.Max(0, ibl - profile.Low);
        return stats with
        {
            IbExtensionUp = up,
            IbExtensionUpLetter = profile.Periods.FirstOrDefault(p => p.High > ibh)?.Letter,
            IbExtensionDown = down,
            IbExtensionDownLetter = profile.Periods.FirstOrDefault(p => p.Low < ibl)?.Letter,
            RangeExtension = (up > 0, down > 0) switch
            {
                (true, true) => RangeExtension.Both,
                (true, false) => RangeExtension.Up,
                (false, true) => RangeExtension.Down,
                _ => RangeExtension.None,
            },
        };
    }

    /// <summary>
    /// Kształt (kolejność sprawdzania):
    ///  - B: dwa wyraźne skupiska TPO (wygładzone liczby TPO), przewężenie między nimi ≤ 50% mniejszego szczytu,
    ///    każde skupisko ma co najmniej 25% TPO;
    ///  - Trend: POC odwiedziło najwyżej 35% liter sesji (litery prawie się nie nakładają – profil wąski i wydłużony,
    ///    niezależnie od wysokości wiersza), a sesja zamyka się przy skraju zakresu (w zewnętrznych 25%);
    ///  - P / b: środek ciężkości TPO i POC w górnych / dolnych ~40% zakresu;
    ///  - w pozostałych przypadkach D.
    /// </summary>
    internal static ProfileShape ShapeOf(IReadOnlyList<PriceLevel> levels, int pocIndex, int periods, double closePosition)
    {
        var n = levels.Count;
        var total = levels.Sum(l => l.TpoCount);
        if (n < 5 || total < 5 || pocIndex < 0)
        {
            return ProfileShape.None;
        }

        var counts = levels.Select(l => (double)l.TpoCount).ToArray();
        if (IsDoubleDistribution(counts, total))
        {
            return ProfileShape.DoubleB;
        }

        if (periods >= 4 && counts[pocIndex] <= 0.35 * periods && (closePosition >= 0.75 || closePosition <= 0.25))
        {
            return ProfileShape.Trend;
        }

        var centre = counts.Select((c, i) => c * i).Sum() / total / (n - 1);
        var pocPosition = (double)pocIndex / (n - 1);
        if (centre >= 0.58 && pocPosition >= 0.6)
        {
            return ProfileShape.P;
        }

        if (centre <= 0.42 && pocPosition <= 0.4)
        {
            return ProfileShape.LowerB;
        }

        return ProfileShape.D;
    }

    private static bool IsDoubleDistribution(double[] counts, int total)
    {
        var n = counts.Length;
        var smooth = new double[n];
        for (var i = 0; i < n; i++)
        {
            var from = Math.Max(0, i - 1);
            var to = Math.Min(n - 1, i + 1);
            smooth[i] = counts[from..(to + 1)].Average();
        }

        // Maksima wygładzonych liczb TPO od dołu i od góry – szczyt po każdej stronie każdego wiersza.
        var maxBelow = new double[n];
        var maxAbove = new double[n];
        for (var i = 0; i < n; i++)
        {
            maxBelow[i] = Math.Max(i > 0 ? maxBelow[i - 1] : 0, smooth[i]);
        }

        for (var i = n - 1; i >= 0; i--)
        {
            maxAbove[i] = Math.Max(i < n - 1 ? maxAbove[i + 1] : 0, smooth[i]);
        }

        // Przewężenie w wierszu v: oba szczyty co najmniej 2 TPO, przewężenie ≤ 50% mniejszego z nich,
        // a po każdej stronie co najmniej 25% wszystkich TPO.
        var below = counts[0];
        for (var v = 2; v < n - 2; v++)
        {
            below += counts[v - 1];
            var smallerPeak = Math.Min(maxBelow[v - 2], maxAbove[v + 2]);
            if (smallerPeak < 2 || smooth[v] > 0.5 * smallerPeak)
            {
                continue;
            }

            var above = total - below - counts[v];
            if (below >= 0.25 * total && above >= 0.25 * total)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ogony (excess), poor high/low i single printy wewnątrz profilu.</summary>
    private static ProfileStats WithTails(ProfileStats stats, IReadOnlyList<PriceLevel> levels)
    {
        var n = levels.Count;
        if (n == 0)
        {
            return stats;
        }

        var topRun = 0;
        while (topRun < n && levels[n - 1 - topRun].TpoCount == 1)
        {
            topRun++;
        }

        var bottomRun = 0;
        while (bottomRun < n - topRun && levels[bottomRun].TpoCount == 1)
        {
            bottomRun++;
        }

        // Single printy: odcinki ≥ 2 wierszy z jedną literą między ogonami.
        var singles = new List<PriceRange>();
        var runStart = -1;
        for (var i = bottomRun; i <= n - topRun; i++)
        {
            if (i < n - topRun && levels[i].TpoCount == 1)
            {
                if (runStart < 0)
                {
                    runStart = i;
                }

                continue;
            }

            if (runStart >= 0 && i - runStart >= 2)
            {
                singles.Add(new PriceRange(levels[runStart].Price, levels[i - 1].Price));
            }

            runStart = -1;
        }

        return stats with
        {
            ExcessHigh = topRun >= 2 ? topRun : 0,
            ExcessLow = bottomRun >= 2 ? bottomRun : 0,
            PoorHigh = topRun == 0 && levels[n - 1].TpoCount >= 2,
            PoorLow = bottomRun == 0 && levels[0].TpoCount >= 2,
            SinglePrints = singles,
        };
    }

    /// <summary>
    /// Otwarcie względem poprzedniego profilu i typ otwarcia (A = pierwsza litera, B = druga, O = cena otwarcia):
    ///  - Open-Drive: O przy skraju A (≤ 15% zakresu A), A zamyka się po drugiej stronie, B nie wraca do O;
    ///  - Open-Test-Drive: A testuje stronę przeciwną (15–50% zakresu A), zamyka się po stronie ruchu,
    ///    a B rozszerza zakres w kierunku ruchu (wyższe High i Low przy ruchu w górę);
    ///  - Open-Rejection-Reverse: A jednostronna (ruch od O ≥ 40% zakresu IB i ≥ 2× ruchu w drugą stronę),
    ///    a B zamyka się po drugiej stronie O;
    ///  - Open-Auction: pozostałe przypadki.
    /// </summary>
    private static ProfileStats WithOpen(ProfileStats stats, ProfileSnapshot profile, ProfileSnapshot? previous)
    {
        var o = profile.Open;
        var location = OpenLocation.None;
        if (previous is not null && !profile.IsEmpty)
        {
            var vahTop = previous.ValueAreaHigh + previous.RowHeight;
            location = o > previous.High ? OpenLocation.AboveRange
                : o >= vahTop ? OpenLocation.AboveValue
                : o >= previous.ValueAreaLow ? OpenLocation.InValue
                : o >= previous.Low ? OpenLocation.BelowValue
                : OpenLocation.BelowRange;
        }

        var (type, direction) = OpenTypeOf(profile);
        return stats with { OpenLocation = location, OpenType = type, OpenDirection = direction };
    }

    internal static (OpenType Type, int Direction) OpenTypeOf(ProfileSnapshot profile)
    {
        var periods = profile.Periods;
        if (periods.Count < 2 || periods[0].Index != profile.FirstPeriod)
        {
            return (OpenType.None, 0);
        }

        var a = periods[0];
        var b = periods[1];
        var o = profile.Open;
        var rangeA = a.High - a.Low;
        var ibRange = Math.Max(a.High, b.High) - Math.Min(a.Low, b.Low);
        if (rangeA <= 0 || ibRange <= 0)
        {
            return (OpenType.OpenAuction, 0);
        }

        var fromLow = (o - a.Low) / rangeA;   // 0 = otwarcie na dole A
        var fromHigh = (a.High - o) / rangeA; // 0 = otwarcie na górze A

        if (fromLow <= 0.15m && a.Close > o && b.Low >= o)
        {
            return (OpenType.OpenDrive, 1);
        }

        if (fromHigh <= 0.15m && a.Close < o && b.High <= o)
        {
            return (OpenType.OpenDrive, -1);
        }

        if (fromLow > 0.15m && fromLow <= 0.5m && a.Close > o && b.High > a.High && b.Low > a.Low)
        {
            return (OpenType.OpenTestDrive, 1);
        }

        if (fromHigh > 0.15m && fromHigh <= 0.5m && a.Close < o && b.Low < a.Low && b.High < a.High)
        {
            return (OpenType.OpenTestDrive, -1);
        }

        // Litera A wyraźnie jednostronna (ruch od otwarcia co najmniej 2× większy niż w drugą stronę), potem B zamyka się
        // po drugiej stronie otwarcia. Rotacja wokół otwarcia (A po równo w obie strony) to Open-Auction.
        var aUp = a.High - o;
        var aDown = o - a.Low;
        if (aUp >= 0.4m * ibRange && aUp >= 2 * aDown && b.Close < o)
        {
            return (OpenType.OpenRejectionReverse, -1); // ruch w górę odrzucony – w dół
        }

        if (aDown >= 0.4m * ibRange && aDown >= 2 * aUp && b.Close > o)
        {
            return (OpenType.OpenRejectionReverse, 1); // ruch w dół odrzucony – w górę
        }

        return (OpenType.OpenAuction, 0);
    }

    /// <summary>Migracja obszaru wartości i jego nakładanie się z poprzednim profilem tego samego typu.</summary>
    private static ProfileStats WithValueMigration(ProfileStats stats, ProfileSnapshot profile, ProfileSnapshot? previous)
    {
        if (previous is null || profile.IsEmpty)
        {
            return stats;
        }

        var low = profile.ValueAreaLow;
        var top = profile.ValueAreaHigh + profile.RowHeight;
        var prevLow = previous.ValueAreaLow;
        var prevTop = previous.ValueAreaHigh + previous.RowHeight;

        var overlap = Math.Max(0, Math.Min(top, prevTop) - Math.Max(low, prevLow));
        var migration = low >= prevTop ? ValueMigration.Higher
            : top <= prevLow ? ValueMigration.Lower
            : low >= prevLow && top <= prevTop ? ValueMigration.Inside
            : low <= prevLow && top >= prevTop ? ValueMigration.Outside
            : low + top >= prevLow + prevTop ? ValueMigration.OverlappingHigher
            : ValueMigration.OverlappingLower;

        return stats with
        {
            ValueMigration = migration,
            ValueAreaOverlap = top > low ? Math.Round((double)(overlap / (top - low)), 3) : null,
        };
    }

    /// <summary>
    /// Nietknięte POC/VPOC: poziomy z ostatnich sesji tego samego typu, których wiersza nie dotknęła cena
    /// w żadnym późniejszym profilu (dowolnego typu sesji), łącznie z bieżącym.
    /// </summary>
    private static ProfileStats WithNakedPocs(ProfileStats stats, ProfileSnapshot profile, IReadOnlyList<ProfileSnapshot> history,
        ProfileAnalysisOptions options)
    {
        var earlier = history.Where(h => !h.IsEmpty && h.SessionStart < profile.SessionStart).ToList();
        var candidates = earlier
            .Select((h, i) => (Profile: h, Index: i))
            .Where(x => x.Profile.Session.Name == profile.Session.Name)
            .TakeLast(options.NakedPocLookback)
            .ToList();

        bool Touched(decimal level, decimal rowHeight, int index)
        {
            foreach (var later in earlier.Skip(index + 1).Append(profile))
            {
                if (!later.IsEmpty && level <= later.High && level + rowHeight > later.Low)
                {
                    return true;
                }
            }

            return false;
        }

        var pocs = new List<decimal>();
        var vpocs = new List<decimal>();
        foreach (var (candidate, index) in candidates)
        {
            if (!Touched(candidate.Poc, candidate.RowHeight, index))
            {
                pocs.Add(candidate.Poc);
            }

            if (candidate.VolumePoc is { } vpoc && !Touched(vpoc, candidate.RowHeight, index))
            {
                vpocs.Add(vpoc);
            }
        }

        return stats with { NakedPocs = pocs, NakedVolumePocs = vpocs };
    }
}
