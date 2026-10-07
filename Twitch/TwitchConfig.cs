using System.Text.Json.Nodes;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.Twitch;

public sealed record ClientProfile(string Key, string Url, string Id, string UserAgent);

public sealed record GqlOperation(string Name, string Hash, JsonObject Variables);

public sealed record GqlRawQuery(string Query, JsonObject Variables);

// Хэши GQL и данные клиентов. Twitch их периодически меняет, поэтому файл
// можно обновить в репозитории, и программа подтянет его без пересборки.
public sealed class TwitchConfig
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/zuckerigprod/ZeTwitchMiner/main/Twitch/twitch.json";
    public const string WebClient = "web";

    private static string CachePath => Path.Combine(AppPaths.DataDir, "twitch.json");

    public int Revision { get; private init; }
    public IReadOnlyList<string> LoginClients { get; private init; } = [];
    public IReadOnlyDictionary<string, ClientProfile> Clients { get; private init; } = new Dictionary<string, ClientProfile>();
    public IReadOnlyDictionary<string, GqlOperation> Operations { get; private init; } = new Dictionary<string, GqlOperation>();
    public IReadOnlyDictionary<string, GqlRawQuery> Queries { get; private init; } = new Dictionary<string, GqlRawQuery>();

    public ClientProfile Client(string? key) =>
        key is not null && Clients.TryGetValue(key, out var c) ? c : Clients[LoginClients[0]];

    public static TwitchConfig Load()
    {
        var embedded = Parse(ReadEmbedded())!;
        try
        {
            if (File.Exists(CachePath) && Parse(File.ReadAllText(CachePath)) is { } cached && cached.Revision > embedded.Revision)
                return cached;
        }
        catch (Exception ex)
        {
            Log.Warn("Cached twitch.json ignored: " + ex.Message);
        }
        return embedded;
    }

    // true, если пришла более свежая ревизия; она применится при следующей перезагрузке майнера
    public async Task<bool> TryUpdateAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var text = await http.GetStringAsync(RemoteUrl, cts.Token);
            if (Parse(text) is { } remote && remote.Revision > Revision)
            {
                await File.WriteAllTextAsync(CachePath, text, ct);
                Log.Info($"Twitch config updated to revision {remote.Revision}");
                return true;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Debug("Remote twitch.json unavailable: " + ex.Message);
        }
        return false;
    }

    private static string ReadEmbedded()
    {
        using var stream = typeof(TwitchConfig).Assembly.GetManifestResourceStream("twitch.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static TwitchConfig? Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return null;

        var clients = new Dictionary<string, ClientProfile>();
        foreach (var (key, value) in root["clients"]!.AsObject())
        {
            var agents = value!["userAgents"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
            // Один UA на весь запуск, как у настоящего клиента
            clients[key] = new ClientProfile(key, value.Str("url"), value.Str("id"), agents[Random.Shared.Next(agents.Length)]);
        }

        var ops = new Dictionary<string, GqlOperation>();
        foreach (var (key, value) in root["operations"]!.AsObject())
            ops[key] = new GqlOperation(value.Str("name"), value.Str("hash"), value!["variables"]?.AsObject() ?? new JsonObject());

        var queries = new Dictionary<string, GqlRawQuery>();
        foreach (var (key, value) in root["queries"]?.AsObject() ?? [])
            queries[key] = new GqlRawQuery(value.Str("query"), value!["variables"]?.AsObject() ?? new JsonObject());

        return new TwitchConfig
        {
            Queries = queries,
            Revision = root["revision"]?.GetValue<int>() ?? 0,
            LoginClients = root["loginClients"]!.AsArray().Select(c => c!.GetValue<string>()).Where(clients.ContainsKey).ToArray(),
            Clients = clients,
            Operations = ops,
        };
    }
}
