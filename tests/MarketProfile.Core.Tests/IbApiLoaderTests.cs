using MarketProfile.DataFeed.IB;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Testy wyszukiwania biblioteki TWS API (CSharpAPI.dll) na podstawie ścieżki z ustawień.
/// Działają na tymczasowej strukturze folderów – nie wymagają zainstalowanego TWS API.
/// </summary>
public sealed class IbApiLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mp-twsapi-{Guid.NewGuid():N}");

    public IbApiLoaderTests()
    {
        // Struktura jak w paczce TWS API: kilka wersji biblioteki + pliki pośrednie kompilacji w "obj".
        foreach (var folder in new[] { @"client\bin\Release\net48", @"client\bin\Release\net8.0", @"client\obj\Release\net8.0" })
        {
            Directory.CreateDirectory(Path.Combine(_root, folder));
            File.WriteAllText(Path.Combine(_root, folder, "CSharpAPI.dll"), "test");
        }

        File.WriteAllText(Path.Combine(_root, "inny.dll"), "test");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Folder_WybieraWersjeNet8_IPomijaObj()
    {
        var found = IbApiLoader.ResolveDllPath(_root);
        Assert.Equal(Path.Combine(_root, @"client\bin\Release\net8.0\CSharpAPI.dll"), found);
    }

    [Fact]
    public void Plik_JestUzywanyBezposrednio()
    {
        var file = Path.Combine(_root, @"client\bin\Release\net48\CSharpAPI.dll");
        Assert.Equal(file, IbApiLoader.ResolveDllPath($"\"{file}\"")); // cudzysłowy z kopiowania ścieżki są ignorowane
    }

    [Fact]
    public void BlednaSciezka_ZwracaNullICzytelnyKomunikat()
    {
        Assert.Null(IbApiLoader.ResolveDllPath(Path.Combine(_root, "inny.dll")));      // inny plik
        Assert.Null(IbApiLoader.ResolveDllPath(Path.Combine(_root, "nie-istnieje")));  // brak folderu
        Assert.Null(IbApiLoader.ResolveDllPath("   "));

        Assert.False(IbApiLoader.TryValidate(Path.Combine(_root, "nie-istnieje"), out _, out var message));
        Assert.Contains("Nie znaleziono pliku CSharpAPI.dll", message);
    }

    [Fact]
    public void PlikNieBedacyBiblioteka_NieJestAkceptowany()
    {
        // Plik nazywa się CSharpAPI.dll, ale nie jest prawdziwą biblioteką .NET.
        Assert.False(IbApiLoader.TryValidate(_root, out var dll, out var message));
        Assert.NotNull(dll);
        Assert.Contains("Nie można odczytać", message);
    }
}
