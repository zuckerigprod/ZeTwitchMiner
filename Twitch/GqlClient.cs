using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.Twitch;

public sealed class GqlException(string message) : MinerException(message)
{
    public bool IsIntegrity => Message.Contains("IntegrityCheckFailed", StringComparison.Ordinal)
        || Message.Contains("failed integrity check", StringComparison.Ordinal);
}

public sealed class GqlClient(TwitchHttp http, TwitchAuth auth)
{
    private const string Url = "https://gql.twitch.tv/gql";
    public const int BatchSize = 20;

    // Twitch очень болезненно реагирует на превышение лимита, поэтому не больше 5 запросов в секунду
    private readonly SemaphoreSlim _concurrency = new(5, 5);
    private readonly Queue<DateTime> _recent = new();

    public JsonObject Op(string key, JsonObject? variables = null)
    {
        if (!http.Config.Operations.TryGetValue(key, out var op))
            throw new MinerException($"GQL operation '{key}' is missing in twitch.json");

        var vars = (JsonObject)op.Variables.DeepClone();
        if (variables is not null) Merge(vars, variables);

        return new JsonObject
        {
            ["operationName"] = op.Name,
            ["extensions"] = new JsonObject
            {
                ["persistedQuery"] = new JsonObject { ["version"] = 1, ["sha256Hash"] = op.Hash },
            },
            ["variables"] = vars,
        };
    }

    // Обычный GraphQL-запрос по тексту, без хэша
    public JsonObject Raw(string key, JsonObject? variables = null)
    {
        if (!http.Config.Queries.TryGetValue(key, out var q))
            throw new MinerException($"GQL query '{key}' is missing in twitch.json");
        var vars = (JsonObject)q.Variables.DeepClone();
        if (variables is not null) Merge(vars, variables);
        return new JsonObject { ["query"] = q.Query, ["variables"] = vars };
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject sub && target[key] is JsonObject existing)
                Merge(existing, sub);
            else
                target[key] = value?.DeepClone();
        }
    }

    public async Task<JsonNode> RequestAsync(JsonObject op, CancellationToken ct) =>
        (await SendAsync(op, ct))[0];

    public async Task<List<JsonNode>> RequestBatchAsync(IReadOnlyList<JsonObject> ops, CancellationToken ct)
    {
        var tasks = ops.Chunk(BatchSize).Select(chunk => SendAsync(new JsonArray(chunk.Select(c => (JsonNode)c.DeepClone()).ToArray()), ct));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    private async Task<List<JsonNode>> SendAsync(JsonNode body, CancellationToken ct)
    {
        var backoff = new Backoff(60);
        var singleRetry = true;
        var unauthorized = 0;
        var payload = body.ToJsonString();

        while (true)
        {
            var delay = backoff.Next();
            JsonNode? json;

            await ThrottleAsync(ct);
            try
            {
                await auth.EnsureAsync(ct);
                using var response = await http.SendAsync(() => auth.WithHeaders(new HttpRequestMessage(HttpMethod.Post, Url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                }, gql: true), ct);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Токен живой (validate его принимает), но GQL от этого клиента его не берёт
                    if (++unauthorized >= 2)
                    {
                        Log.Error($"GQL rejects the token of '{auth.Profile.Key}' client: {await response.Content.ReadAsStringAsync(ct)}");
                        auth.Reject();
                        throw new MinerException(Loc.T("Login.Rejected"));
                    }
                    auth.Invalidate();
                    await Task.Delay(delay, ct);
                    continue;
                }
                json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            finally
            {
                _concurrency.Release();
            }

            var responses = json is JsonArray arr ? arr.Select(n => n!).ToList() : [json!];
            var retry = false;

            foreach (var r in responses)
            {
                if (r["errors"] is JsonArray errors && errors.Count > 0)
                {
                    var handled = false;
                    foreach (var e in errors)
                    {
                        var msg = e?["message"]?.GetValue<string>();
                        if (msg is null) continue;
                        if (singleRetry && msg is "service error" or "PersistedQueryNotFound")
                        {
                            Log.Info($"Retrying a {msg} for {r["extensions"]?["operationName"]}");
                            singleRetry = false;
                            if (delay < TimeSpan.FromSeconds(5)) delay = TimeSpan.FromSeconds(5);
                            retry = handled = true;
                            break;
                        }
                        if (msg == "server error")
                        {
                            NullifyPath(r["data"], e!["path"] as JsonArray);
                            handled = true;
                            break;
                        }
                        if (msg is "service timeout" or "request cancelled" or "service unavailable" or "context deadline exceeded")
                        {
                            retry = handled = true;
                            break;
                        }
                    }
                    if (!handled) throw new GqlException(errors.ToJsonString());
                }
                else if (r["error"] is { } error)
                {
                    throw new GqlException($"{error}: {r["message"]}");
                }
                if (retry) break;
            }

            if (!retry) return responses;
            await Task.Delay(delay, ct);
        }
    }

    private static void NullifyPath(JsonNode? data, JsonArray? path)
    {
        if (data is null || path is null || path.Count == 0) return;
        var node = data;
        for (var i = 0; i < path.Count - 1 && node is not null; i++)
            node = path[i] is JsonValue v && v.TryGetValue<int>(out var idx) ? node[idx] : node[path[i]!.GetValue<string>()];
        if (node is JsonObject obj) obj[path[^1]!.GetValue<string>()] = null;
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            while (true)
            {
                var now = DateTime.UtcNow;
                while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromSeconds(1)) _recent.Dequeue();
                if (_recent.Count < 5)
                {
                    _recent.Enqueue(now);
                    return;
                }
                await Task.Delay(_recent.Peek().AddSeconds(1) - now + TimeSpan.FromMilliseconds(5), ct);
            }
        }
        catch
        {
            _concurrency.Release();
            throw;
        }
    }
}
