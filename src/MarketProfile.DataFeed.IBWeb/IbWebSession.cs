using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace MarketProfile.DataFeed.IBWeb;

/// <summary>Zapytanie odrzucone przez limit IBKR Web API (HTTP 429).</summary>
public sealed class IbWebPacingException(string message) : Exception(message);

/// <summary>
/// Wspólna sesja IBKR Web API dla całej aplikacji (dane na żywo, pobieranie w tle, ustawienia):
///  - jeden live session token (LST) i jedna sesja brokerska – IBKR pozwala na jedną sesję na użytkownika,
///  - podtrzymanie sesji (<c>/tickle</c> co minutę), ponowne logowanie po wygaśnięciu LST (HTTP 401),
///  - kolejkowanie zapytań (odstęp między zapytaniami – limit IBKR ok. 10 zapytań/s),
///  - wspólny strumień WebSocket (<see cref="IbWebMarketStream"/>).
///
/// Klucze są odczytywane z <see cref="IbWebCredentialStore"/> dopiero przy logowaniu i trzymane tylko w pamięci.
/// </summary>
public sealed class IbWebSession : IAsyncDisposable
{
    public const string BaseUrl = "https://api.ibkr.com/v1/api";
    public const string WebSocketUrl = "wss://api.ibkr.com/v1/api/ws";
    internal const string UserAgent = "MarketProfile/1.0";

    private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan TickleInterval = TimeSpan.FromSeconds(60);

    private static readonly Lock SharedLock = new();
    private static IbWebSession? _shared;

    private readonly IbWebCredentialStore _store;
    private readonly bool _compete;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private DateTimeOffset _lastRequestAt;

    private IbWebCredentials? _credentials;
    private string? _liveSessionToken;
    private DateTimeOffset _lstExpires;
    private string? _sessionCookie;
    private bool _ready;
    private CancellationTokenSource? _tickleCts;
    private IbWebMarketStream? _stream;

    private IbWebSession(IbWebCredentialStore store, bool compete)
    {
        _store = store;
        _compete = compete;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Komunikaty o stanie sesji (do paska statusu) – nigdy nie zawierają kluczy.</summary>
    public static event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Wspólna sesja została zamknięta i zastąpiona (zmiana kluczy, inne „Przejmij sesję”). Źródła danych, które
    /// trzymały starą sesję, łączą się ponownie (<see cref="TryGetShared"/>). Wywoływane poza blokadą.
    /// </summary>
    public static event Action? SharedReplaced;

    /// <summary>Sesja zamknięta – nie wolno już z niej korzystać (trzeba pobrać nową przez <see cref="Shared"/>).</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Wspólna sesja aplikacji. <paramref name="compete"/> = true: przejęcie sesji brokerskiej, nawet jeśli
    /// ten sam użytkownik jest zalogowany w TWS (TWS zostanie rozłączony).
    /// </summary>
    public static IbWebSession Shared(bool compete)
    {
        IbWebSession? old;
        IbWebSession created;
        lock (SharedLock)
        {
            if (_shared is { } existing && existing._compete == compete)
            {
                return existing;
            }

            old = _shared;
            created = _shared = new IbWebSession(new IbWebCredentialStore(), compete);
        }

        if (old is not null)
        {
            _ = Task.Run(async () =>
            {
                await DisposeQuietlyAsync(old).ConfigureAwait(false);
                SharedReplaced?.Invoke();
            });
        }

        return created;
    }

    /// <summary>
    /// Wspólna sesja dla źródła, którego sesję zamknięto: istniejąca (z tym samym „Przejmij sesję”) albo nowa,
    /// jeśli żadnej nie ma. Null, gdy wspólną sesję ma teraz inne ustawienie „Przejmij sesję” – nie odbieramy jej,
    /// bo dwa źródła na zmianę przejmowałyby sobie sesję w nieskończoność.
    /// </summary>
    public static IbWebSession? TryGetShared(bool compete)
    {
        lock (SharedLock)
        {
            if (_shared is { } existing)
            {
                return existing._compete == compete ? existing : null;
            }
        }

        return Shared(compete);
    }

    /// <summary>Zamyka wspólną sesję (np. po zmianie kluczy albo przy zamykaniu aplikacji).</summary>
    public static async Task ResetSharedAsync()
    {
        IbWebSession? old;
        lock (SharedLock)
        {
            old = _shared;
            _shared = null;
        }

        if (old is not null)
        {
            await DisposeQuietlyAsync(old).ConfigureAwait(false);
            SharedReplaced?.Invoke();
        }
    }

    private static async Task DisposeQuietlyAsync(IbWebSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status($"IBKR Web API: zamykanie sesji – {ex.Message}");
        }
    }

    /// <summary>Strumień notowań WebSocket (tworzony przy pierwszym użyciu).</summary>
    public IbWebMarketStream Stream
    {
        get
        {
            lock (SharedLock)
            {
                return _stream ??= new IbWebMarketStream(this);
            }
        }
    }

    /// <summary>Access token (jawny parametr adresu WebSocket – nie jest sekretem bez kluczy prywatnych).</summary>
    internal string AccessToken => _credentials?.AccessToken ?? string.Empty;

    /// <summary>Identyfikator sesji z <c>/tickle</c> – cookie „api” dla WebSocket.</summary>
    internal string? SessionCookie => _sessionCookie;

    internal static void Status(string message)
    {
        MarketProfile.Core.AppLog.Status("IBWeb sesja", message);
        StatusChanged?.Invoke(null, message);
    }

    // =====================================================================
    // Logowanie
    // =====================================================================

    /// <summary>Loguje się (jeśli trzeba): LST, sesja brokerska, lista kont; uruchamia podtrzymanie sesji.</summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_ready && DateTimeOffset.UtcNow < _lstExpires)
        {
            return;
        }

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready && DateTimeOffset.UtcNow < _lstExpires)
            {
                return;
            }

            _ready = false;
            _credentials = _store.Load() ?? throw new InvalidOperationException(
                "Brak kluczy IBKR Web API. Wczytaj je w Ustawieniach → Dane i broker → Źródło i połączenie.");
            if (!_credentials.IsComplete)
            {
                throw new InvalidOperationException($"Niekompletne klucze IBKR Web API – brakuje: {string.Join(", ", _credentials.Missing())}.");
            }

            Status("IBKR Web API: logowanie (live session token)...");
            await RequestLiveSessionTokenAsync(_credentials, cancellationToken).ConfigureAwait(false);

            Status("IBKR Web API: otwieranie sesji brokerskiej...");
            using (var init = await SendCoreAsync(HttpMethod.Post, "/iserver/auth/ssodh/init",
                       JsonSerializer.Serialize(new { publish = true, compete = _compete }), cancellationToken).ConfigureAwait(false))
            {
                if (init.RootElement.ValueKind == JsonValueKind.Object &&
                    init.RootElement.TryGetProperty("authenticated", out var authenticated) &&
                    authenticated.ValueKind == JsonValueKind.False)
                {
                    var competing = init.RootElement.TryGetProperty("competing", out var c) && c.ValueKind == JsonValueKind.True;
                    throw new InvalidOperationException(competing
                        ? "IBKR: inna sesja tego użytkownika jest aktywna (np. TWS). Zamknij ją albo zaznacz w ustawieniach „Przejmij sesję”."
                        : "IBKR: nie udało się otworzyć sesji brokerskiej.");
                }
            }

            // Podtrzymanie sesji i identyfikator sesji dla WebSocket.
            await TickleAsync(cancellationToken).ConfigureAwait(false);

            // IBKR wymaga pobrania listy kont przed zapytaniami o dane rynkowe.
            using (await SendCoreAsync(HttpMethod.Get, "/iserver/accounts", null, cancellationToken).ConfigureAwait(false))
            {
            }

            _ready = true;
            StartTickle();
            Status("IBKR Web API: zalogowano");
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>Kroki 1–4 protokołu OAuth (patrz <see cref="IbWebOAuth"/>).</summary>
    private async Task RequestLiveSessionTokenAsync(IbWebCredentials credentials, CancellationToken cancellationToken)
    {
        string prepend;
        try
        {
            prepend = IbWebOAuth.DecryptPrepend(credentials.EncryptionKeyPem, credentials.AccessTokenSecret);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Nie udało się odszyfrować access token secret kluczem szyfrowania ({ex.GetType().Name}). Sprawdź, czy klucze pasują do siebie.", ex);
        }

        var (prime, generator) = IbWebOAuth.ReadDhParameters(credentials.DhParamPem);
        var secret = IbWebOAuth.NewDhSecret();

        const string path = "/oauth/live_session_token";
        var url = BaseUrl + path;
        var parameters = IbWebOAuth.BaseParameters(credentials.ConsumerKey, credentials.AccessToken, IbWebOAuth.RsaSha256);
        parameters["diffie_hellman_challenge"] = IbWebOAuth.DhChallenge(generator, secret, prime);
        var baseString = IbWebOAuth.BaseString("POST", url, parameters, prepend);
        var signature = IbWebOAuth.SignRsa(credentials.SignatureKeyPem, baseString);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Authorization", IbWebOAuth.AuthorizationHeader(credentials.Realm, parameters, signature));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"IBKR odrzucił logowanie OAuth (HTTP {(int)response.StatusCode}): {Shorten(body)}");
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var dhResponse = root.GetProperty("diffie_hellman_response").GetString() ?? string.Empty;
        var lstSignature = root.GetProperty("live_session_token_signature").GetString() ?? string.Empty;
        var lst = IbWebOAuth.ComputeLiveSessionToken(dhResponse, secret, prime, prepend);
        if (!IbWebOAuth.ValidateLiveSessionToken(lst, credentials.ConsumerKey, lstSignature))
        {
            throw new InvalidOperationException("IBKR: niepoprawny live session token (klucze nie pasują do konta?).");
        }

        _liveSessionToken = lst;
        _lstExpires = root.TryGetProperty("live_session_token_expiration", out var exp) && exp.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).AddMinutes(-5)
            : DateTimeOffset.UtcNow.AddHours(8);
    }

    private void StartTickle()
    {
        _tickleCts?.Cancel();
        var cts = _tickleCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TickleInterval, cts.Token).ConfigureAwait(false);
                    await TickleAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Status($"IBKR Web API: podtrzymanie sesji – {ex.Message}");
                    _ready = false;
                }

                // Sesja wygasła / podtrzymanie nie działa – logujemy się ponownie od razu (także gdy działa sam strumień
                // na żywo i nie ma zapytań REST, które zalogowałyby przy okazji). Udane logowanie uruchamia nowe podtrzymanie.
                if (!_ready && !cts.IsCancellationRequested)
                {
                    try
                    {
                        await EnsureReadyAsync(cts.Token).ConfigureAwait(false);
                        _stream?.Reconnect(); // nowa sesja brokerska = nowy identyfikator dla WebSocket
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Status($"IBKR Web API: ponowne logowanie nie powiodło się – {ex.Message}; kolejna próba za minutę");
                    }
                }
            }
        });
    }

    /// <summary><c>/tickle</c>: podtrzymuje sesję i zwraca identyfikator sesji dla WebSocket.</summary>
    private async Task TickleAsync(CancellationToken cancellationToken)
    {
        using var tickle = await SendCoreAsync(HttpMethod.Post, "/tickle", null, cancellationToken).ConfigureAwait(false);
        var root = tickle.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("session", out var session))
        {
            _sessionCookie = session.GetString();
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("iserver", out var iserver) &&
            iserver.TryGetProperty("authStatus", out var auth) &&
            auth.TryGetProperty("authenticated", out var authenticated) &&
            authenticated.ValueKind == JsonValueKind.False)
        {
            _ready = false;
            Status("IBKR Web API: sesja brokerska wygasła – ponowne logowanie przy następnym zapytaniu");
        }
    }

    // =====================================================================
    // Zapytania
    // =====================================================================

    /// <summary>
    /// Zapytanie do Web API (po zalogowaniu). <paramref name="pathAndQuery"/> np. "/iserver/marketdata/history?conid=1&amp;bar=1min".
    /// Po HTTP 401 loguje się ponownie i powtarza raz; HTTP 429 → <see cref="IbWebPacingException"/>.
    /// </summary>
    public async Task<JsonDocument> SendAsync(HttpMethod method, string pathAndQuery, object? body = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var json = body is null ? null : JsonSerializer.Serialize(body);
        try
        {
            return await SendCoreAsync(method, pathAndQuery, json, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _ready = false;
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            return await SendCoreAsync(method, pathAndQuery, json, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Podpisane zapytanie (HMAC-SHA256 kluczem LST) z odstępem od poprzedniego.</summary>
    private async Task<JsonDocument> SendCoreAsync(HttpMethod method, string pathAndQuery, string? jsonBody, CancellationToken cancellationToken)
    {
        var credentials = _credentials ?? throw new InvalidOperationException("Brak kluczy IBKR Web API.");
        var lst = _liveSessionToken ?? throw new InvalidOperationException("Brak live session token.");

        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _lastRequestAt + MinRequestSpacing - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            var url = BaseUrl + pathAndQuery;
            var queryStart = url.IndexOf('?');
            var signedUrl = queryStart < 0 ? url : url[..queryStart];
            var parameters = IbWebOAuth.BaseParameters(credentials.ConsumerKey, credentials.AccessToken, IbWebOAuth.HmacSha256);
            var signature = IbWebOAuth.SignHmac(lst, IbWebOAuth.BaseString(method.Method, signedUrl, parameters));

            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", IbWebOAuth.AuthorizationHeader(credentials.Realm, parameters, signature));
            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _lastRequestAt = DateTimeOffset.UtcNow;
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new IbWebPacingException($"limit zapytań IBKR (HTTP 429): {Shorten(text)}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"IBKR {method.Method} {StripQuery(pathAndQuery)}: HTTP {(int)response.StatusCode} {Shorten(text)}",
                    null, response.StatusCode);
            }

            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private static string StripQuery(string pathAndQuery) =>
        pathAndQuery.IndexOf('?') is var i and >= 0 ? pathAndQuery[..i] : pathAndQuery;

    /// <summary>Skrócona treść odpowiedzi do komunikatu błędu.</summary>
    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    public async ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _tickleCts?.Cancel();
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _http.Dispose();
        _liveSessionToken = null;
        _credentials = null;
        _ready = false;
    }
}
