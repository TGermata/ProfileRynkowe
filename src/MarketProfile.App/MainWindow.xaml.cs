using System.ComponentModel;
using System.Windows;
using MarketProfile.App.ViewModels;
using MarketProfile.Core;

namespace MarketProfile.App;

/// <summary>
/// Główne okno aplikacji. Kod okna jest minimalny – cała logika jest w <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Akcje z wykresu przekazujemy do modelu widoku (okna dialogowe otwieramy tutaj, bo to część widoku).
        Chart.AddLineRequested += price => EditLine(_viewModel.CreateLineTemplate(price), isNew: true);
        Chart.EditLineRequested += line => EditLine(line, isNew: false);
        Chart.DeleteLineRequested += _viewModel.DeleteLine;
        Chart.LineMoved += _viewModel.MoveLine;
        Chart.SplitRequested += _viewModel.SplitProfile;
        Chart.MergeRequested += _viewModel.MergeProfile;
        Chart.ToggleExpandRequested += _viewModel.ToggleExpanded;
        Chart.ToggleLockRequested += _viewModel.ToggleLineLock;
        Chart.LockAllRequested += _viewModel.SetAllLinesLocked;

        // Widać początek najstarszego profilu – doczytujemy starszą historię.
        Chart.OlderHistoryRequested += () => _ = _viewModel.LoadOlderHistoryAsync();

        // Po zmianie instrumentu (i po pierwszym wczytaniu danych) – dopasowanie wykresu do ekranu.
        _viewModel.FitRequested += Chart.FitToScreen;

        // Dane od brokera (na żywo lub opóźnione) łączą się same po otwarciu okna – bez przycisku Start.
        // Parametr --autostart uruchamia od razu także generator.
        Loaded += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--autostart"))
            {
                await _viewModel.StartAsync();
            }
            else
            {
                await _viewModel.AutoStartBrokerAsync();
            }
        };
    }

    // --- Pole godziny symulacji: w trakcie edycji zegar go nie nadpisuje; Enter / wyjście z pola = przejście do tej godziny. ---

    private void OnSimTimeGotFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        _viewModel.BeginEditSimTime();

    private async void OnSimTimeLostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        await _viewModel.EndEditSimTimeAsync();

    private void OnSimTimeKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            Chart.Focus(); // utrata fokusu pola zatwierdza godzinę
            System.Windows.Input.Keyboard.ClearFocus();
        }
    }

    /// <summary>Otwiera okno ustawień; po zatwierdzeniu przekazuje nowe ustawienia do modelu widoku.</summary>
    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_viewModel.Settings, _viewModel.GetStore()) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } newSettings)
        {
            await _viewModel.ApplySettingsAsync(newSettings);
        }
    }

    // Przyciski powiększania na pasku narzędzi.
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Chart.ZoomOut();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => Chart.ZoomIn();

    private void OnZoomFitClick(object sender, RoutedEventArgs e) => Chart.FitToScreen();

    /// <summary>Otwiera okno edycji linii; po zatwierdzeniu dodaje nową linię lub zapisuje zmiany.</summary>
    private void EditLine(PriceLine line, bool isNew)
    {
        var dialog = new PriceLineWindow(line, _viewModel.Settings.Instrument.TickSize) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
        {
            return;
        }

        if (isNew)
        {
            _viewModel.AddLine(result);
        }
        else
        {
            _viewModel.UpdateLine(result);
        }
    }

    /// <summary>
    /// Przy zamykaniu okna pytamy, czy na pewno wyjść z aplikacji.
    /// Po potwierdzeniu zatrzymujemy pobieranie danych (rejestrator zapisuje ostatnie ticki do bazy).
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Czy na pewno chcesz wyjść z aplikacji?",
            "Zamykanie aplikacji",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true; // użytkownik się rozmyślił – okno zostaje otwarte
            return;
        }

        base.OnClosing(e);
        await _viewModel.DisposeAsync();
    }
}
