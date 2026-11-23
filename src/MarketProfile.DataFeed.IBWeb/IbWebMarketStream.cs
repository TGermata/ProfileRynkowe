using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MarketProfile.DataFeed.IBWeb;

/// <summary>
/// Strumień notowań IBKR Web API przez WebSocket (jedno połączenie na całą aplikację):
///  - subskrypcja <c>smd+{conid}+{"fields":[...]}</c>, rezygnacja <c>umd+{conid}+{}</c>,
///  - ping <c>tic</c> co 50 s (inaczej IBKR zamyka połączenie),
///  - po zerwaniu – ponowne połączenie i odnowienie subskrypcji,
///  - strażnik: brak wiadomości dla subskrypcji przez 60 s → odnowienie subskrypcji.
/// </summary>
public sealed class IbWebMarketStream : IAsyncDisposable
{
    /// <summary>
    /// Pola notowań: 31 ostatnia cena, 7059 wielkość ostatniej transakcji, 7762 wolumen dnia, 6509 dostępność danych,
    /// 84 bid, 86 ask (strona transakcji – delta wolumenu).
    /// </summary>
    public static readonly string[] Fields = ["31", "7059", "7762", "6509", "84", "86"];

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);

    /// <summary>Tyle kolejnych odnowień subskrypcji bez odpowiedzi – potem zrywamy połączenie i łączymy od nowa.</summary>
    private const int SilentRenewalsBeforeReconnect = 3;

    private readonly IbWebSession? _session;
    private readonly ConcurrentDictionary<long, Topic> _topics = new();
    private readonly Lock _topicsLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, Task>? _sendOverride;
    private ClientWebSocket? _socket;
    private Task? _loop;

    internal IbWebMarketStream(IbWebSession session) => _session = session;

    /// <summary>Strumień bez połączenia – wiadomości wysyłane do <paramref name="send"/> (testy subskrypcji).</summary>
    internal IbWebMarketStream(Func<string, Task> send) => _sendOverride = send;

    /// <summary>
    /// Subskrypcja notowań instrumentu; <paramref name="onMessage"/> dostaje pola wiadomości (nazwa → tekst).
    /// Kilku subskrybentów tego samego instrumentu (np. wykres i test połączenia w ustawieniach) dostaje te same
    /// wiadomości; IBKR dostaje „smd” przy pierwszym i „umd” dopiero po odejściu ostatniego.
    /// </summary>
    public IDisposable Subscribe(long conid, Action<IReadOnlyDictionary<string, string>> onMessage)
    {
        var subscription = new Subscription(this, conid, onMessage);
        bool first;
        lock (_topicsLock)
        {
            var topic = _topics.GetOrAdd(conid, c => new Topic(c));
            first = topic.Subscribers.Length == 0;
            topic.Subscribers = [.. topic.Subscribers, subscription];
        }

        if (_sendOverride is null)
        {
            _loop ??= Task.Run(() => RunAsync(_cts.Token));
        }

        if (first)
        {
            _ = SendSubscribeAsync(conid);
        }

        return subscription;
    }

    private void Unsubscribe(Subscription subscription)
    {
        bool last;
        lock (_topicsLock)
        {
            if (!_topics.TryGetValue(subscription.Conid, out var topic) || !topic.Subscribers.Contains(subscription))
            {
                return; // już wypisany
            }

            topic.Subscribers = topic.Subscribers.Where(s => !ReferenceEquals(s, subscription)).ToArray();
            last = topic.Subscribers.Length == 0;
            if (last)
            {
                _topics.TryRemove(subscription.Conid, out _);
            }
        }

        if (last)
        {
            _ = SendAsync($"umd+{subscription.Conid}+{{}}");
        }
    }

    /// <summary>Liczba subskrybentów instrumentu (testy).</summary>
    internal int SubscriberCount(long conid) => _topics.TryGetValue(conid, out var topic) ? topic.Subscribers.Length : 0;

    /// <summary>Zrywa bieżące połączenie WebSocket – pętla połączy się ponownie (np. po ponownym zalogowaniu).</summary>
    internal void Reconnect() => _socket?.Abort();

    /// <summary>Pętla połączenia: łączy, odbiera wiadomości, po zerwaniu łączy ponownie (co 5 s).</summary>
    private async Task RunAsync(CancellationToken token)
    {
        var session = _session!;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await session.EnsureReadyAsync(token).ConfigureAwait(false);
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("User-Agent", IbWebSession.UserAgent);
                if (session.SessionCookie is { } cookie)
                {
                    socket.Options.SetRequestHeader("Cookie", $"api={cookie}");
                }

                var uri = new Uri($"{IbWebSession.WebSocketUrl}?oauth_token={Uri.EscapeDataString(session.AccessToken)}");
                await socket.ConnectAsync(uri, token).ConfigureAwait(false);
                _socket = socket;
                IbWebSession.Status("IBKR Web API: strumień notowań połączony");

                foreach (var conid in _topics.Keys)
                {
                    await SendSubscribeAsync(conid).ConfigureAwait(false);
                }

                using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var ping = PingAsync(loopCts.Token);
                await ReceiveAsync(socket, token).ConfigureAwait(false);
                await loopCts.CancelAsync().ConfigureAwait(false);
                await ping.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                IbWebSession.Status($"IBKR Web API: strumień notowań – {ex.Message}; ponowne połączenie za 5 s");
            }
            finally
            {
                _socket = null;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                IbWebSession.Status("IBKR Web API: strumień notowań zamknięty przez serwer");
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            Dispatch(text);
        }
    }

    /// <summary>Rozdziela wiadomość „smd+…” do subskrybentów danego instrumentu.</summary>
    internal void Dispatch(string text)
    {
        if (ParseMarketData(text) is not { } data || !_topics.TryGetValue(data.Conid, out var topic))
        {
            return;
        }

        topic.LastMessageAt = DateTimeOffset.UtcNow;
        topic.SilentRenewals = 0;
        foreach (var subscription in topic.Subscribers)
        {
            try
            {
                subscription.OnMessage(data.Fields);
            }
            catch (Exception ex)
            {
                IbWebSession.Status($"IBKR Web API: błąd obsługi notowania – {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Wiadomość notowań: <c>{"topic":"smd+265598","conid":265598,"_updated":1700000000000,"31":"189.50",...}</c>.
    /// Zwraca null dla innych wiadomości (system, heartbeat, błędy).
    /// </summary>
    public static (long Conid, IReadOnlyDictionary<string, string> Fields)? ParseMarketData(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("topic", out var topic) ||
                topic.GetString() is not { } topicText ||
                !topicText.StartsWith("smd+", StringComparison.Ordinal))
            {
                return null;
            }

            long conid;
            if (root.TryGetProperty("conid", out var conidElement) && conidElement.TryGetInt64(out var c))
            {
                conid = c;
            }
            else if (!long.TryParse(topicText.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out conid))
            {
                return null;
            }

            var fields = new Dictionary<string, string>();
            foreach (var property in root.EnumerateObject())
            {
                fields[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    _ => property.Value.GetRawText(),
                };
            }

            return (conid, fields);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Ping co 50 s i strażnik ciszy subskrypcji.</summary>
    private async Task PingAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, token).ConfigureAwait(false);
                await SendAsync("tic").ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                foreach (var topic in _topics.Values)
                {
                    if (now - topic.LastMessageAt <= SilenceLimit)
                    {
                        continue;
                    }

                    topic.LastMessageAt = now;
                    if (++topic.SilentRenewals > SilentRenewalsBeforeReconnect)
                    {
                        // Odnawianie subskrypcji nie pomaga (np. serwer trzyma połączenie, ale sesja już nie działa) –
                        // zrywamy połączenie: pętla zaloguje się w razie potrzeby i połączy od nowa.
                        topic.SilentRenewals = 0;
                        IbWebSession.Status("IBKR Web API: brak notowań mimo odnowienia subskrypcji – ponowne połączenie strumienia");
                        Reconnect();
                        return;
                    }

                    await SendAsync($"umd+{topic.Conid}+{{}}").ConfigureAwait(false);
                    await SendSubscribeAsync(topic.Conid).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task SendSubscribeAsync(long conid) =>
        SendAsync($"smd+{conid}+" + JsonSerializer.Serialize(new { fields = Fields }));

    private async Task SendAsync(string text)
    {
        if (_sendOverride is not null)
        {
            await _sendOverride(text).ConfigureAwait(false);
            return;
        }

        if (_socket is not { State: WebSocketState.Open } socket)
        {
            return; // wyślemy po połączeniu (pętla odnawia subskrypcje)
        }

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            IbWebSession.Status($"IBKR Web API: wysyłanie do strumienia – {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_socket is { State: WebSocketState.Open } socket)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // zamykanie – błędy bez znaczenia
            }
        }
    }

    /// <summary>Notowania jednego instrumentu: subskrybenci (tablica podmieniana pod blokadą) i strażnik ciszy.</summary>
    private sealed class Topic(long conid)
    {
        public long Conid { get; } = conid;
        public Subscription[] Subscribers { get; set; } = [];
        public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
        public int SilentRenewals { get; set; }
    }

    private sealed class Subscription(IbWebMarketStream owner, long conid, Action<IReadOnlyDictionary<string, string>> onMessage) : IDisposable
    {
        public long Conid { get; } = conid;
        public Action<IReadOnlyDictionary<string, string>> OnMessage { get; } = onMessage;

        public void Dispose() => owner.Unsubscribe(this);
    }
}
