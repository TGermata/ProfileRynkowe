using System.Windows;
using MarketProfile.App.Settings;
using MarketProfile.App.ViewModels;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App;

/// <summary>
/// Okno ustawień w czterech grupach: Instrumenty; Profile (TPO, kolory, linie i znaczniki, profil wolumenowy, zegar);
/// Wykres D/W/M (rodzaj prezentacji, kolory, średnie kroczące); Dane i broker (źródło i połączenie, pobieranie i baza, generator i dane testowe).
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(AppSettings settings, MarketDataStore store)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(settings, store);
        DataContext = _viewModel;
    }

    /// <summary>Nowe ustawienia (wypełnione po kliknięciu "Zapisz").</summary>
    public AppSettings? Result { get; private set; }

    /// <summary>Przywraca domyślne kolory i wygląd profilu wolumenowego (zapis dopiero po kliknięciu "Zapisz").</summary>
    private void OnResetAppearanceClick(object sender, RoutedEventArgs e) => _viewModel.ResetAppearance();

    /// <summary>Generuje dane testowe do bazy (w tle – okno pozostaje responsywne).</summary>
    private async void OnGenerateTestDataClick(object sender, RoutedEventArgs e) => await _viewModel.GenerateTestDataAsync();

    // Zakładka „Wykres D/W/M”.
    private void OnAddMovingAverageClick(object sender, RoutedEventArgs e) => _viewModel.AddMovingAverage();

    private void OnRemoveMovingAverageClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MovingAverageEditorViewModel average })
        {
            _viewModel.RemoveMovingAverage(average);
        }
    }

    private void OnResetCandleChartClick(object sender, RoutedEventArgs e) => _viewModel.ResetCandleChart();

    // Zakładka „Instrumenty”.
    private void OnAddInstrumentClick(object sender, RoutedEventArgs e) => _viewModel.AddInstrument();

    private void OnRemoveInstrumentClick(object sender, RoutedEventArgs e) => _viewModel.RemoveSelectedInstrument();

    private void OnRestoreInstrumentsClick(object sender, RoutedEventArgs e) => _viewModel.RestoreBuiltInInstruments();

    private async void OnLookupInstrumentClick(object sender, RoutedEventArgs e) => await _viewModel.LookupSelectedInstrumentAsync();

    /// <summary>Wybór pliku CSharpAPI.dll z dysku.</summary>
    private void OnIbBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Wskaż bibliotekę klienta TWS API (CSharpAPI.dll)",
            Filter = "Biblioteka TWS API (CSharpAPI.dll)|CSharpAPI.dll|Biblioteki (*.dll)|*.dll",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.IbApiPath = dialog.FileName;
        }
    }

    /// <summary>Automatyczne wyszukanie biblioteki TWS API.</summary>
    private void OnIbAutoDetectClick(object sender, RoutedEventArgs e) => _viewModel.AutoDetectIbApi();

    /// <summary>Test połączenia z TWS (w tle – okno pozostaje responsywne).</summary>
    private async void OnIbTestClick(object sender, RoutedEventArgs e) => await _viewModel.TestIbConnectionAsync();

    private async void OnIbTickProbeClick(object sender, RoutedEventArgs e) => await _viewModel.ProbeTickHistoryAsync();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Result = _viewModel.TryCreateSettings();
        if (Result is not null)
        {
            DialogResult = true; // zamyka okno
        }
    }
}
