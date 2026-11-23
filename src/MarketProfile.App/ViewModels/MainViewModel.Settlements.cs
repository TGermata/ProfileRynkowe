using MarketProfile.Core;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Ceny rozliczeniowe (SETTLE) sesji dziennych: za SETTLE przyjmujemy cenę zamknięcia sesji (Close), chyba że
/// użytkownik wpisał cenę ręcznie (menu profilu dziennego / „Parametry profili”) – wpisy ręczne są w bazie (tabela settlements).
/// Pobieranie ze strony CME (<see cref="DataFeed.Settlements.CmeSettlementClient"/>) jest na razie wyłączone.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly IReadOnlyDictionary<DateOnly, Settlement> NoSettlements = new Dictionary<DateOnly, Settlement>();

    private IReadOnlyDictionary<DateOnly, Settlement> _settlements = NoSettlements;
    private string? _settlementsSymbol;
    private int _settlementsVersion;

    /// <summary>Wpisy ręczne SETTLE do analizy profili (dni bez wpisu – Close sesji).</summary>
    private IReadOnlyDictionary<DateOnly, Settlement> SettlementsForStats()
    {
        if (_settlementsSymbol != _settings.InstrumentSymbol)
        {
            ReloadSettlements();
        }

        return _settlements;
    }

    /// <summary>Wpis ręczny SETTLE dnia (null = brak – obowiązuje Close sesji).</summary>
    public Settlement? GetSettlement(DateOnly tradingDate) => SettlementsForStats().GetValueOrDefault(tradingDate);

    /// <summary>Wpis ręczny ceny rozliczeniowej (zastępuje Close sesji).</summary>
    public void SetManualSettlement(DateOnly tradingDate, decimal price)
    {
        GetStore().SaveSettlement(_settings.InstrumentSymbol, new Settlement(tradingDate, price, Settlement.SourceManual));
        ReloadSettlements();
    }

    /// <summary>Usuwa wpis ręczny – za SETTLE znów przyjmowana jest cena zamknięcia sesji.</summary>
    public void RemoveSettlement(DateOnly tradingDate)
    {
        GetStore().DeleteSettlement(_settings.InstrumentSymbol, tradingDate);
        ReloadSettlements();
    }

    private void ReloadSettlements()
    {
        _settlementsSymbol = _settings.InstrumentSymbol;
        try
        {
            _settlements = GetStore().ReadSettlements(_settlementsSymbol)
                .Where(s => s.Value.IsManual)
                .ToDictionary(s => s.Key, s => s.Value);
        }
        catch (Exception)
        {
            _settlements = NoSettlements;
        }

        _settlementsVersion++; // parametry profili przeliczą się z nowymi cenami
    }
}
