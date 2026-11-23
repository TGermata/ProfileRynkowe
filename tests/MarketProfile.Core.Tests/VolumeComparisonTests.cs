using MarketProfile.Core;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Porównanie wolumenu okresów TPO bieżącej sesji z poprzednią: wolumen co minutę, okresy zakończone,
/// okres w trakcie „do tej samej minuty”, okresy przyszłe, sesja nocna z nocną.
/// </summary>
public sealed class VolumeComparisonTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Tick o czasie lokalnym Nowego Jorku (listopad 2026).</summary>
    private static Tick At(int day, int hour, int minute, long volume, decimal price = 6800m)
    {
        var local = new DateTime(2026, 11, day, hour, minute, 0);
        return new Tick(new DateTimeOffset(local, NewYork.GetUtcOffset(local)), price, volume);
    }

    private static ProfileBuilder Builder() => new(new ProfileSettings { TicksPerRow = 4 }); // okres TPO 30 min

    private static (SessionProfile Today, SessionProfile? Previous) Day(ProfileBuilder builder)
    {
        var today = builder.Profiles.Where(p => p.Session.Name == "Dzienna").MaxBy(p => p.SessionStart)!;
        return (today, VolumeComparison.FindPrevious(builder.Profiles, today));
    }

    [Fact]
    public void WolumenCoMinute_ZTickowIZeSwiec()
    {
        var builder = Builder();
        builder.ProcessTick(At(16, 9, 30, 5));
        builder.ProcessTick(At(16, 9, 30, 3));
        var barTime = new DateTime(2026, 11, 16, 9, 41, 0);
        builder.ProcessBar(new Bar(new DateTimeOffset(barTime, NewYork.GetUtcOffset(barTime)), 6800m, 6802m, 6799m, 6801m, 40));

        var profile = Assert.Single(builder.Profiles);
        Assert.Equal(new Dictionary<int, long> { [0] = 8, [11] = 40 }, profile.GetMinuteVolumes());
        Assert.Equal(48, profile.GetPeriodVolumes()[0]);
    }

    [Fact]
    public void OkresyZakonczone_RoznicaIProcent_BiezacyDoTejSamejMinuty_PrzyszleBezDzis()
    {
        var builder = Builder();
        // Poprzednia sesja dzienna (piątek 13.11): A = 100 + 50, B = 200 (w 5. i 20. minucie), C = 80.
        builder.ProcessTick(At(13, 9, 30, 100));
        builder.ProcessTick(At(13, 9, 50, 50));
        builder.ProcessTick(At(13, 10, 4, 120));
        builder.ProcessTick(At(13, 10, 20, 80));
        builder.ProcessTick(At(13, 10, 35, 80));
        // Bieżąca sesja (poniedziałek 16.11): A = 300, B na razie 90 (w 3. minucie).
        builder.ProcessTick(At(16, 9, 31, 300));
        builder.ProcessTick(At(16, 10, 3, 90));

        var (today, previous) = Day(builder);
        Assert.Equal(new DateTime(2026, 11, 13, 9, 30, 0), previous!.SessionStart);

        // „Teraz” 10:06 – litera B trwa 7 minut (minuty 0–6 okresu).
        var result = VolumeComparison.Compare(today, previous, new DateTime(2026, 11, 16, 10, 6, 30));

        var a = result.Rows[0];
        Assert.Equal('A', a.Letter);
        Assert.Equal((300L, 150L, 150L), (a.Today!.Value, a.Previous, a.Difference!.Value));
        Assert.Equal(100.0, a.DifferencePercent);
        Assert.False(a.IsCurrent);

        var b = result.Rows[1];
        Assert.True(b.IsCurrent);
        Assert.Equal(7, b.Minutes);
        Assert.Equal(90, b.Today);
        Assert.Equal(120, b.Previous); // tylko do tej samej minuty – bez wolumenu z 10:20
        Assert.Equal(-25.0, b.DifferencePercent);

        var c = result.Rows[2];
        Assert.Null(c.Today);          // okres jeszcze się nie zaczął
        Assert.Equal(80, c.Previous);  // pełny okres poprzedniej sesji – tło
        Assert.Null(c.Difference);

        Assert.Equal(13, result.Rows.Count); // 9:30–16:00 = 13 okresów po 30 min
        Assert.Equal((390L, 270L), (result.TodayTotal, result.PreviousTotal));
        Assert.Equal(430, result.PreviousSessionTotal); // cała poprzednia sesja – także po 10:06
        Assert.Equal(120, result.TotalDifference);
        Assert.True(result.HasPrevious);
        Assert.Equal(new DateTime(2026, 11, 16, 10, 7, 0), result.CutoffLocal);
    }

    [Fact]
    public void BezPoprzedniejSesji_ProcentPusty_PoKoncuSesjiCalaSesja()
    {
        var builder = Builder();
        builder.ProcessTick(At(16, 9, 30, 10));
        builder.ProcessTick(At(16, 15, 59, 20));

        var (today, previous) = Day(builder);
        Assert.Null(previous);

        var result = VolumeComparison.Compare(today, previous, new DateTime(2026, 11, 16, 18, 0, 0));
        Assert.Null(result.CutoffLocal);
        Assert.All(result.Rows, r => Assert.NotNull(r.Today));
        Assert.Equal(20, result.Rows[^1].Today);
        Assert.Null(result.Rows[0].DifferencePercent);
        Assert.Null(result.TotalPercent);
    }

    [Fact]
    public void SesjaNocna_PorownywanaZPoprzedniaNocna()
    {
        var builder = Builder();
        builder.ProcessTick(At(13, 9, 30, 999));   // dzienna piątek – nie może być „poprzednią” dla nocnej
        builder.ProcessTick(At(12, 18, 5, 40));    // nocna czw → pt
        builder.ProcessTick(At(15, 18, 5, 70));    // nocna nd → pn

        var night = builder.Profiles.Where(p => p.Session.Name == "Nocna").MaxBy(p => p.SessionStart)!;
        var previous = VolumeComparison.FindPrevious(builder.Profiles, night);
        Assert.Equal("Nocna", previous!.Session.Name);

        var a = VolumeComparison.Compare(night, previous, null).Rows[0];
        Assert.Equal((70L, 40L), (a.Today!.Value, a.Previous));
    }

    [Fact]
    public void WeekendowyOgonSesjiNocnej_NieJestPoprzedniaSesja()
    {
        var builder = Builder();
        builder.ProcessTick(At(12, 20, 0, 40));    // nocna czw → pt (data handlowa 13.11, piątek)
        builder.ProcessTick(At(13, 16, 30, 900));  // nocna pt → sob – tylko 16:00–17:00 (data handlowa 14.11, sobota)
        builder.ProcessTick(At(15, 18, 5, 70));    // nocna nd → pn (data handlowa 16.11)

        var night = builder.Profiles.Where(p => p.Session.Name == "Nocna").MaxBy(p => p.SessionStart)!;
        var previous = VolumeComparison.FindPrevious(builder.Profiles, night);
        Assert.Equal(new DateOnly(2026, 11, 13), previous!.TradingDate);
    }
}
