namespace MarketProfile.Core;

/// <summary>
/// Cena rozliczeniowa (SETTLE) sesji dziennej: wpis ręczny, a gdy go brak – cena zamknięcia sesji
/// (pobieranie z CME jest na razie wyłączone).
/// </summary>
/// <param name="TradingDate">Data handlowa sesji dziennej.</param>
/// <param name="Price">Cena rozliczeniowa.</param>
/// <param name="Source"><see cref="SourceManual"/>, <see cref="SourceClose"/> albo <see cref="SourceCme"/>.</param>
/// <param name="Contract">Miesiąc kontraktu, którego dotyczy (np. "DEC 26"); pusty przy wpisie ręcznym.</param>
public sealed record Settlement(DateOnly TradingDate, decimal Price, string Source, string Contract = "")
{
    public const string SourceCme = "CME";
    public const string SourceManual = "ręcznie";

    /// <summary>Brak wpisu – za SETTLE przyjmujemy cenę zamknięcia sesji dziennej.</summary>
    public const string SourceClose = "Close";

    public bool IsManual => Source == SourceManual;
}
