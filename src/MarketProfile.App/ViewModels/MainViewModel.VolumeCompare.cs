using System.Collections.ObjectModel;
using System.Globalization;
using MarketProfile.App.Controls;
using MarketProfile.App.Infrastructure;
using MarketProfile.Core;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Zakładka „Wolumen dziś / poprzednia sesja”: wolumen bieżącej sesji w okresach TPO, liczony na bieżąco,
/// obok wolumenu tych samych okresów poprzedniej sesji tego samego rodzaju i różnica (tabela + słupki).
/// Okres w trakcie porównywany „do tej samej minuty” (<see cref="VolumeComparison"/>). „Teraz” = czas danych
/// (zegar symulacji / ostatni tick), więc w symulacji porównanie nie widzi przyszłości.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Indeks zakładki porównania wolumenu.</summary>
    public const int VolumeTab = 2;

    private static readonly TimeSpan VolumeRefreshInterval = TimeSpan.FromSeconds(1);

    private VolumeComparisonResult _volumeComparison = VolumeComparisonResult.Empty;
    private string _volumeSummary = string.Empty;
    private DateTime _volumeUpdatedAt;
    private int _volumeSelectedIndex = -1;
    private (string Session, DateTime Start) _volumeRowsKey;

    /// <summary>Wynik porównania (do wykresu słupkowego).</summary>
    public VolumeComparisonResult VolumeResult
    {
        get => _volumeComparison;
        private set => SetProperty(ref _volumeComparison, value);
    }

    /// <summary>
    /// Wiersze tabeli (sformatowane). Te same obiekty przez całą sesję – co sekundę zmieniają się tylko ich wartości,
    /// więc tabela nie gubi zaznaczenia ani przewinięcia.
    /// </summary>
    public ObservableCollection<VolumeCompareRowView> VolumeRows { get; } = [];

    /// <summary>
    /// Zaznaczony okres (numer litery; −1 = brak) – wspólny dla tabeli i wykresu: kliknięcie wiersza zaznacza słupki,
    /// kliknięcie słupków zaznacza wiersz. Zaznaczenie przetrwa odświeżanie co sekundę.
    /// </summary>
    public int VolumeSelectedIndex
    {
        get => _volumeSelectedIndex;
        set => SetProperty(ref _volumeSelectedIndex, value);
    }

    /// <summary>Opis nad tabelą: które sesje są porównywane i suma do teraz.</summary>
    public string VolumeSummary
    {
        get => _volumeSummary;
        private set => SetProperty(ref _volumeSummary, value);
    }

    /// <summary>Przelicza porównanie (z zegara odświeżania – co sekundę, tylko na tej zakładce; <paramref name="force"/> – od razu).</summary>
    private void UpdateVolumeComparison(bool force = false)
    {
        if (!IsTabShown(VolumeTab) || (!force && DateTime.UtcNow - _volumeUpdatedAt < VolumeRefreshInterval))
        {
            return;
        }

        _volumeUpdatedAt = DateTime.UtcNow;
        if (_builder is null || CurrentProfileAt(_builder.Profiles, DataNow()) is not { } found)
        {
            VolumeResult = VolumeComparisonResult.Empty;
            VolumeRows.Clear();
            _volumeRowsKey = default;
            VolumeSummary = "Brak danych – uruchom dane na żywo albo symulację.";
            return;
        }

        var (today, _, nowLocal) = found;
        var previous = Core.VolumeComparison.FindPrevious(_builder.Profiles, today);
        var result = Core.VolumeComparison.Compare(today, previous, nowLocal);

        VolumeResult = result;

        // Ta sama sesja – aktualizacja wartości istniejących wierszy; nowa sesja – nowe wiersze i bez zaznaczenia.
        var key = (today.Session.Name, today.SessionStart);
        if (key == _volumeRowsKey && VolumeRows.Count == result.Rows.Count)
        {
            for (var i = 0; i < result.Rows.Count; i++)
            {
                VolumeRows[i].Row = result.Rows[i];
            }
        }
        else
        {
            _volumeRowsKey = key;
            VolumeSelectedIndex = -1;
            VolumeRows.Clear();
            foreach (var row in result.Rows)
            {
                VolumeRows.Add(new VolumeCompareRowView(row));
            }
        }
        VolumeSummary = SummaryText(today, previous, result);
    }

    private static string SummaryText(SessionProfile today, SessionProfile? previous, VolumeComparisonResult result)
    {
        var session = $"Sesja {today.Session.Name.ToLowerInvariant()} {today.TradingDate:dd.MM}";
        if (previous is null)
        {
            return $"{session} – brak poprzedniej sesji tego rodzaju w wczytanej historii.";
        }

        var until = result.CutoffLocal is { } cutoff ? $"do {cutoff:HH:mm}" : "cała sesja";
        var percent = result.TotalPercent is { } p
            ? $" ({(p >= 0 ? "+" : "−")}{Math.Abs(p).ToString("0.0", CultureInfo.CurrentCulture)}%)"
            : string.Empty;
        return $"{session} vs {previous.Session.Name.ToLowerInvariant()} {previous.TradingDate:dd.MM}  –  {until}:  " +
               $"{VolumeFormat.Long(result.TodayTotal)}  vs  {VolumeFormat.Long(result.PreviousTotal)}{percent}";
    }
}

/// <summary>Wiersz tabeli porównania wolumenu – wartości sformatowane do wyświetlenia (aktualizowane w miejscu).</summary>
public sealed class VolumeCompareRowView(VolumeComparisonRow row) : ObservableObject
{
    private VolumeComparisonRow _row = row;

    /// <summary>Dane wiersza; zmiana odświeża wszystkie kolumny (tylko gdy wartości faktycznie się zmieniły).</summary>
    public VolumeComparisonRow Row
    {
        get => _row;
        set
        {
            if (_row != value)
            {
                _row = value;
                OnPropertyChanged(string.Empty);
            }
        }
    }

    public string Letter => Row.Letter.ToString();

    public string Hours => $"{Row.Start:HH:mm}–{Row.End:HH:mm}" + (Row.IsCurrent ? $"  (do {Row.Minutes} min)" : string.Empty);

    public string Today => Row.Today is { } v ? VolumeFormat.Long(v) : "–";

    public string Previous => VolumeFormat.Long(Row.Previous);

    public string Difference => Row.Difference is { } d ? (d > 0 ? "+" : d < 0 ? "−" : string.Empty) + VolumeFormat.Long(Math.Abs(d)) : "–";

    public string DifferencePercent => Row.DifferencePercent is { } p
        ? (p > 0 ? "+" : p < 0 ? "−" : string.Empty) + Math.Abs(p).ToString("0.0", CultureInfo.CurrentCulture) + "%"
        : "–";

    /// <summary>1 = więcej niż w poprzedniej sesji, −1 = mniej, 0 = bez zmian / brak danych (kolor w tabeli).</summary>
    public int Sign => Row.Difference is { } d ? Math.Sign(d) : 0;

    public bool IsCurrent => Row.IsCurrent;

    public bool IsFuture => Row.Today is null;
}
