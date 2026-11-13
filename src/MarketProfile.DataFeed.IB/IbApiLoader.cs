using System.Reflection;
using System.Runtime.Loader;

namespace MarketProfile.DataFeed.IB;

/// <summary>
/// Ładowanie biblioteki klienta Interactive Brokers (TWS API – plik CSharpAPI.dll) ze ścieżki z ustawień.
///
/// Biblioteka nie jest kopiowana do folderu aplikacji. Gdy .NET potrzebuje klas z przestrzeni nazw IBApi,
/// zdarzenie <see cref="AssemblyLoadContext.Resolving"/> wskazuje mu plik wybrany przez użytkownika.
/// Ważne: <see cref="Use"/> trzeba wywołać ZANIM zostanie użyta jakakolwiek klasa korzystająca z IBApi
/// (np. <see cref="IbDataFeed"/>).
/// </summary>
public static class IbApiLoader
{
    /// <summary>Nazwa pliku biblioteki klienta.</summary>
    public const string AssemblyFileName = "CSharpAPI.dll";

    private const string ApiAssemblyName = "CSharpAPI";

    /// <summary>
    /// Kolejność preferowanych wersji biblioteki (nazwy folderów kompilacji TWS API).
    /// Wersja net8.0 jest najlepsza dla aplikacji .NET; net48 jest przeznaczona dla starego .NET Framework.
    /// </summary>
    private static readonly string[] PreferredFrameworks = ["net8.0", "netstandard2.0", "netcoreapp3.1"];

    private static readonly object Sync = new();
    private static string? _dllPath;
    private static bool _registered;

    /// <summary>Ścieżka biblioteki aktualnie używanej przez aplikację (null – jeszcze nie wybrano).</summary>
    public static string? LoadedPath => _dllPath;

    /// <summary>
    /// Zamienia ścieżkę z ustawień (plik CSharpAPI.dll albo folder, w którym go szukamy) na ścieżkę pliku.
    /// W folderze szukamy także w podfolderach – można wskazać np. cały folder „TWS API”.
    /// </summary>
    /// <returns>Pełna ścieżka do CSharpAPI.dll albo null, gdy nie znaleziono.</returns>
    public static string? ResolveDllPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = path.Trim().Trim('"');

        if (File.Exists(path))
        {
            return string.Equals(Path.GetFileName(path), AssemblyFileName, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(path)
                : null;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        // Szukamy CSharpAPI.dll w folderze i podfolderach (z pominięciem pośrednich plików kompilacji "obj"),
        // wybierając najlepiej pasującą wersję (net8.0 > netstandard2.0 > netcoreapp3.1 > pozostałe).
        try
        {
            return Directory
                .EnumerateFiles(path, AssemblyFileName, SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(FrameworkRank)
                .Select(Path.GetFullPath)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null; // np. brak dostępu do któregoś podfolderu
        }
    }

    /// <summary>
    /// Sprawdza ścieżkę z ustawień i przygotowuje komunikat dla użytkownika.
    /// </summary>
    /// <param name="path">Plik CSharpAPI.dll lub folder.</param>
    /// <param name="dllPath">Znaleziona ścieżka pliku.</param>
    /// <param name="message">Opis wyniku (np. wersja biblioteki albo przyczyna błędu).</param>
    public static bool TryValidate(string? path, out string? dllPath, out string message)
    {
        dllPath = ResolveDllPath(path);
        if (dllPath is null)
        {
            message = string.IsNullOrWhiteSpace(path)
                ? "Nie podano ścieżki do biblioteki TWS API."
                : $"Nie znaleziono pliku {AssemblyFileName} w: {path}";
            return false;
        }

        try
        {
            var name = System.Reflection.AssemblyName.GetAssemblyName(dllPath);
            if (!string.Equals(name.Name, ApiAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                message = $"Plik {dllPath} nie jest biblioteką TWS API.";
                return false;
            }

            var framework = Path.GetFileName(Path.GetDirectoryName(dllPath)) ?? string.Empty;
            var warning = string.Equals(framework, "net48", StringComparison.OrdinalIgnoreCase)
                ? " – UWAGA: to wersja dla .NET Framework, wskaż wersję net8.0"
                : string.Empty;
            message = $"Znaleziono {AssemblyFileName} (wersja {name.Version}){warning}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Nie można odczytać {dllPath}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Automatyczne wyszukiwanie biblioteki: folder „TWS API” w folderze aplikacji lub w folderach nadrzędnych
    /// (np. obok rozwiązania), a potem standardowa instalacja „C:\TWS API”.
    /// </summary>
    public static string? AutoDetect()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; directory is not null && level < 8; level++, directory = directory.Parent)
        {
            var found = ResolveDllPath(Path.Combine(directory.FullName, "TWS API"));
            if (found is not null)
            {
                return found;
            }
        }

        return ResolveDllPath(@"C:\TWS API");
    }

    /// <summary>
    /// Ustawia bibliotekę, z której aplikacja będzie korzystać. Wywołać przed pierwszym użyciem klas IBApi.
    /// Biblioteki raz załadowanej nie da się podmienić w działającej aplikacji – wtedy potrzebny jest restart.
    /// </summary>
    /// <exception cref="InvalidOperationException">Gdy ścieżka jest błędna albo załadowano już inną bibliotekę.</exception>
    public static void Use(string? path)
    {
        if (!TryValidate(path, out var dllPath, out var message))
        {
            throw new InvalidOperationException(message);
        }

        lock (Sync)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, ApiAssemblyName, StringComparison.OrdinalIgnoreCase));

            if (loaded is not null && !string.Equals(loaded.Location, dllPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Biblioteka TWS API jest już załadowana z innej lokalizacji – uruchom aplikację ponownie, aby użyć nowej ścieżki.");
            }

            _dllPath = dllPath;

            if (!_registered)
            {
                AssemblyLoadContext.Default.Resolving += OnResolving;
                _registered = true;
            }
        }
    }

    /// <summary>.NET nie znalazł biblioteki w folderze aplikacji – podajemy plik ze ścieżki z ustawień.</summary>
    private static Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name) =>
        string.Equals(name.Name, ApiAssemblyName, StringComparison.OrdinalIgnoreCase) && _dllPath is { } path
            ? context.LoadFromAssemblyPath(path)
            : null;

    /// <summary>Ranking wersji biblioteki według nazwy folderu (mniejszy = lepszy).</summary>
    private static int FrameworkRank(string file)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(file)) ?? string.Empty;
        var index = Array.FindIndex(PreferredFrameworks, f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : PreferredFrameworks.Length + (string.Equals(folder, "net48", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
    }
}
