using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MarketProfile.Core;

namespace MarketProfile.App;

/// <summary>
/// Aplikacja. Dziennik zdarzeń i błędów (<see cref="AppLog"/>) – pliki dzienne w folderze z ustawień
/// (domyślnie %AppData%\MarketProfile\logs).
/// Nieobsłużone wyjątki (np. z przycisków asynchronicznych, zegarów, zdarzeń) nie zamykają aplikacji w trakcie sesji:
/// są zapisywane w dzienniku (ze stosem wywołań) i pokazywane użytkownikowi.
/// </summary>
public partial class App : Application
{
    private bool _showingError;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Folder dziennika ustawia model widoku po wczytaniu ustawień (Ustawienia → Dane i broker → Pobieranie i baza);
        // wpisy sprzed tej chwili czekają w kolejce.
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AppLog.Info("Aplikacja", $"start – wersja {version}, .NET {Environment.Version}, {Environment.OSVersion}, " +
                                 $"kultura {CultureInfo.CurrentCulture.Name}, strefa {TimeZoneInfo.Local.Id}, " +
                                 $"argumenty: {string.Join(' ', e.Args)}");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Error("Aplikacja", $"nieobsłużony wyjątek (wątek w tle){(args.IsTerminating ? " – aplikacja zostanie zamknięta" : string.Empty)}",
                args.ExceptionObject as Exception);
            if (args.IsTerminating)
            {
                AppLog.Flush(TimeSpan.FromSeconds(2));
            }
        };
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("Aplikacja", $"zamknięcie (kod {e.ApplicationExitCode})");
        AppLog.Flush(TimeSpan.FromSeconds(2));
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Aplikacja", "nieobsłużony wyjątek", e.Exception);
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
                $"Wystąpił nieoczekiwany błąd:\n\n{e.Exception.Message}\n\nAplikacja działa dalej. Szczegóły zapisano w dzienniku:\n{AppLog.CurrentFile}",
                "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _showingError = false;
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Aplikacja", "nieobserwowany wyjątek zadania", e.Exception);
        e.SetObserved();
    }
}
