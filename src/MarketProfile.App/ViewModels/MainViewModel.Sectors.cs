using System.Globalization;
using MarketProfile.Core;
using MarketProfile.DataFeed.IB;

namespace MarketProfile.App.ViewModels;

/// <summary>Kafelek sektora na pasku: symbol, nazwa, waga w S&amp;P 500, zmiana od otwarcia sesji kasowej i jej poziom (kolor).</summary>
public sealed record SectorTileView(SectorInfo Sector, decimal Weight, decimal? ChangePercent, SectorChangeLevel Level, string ToolTip);

/// <summary>
/// Pasek 11 sektorów S&amp;P 500 na dole zakładki „Szeroki rynek”: kafelki od największej wagi (wagi z ustawień, wg comiesięcznych danych
/// S&amp;P), zmiana od otwarcia sesji kasowej z ETF-ów sektorowych SPDR. Notowania – jedno połączenie TWS (Client ID + 8),
/// uruchamiane razem z dodatkowymi połączeniami „Szerokiego rynku” (po połączeniu głównego źródła IB).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>O ile większy od głównego jest Client ID tablicy notowań sektorów.</summary>
    private const int SectorClientIdOffset = 8;

    private IbQuoteBoard? _sectorBoard;
    private string? _sectorBoardStatus;    // problem połączenia (np. brak TWS) – w opisie paska
    private long _sectorBoardVersion = -1;
    private DateTime _nextSectorUpdate;
    private IReadOnlyList<SectorTileView> _sectorTiles = [];
    private string _sectorInfo = string.Empty;

    /// <summary>Kafelki sektorów od największej wagi.</summary>
    public IReadOnlyList<SectorTileView> SectorTiles
    {
        get => _sectorTiles.Count > 0 ? _sectorTiles : _sectorTiles = BuildSectorTiles();
        private set => SetProperty(ref _sectorTiles, value);
    }

    /// <summary>Opis na końcu paska: źródło danych, stan, przypomnienie o nieaktualnych wagach.</summary>
    public string SectorInfo
    {
        get => _sectorInfo.Length > 0 ? _sectorInfo : BuildSectorInfo(); // przed pierwszym odświeżeniem – od razu stan
        private set => SetProperty(ref _sectorInfo, value);
    }

    /// <summary>Czy pokazywać pasek sektorów (Ustawienia → Szeroki rynek).</summary>
    public bool ShowSectorStrip => _settings.ShowSectorStrip;

    /// <summary>Start tablicy notowań sektorów (po połączeniu głównego źródła IB przez TWS).</summary>
    private async Task StartSectorsAsync(int generation)
    {
        await StopSectorsAsync();
        if (!_settings.ShowSectorStrip)
        {
            return;
        }

        var connection = _settings.ToIbSettings();
        if (connection.UseWebApi)
        {
            _sectorBoardStatus = "notowania sektorów tylko przez TWS API (źródło: IBKR Web API)";
            UpdateSectors(force: true);
            return;
        }

        var board = new IbQuoteBoard(connection with { ClientId = connection.ClientId + SectorClientIdOffset },
            SectorPerformance.Sectors.Select(s => s.Etf).ToList(), primaryExchange: "ARCA"); // ETF-y SPDR – NYSE Arca
        _sectorBoard = board;
        _sectorBoardStatus = "łączenie z TWS...";
        try
        {
            await Task.Run(() => board.StartAsync());
            if (ReferenceEquals(board, _sectorBoard))
            {
                _sectorBoardStatus = null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Sektory", "połączenie notowań sektorów nie powiodło się", ex);
            if (ReferenceEquals(board, _sectorBoard))
            {
                _sectorBoardStatus = $"brak notowań sektorów: {ex.Message}";
            }
        }

        if (generation != _broadLiveGeneration && ReferenceEquals(board, _sectorBoard))
        {
            await StopSectorsAsync(); // w międzyczasie zatrzymano źródło danych
        }

        UpdateSectors(force: true);
    }

    /// <summary>Zatrzymanie tablicy notowań (ostatnie wartości zostają na pasku).</summary>
    private async Task StopSectorsAsync()
    {
        if (_sectorBoard is { } board)
        {
            _sectorBoard = null;
            await board.DisposeAsync();
        }
    }

    /// <summary>Z zegara odświeżania: kafelki co sekundę, gdy przyszły nowe notowania.</summary>
    private void OnSectorTimer()
    {
        if (DateTime.UtcNow < _nextSectorUpdate)
        {
            return;
        }

        _nextSectorUpdate = DateTime.UtcNow.AddSeconds(1);
        UpdateSectors(force: false);
    }

    /// <summary>Zmieniono ustawienia (wagi, włączenie paska) – kafelki od nowa.</summary>
    private void OnSectorSettingsChanged()
    {
        OnPropertyChanged(nameof(ShowSectorStrip));
        if (!_settings.ShowSectorStrip)
        {
            RunSafely(StopSectorsAsync(), "Sektory – zatrzymanie");
        }
        else if (_sectorBoard is null && _broadFeeds is not null)
        {
            RunSafely(StartSectorsAsync(_broadLiveGeneration), "Sektory – połączenie");
        }

        UpdateSectors(force: true);
    }

    private void UpdateSectors(bool force)
    {
        var version = _sectorBoard?.Version ?? 0;
        if (!force && version == _sectorBoardVersion)
        {
            return;
        }

        _sectorBoardVersion = version;
        SectorTiles = BuildSectorTiles();
        SectorInfo = BuildSectorInfo();
    }

    private IReadOnlyList<SectorTileView> BuildSectorTiles()
    {
        var quotes = _lastSectorQuotes = _sectorBoard?.Snapshot() ?? _lastSectorQuotes;
        var weightsDate = _settings.SectorWeightsDate.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
        return SectorPerformance.OrderedByWeight(_settings.SectorWeights)
            .Select(x =>
            {
                var quote = quotes?.GetValueOrDefault(x.Sector.Etf);
                var change = SectorPerformance.ChangePercent(quote?.Open, quote?.Last);
                var tip = $"{x.Sector.Icon} {x.Sector.Name} ({x.Sector.EnglishName})\n" +
                          $"Waga w S&P 500: {x.Weight.ToString("0.0", CultureInfo.CurrentCulture)}% (wg S&P z {weightsDate})\n" +
                          $"ETF {x.Sector.Etf}: " + (quote is { Open: { } open, Last: { } last }
                              ? $"otwarcie {open.ToString("0.00", CultureInfo.CurrentCulture)}, ostatnia {last.ToString("0.00", CultureInfo.CurrentCulture)}" +
                                (quote.Delayed ? " (dane opóźnione)" : string.Empty)
                              : quote?.Problem ?? "brak notowań");
                return new SectorTileView(x.Sector, x.Weight, change, SectorPerformance.Classify(change), tip);
            })
            .ToList();
    }

    private IReadOnlyDictionary<string, BoardQuote>? _lastSectorQuotes;

    private string BuildSectorInfo()
    {
        var parts = new List<string>();
        if (_sectorBoardStatus is { } status)
        {
            parts.Add(status);
        }
        else if (_sectorBoard is null)
        {
            parts.Add(_lastSectorQuotes is null ? "połącz się z IB (tryb „Na żywo”)" : "ostatnie notowania");
        }
        else if (!CashSession.Contains(_settings.BroadMarketInstrument ?? _settings.Instrument, DateTimeOffset.UtcNow))
        {
            parts.Add("sesja kasowa zamknięta – zmiana z ostatniej sesji");
        }

        var age = DateOnly.FromDateTime(DateTime.Today).DayNumber - _settings.SectorWeightsDate.DayNumber;
        if (age > SectorPerformance.WeightsMaxAgeDays)
        {
            parts.Add($"wagi z {_settings.SectorWeightsDate:dd.MM.yyyy} – zaktualizuj w Ustawieniach → Szeroki rynek");
        }

        return string.Join("  ·  ", parts);
    }
}
