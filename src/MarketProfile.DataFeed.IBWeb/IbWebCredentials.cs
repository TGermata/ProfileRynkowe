namespace MarketProfile.DataFeed.IBWeb;

/// <summary>
/// Klucze OAuth 1.0a do IBKR Web API (wygenerowane w portalu IBKR dla konta).
///
/// BEZPIECZEŃSTWO: obiekt istnieje tylko w pamięci. Na dysku jest wyłącznie w postaci zaszyfrowanej
/// (<see cref="IbWebCredentialStore"/>, DPAPI – tylko bieżący użytkownik Windows), poza repozytorium.
/// <see cref="ToString"/> nie zwraca żadnego sekretu, więc przypadkowe wypisanie obiektu do logu nic nie ujawnia.
/// </summary>
public sealed record IbWebCredentials
{
    /// <summary>Consumer key (9 znaków, np. nadany w portalu IBKR).</summary>
    public string ConsumerKey { get; init; } = string.Empty;

    /// <summary>Access token.</summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>Access token secret – zaszyfrowany przez IBKR kluczem publicznym szyfrowania (base64).</summary>
    public string AccessTokenSecret { get; init; } = string.Empty;

    /// <summary>Prywatny klucz RSA do podpisu (PEM).</summary>
    public string SignatureKeyPem { get; init; } = string.Empty;

    /// <summary>Prywatny klucz RSA do szyfrowania (PEM) – odszyfrowuje access token secret.</summary>
    public string EncryptionKeyPem { get; init; } = string.Empty;

    /// <summary>Parametry Diffiego-Hellmana (plik dhparam.pem).</summary>
    public string DhParamPem { get; init; } = string.Empty;

    /// <summary>Realm OAuth: „limited_poa” dla kont klientów; „test_realm” dla testowego consumer key TESTCONS.</summary>
    public string Realm => string.Equals(ConsumerKey, "TESTCONS", StringComparison.Ordinal) ? "test_realm" : "limited_poa";

    /// <summary>Czy wszystkie pola są wypełnione.</summary>
    public bool IsComplete => Missing().Count == 0;

    /// <summary>Nazwy brakujących pól (do komunikatu dla użytkownika).</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(ConsumerKey)) missing.Add("Consumer key");
        if (string.IsNullOrWhiteSpace(AccessToken)) missing.Add("Access token");
        if (string.IsNullOrWhiteSpace(AccessTokenSecret)) missing.Add("Access token secret");
        if (string.IsNullOrWhiteSpace(SignatureKeyPem)) missing.Add("klucz podpisu");
        if (string.IsNullOrWhiteSpace(EncryptionKeyPem)) missing.Add("klucz szyfrowania");
        if (string.IsNullOrWhiteSpace(DhParamPem)) missing.Add("dhparam.pem");
        return missing;
    }

    /// <summary>Opis bez sekretów – nigdy nie zwraca wartości kluczy.</summary>
    public override string ToString() => $"IbWebCredentials (consumer key: {Mask(ConsumerKey)}, kompletne: {IsComplete})";

    /// <summary>Maskowanie: tylko pierwsze 2 znaki, reszta gwiazdki (do wyświetlenia, że klucz jest zapisany).</summary>
    public static string Mask(string value) =>
        string.IsNullOrEmpty(value) ? "(brak)" : value.Length <= 2 ? "**" : value[..2] + new string('*', Math.Min(6, value.Length - 2));
}
