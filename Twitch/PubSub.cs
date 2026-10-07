using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.Twitch;

public static class Topics
{
    public static string UserDrops(long userId) => $"user-drop-events.{userId}";
    public static string Notifications(long userId) => $"onsite-notifications.{userId}";
    public static string Playback(long channelId) => $"video-playback-by-id.{channelId}";
    public static string Settings(long channelId) => $"broadcast-settings-update.{channelId}";
}

public sealed class PubSubPool(TwitchHttp http, TwitchAuth auth)
{
    public const int MaxConnections = 8;
    public const int TopicsPerConnection = 50;

    private readonly List<PubSubConnection> _connections = [];
    private bool _running;

    public event Action<string, JsonNode>? Message;
    public event Action? StatusChanged;

    public IReadOnlyList<PubSubConnection> Connections => _connections;

    public void Start()
    {
        _running = true;
        foreach (var c in _connections) c.Start();
    }

    public async Task StopAsync()
    {
        _running = false;
        await Task.WhenAll(_connections.Select(c => c.StopAsync()));
        _connections.Clear();
        StatusChanged?.Invoke();
    }

    public void AddTopics(IEnumerable<string> topics)
    {
        var existing = _connections.SelectMany(c => c.Topics).ToHashSet();
        var pending = new Queue<string>(topics.Where(t => existing.Add(t)));
        if (pending.Count == 0) return;

        for (var i = 0; i < MaxConnections && pending.Count > 0; i++)
        {
            if (i >= _connections.Count)
            {
                var conn = new PubSubConnection(i + 1, http, auth, (topic, msg) => Message?.Invoke(topic, msg), () => StatusChanged?.Invoke());
                _connections.Add(conn);
                if (_running) conn.Start();
            }
            var c = _connections[i];
            while (pending.Count > 0 && c.Topics.Count < TopicsPerConnection)
                c.Add(pending.Dequeue());
        }
        StatusChanged?.Invoke();
        if (pending.Count > 0) throw new MinerException("Maximum topics limit has been reached");
    }

    public void RemoveTopics(IEnumerable<string> topics)
    {
        var set = topics.ToHashSet();
        foreach (var c in _connections) c.Remove(set);

        // Ужимаем пул, если топики помещаются в меньшее число соединений
        var orphaned = new List<string>();
        while (_connections.Count > 0 && _connections.Sum(c => c.Topics.Count) <= (_connections.Count - 1) * TopicsPerConnection)
        {
            var last = _connections[^1];
            _connections.RemoveAt(_connections.Count - 1);
            orphaned.AddRange(last.Topics);
            _ = last.StopAsync();
        }
        if (orphaned.Count > 0) AddTopics(orphaned);
        StatusChanged?.Invoke();
    }
}

public enum WsStatus { Initializing, Connecting, Connected, Reconnecting, Disconnected }

public sealed class PubSubConnection(int index, TwitchHttp http, TwitchAuth auth, Action<string, JsonNode> onMessage, Action onStatus)
{
    private static readonly Uri Endpoint = new("wss://pubsub-edge.twitch.tv/v1");
    private static readonly TimeSpan PingInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(10);

    private readonly HashSet<string> _topics = [];
    private readonly HashSet<string> _submitted = [];
    private CancellationTokenSource? _stop;
    private Task? _task;
    private DateTime? _pongDeadline;
    private bool _reconnect;

    public int Index { get; } = index;
    public WsStatus Status { get; private set; } = WsStatus.Initializing;
    public IReadOnlyCollection<string> Topics => _topics;

    public void Add(string topic) => _topics.Add(topic);
    public void Remove(HashSet<string> topics) => _topics.RemoveWhere(topics.Contains);

    public void Start()
    {
        if (_task is not null) return;
        _stop = new CancellationTokenSource();
        _task = RunAsync(_stop.Token);
    }

    public async Task StopAsync()
    {
        if (_stop is null || _task is null) return;
        _stop.Cancel();
        await Task.WhenAny(_task, Task.Delay(2000));
        _task = null;
        SetStatus(WsStatus.Disconnected);
    }

    private void SetStatus(WsStatus status)
    {
        Status = status;
        onStatus();
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var backoff = new Backoff(180);
        while (!stop.IsCancellationRequested)
        {
            SetStatus(WsStatus.Connecting);
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("User-Agent", http.Profile.UserAgent);
            if (http.Proxy is not null) ws.Options.Proxy = http.Proxy;
            ws.Options.KeepAliveInterval = TimeSpan.Zero;

            try
            {
                await auth.EnsureAsync(stop);
                await ws.ConnectAsync(Endpoint, stop);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                Log.Debug($"Websocket #{Index} connect failed: {ex.Message}");
                SetStatus(WsStatus.Reconnecting);
                await Delay(backoff.Next(), stop);
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff.Reset();
            SetStatus(WsStatus.Connected);
            _submitted.Clear();
            _reconnect = false;
            _pongDeadline = null;

            using var conn = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var receive = ReceiveLoopAsync(ws, conn.Token);
            var nextPing = DateTime.UtcNow;

            try
            {
                while (!conn.IsCancellationRequested && ws.State == WebSocketState.Open && !_reconnect)
                {
                    var now = DateTime.UtcNow;
                    if (now >= nextPing)
                    {
                        nextPing = now + PingInterval;
                        _pongDeadline = now + PongTimeout;
                        await SendAsync(ws, new JsonObject { ["type"] = "PING" }, conn.Token);
                    }
                    if (_pongDeadline is { } deadline && now >= deadline)
                    {
                        Log.Warn($"Websocket #{Index} didn't receive a PONG, reconnecting");
                        break;
                    }
                    await SyncTopicsAsync(ws, conn.Token);
                    if (await Task.WhenAny(receive, Task.Delay(500, conn.Token)) == receive) break;
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                Log.Debug($"Websocket #{Index} error: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
            }

            conn.Cancel();
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var closeCts = new CancellationTokenSource(1000);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token);
                }
            }
            catch
            {
                // При закрытии ошибки не важны
            }

            if (!stop.IsCancellationRequested) SetStatus(WsStatus.Reconnecting);
        }
    }

    private async Task SyncTopicsAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var removed = _submitted.Where(t => !_topics.Contains(t)).ToList();
        var added = _topics.Where(t => !_submitted.Contains(t)).ToList();

        foreach (var chunk in removed.Chunk(20))
        {
            await SendListenAsync(ws, "UNLISTEN", chunk, ct);
            foreach (var t in chunk) _submitted.Remove(t);
        }
        foreach (var chunk in added.Chunk(20))
        {
            await SendListenAsync(ws, "LISTEN", chunk, ct);
            foreach (var t in chunk) _submitted.Add(t);
        }
    }

    private Task SendListenAsync(ClientWebSocket ws, string type, string[] topics, CancellationToken ct) =>
        SendAsync(ws, new JsonObject
        {
            ["type"] = type,
            ["data"] = new JsonObject
            {
                ["topics"] = new JsonArray(topics.Select(t => (JsonNode)t).ToArray()),
                ["auth_token"] = auth.AccessToken,
            },
            ["nonce"] = TwitchAuth.RandomNonce(30),
        }, ct);

    private static Task SendAsync(ClientWebSocket ws, JsonObject message, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log.Warn($"Websocket #{Index} closed by server: {result.CloseStatus}");
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                Handle(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            // Соединение оборвалось, внешний цикл переподключится
        }
    }

    private void Handle(string text)
    {
        JsonNode? json;
        try { json = JsonNode.Parse(text); }
        catch { return; }

        switch (json.Str("type"))
        {
            case "PONG":
                _pongDeadline = null;
                break;
            case "RECONNECT":
                _reconnect = true;
                break;
            case "RESPONSE":
                var error = json.Str("error");
                if (!string.IsNullOrEmpty(error)) Log.Debug($"Websocket #{Index} LISTEN error: {error}");
                break;
            case "MESSAGE":
                var topic = json?["data"].Str("topic") ?? "";
                var raw = json?["data"].Str("message");
                if (string.IsNullOrEmpty(raw)) break;
                try
                {
                    if (JsonNode.Parse(raw) is { } msg) onMessage(topic, msg);
                }
                catch (Exception ex)
                {
                    Log.Debug($"Bad pubsub message on {topic}: {ex.Message}");
                }
                break;
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}
