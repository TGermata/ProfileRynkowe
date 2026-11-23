using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MarketProfile.App;

/// <summary>
/// Aplikacja. Nieobsłużone wyjątki (np. z przycisków asynchronicznych, zegarów, zdarzeń) nie zamykają aplikacji
/// w trakcie sesji: są zapisywane do pliku errors.log (w %AppData%\MarketProfile) i pokazywane użytkownikowi.
/// </summary>
public partial class App : Application
{
    private static readonly string ErrorLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarketProfile", "errors.log");

    private bool _showingError;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogError("Nieobsłużony wyjątek (wątek w tle)", args.ExceptionObject as Exception);
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("Nieobsłużony wyjątek", e.Exception);
        e.Handled = true; // aplikacja działa dalej – dane na żywo i alarmy nie mogą zniknąć przez pojedynczy błąd

        // Seria błędów (np. z zegara co 100 ms) – jedno okno naraz.
        if (_showingError)
        {
            return;
        }

        _showingError = true;
        try
        {
            MessageBox.Show(MainWindow,
                $"Wystąpił nieoczekiwany błąd:\n\n{e.Exception.Message}\n\nAplikacja działa dalej. Szczegóły zapisano w pliku:\n{ErrorLogPath}",
                "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _showingError = false;
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogError("Nieobserwowany wyjątek zadania", e.Exception);
        e.SetObserved();
    }

    private static void LogError(string title, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}: {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Zapis dziennika nie może spowodować kolejnego błędu.
        }
    }
}
