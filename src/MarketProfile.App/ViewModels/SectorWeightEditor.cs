using MarketProfile.Core;

namespace MarketProfile.App.ViewModels;

/// <summary>Wiersz tabeli wag sektorów w ustawieniach: symbol, nazwa, ETF i edytowana waga (%).</summary>
public sealed class SectorWeightEditor(SectorInfo sector, string weightText)
{
    public string Key => sector.Key;

    public string Icon => sector.Icon;

    public string Name => sector.Name;

    /// <summary>Nazwa angielska (jak na spglobal.com) i ETF – podpowiedź przy wierszu.</summary>
    public string Description => $"{sector.EnglishName} – notowania z ETF {sector.Etf}";

    public string Etf => sector.Etf;

    /// <summary>Waga w % (tekst – sprawdzany przy zapisie).</summary>
    public string WeightText { get; set; } = weightText;
}
