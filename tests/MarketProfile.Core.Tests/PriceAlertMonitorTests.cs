using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketProfile.Core.Tests;

/// <summary>Poziomy alarmowe: wykrywanie przebić, kierunek, tryb jednokrotny / przy każdym przebiciu.</summary>
public class PriceAlertMonitorTests
{
    private const decimal Tick = 0.25m;

    private static PriceLine Alert(decimal price, AlertRepeat repeat = AlertRepeat.Once,
        AlertDirection direction = AlertDirection.Both, int rearmTicks = 2, bool enabled = true, bool triggered = false) =>
        new()
        {
            Price = price,
            Alert = new PriceAlert
            {
                Repeat = repeat, Direction = direction, RearmTicks = rearmTicks, IsEnabled = enabled, IsTriggered = triggered,
            },
        };

    /// <summary>Jedno sprawdzenie dla pojedynczej ceny (min = max = ostatnia).</summary>
    private static IReadOnlyList<PriceAlertHit> At(PriceAlertMonitor monitor, PriceLine line, decimal price) =>
        monitor.Check([line], price, price, price, Tick);

    [Fact]
    public void FirstCheckOnlySetsReference()
    {
        var monitor = new PriceAlertMonitor();
        var line = Alert(100);

        Assert.Empty(At(monitor, line, 101)); // historia / start – cena już nad poziomem, bez alarmu
        Assert.True(monitor.HasReference);
        Assert.Empty(At(monitor, line, 102));
    }

    [Fact]
    public void DetectsUpAndDownCross()
    {
        var monitor = new PriceAlertMonitor();
        var line = Alert(100, AlertRepeat.EveryCross, rearmTicks: 0);

        At(monitor, line, 99);
        var up = Assert.Single(At(monitor, line, 100)); // dotknięcie poziomu od dołu
        Assert.True(up.Upward);

        At(monitor, line, 101);
        var down = Assert.Single(At(monitor, line, 99.75m));
        Assert.False(down.Upward);
    }

    [Fact]
    public void DetectsJumpWithinOneCheck()
    {
        var monitor = new PriceAlertMonitor();
        var line = Alert(100);

        At(monitor, line, 99);
        // Cena wyskoczyła ponad poziom i wróciła między sprawdzeniami – liczy się najwyższa cena.
        var hit = Assert.Single(monitor.Check([line], 98.5m, 100.5m, 99.5m, Tick));
        Assert.True(hit.Upward);
    }

    [Theory]
    [InlineData(AlertDirection.Up, 99, 101, true)]
    [InlineData(AlertDirection.Up, 101, 99, false)]
    [InlineData(AlertDirection.Down, 101, 99, true)]
    [InlineData(AlertDirection.Down, 99, 101, false)]
    public void DirectionFilter(AlertDirection direction, double from, double to, bool expected)
    {
        var monitor = new PriceAlertMonitor();
        var line = Alert(100, direction: direction);

        At(monitor, line, (decimal)from);
        Assert.Equal(expected, At(monitor, line, (decimal)to).Count == 1);
    }

    [Fact]
    public void DisabledOrTriggeredAlertIsSilent()
    {
        var monitor = new PriceAlertMonitor();
        var disabled = Alert(100, enabled: false);
        var triggered = Alert(100, triggered: true);
        var plainLine = new PriceLine { Price = 100 };

        monitor.Check([disabled, triggered, plainLine], 99, 99, 99, Tick);
        Assert.Empty(monitor.Check([disabled, triggered, plainLine], 101, 101, 101, Tick));
    }

    [Fact]
    public void EveryCrossWaitsForDistanceBeforeRearming()
    {
        var monitor = new PriceAlertMonitor();
        var line = Alert(100, AlertRepeat.EveryCross, rearmTicks: 4); // 4 ticki = 1 punkt

        At(monitor, line, 99);
        Assert.Single(At(monitor, line, 100.25m));

        // Cena „pływa” wokół poziomu – bez kolejnych alarmów.
        Assert.Empty(At(monitor, line, 99.75m));
        Assert.Empty(At(monitor, line, 100.25m));
        Assert.Empty(At(monitor, line, 99.5m));

        // Odsunięcie o 1 punkt uzbraja alarm, następne przebicie znowu alarmuje.
        Assert.Empty(At(monitor, line, 99));
        Assert.Single(At(monitor, line, 100));
    }

    [Fact]
    public void RearmAndResetClearWaiting()
    {
        var monitor = new PriceAlertMonitor();
        var line = Alert(100, AlertRepeat.EveryCross, rearmTicks: 100);

        At(monitor, line, 99);
        Assert.Single(At(monitor, line, 101));
        Assert.Empty(At(monitor, line, 99));

        monitor.Rearm(line.Id);
        Assert.Single(At(monitor, line, 101));

        monitor.Reset();
        Assert.False(monitor.HasReference);
        Assert.Empty(At(monitor, line, 99)); // tylko punkt odniesienia
        Assert.Single(At(monitor, line, 101));
    }

    [Fact]
    public void SerializationKeepsAlertAndOldFormatLoads()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var line = Alert(6012.25m, AlertRepeat.EveryCross, AlertDirection.Down) with { Label = "opór" };

        var copy = JsonSerializer.Deserialize<PriceLine>(JsonSerializer.Serialize(line, options), options)!;
        Assert.Equal(line.Alert, copy.Alert);
        Assert.True(copy.IsAlert);

        // Plik z wersji bez alarmów – zwykła linia.
        var old = JsonSerializer.Deserialize<PriceLine>("""{"Price":5000,"Color":"#FFD54F","Style":"Dashed"}""", options)!;
        Assert.Null(old.Alert);
        Assert.False(old.IsAlert);
    }
}
