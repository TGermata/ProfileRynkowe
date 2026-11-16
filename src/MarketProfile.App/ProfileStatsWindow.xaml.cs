using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MarketProfile.App.ViewModels;
using MarketProfile.Core;

namespace MarketProfile.App;

/// <summary>
/// Okno „Parametry profili”: tabela parametrów profili bieżącego instrumentu (zapisane w bazie + bieżące z pamięci),
/// filtr po sesji i datach, eksport CSV. Podwójne kliknięcie wiersza pokazuje profil na wykresie.
/// </summary>
public partial class ProfileStatsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Func<string, DateTime, bool> _showProfile;
    private readonly Action _clearHighlight;
    private bool _refreshing;   // zaznaczenie ustawia odświeżanie tabeli, nie użytkownik – bez przesuwania wykresu
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _dirty;
    private bool _loaded;

    /// <param name="viewModel">Model widoku okna głównego (źródło parametrów).</param>
    /// <param name="showProfile">Pokazuje i podświetla profil (sesja, początek sesji) na wykresie; false = profilu nie ma na wykresie.</param>
    /// <param name="clearHighlight">Usuwa podświetlenie profilu (po zamknięciu okna).</param>
    public ProfileStatsWindow(MainViewModel viewModel, Func<string, DateTime, bool> showProfile, Action clearHighlight)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _showProfile = showProfile;
        _clearHighlight = clearHighlight;

        // Odświeżanie na bieżąco – najwyżej co 2 s (parametry bieżącego profilu zmieniają się z każdą transakcją).
        _viewModel.ProfileStatsUpdated += OnStatsUpdated;
        _refreshTimer.Tick += (_, _) =>
        {
            if (_dirty && AutoRefresh.IsChecked == true)
            {
                Refresh();
            }
        };
        _refreshTimer.Start();

        Loaded += (_, _) =>
        {
            _loaded = true;
            Refresh();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _viewModel.ProfileStatsUpdated -= OnStatsUpdated;
            _clearHighlight();
        };
    }

    private void OnStatsUpdated(object? sender, IReadOnlyList<ProfileStats> e) => _dirty = true;

    private void OnFilterChanged(object sender, EventArgs e)
    {
        if (_loaded)
        {
            Refresh();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>Wiersze wg filtrów: najnowsze profile na górze.</summary>
    private List<ProfileStatsRow> LoadRows()
    {
        var from = FromDate.SelectedDate is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
        var to = ToDate.SelectedDate is { } t ? DateOnly.FromDateTime(t) : (DateOnly?)null;
        var session = (SessionFilter.SelectedItem as ComboBoxItem)?.Content as string;
        var tick = _viewModel.Settings.Instrument.TickSize;

        return _viewModel.GetProfileStatsForView(from, to)
            .Where(s => session is null or "Wszystkie" || s.Session == session)
            .OrderByDescending(s => s.SessionStart)
            .Select(s => new ProfileStatsRow(s, tick))
            .ToList();
    }

    private void Refresh()
    {
        _dirty = false;
        var selected = (Grid.SelectedItem as ProfileStatsRow)?.Stats;
        var rows = LoadRows();
        _refreshing = true;
        try
        {
            Grid.ItemsSource = rows;
            if (selected is not null)
            {
                Grid.SelectedItem = rows.FirstOrDefault(r => r.Stats.Session == selected.Session && r.Stats.SessionStart == selected.SessionStart);
            }
        }
        finally
        {
            _refreshing = false;
        }

        SymbolText.Text = $"Instrument: {_viewModel.ProfileStatsSymbol}";
        FooterText.Text = $"Profili: {rows.Count}. Zakończone sesje są zapisywane w bazie (tabela profile_stats w pliku data\\marketdata.db) – " +
                          "algorytm może je czytać bezpośrednio z bazy albo z eksportu CSV. Bieżąca sesja na żywo jest zapisywana po każdej literze. " +
                          "Kliknięcie wiersza – pokaż i podświetl profil na wykresie (kolor i krycie: Ustawienia → Profile → Linie i znaczniki).";
    }

    /// <summary>Kliknięcie wiersza (zmiana zaznaczenia przez użytkownika) – pokaż i podświetl profil na wykresie.</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing)
        {
            ShowSelected();
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => ShowSelected();

    private void ShowSelected()
    {
        if (Grid.SelectedItem is not ProfileStatsRow row)
        {
            return;
        }

        // Informacja nad tabelą zamiast okna komunikatu – kliknięcie wiersza nie może za każdym razem blokować pracy.
        if (!_showProfile(row.Stats.Session, row.Stats.SessionStart))
        {
            _clearHighlight();
            NoticeText.Text = $"{row.Date} {row.Session}: tego profilu nie ma teraz na wykresie – jest poza wczytaną historią " +
                              "albo jego sesja jest ukryta. Przewiń wykres w lewo (doczyta się starsza historia) albo włącz sesję na pasku narzędzi.";
            NoticeText.Visibility = Visibility.Visible;
        }
        else
        {
            NoticeText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Eksport widocznych wierszy do CSV: liczby jak w bazie (kropka dziesiętna), separator średnik.</summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Eksport parametrów profili",
            Filter = "Plik CSV (*.csv)|*.csv",
            FileName = $"profile_stats_{_viewModel.ProfileStatsSymbol}_{DateTime.Now:yyyyMMdd}.csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var rows = (Grid.ItemsSource as IEnumerable<ProfileStatsRow> ?? []).Select(r => r.Stats).OrderBy(s => s.SessionStart);
        File.WriteAllText(dialog.FileName, ToCsv(_viewModel.ProfileStatsSymbol, rows), new UTF8Encoding(true));
    }

    internal static string ToCsv(string symbol, IEnumerable<ProfileStats> stats)
    {
        static string N(decimal? v) => v?.ToString("0.########", CultureInfo.InvariantCulture) ?? string.Empty;
        static string D(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
        static string L(IEnumerable<decimal> values) => string.Join("|", values.Select(v => N(v)));

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(";",
            "symbol", "session", "trading_date", "session_start", "is_complete", "periods", "last_letter",
            "open", "high", "low", "close", "range", "total_tpo", "volume", "poc", "vpoc", "vah", "val", "va_width", "poc_position",
            "ib_high", "ib_low", "ib_width", "ib_ext_up", "ib_ext_up_letter", "ib_ext_down", "ib_ext_down_letter", "range_extension",
            "shape", "tpo_above_poc", "tpo_below_poc", "single_prints", "excess_high", "excess_low", "poor_high", "poor_low",
            "previous_trading_date", "open_location", "open_type", "open_direction", "value_migration", "va_overlap",
            "naked_pocs", "naked_vpocs"));

        foreach (var s in stats)
        {
            csv.AppendLine(string.Join(";",
                symbol, s.Session, s.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                s.SessionStart.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), s.IsComplete ? "1" : "0",
                s.Periods.ToString(CultureInfo.InvariantCulture), s.LastLetter?.ToString() ?? string.Empty,
                N(s.Open), N(s.High), N(s.Low), N(s.Close), N(s.Range), s.TotalTpo.ToString(CultureInfo.InvariantCulture),
                s.Volume.ToString(CultureInfo.InvariantCulture), N(s.Poc), N(s.VolumePoc), N(s.ValueAreaHigh), N(s.ValueAreaLow),
                N(s.ValueAreaWidth), D(s.PocPosition), N(s.IbHigh), N(s.IbLow), N(s.IbWidth),
                N(s.IbExtensionUp), s.IbExtensionUpLetter?.ToString() ?? string.Empty,
                N(s.IbExtensionDown), s.IbExtensionDownLetter?.ToString() ?? string.Empty, s.RangeExtension.ToString(),
                s.ShapeCode, s.TpoAbovePoc.ToString(CultureInfo.InvariantCulture), s.TpoBelowPoc.ToString(CultureInfo.InvariantCulture),
                string.Join("|", s.SinglePrints.Select(r => $"{N(r.Low)}-{N(r.High)}")),
                s.ExcessHigh.ToString(CultureInfo.InvariantCulture), s.ExcessLow.ToString(CultureInfo.InvariantCulture),
                s.PoorHigh ? "1" : "0", s.PoorLow ? "1" : "0",
                s.PreviousTradingDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                s.OpenLocation.ToString(), s.OpenType.ToString(), s.OpenDirection.ToString(CultureInfo.InvariantCulture),
                s.ValueMigration.ToString(), D(s.ValueAreaOverlap), L(s.NakedPocs), L(s.NakedVolumePocs)));
        }

        return csv.ToString();
    }
}
