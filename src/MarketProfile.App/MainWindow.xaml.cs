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
        Chart.SimulateBackToRequested += time => _ = _viewModel.SimulateBackToAsync(time);
        _viewModel.SimulationStartFocusRequested += () => Chart.FocusSimulationStart();
        Chart.ToggleExpandRequested += _viewModel.ToggleExpanded;
        Chart.ToggleLockRequested += _viewModel.ToggleLineLock;
        Chart.LockAllRequested += _viewModel.SetAllLinesLocked;
        Chart.SettleRequested += (date, close) => SettleWindow.Edit(this, _viewModel, date, close);

        // Widać początek najstarszego profilu – doczytujemy starszą historię.
        Chart.OlderHistoryRequested += () => _ = _viewModel.LoadOlderHistoryAsync();

        // Po zmianie instrumentu (i po pierwszym wczytaniu danych) – dopasowanie wykresu do ekranu.
        _viewModel.FitRequested += Chart.FitAndPinLatest; // „Dopasuj” po starcie, po zmianie instrumentu i po zmianie czasu symulacji
        _viewModel.PinLatestRequested += () => Chart.PinLatest();

        // Panel narzędzi nad wykresem TPO – w miejscu z ostatniego uruchomienia; po przesunięciu zapamiętujemy położenie.
        ToolPanel.MoveTo(_viewModel.Settings.ToolPanelLeft, _viewModel.Settings.ToolPanelTop);
        ToolPanel.PositionCommitted += _viewModel.SaveToolPanelPosition;
        DisplayPanel.MoveTo(_viewModel.Settings.DisplayPanelLeft, _viewModel.Settings.DisplayPanelTop);
        DisplayPanel.PositionCommitted += _viewModel.SaveDisplayPanelPosition;
        OpeningPanel.MoveTo(_viewModel.Settings.OpeningPanelLeft, _viewModel.Settings.OpeningPanelTop);
        OpeningPanel.PositionCommitted += _viewModel.SaveOpeningPanelPosition;

        // Zakładki w osobnych oknach (kilka monitorów): obsługa nagłówków i układ okien z ostatniego uruchomienia.
        InitializeDetachableTabs();
        RestoreMainWindowLayout();

        // Dane od brokera (na żywo lub opóźnione) łączą się same po otwarciu okna – bez przycisku Start.
        // Parametr --autostart uruchamia od razu także generator.
        Loaded += async (_, _) =>
        {
            RestoreDetachedTabs();

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

    /// <summary>Pole godziny końca symulacji: Enter zatwierdza wpisaną godzinę (tak jak wyjście z pola).</summary>
    private void OnSimEndTimeKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is System.Windows.Controls.TextBox box)
        {
            e.Handled = true;
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        }
    }

    private ProfileStatsWindow? _profileStatsWindow;

    /// <summary>✕ na karcie „Otwarcie”.</summary>
    private void OnCloseOpeningCardClick(object sender, RoutedEventArgs e) => _viewModel.CloseOpeningCard();

    /// <summary>Pomoc – opis parametrów profili (EN wg Daltona i PL).</summary>
    private void OnHelpClick(object sender, RoutedEventArgs e) => HelpWindow.ShowHelp(this);

    /// <summary>F1 – pomoc.</summary>
    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F1)
        {
            HelpWindow.ShowHelp(this);
            e.Handled = true;
        }
    }

    /// <summary>Otwiera (albo przywołuje) okno „Parametry profili” – okno niemodalne, wykres działa dalej.</summary>
    private void OnProfileStatsClick(object sender, RoutedEventArgs e)
    {
        if (_profileStatsWindow is { IsLoaded: true })
        {
            _profileStatsWindow.Activate();
            return;
        }

        _profileStatsWindow = new ProfileStatsWindow(_viewModel, Chart.ShowProfile, Chart.ClearHighlight) { Owner = this };
        _profileStatsWindow.Closed += (_, _) => _profileStatsWindow = null;
        _profileStatsWindow.Show();
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

    private void OnZoomFitClick(object sender, RoutedEventArgs e) => Chart.FitAndPinLatest();

    private void OnCandleFitClick(object sender, RoutedEventArgs e) => CandleChart.FitLatest();

    private int _lastScrolledVolumeIndex = -1;

    /// <summary>
    /// Zaznaczony okres (np. kliknięty na wykresie wolumenu) – przewinięcie tabeli do jego wiersza. Tylko przy zmianie
    /// okresu: tabela co sekundę dostaje nowe wiersze z tym samym zaznaczeniem i nie może wtedy przeskakiwać.
    /// </summary>
    private void OnVolumeGridSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var index = VolumeGrid.SelectedIndex;
        if (index >= 0 && index != _lastScrolledVolumeIndex && VolumeGrid.SelectedItem is { } item)
        {
            VolumeGrid.ScrollIntoView(item);
        }

        if (index >= 0 || e.RemovedItems.Count > 0 && e.AddedItems.Count == 0 && _viewModel.VolumeSelectedIndex < 0)
        {
            _lastScrolledVolumeIndex = index;
        }
    }

    private void OnCandleZoomInClick(object sender, RoutedEventArgs e) => CandleChart.ZoomIn();

    private void OnCandleZoomOutClick(object sender, RoutedEventArgs e) => CandleChart.ZoomOut();

    private void OnCandleAutoScaleClick(object sender, RoutedEventArgs e) => CandleChart.AutoScale();

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

        // Układ okien na następne uruchomienie; okna zakładek zamykają się razem z aplikacją.
        SaveWindowLayout();
        foreach (var window in _detachedWindows.ToList())
        {
            window.CloseWithApplication();
        }

        base.OnClosing(e);
        await _viewModel.DisposeAsync();
    }
}
