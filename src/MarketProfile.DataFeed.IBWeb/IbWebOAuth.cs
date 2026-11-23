using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace MarketProfile.DataFeed.IBWeb;

/// <summary>
/// Kryptografia OAuth 1.0a w wersji IBKR („first party OAuth” dla kont indywidualnych):
///
///  1. <b>prepend</b> – access token secret odszyfrowany prywatnym kluczem szyfrowania (RSA PKCS#1 v1.5), zapisany szesnastkowo;
///  2. <b>Diffie-Hellman</b> – losowe a, wyzwanie A = g^a mod p (p, g z pliku dhparam.pem);
///  3. zapytanie <c>POST /oauth/live_session_token</c> podpisane RSA-SHA256 (kluczem podpisu), z prepend przed base string;
///  4. odpowiedź B → K = B^a mod p → <b>live session token</b> (LST) = HMAC-SHA1(K, prepend);
///     poprawność: HMAC-SHA1(LST, consumer key) musi się zgadzać z podpisem podanym przez IBKR;
///  5. każde kolejne zapytanie podpisywane HMAC-SHA256 kluczem LST.
///
/// Klasa jest bezstanowa (same obliczenia) – łatwo ją testować na kluczach generowanych w teście.
/// Żadna metoda nie zapisuje ani nie wypisuje sekretów.
/// </summary>
public static class IbWebOAuth
{
    public const string RsaSha256 = "RSA-SHA256";
    public const string HmacSha256 = "HMAC-SHA256";

    /// <summary>Odszyfrowuje access token secret i zwraca „prepend” (bajty zapisane szesnastkowo, małe litery).</summary>
    public static string DecryptPrepend(string encryptionKeyPem, string accessTokenSecretBase64)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(encryptionKeyPem);
        var decrypted = rsa.Decrypt(Convert.FromBase64String(accessTokenSecretBase64.Trim()), RSAEncryptionPadding.Pkcs1);
        try
        {
            return Convert.ToHexStringLower(decrypted);
        }
        finally
        {
            Array.Clear(decrypted);
        }
    }

    /// <summary>Odczytuje p i g z pliku dhparam.pem („-----BEGIN DH PARAMETERS-----”, DER: SEQUENCE { p, g }).</summary>
    public static (BigInteger Prime, BigInteger Generator) ReadDhParameters(string dhParamPem)
    {
        var fields = PemEncoding.Find(dhParamPem);
        var der = Convert.FromBase64String(dhParamPem[fields.Base64Data]);
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var prime = sequence.ReadInteger();
        var generator = sequence.ReadInteger();
        if (prime.Sign <= 0 || generator.Sign <= 0)
        {
            throw new InvalidOperationException("Nieprawidłowy plik dhparam.pem.");
        }

        return (prime, generator);
    }

    /// <summary>Losowa wartość tajna a (256 bitów) do wymiany Diffiego-Hellmana.</summary>
    public static BigInteger NewDhSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }

    /// <summary>Wyzwanie A = g^a mod p jako tekst szesnastkowy (bez zer wiodących, jak oczekuje IBKR).</summary>
    public static string DhChallenge(BigInteger generator, BigInteger secret, BigInteger prime) =>
        ToHex(BigInteger.ModPow(generator, secret, prime));

    /// <summary>
    /// Live session token = base64(HMAC-SHA1(K, prepend)), gdzie K = B^a mod p.
    /// K zamieniamy na bajty tak jak Java BigInteger.toByteArray (bajt 0 z przodu, gdy najwyższy bit jest ustawiony) –
    /// tego wymaga IBKR.
    /// </summary>
    public static string ComputeLiveSessionToken(string dhResponseHex, BigInteger secret, BigInteger prime, string prependHex)
    {
        var b = ParseHex(dhResponseHex);
        var k = BigInteger.ModPow(b, secret, prime);
        var key = ToJavaByteArray(k);
        try
        {
            return Convert.ToBase64String(HMACSHA1.HashData(key, Convert.FromHexString(prependHex)));
        }
        finally
        {
            Array.Clear(key);
        }
    }

    /// <summary>Sprawdza LST: HMAC-SHA1(LST, consumer key) w zapisie szesnastkowym = podpis od IBKR.</summary>
    public static bool ValidateLiveSessionToken(string liveSessionToken, string consumerKey, string signatureHex)
    {
        var hash = HMACSHA1.HashData(Convert.FromBase64String(liveSessionToken), Encoding.UTF8.GetBytes(consumerKey));
        return string.Equals(Convert.ToHexStringLower(hash), signatureHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Base string OAuth: [prepend]METODA&amp;zakodowany_URL&amp;zakodowane(k1=v1&amp;k2=v2...) – parametry posortowane po nazwie.
    /// URL bez części zapytania (tak jak w działających klientach IBKR Web API).
    /// </summary>
    public static string BaseString(string method, string url, IReadOnlyDictionary<string, string> parameters, string? prepend = null)
    {
        var parameterString = string.Join("&", parameters
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));
        return $"{prepend}{method.ToUpperInvariant()}&{Encode(url)}&{Encode(parameterString)}";
    }

    /// <summary>Podpis RSA-SHA256 (PKCS#1 v1.5) kluczem podpisu, base64.</summary>
    public static string SignRsa(string signatureKeyPem, string baseString)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(signatureKeyPem);
        return Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(baseString), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    /// <summary>Podpis HMAC-SHA256 kluczem LST, base64.</summary>
    public static string SignHmac(string liveSessionToken, string baseString) =>
        Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(liveSessionToken), Encoding.UTF8.GetBytes(baseString)));

    /// <summary>
    /// Nagłówek Authorization: <c>OAuth realm="…", k="v", …</c> – parametry posortowane, podpis zakodowany do URL.
    /// </summary>
    public static string AuthorizationHeader(string realm, IReadOnlyDictionary<string, string> parameters, string signature)
    {
        var all = parameters.ToDictionary(p => p.Key, p => p.Value);
        all["oauth_signature"] = Encode(signature);
        var parts = all.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}=\"{p.Value}\"");
        return $"OAuth realm=\"{realm}\", " + string.Join(", ", parts);
    }

    /// <summary>Podstawowe parametry OAuth zapytania (bez podpisu).</summary>
    public static Dictionary<string, string> BaseParameters(string consumerKey, string accessToken, string signatureMethod) => new()
    {
        ["oauth_consumer_key"] = consumerKey,
        ["oauth_nonce"] = Nonce(),
        ["oauth_signature_method"] = signatureMethod,
        ["oauth_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        ["oauth_token"] = accessToken,
    };

    /// <summary>Kodowanie procentowe RFC 3986 (litery, cyfry i -._~ bez zmian).</summary>
    public static string Encode(string value) => Uri.EscapeDataString(value);

    /// <summary>Losowy ciąg 16 znaków alfanumerycznych.</summary>
    public static string Nonce()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return RandomNumberGenerator.GetString(chars, 16);
    }

    /// <summary>Liczba dodatnia jako tekst szesnastkowy, małe litery, bez zer wiodących.</summary>
    public static string ToHex(BigInteger value)
    {
        var hex = Convert.ToHexStringLower(value.ToByteArray(isUnsigned: true, isBigEndian: true)).TrimStart('0');
        return hex.Length == 0 ? "0" : hex;
    }

    /// <summary>Tekst szesnastkowy jako liczba dodatnia.</summary>
    public static BigInteger ParseHex(string hex) =>
        BigInteger.Parse("0" + hex.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Bajty liczby jak Java BigInteger.toByteArray dla liczby dodatniej (big-endian, z bajtem znaku, gdy potrzebny).</summary>
    public static byte[] ToJavaByteArray(BigInteger value) => value.ToByteArray(isUnsigned: false, isBigEndian: true);
}
