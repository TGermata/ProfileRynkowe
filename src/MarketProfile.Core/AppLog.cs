using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace MarketProfile.Core;

/// <summary>Waga wpisu dziennika.</summary>
public enum LogLevel
{
    /// <summary>Zwykłe zdarzenie (połączenie, start, pobranie danych).</summary>
    Info,

    /// <summary>Problem, z którym aplikacja sobie radzi (przerwa w danych, ponowna subskrypcja, brak subskrypcji).</summary>
    Warning,

    /// <summary>Błąd (wyjątek, nieudane połączenie, nieudany zapis).</summary>
    Error,
}

/// <summary>
/// Dziennik zdarzeń i błędów aplikacji – plik tekstowy na każdy dzień (<c>app-RRRR-MM-DD.log</c>), żeby po problemie
/// (przerwa w danych, rozłączenie, błąd) było wiadomo, co się działo i kiedy.
/// Wpisy z dowolnego wątku trafiają do kolejki, a zapisuje je jeden wątek w tle (zapis nie spowalnia odbioru ticków).
/// Przed <see cref="Initialize"/> wpisy czekają w kolejce. Dziennik nigdy nie zgłasza wyjątków.
/// Pliki starsze niż <see cref="RetentionDays"/> dni są usuwane przy starcie.
/// </summary>
public static class AppLog
{
    /// <summary>Ile dni przechowywać pliki dziennika.</summary>
    public const int RetentionDays = 30;

    /// <summary>Najwięcej wpisów czekających w kolejce – przy zalewie komunikatów najstarsze nadmiarowe są pomijane.</summary>
    private const int MaxQueued = 20_000;

    /// <summary>Ten sam komunikat (ta sama kategoria i treść) zapisywany najwyżej raz na tyle sekund – reszta liczona.</summary>
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(60);

    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    private static readonly ConcurrentDictionary<string, (DateTime Last, int Suppressed)> Recent = new();
    private static readonly Lock InitLock = new();
    private static volatile string? _directory;
    private static Thread? _writer;
    private static int _dropped;

    /// <summary>Folder plików dziennika (null – jeszcze nie ustawiony).</summary>
    public static string? Directory => _directory;

    /// <summary>Ścieżka dzisiejszego pliku dziennika (null – dziennik nie działa).</summary>
    public static string? CurrentFile => _directory is { } dir ? Path.Combine(dir, FileName(DateTime.Now)) : null;

    /// <summary>
    /// Ustawia folder dziennika (tworzy go, usuwa w nim stare pliki) i – przy pierwszym wywołaniu – uruchamia zapis w tle.
    /// Kolejne wywołania z innym folderem przenoszą zapis do nowego folderu (zmiana w ustawieniach); dotychczasowe pliki
    /// zostają w starym folderze. Zwraca false, gdy folderu nie da się utworzyć (zapis spróbuje i tak).
    /// </summary>
    public static bool Initialize(string directory)
    {
        lock (InitLock)
        {
            var ok = true;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                DeleteOldFiles(directory);
            }
            catch (Exception)
            {
                ok = false; // brak dostępu do folderu – dziennik nie może zatrzymać aplikacji
            }

            var previous = _directory;
            _directory = directory;
            if (_writer is null)
            {
                _writer = new Thread(WriteLoop) { IsBackground = true, Name = "AppLog", Priority = ThreadPriority.BelowNormal };
                _writer.Start();
            }
            else if (!string.Equals(previous, directory, StringComparison.OrdinalIgnoreCase))
            {
                Info("Dziennik", $"folder dziennika zmieniony z {previous} na {directory}");
            }

            return ok;
        }
    }

    public static void Info(string category, string message) => Write(LogLevel.Info, category, message);

    public static void Warning(string category, string message) => Write(LogLevel.Warning, category, message);

    public static void Error(string category, string message, Exception? exception = null) =>
        Write(LogLevel.Error, category, message, exception);

    /// <summary>
    /// Komunikat stanu źródła danych – waga z treści: „błąd” → błąd; „uwaga”, „brak”, „ponawiam”, „zerwane”,
    /// „zamknięte”, „przekroczono” → ostrzeżenie; reszta – informacja.
    /// </summary>
    public static void Status(string category, string message)
    {
        var text = message.ToLowerInvariant();
        var level = text.Contains("błąd") ? LogLevel.Error
            : text.Contains("uwaga") || text.Contains("brak") || text.Contains("ponawiam") || text.Contains("zerwan") ||
              text.Contains("zamknięt") || text.Contains("przekroczono") || text.Contains("utrat") ||
              text.Contains("broken") || text.Contains("lost") || text.Contains("inactive") ? LogLevel.Warning
            : LogLevel.Info;
        Write(level, category, message);
    }

    /// <summary>
    /// Dopisuje wpis: czas lokalny z milisekundami, waga, kategoria (np. „IB#1”, „Połączenie”), treść i – przy błędzie –
    /// pełny wyjątek ze stosem wywołań. Powtórki tego samego komunikatu w ciągu minuty są zliczane, nie zapisywane.
    /// </summary>
    public static void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        try
        {
            var now = DateTime.Now;
            var key = $"{level}|{category}|{message}";
            var suppressedBefore = 0;
            if (exception is null)
            {
                var repeat = false;
                Recent.AddOrUpdate(key, (now, 0), (_, old) =>
                {
                    if (now - old.Last < RepeatWindow)
                    {
                        repeat = true;
                        return (old.Last, old.Suppressed + 1);
                    }

                    repeat = false;
                    suppressedBefore = old.Suppressed; // ile powtórek pominięto od poprzedniego zapisu
                    return (now, 0);
                });

                if (repeat)
                {
                    return; // powtórka w oknie – tylko policzona
                }

                if (Recent.Count > 2_000)
                {
                    Recent.Clear();
                }
            }

            var text = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(level switch { LogLevel.Warning => "UWAGA", LogLevel.Error => "BŁĄD ", _ => "INFO " })
                .Append(" [").Append(category).Append("] ")
                .Append(message.ReplaceLineEndings(" | "));
            if (suppressedBefore > 0)
            {
                text.Append(" (wcześniej powtórzone jeszcze ").Append(suppressedBefore).Append("×)");
            }

            if (exception is not null)
            {
                text.AppendLine().Append("    ").Append(exception.ToString().ReplaceLineEndings(Environment.NewLine + "    "));
            }

            if (Queue.Count >= MaxQueued)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            Queue.Add(text.ToString());
        }
        catch (Exception)
        {
            // Dziennik nie może spowodować błędu aplikacji.
        }
    }

    /// <summary>Zapisuje oczekujące wpisy (np. przed zamknięciem aplikacji) – czeka najwyżej <paramref name="timeout"/>.</summary>
    public static void Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Queue.Count > 0 && _writer is not null && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Thread.Sleep(50); // ostatnia porcja jest właśnie zapisywana
    }

    private static void WriteLoop()
    {
        var batch = new List<string>();
        foreach (var first in Queue.GetConsumingEnumerable())
        {
            batch.Clear();
            batch.Add(first);
            while (batch.Count < 500 && Queue.TryTake(out var next))
            {
                batch.Add(next);
            }

            var dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
            {
                batch.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} UWAGA [Dziennik] pominięto {dropped} wpisów (zbyt wiele naraz)");
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var file = CurrentFile!;
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!); // folder mógł zostać usunięty
                    File.AppendAllLines(file, batch, Encoding.UTF8);
                    break;
                }
                catch (Exception)
                {
                    Thread.Sleep(200); // plik chwilowo zablokowany (np. otwarty w innym programie) – ponawiamy
                }
            }
        }
    }

    private static string FileName(DateTime day) => $"app-{day:yyyy-MM-dd}.log";

    private static void DeleteOldFiles(string directory)
    {
        var limit = DateTime.Now.Date.AddDays(-RetentionDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "app-*.log"))
        {
            if (File.GetLastWriteTime(file) < limit)
            {
                File.Delete(file);
            }
        }
    }
}
