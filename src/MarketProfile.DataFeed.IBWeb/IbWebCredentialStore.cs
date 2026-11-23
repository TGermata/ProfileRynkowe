using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MarketProfile.DataFeed.IBWeb;

/// <summary>
/// Magazyn kluczy OAuth IBKR Web API: plik zaszyfrowany przez Windows (DPAPI, zakres „bieżący użytkownik”) –
/// odczytać go może tylko ten sam użytkownik Windows na tym samym komputerze.
///
/// Domyślna lokalizacja: %AppData%\MarketProfile\ibkr-web-api.secret – POZA repozytorium Git
/// (dodatkowo *.secret jest w .gitignore). Klucze nigdy nie trafiają do settings.json.
/// </summary>
public sealed class IbWebCredentialStore
{
    /// <summary>Dodatkowa „entropia” DPAPI – inna aplikacja tego samego użytkownika nie odszyfruje pliku bez niej.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MarketProfile/IBKR-WebAPI/v1");

    public IbWebCredentialStore(string? path = null)
    {
        Path = path ?? DefaultPath;
    }

    /// <summary>Domyślny plik: %AppData%\MarketProfile\ibkr-web-api.secret.</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarketProfile", "ibkr-web-api.secret");

    /// <summary>Ścieżka pliku z zaszyfrowanymi kluczami.</summary>
    public string Path { get; }

    /// <summary>Czy klucze zostały zapisane.</summary>
    public bool Exists => File.Exists(Path);

    /// <summary>Zapisuje klucze (zaszyfrowane). Nadpisuje poprzednie.</summary>
    public void Save(IbWebCredentials credentials)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new StoredCredentials
        {
            ConsumerKey = credentials.ConsumerKey.Trim(),
            AccessToken = credentials.AccessToken.Trim(),
            AccessTokenSecret = credentials.AccessTokenSecret.Trim(),
            SignatureKeyPem = credentials.SignatureKeyPem,
            EncryptionKeyPem = credentials.EncryptionKeyPem,
            DhParamPem = credentials.DhParamPem,
        });

        try
        {
            var encrypted = Dpapi.Protect(json, Entropy);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            Array.Clear(json);
        }
    }

    /// <summary>Wczytuje klucze; null, gdy nie zapisano ich jeszcze.</summary>
    public IbWebCredentials? Load()
    {
        if (!Exists)
        {
            return null;
        }

        var json = Dpapi.Unprotect(File.ReadAllBytes(Path), Entropy);
        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredentials>(json) ?? new StoredCredentials();
            return new IbWebCredentials
            {
                ConsumerKey = stored.ConsumerKey ?? string.Empty,
                AccessToken = stored.AccessToken ?? string.Empty,
                AccessTokenSecret = stored.AccessTokenSecret ?? string.Empty,
                SignatureKeyPem = stored.SignatureKeyPem ?? string.Empty,
                EncryptionKeyPem = stored.EncryptionKeyPem ?? string.Empty,
                DhParamPem = stored.DhParamPem ?? string.Empty,
            };
        }
        finally
        {
            Array.Clear(json);
        }
    }

    /// <summary>Usuwa zapisane klucze.</summary>
    public void Delete()
    {
        if (Exists)
        {
            File.Delete(Path);
        }
    }

    /// <summary>Postać zapisywana w pliku (przed zaszyfrowaniem).</summary>
    private sealed class StoredCredentials
    {
        public string? ConsumerKey { get; init; }
        public string? AccessToken { get; init; }
        public string? AccessTokenSecret { get; init; }
        public string? SignatureKeyPem { get; init; }
        public string? EncryptionKeyPem { get; init; }
        public string? DhParamPem { get; init; }
    }

    /// <summary>Szyfrowanie Windows DPAPI (crypt32.dll) – bez dodatkowych pakietów.</summary>
    private static class Dpapi
    {
        private const int CryptProtectUiForbidden = 0x1;

        public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, protect: true);

        public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, protect: false);

        private static byte[] Run(byte[] data, byte[] entropy, bool protect)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Szyfrowanie kluczy (DPAPI) działa tylko w Windows.");
            }

            var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
            var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            var output = default(DataBlob);
            try
            {
                var input = new DataBlob { Size = data.Length, Data = dataHandle.AddrOfPinnedObject() };
                var extra = new DataBlob { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
                var ok = protect
                    ? CryptProtectData(ref input, null, ref extra, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
                if (!ok)
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    throw new InvalidOperationException(protect
                        ? $"Nie udało się zaszyfrować kluczy: {error}"
                        : $"Nie udało się odszyfrować kluczy (zapisane przez innego użytkownika Windows lub uszkodzone): {error}");
                }

                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                dataHandle.Free();
                entropyHandle.Free();
                if (output.Data != IntPtr.Zero)
                {
                    LocalFree(output.Data);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);
    }
}
