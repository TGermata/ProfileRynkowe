using MarketProfile.Core;

namespace MarketProfile.Core.Tests;

/// <summary>Dziennik zdarzeń: zapis do pliku dziennego, wagi, powtórki zliczane zamiast zapisywane, wyjątek ze stosem.</summary>
public sealed class AppLogTests
{
    [Fact]
    public void Wpisy_TrafiajaDoPlikuDziennego_PowtorkiPomijane()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketProfileLogTest-" + Guid.NewGuid().ToString("N"));
        AppLog.Initialize(directory);
        var marker = Guid.NewGuid().ToString("N");

        AppLog.Info("Test", $"zdarzenie {marker}");
        AppLog.Info("Test", $"zdarzenie {marker}");   // powtórka w ciągu minuty – tylko policzona
        AppLog.Status("IB#1 ES", $"IB błąd 504: Not connected {marker}");
        AppLog.Status("IB#1 ES", $"IB kod 2103: Market data farm connection is broken {marker}");
        AppLog.Error("Test", $"wyjątek {marker}", new InvalidOperationException("coś poszło nie tak"));
        AppLog.Flush(TimeSpan.FromSeconds(5));

        var text = File.ReadAllText(AppLog.CurrentFile!);
        Assert.StartsWith(directory, AppLog.CurrentFile);
        Assert.Single(text.Split('\n'), l => l.Contains($"INFO  [Test] zdarzenie {marker}"));
        Assert.Contains($"BŁĄD  [IB#1 ES] IB błąd 504: Not connected {marker}", text);
        Assert.Contains($"UWAGA [IB#1 ES] IB kod 2103: Market data farm connection is broken {marker}", text);
        Assert.Contains($"BŁĄD  [Test] wyjątek {marker}", text);
        Assert.Contains("System.InvalidOperationException: coś poszło nie tak", text);
    }
}
