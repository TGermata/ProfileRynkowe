using System.IO;
using System.Text.Json;

namespace MarketProfile.App.Settings;

/// <summary>
/// Bezpieczny odczyt i zapis plików JSON z ustawieniami:
///  - zapis atomowy (najpierw plik tymczasowy, potem podmiana) – awaria w trakcie zapisu nie niszczy pliku,
///  - uszkodzony plik nie jest po cichu nadpisywany: odkładamy jego kopię (*.corrupt-…) i zgłaszamy problem,
///  - błąd zapisu (np. plik zablokowany przez antywirus / synchronizację) nie wywraca aplikacji – jest zgłaszany.
/// </summary>
public static class JsonFileStore
{
    private static readonly List<string> PendingProblems = [];
    private static readonly Lock ProblemsLock = new();
    private static Action<string>? _problem;

    /// <summary>
    /// Problem z plikiem ustawień (uszkodzony plik, nieudany zapis). Problemy zgłoszone, zanim ktokolwiek
    /// zasubskrybował zdarzenie (np. przy wczytywaniu ustawień na starcie), są dostarczane przy subskrypcji.
    /// </summary>
    public static event Action<string>? Problem
    {
        add
        {
            string[] pending;
            lock (ProblemsLock)
            {
                _problem += value;
                pending = [.. PendingProblems];
                PendingProblems.Clear();
            }

            foreach (var message in pending)
            {
                value?.Invoke(message);
            }
        }
        remove
        {
            lock (ProblemsLock)
            {
                _problem -= value;
            }
        }
    }

    /// <summary>Wczytuje plik; brak pliku → <c>default</c>, uszkodzony plik → kopia zapasowa, zgłoszenie i <c>default</c>.</summary>
    public static T? Read<T>(string path, JsonSerializerOptions options)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
        }
        catch (Exception ex)
        {
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Copy(path, backup, overwrite: true);
            }
            catch (Exception)
            {
                backup = "(nie udało się zrobić kopii)";
            }

            Report($"Nie udało się odczytać pliku {Path.GetFileName(path)} ({ex.Message}) – użyto wartości domyślnych. " +
                   $"Kopia uszkodzonego pliku: {backup}");
            return default;
        }
    }

    /// <summary>Zapisuje plik atomowo; przy błędzie zgłasza problem i zwraca false (bez wyjątku).</summary>
    public static bool Write<T>(string path, T value, JsonSerializerOptions options)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(value, options));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // Plik tymczasowy zostanie nadpisany przy następnym zapisie.
            }

            Report($"Nie udało się zapisać pliku {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    private static void Report(string message)
    {
        Action<string>? handler;
        lock (ProblemsLock)
        {
            handler = _problem;
            if (handler is null)
            {
                PendingProblems.Add(message);
                return;
            }
        }

        handler(message);
    }
}
