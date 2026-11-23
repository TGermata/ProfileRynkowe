using MarketProfile.Core;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Tempo budowania TPO: nowe wiersze każdej litery względem normy, stan (szybkie / zwalnia / zatrzymanie / równowaga),
/// rzut niedokończonej litery i zdarzenia zatrzymania.
/// </summary>
public sealed class TpoTempoTests
{
    private static readonly DateTime Start = new(2026, 11, 12, 9, 30, 0);
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(30);

    /// <summary>Sesja z liter podanych jako (Low, High); wysokość wiersza 1.</summary>
    private static TempoSession Session(decimal open, params (decimal Low, decimal High)[] letters) =>
        new("Dzienna", Start, Period, 1m, open, letters
            .Select((l, i) => new TpoPeriod(i, Start + i * Period, Start + (i + 1) * Period, l.High, l.Low))
            .ToList());

    /// <summary>Norma: 3 sesje, w których każda litera dokłada 4 wiersze (w górę lub w dół).</summary>
    private static TempoReference History(int direction)
    {
        var sessions = Enumerable.Range(0, 3).Select(_ =>
        {
            var letters = Enumerable.Range(0, 8)
                .Select(i => direction > 0 ? (100m + 4 * i, 100m + 4 * (i + 1)) : (100m - 4 * (i + 1), 100m - 4 * i))
                .ToArray();
            return Session(100m, letters);
        });
        return TempoReference.FromHistory(sessions, 20);
    }

    // A: rozgrzewka (zakres 100–108), B: +7, C: +3, D: 0 (stoi na 118), E: 0 (te same wiersze).
    private static readonly (decimal, decimal)[] Rally = [(100, 108), (107, 115), (114, 118), (112, 117), (113, 117)];

    private static TempoReading Read(TempoReference reference, int letters, DateTime? now = null, params (decimal, decimal)[] source) =>
        TpoTempo.Analyze(Session(100m, (source.Length > 0 ? source : Rally)[..letters]), reference, now).Current;

    [Fact]
    public void Wzrost_SzybkieZwalniaZatrzymanie()
    {
        var reference = History(1);

        var a = Read(reference, 1);
        Assert.Equal(TempoState.Starting, a.State); // litera A tylko buduje zakres odniesienia
        Assert.Equal(0, a.Tempo);

        var b = Read(reference, 2);
        Assert.Equal(TempoState.Fast, b.State);
        Assert.Equal(1.75, b.Tempo);       // 7 nowych wierszy ponad zakres A przy normie 4
        Assert.Equal(1.75, b.TempoChange); // względem 0 – litera A nie ma tempa

        var c = Read(reference, 3);
        Assert.Equal(TempoState.Slowing, c.State); // +3 → 0,75
        Assert.Equal(0.75, c.Tempo);
        Assert.True(c.TempoChange < 0); // wzrost zwalnia

        var d = Read(reference, 4);
        Assert.Equal(TempoState.Stopped, d.State);
        Assert.Equal(0, d.Tempo);
        Assert.Equal(118m, d.StopPrice);
        Assert.Equal(1, d.Direction);
    }

    [Fact]
    public void Spadek_LustrzanieUjemneTempo()
    {
        var reference = History(-1);
        (decimal, decimal)[] drop = [(92, 100), (85, 93), (83, 86), (83, 88)];

        Assert.Equal(TempoState.Starting, Read(reference, 1, null, drop).State);

        var b = Read(reference, 2, null, drop);
        Assert.Equal(TempoState.Fast, b.State);
        Assert.Equal(-1.75, b.Tempo); // 7 wierszy poniżej zakresu A

        var c = Read(reference, 3, null, drop);
        Assert.Equal(TempoState.Slowing, c.State);
        Assert.Equal(-0.5, c.Tempo); // 2 wiersze w dół przy normie 4
        Assert.True(c.TempoChange > 0); // spadek zwalnia

        var d = Read(reference, 4, null, drop);
        Assert.Equal(TempoState.Stopped, d.State);
        Assert.Equal(83m, d.StopPrice);
        Assert.Equal(-1, d.Direction);
    }

    [Fact]
    public void DwieLiteryBezNowychPoziomow_Rownowaga()
    {
        var e = Read(History(1), 5);

        Assert.Equal(TempoState.Balance, e.State);
        Assert.Equal(112m, e.BalanceLow);
        Assert.Equal(117m, e.BalanceHigh);
    }

    [Fact]
    public void BiezacaLitera_RzutNaPelnyOkres_ANaPoczatkuTempoPoprzedniej()
    {
        var reference = History(1);
        var cStart = Start + 2 * Period;

        // 10 min z 30, +3 wiersze → rzut +9 → 2,25 × normy.
        var projected = Read(reference, 3, cStart.AddMinutes(10));
        Assert.True(projected.IsProjected);
        Assert.Equal(9, projected.NewRowsPerPeriod);
        Assert.Equal(2.25, projected.Tempo);
        Assert.Equal(TempoState.Fast, projected.State);

        // 3 min – za wcześnie na rzut: odczyt litery B.
        var early = Read(reference, 3, cStart.AddMinutes(3));
        Assert.Equal('B', early.Letter);
        Assert.False(early.IsProjected);
        Assert.Equal(1.75, early.Tempo);

        // 3 min litery B – litera A to rozgrzewka, więc jeszcze początek sesji.
        Assert.Equal(TempoState.Starting, Read(reference, 2, Start + Period + TimeSpan.FromMinutes(3)).State);
    }

    [Fact]
    public void PoKoncuSesji_TempoZero_SesjaZakonczona()
    {
        // Rynek zamknięty: „teraz” długo po końcu sesji – ostatnia litera była szybka, ale od tego czasu nic się nie dzieje.
        var session = Session(100m, Rally[..2]) with { SessionEnd = Start.AddHours(7) };
        var reading = TpoTempo.Analyze(session, History(1), Start.AddDays(2)).Current;

        Assert.Equal(TempoState.SessionEnded, reading.State);
        Assert.Equal(0, reading.Tempo);
        Assert.True(reading.TempoChange < 0);
    }

    [Fact]
    public void WTrakcieSesjiBezNowejLitery_BrakHandlu_AKrotkoPoKoncuLiteryZwyklyOdczyt()
    {
        var session = Session(100m, Rally[..2]) with { SessionEnd = Start.AddHours(7) };
        var afterB = Start + 2 * Period;

        // 3 min po końcu B – jeszcze zwykły odczyt litery B.
        Assert.Equal(TempoState.Fast, TpoTempo.Analyze(session, History(1), afterB.AddMinutes(3)).Current.State);

        // 20 min bez żadnej transakcji – brak handlu, tempo 0.
        var idle = TpoTempo.Analyze(session, History(1), afterB.AddMinutes(20)).Current;
        Assert.Equal(TempoState.NoTrading, idle.State);
        Assert.Equal(0, idle.Tempo);
    }

    [Fact]
    public void PierwszeMinutySesji_StanPoczatek_TempoZero()
    {
        // 09:32 – litera A trwa dopiero 2 min: brak poprzedniej litery i za wcześnie na rzut, ale odczyt nie jest pusty.
        var reading = TpoTempo.Analyze(Session(100m, Rally[..1]), History(1), Start.AddMinutes(2)).Current;

        Assert.Equal(TempoState.Starting, reading.State);
        Assert.Equal(0, reading.Tempo);
        Assert.Equal('A', reading.Letter);
    }

    [Fact]
    public void CalaLiteraA_BezTempa_BWewnatrzZakresuA_Rownowaga()
    {
        // 20 min litery A z dużym ruchem – nadal początek sesji (bez rzutu A).
        var duringA = TpoTempo.Analyze(Session(100m, Rally[..1]), History(1), Start.AddMinutes(20)).Current;
        Assert.Equal(TempoState.Starting, duringA.State);
        Assert.Equal(0, duringA.Tempo);

        // A chodzi w obie strony od otwarcia, B w całości w jej zakresie – równowaga, nie zatrzymanie po ruchu A.
        var analysis = TpoTempo.Analyze(Session(100m, (95, 105), (97, 103)), History(1), null);
        Assert.Equal(TempoState.Balance, analysis.Current.State);
        Assert.Equal(0, analysis.Current.Direction);
        Assert.Equal(95m, analysis.Current.BalanceLow);
        Assert.Equal(105m, analysis.Current.BalanceHigh);
        Assert.Empty(analysis.Events);
    }

    [Fact]
    public void Zdarzenie_ZatrzymaniePoRuchu()
    {
        var events = TpoTempo.Analyze(Session(100m, Rally), History(1), null).Events;

        var stop = Assert.Single(events); // E to dalej ten sam postój
        Assert.Equal(TempoEventKind.Stopped, stop.Kind);
        Assert.Equal(Start + 3 * Period, stop.Time); // litera D
        Assert.Equal(118m, stop.Price);
        Assert.Equal(11m, stop.Move);                // od Low litery B (107) – A to rozgrzewka
        Assert.Equal(TimeSpan.FromMinutes(60), stop.Duration);
    }

    [Fact]
    public void Norma_ZTejSamejLitery_OsobnoWGoreIWDol()
    {
        var reference = History(1);

        Assert.Equal((4.0, 0.0), reference.For(1));
        Assert.Equal((4.0, 0.0), reference.For(0)); // A (rozgrzewka) poza średnimi – norma ogólna
        Assert.Equal(3, reference.Sessions);

        // Bez historii: pierwsza mierzona litera (B) – surowa liczba nowych wierszy, C – norma ze średniej B.
        Assert.Equal(7.0, Read(TempoReference.None, 2).Tempo, 2);
        Assert.Equal(3.0 / 7.0, Read(TempoReference.None, 3).Tempo, 2);
    }

    [Fact]
    public void SessionProfile_DajeLiteryIOtwarcie()
    {
        var settings = new ProfileSettings { TimeZone = TimeZoneInfo.Utc, TicksPerRow = 4 };
        var session = settings.Sessions[0];
        var profile = new SessionProfile(session, Start, settings);
        profile.AddPriceRange(0, 100m, 102m, 101m, 10, openPrice: 100.5m);
        profile.AddPriceRange(1, 101m, 105m, 104m, 10);

        var tempo = profile.GetTempoSession();

        Assert.Equal(100.5m, tempo.Open);
        Assert.Equal(2, tempo.Periods.Count);
        Assert.Single(profile.GetTempoSession(toPeriod: 0).Periods);
        Assert.Equal(1m, tempo.RowHeight);
    }
}
