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

        // Sprawdzanie historii ticków może trwać wiele minut i trzyma połączenie z TWS – kończymy je razem z oknem.
        Closed += (_, _) => _viewModel.CancelPendingOperations();
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

    // Zakładka „Szeroki rynek”.
    private void OnAddBroadMovingAverageClick(object sender, RoutedEventArgs e) => _viewModel.AddBroadMovingAverage();

    private void OnRemoveBroadMovingAverageClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BroadMovingAverageEditorViewModel average })
        {
            _viewModel.RemoveBroadMovingAverage(average);
        }
    }

    private void OnResetBroadMarketChartClick(object sender, RoutedEventArgs e) => _viewModel.ResetBroadMarketChart();

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

    // --- IBKR Web API: klucze OAuth ---

    private void OnWebApiSignatureKeyClick(object sender, RoutedEventArgs e) =>
        PickWebApiKeyFile(ViewModels.WebApiKeyFile.Signature, "Prywatny klucz podpisu (np. private_signature.pem)");

    private void OnWebApiEncryptionKeyClick(object sender, RoutedEventArgs e) =>
        PickWebApiKeyFile(ViewModels.WebApiKeyFile.Encryption, "Prywatny klucz szyfrowania (np. private_encryption.pem)");

    private void OnWebApiDhParamClick(object sender, RoutedEventArgs e) =>
        PickWebApiKeyFile(ViewModels.WebApiKeyFile.DhParam, "Parametry Diffiego-Hellmana (dhparam.pem)");

    private void PickWebApiKeyFile(ViewModels.WebApiKeyFile kind, string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "Klucze PEM (*.pem)|*.pem|Wszystkie pliki (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SetWebApiKeyFile(kind, dialog.FileName);
        }
    }

    /// <summary>Zapis kluczy: wartości z pól hasła trafiają prosto do zaszyfrowanego magazynu, potem pola są czyszczone.</summary>
    private void OnWebApiSaveKeysClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SaveWebApiKeys(WebApiConsumerKey.Password, WebApiAccessToken.Password, WebApiAccessTokenSecret.Password))
        {
            WebApiConsumerKey.Clear();
            WebApiAccessToken.Clear();
            WebApiAccessTokenSecret.Clear();
        }
    }

    private void OnWebApiDeleteKeysClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Usunąć zapisane klucze IBKR Web API?", "IBKR Web API",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _viewModel.DeleteWebApiKeys();
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Result = _viewModel.TryCreateSettings();
        if (Result is not null)
        {
            DialogResult = true; // zamyka okno
        }
    }
}
