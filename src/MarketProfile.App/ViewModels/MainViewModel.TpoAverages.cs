using MarketProfile.App.Controls;
using MarketProfile.Core;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Średnie kroczące z wykresu dziennego (zakładka D/W/M) jako linie poziome na wykresie TPO: dla bieżącego
/// (dziś rysowanego) profilu liczymy wartość każdej średniej „na dziś” – N−1 poprzednich zamknięć dziennych
/// + ostatnia cena profilu jako dzisiejsze zamknięcie. Widać, że cena zbliża się do znaczącej średniej.
/// </summary>
public sealed partial class MainViewModel
{
    private IReadOnlyList<Bar> _dailyBars = [];
    private string? _dailyBarsSymbol;
    private int _dailyBarsVersion;
    private TpoAverageOverlay _tpoAverages = TpoAverageOverlay.Empty;

    /// <summary>Linie średnich na wykresie TPO.</summary>
    public TpoAverageOverlay TpoAverages
    {
        get => _tpoAverages;
        private set => SetProperty(ref _tpoAverages, value);
    }

    /// <summary>Wczytuje (w tle) pełne świece dzienne bieżącego instrumentu z bazy i przelicza średnie.</summary>
    private async Task LoadDailyBarsAsync()
    {
        var version = ++_dailyBarsVersion;
        var symbol = _settings.InstrumentSymbol;
        IReadOnlyList<Bar> bars;
        try
        {
            var store = GetStore();
            bars = await Task.Run(() => BarExtensions.CompleteOnly(store.ReadOhlcBars(symbol, BarInterval.Day)));
        }
        catch (Exception)
        {
            bars = []; // brak bazy / błąd odczytu – po prostu bez średnich
        }

        if (version != _dailyBarsVersion)
        {
            return;
        }

        _dailyBars = bars;
        _dailyBarsSymbol = symbol;
        UpdateTpoAverages();
    }

    /// <summary>
    /// Przelicza poziomy średnich dla najnowszego profilu (jego data handlowa i ostatnia cena).
    /// Wykres odświeżany tylko, gdy coś się zmieniło.
    /// </summary>
    private void UpdateTpoAverages()
    {
        var latest = _snapshots.LastOrDefault(s => !s.IsEmpty);
        if (latest is null || _dailyBars.Count == 0 || _dailyBarsSymbol != _settings.InstrumentSymbol)
        {
            SetTpoAverages(TpoAverageOverlay.Empty);
            return;
        }

        var bars = _dailyBars;
        var overlay = TpoAverageOverlay.Create(_settings.TpoAverages, _settings.CandleChart.MovingAverages,
            period => Indicators.SmaToday(bars, latest.TradingDate, period, latest.LastPrice),
            latest.TickSize, latest.LastPrice);
        SetTpoAverages(overlay);
    }

    private void SetTpoAverages(TpoAverageOverlay overlay)
    {
        if (!overlay.SameAs(_tpoAverages))
        {
            TpoAverages = overlay;
        }
    }
}
