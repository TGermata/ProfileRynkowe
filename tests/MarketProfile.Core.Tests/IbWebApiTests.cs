using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketProfile.DataFeed.IBWeb;

namespace MarketProfile.Core.Tests;

/// <summary>
/// Testy IBKR Web API bez sieci. Wszystkie klucze są generowane w trakcie testu – w repozytorium nie ma żadnych
/// prawdziwych kluczy ani tokenów.
/// </summary>
public class IbWebApiTests
{
    // Liczba pierwsza Mersenne'a 2^521 − 1 – wystarczy do sprawdzenia arytmetyki DH (prawdziwy plik IBKR ma 2048 bitów).
    private static readonly BigInteger TestPrime = BigInteger.Pow(2, 521) - 1;

    private static string DhParamPem(BigInteger prime, BigInteger generator)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(prime);
            writer.WriteInteger(generator);
        }

        return new string(PemEncoding.Write("DH PARAMETERS", writer.Encode()));
    }

    [Fact]
    public void ReadDhParameters_ReadsPrimeAndGenerator()
    {
        var (prime, generator) = IbWebOAuth.ReadDhParameters(DhParamPem(TestPrime, 2));

        Assert.Equal(TestPrime, prime);
        Assert.Equal(new BigInteger(2), generator);
    }

    [Fact]
    public void DecryptPrepend_ReturnsHexOfDecryptedSecret()
    {
        using var rsa = RSA.Create(2048);
        var secret = RandomNumberGenerator.GetBytes(32);
        var encrypted = Convert.ToBase64String(rsa.Encrypt(secret, RSAEncryptionPadding.Pkcs1));

        var prepend = IbWebOAuth.DecryptPrepend(rsa.ExportPkcs8PrivateKeyPem(), encrypted);

        Assert.Equal(Convert.ToHexStringLower(secret), prepend);
    }

    [Fact]
    public void LiveSessionToken_MatchesServerSideComputation_AndValidates()
    {
        const string consumerKey = "TESTKEY01";
        var prepend = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var generator = new BigInteger(2);

        // Klient: wyzwanie A.
        var a = IbWebOAuth.NewDhSecret();
        var challenge = IbWebOAuth.DhChallenge(generator, a, TestPrime);

        // „Serwer IBKR”: B, K = A^b mod p, LST i jego podpis.
        var b = IbWebOAuth.NewDhSecret();
        var dhResponse = IbWebOAuth.ToHex(BigInteger.ModPow(generator, b, TestPrime));
        var k = BigInteger.ModPow(IbWebOAuth.ParseHex(challenge), b, TestPrime);
        var serverLst = Convert.ToBase64String(HMACSHA1.HashData(IbWebOAuth.ToJavaByteArray(k), Convert.FromHexString(prepend)));
        var serverSignature = Convert.ToHexStringLower(HMACSHA1.HashData(Convert.FromBase64String(serverLst), Encoding.UTF8.GetBytes(consumerKey)));

        var lst = IbWebOAuth.ComputeLiveSessionToken(dhResponse, a, TestPrime, prepend);

        Assert.Equal(serverLst, lst);
        Assert.True(IbWebOAuth.ValidateLiveSessionToken(lst, consumerKey, serverSignature));
        Assert.False(IbWebOAuth.ValidateLiveSessionToken(lst, "INNYKLUCZ", serverSignature));
    }

    [Fact]
    public void ToJavaByteArray_AddsSignByte_WhenHighestBitSet()
    {
        Assert.Equal(new byte[] { 0x00, 0x80 }, IbWebOAuth.ToJavaByteArray(new BigInteger(0x80)));
        Assert.Equal(new byte[] { 0x7F }, IbWebOAuth.ToJavaByteArray(new BigInteger(0x7F)));
        Assert.Equal(new byte[] { 0x01, 0x00 }, IbWebOAuth.ToJavaByteArray(new BigInteger(0x100)));
    }

    [Fact]
    public void ToHex_HasNoLeadingZeros()
    {
        Assert.Equal("f", IbWebOAuth.ToHex(new BigInteger(15)));
        Assert.Equal("100", IbWebOAuth.ToHex(new BigInteger(256)));
        Assert.Equal(new BigInteger(0xABCDEF), IbWebOAuth.ParseHex("abcdef"));
    }

    [Fact]
    public void BaseString_SortsAndEncodesParameters_WithPrepend()
    {
        var parameters = new Dictionary<string, string> { ["oauth_b"] = "2", ["oauth_a"] = "1" };

        var baseString = IbWebOAuth.BaseString("post", "https://api.ibkr.com/v1/api/tickle", parameters, "ab");

        Assert.Equal("abPOST&https%3A%2F%2Fapi.ibkr.com%2Fv1%2Fapi%2Ftickle&oauth_a%3D1%26oauth_b%3D2", baseString);
    }

    [Fact]
    public void SignRsa_IsVerifiableWithPublicKey()
    {
        using var rsa = RSA.Create(2048);
        const string baseString = "POST&https%3A%2F%2Fapi.ibkr.com&x%3D1";

        var signature = IbWebOAuth.SignRsa(rsa.ExportPkcs8PrivateKeyPem(), baseString);

        Assert.True(rsa.VerifyData(Encoding.UTF8.GetBytes(baseString), Convert.FromBase64String(signature),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void SignHmac_UsesDecodedLiveSessionTokenAsKey()
    {
        var key = RandomNumberGenerator.GetBytes(20);
        var expected = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("GET&x&y")));

        Assert.Equal(expected, IbWebOAuth.SignHmac(Convert.ToBase64String(key), "GET&x&y"));
    }

    [Fact]
    public void AuthorizationHeader_HasRealmFirst_SortedParameters_AndEncodedSignature()
    {
        var parameters = new Dictionary<string, string> { ["oauth_token"] = "t", ["oauth_consumer_key"] = "c" };

        var header = IbWebOAuth.AuthorizationHeader("limited_poa", parameters, "a+b/c=");

        Assert.Equal("OAuth realm=\"limited_poa\", oauth_consumer_key=\"c\", oauth_signature=\"a%2Bb%2Fc%3D\", oauth_token=\"t\"", header);
    }

    [Fact]
    public void Credentials_ToString_DoesNotRevealSecrets()
    {
        var credentials = new IbWebCredentials
        {
            ConsumerKey = "ABCDEFGHI",
            AccessToken = "token-value-123",
            AccessTokenSecret = "secret-value-456",
        };

        var text = credentials.ToString();

        Assert.DoesNotContain("ABCDEFGHI", text);
        Assert.DoesNotContain("token-value-123", text);
        Assert.DoesNotContain("secret-value-456", text);
        Assert.Equal("limited_poa", credentials.Realm);
        Assert.Contains("klucz podpisu", credentials.Missing());
    }

    [Fact]
    public void CredentialStore_RoundTrips_AndFileIsNotPlainText()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI – tylko Windows
        }

        var path = Path.Combine(Path.GetTempPath(), $"mp-test-{Guid.NewGuid():N}.secret");
        try
        {
            var store = new IbWebCredentialStore(path);
            var credentials = new IbWebCredentials
            {
                ConsumerKey = "FAKEKEY12",
                AccessToken = "fake-access-token",
                AccessTokenSecret = "ZmFrZS1zZWNyZXQ=",
                // Celowo nie-PEM: magazyn nie sprawdza treści, a w repozytorium nie ma nic, co wygląda jak klucz.
                SignatureKeyPem = "fake-signature-key",
                EncryptionKeyPem = "fake-encryption-key",
                DhParamPem = "fake-dh-parameters",
            };

            store.Save(credentials);
            var loaded = store.Load();
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));

            Assert.Equal(credentials, loaded);
            Assert.DoesNotContain("FAKEKEY12", raw);
            Assert.DoesNotContain("fake-access-token", raw);
            Assert.DoesNotContain("fake-signature-key", raw);

            store.Delete();
            Assert.False(store.Exists);
            Assert.Null(store.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("5 Y", "5y")]
    [InlineData("10 D", "10d")]
    [InlineData("2 W", "2w")]
    [InlineData("6 M", "6m")]
    public void ToPeriod_ConvertsTwsDuration(string duration, string expected) =>
        Assert.Equal(expected, IbWebFormats.ToPeriod(duration));

    [Fact]
    public void Futures_FrontMonthAndContractMonth()
    {
        using var json = JsonDocument.Parse("""
            {"ES":[
              {"symbol":"ES","conid":3,"expirationDate":20260320},
              {"symbol":"ES","conid":1,"expirationDate":20251219},
              {"symbol":"ES","conid":2,"expirationDate":20250919}
            ]}
            """);
        var contracts = IbWebFormats.ParseFutures(json.RootElement, "ES");

        var front = IbWebFormats.OrderCandidates(contracts, string.Empty, new DateTime(2025, 10, 1));
        var march = IbWebFormats.OrderCandidates(contracts, "202603", new DateTime(2025, 10, 1));

        Assert.Equal(3, contracts.Count);
        Assert.Equal([1L, 3L], front.Select(c => c.Conid));
        Assert.Equal(3L, Assert.Single(march).Conid);
    }

    [Fact]
    public void ParseMarketData_ReadsSmdMessages_AndIgnoresOthers()
    {
        var data = IbWebMarketStream.ParseMarketData("""{"topic":"smd+495512551","conid":495512551,"_updated":1700000000000,"31":"5012.25","7762":"1200"}""");

        Assert.NotNull(data);
        Assert.Equal(495512551L, data.Value.Conid);
        Assert.Equal("5012.25", data.Value.Fields["31"]);
        Assert.Null(IbWebMarketStream.ParseMarketData("""{"topic":"system","hb":1}"""));
        Assert.Null(IbWebMarketStream.ParseMarketData("tic"));
    }

    private static Dictionary<string, string> Fields(params (string Key, string Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value);

    [Fact]
    public void TickAssembler_UsesCumulativeVolumeIncrements()
    {
        var assembler = new IbWebTickAssembler(isIndex: false);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        // Pierwsza wiadomość: cena i wolumen dnia (punkt odniesienia) → transakcja tylko z ceną.
        var first = assembler.Process(Fields(("31", "5012.25"), ("7762", "1000"), ("6509", "RpB")), now);
        // Przyrost wolumenu 15 przy tej samej cenie.
        var second = assembler.Process(Fields(("7762", "1015")), now);
        // Nowa cena i przyrost 3.
        var third = assembler.Process(Fields(("31", "5012.50"), ("7762", "1018")), now);

        var tick = Assert.Single(first);
        Assert.Equal(5012.25m, tick.Price);
        Assert.Equal(0, tick.Volume);
        Assert.Equal(15, Assert.Single(second).Volume);
        Assert.Equal((5012.50m, 3L), (Assert.Single(third).Price, third[0].Volume));
        Assert.False(assembler.IsDelayed);
    }

    [Fact]
    public void TickAssembler_DelayedData_ShiftsTime_AndSkipsClosePrice()
    {
        var assembler = new IbWebTickAssembler(isIndex: false);
        var updated = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

        var close = assembler.Process(Fields(("31", "C5000.00"), ("6509", "DpB")), updated);
        var trade = assembler.Process(Fields(("31", "5001.00"), ("7059", "2"), ("_updated", "1700000000000")), updated);

        Assert.Empty(close);
        Assert.True(assembler.IsDelayed);
        var tick = Assert.Single(trade);
        Assert.Equal(updated.AddMinutes(-IbWebTickAssembler.DelayedMinutes), tick.Time);
        Assert.Equal(2, tick.Volume); // bez wolumenu narastającego – wielkość ostatniej transakcji
    }

    [Fact]
    public void TickAssembler_Index_HasZeroVolume()
    {
        var assembler = new IbWebTickAssembler(isIndex: true);

        var ticks = assembler.Process(Fields(("31", "6001.15"), ("7762", "999")), DateTimeOffset.UtcNow);

        Assert.Equal(0, Assert.Single(ticks).Volume);
    }

    [Theory]
    [InlineData("1,234", 1234)]
    [InlineData("1.5K", 1500)]
    [InlineData("2M", 2000000)]
    public void TryParseVolume_HandlesFormats(string text, double expected)
    {
        Assert.True(IbWebTickAssembler.TryParseVolume(text, out var volume));
        Assert.Equal((decimal)expected, volume);
    }

    [Fact]
    public void TradingDate_EveningBarBelongsToNextDay()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var evening = new DateTimeOffset(2025, 3, 2, 18, 0, 0, TimeSpan.FromHours(-5)).ToUnixTimeMilliseconds(); // niedziela 18:00
        var midnight = new DateTimeOffset(2025, 3, 3, 0, 0, 0, TimeSpan.FromHours(-5)).ToUnixTimeMilliseconds();

        Assert.Equal(new DateTimeOffset(2025, 3, 3, 0, 0, 0, TimeSpan.Zero), IbWebFormats.TradingDate(evening, newYork));
        Assert.Equal(new DateTimeOffset(2025, 3, 3, 0, 0, 0, TimeSpan.Zero), IbWebFormats.TradingDate(midnight, newYork));
    }
}
