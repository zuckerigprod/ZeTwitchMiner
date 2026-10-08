using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.Twitch;

public enum MinerState { Idle, InventoryFetch, GamesUpdate, ChannelsFetch, ChannelsCleanup, ChannelSwitch, Restart, Exit }

public enum MinerActivity { Starting, LoggingIn, Maintenance, Watching, Idle, Error }

public sealed class ReloadRequest : Exception;

// Вся логика выполняется на UI-потоке через async/await, поэтому
// никаких блокировок, сеть при этом не тормозит интерфейс.
public sealed partial class Miner : ObservableObject
{
    private const int MaxChannels = 199;
    private const int DirectoryLimit = 20;
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(59);
    private static readonly TimeSpan OnlineDelay = TimeSpan.FromSeconds(120);

    private readonly Settings _settings;
    private TwitchConfig _config = null!;
    private TwitchHttp _http = null!;
    private TwitchAuth _auth = null!;
    private GqlClient _gql = null!;
    private PubSubPool _pubsub = null!;

    private CancellationTokenSource? _runCts;
    private MinerState _state = MinerState.InventoryFetch;
    private readonly AsyncSignal _stateChange = new();
    private readonly AsyncSignal _watchingSet = new();
    private readonly AsyncSignal _watchRestart = new();
    private bool _fullCleanup;
    private DateTimeOffset? _minuteStartedAt;

    private List<Campaign> _inventory = [];
    private readonly Dictionary<string, TimedDrop> _drops = new();
    private readonly Dictionary<string, Campaign> _campaigns = new();
    private readonly Dictionary<long, Channel> _channels = new();
    private readonly List<Game> _wantedGames = [];
    private List<DateTimeOffset> _triggers = [];
    private Task? _watchTask;
    private CancellationTokenSource? _maintenanceCts;

    public ImageCache Images { get; }
    public ObservableCollection<Campaign> Inventory { get; } = [];
    public ObservableCollection<Channel> Channels { get; } = [];
    public IReadOnlyList<PubSubConnection> Connections => _pubsub?.Connections ?? [];
    public HashSet<string> KnownGames { get; } = new(StringComparer.Ordinal);

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private MinerActivity _activity = MinerActivity.Starting;
    [ObservableProperty] private Channel? _watching;
    [ObservableProperty] private TimedDrop? _currentDrop;
    [ObservableProperty] private DeviceCode? _loginCode;
    [ObservableProperty] private bool _browserLoginActive;
    [ObservableProperty] private bool _loggedIn;
    [ObservableProperty] private long _userId;
    [ObservableProperty] private string _clientName = "";
    [ObservableProperty] private string? _lastError;

    public Channel? RequestedChannel { get; set; }

    public event Action<string, string>? DropClaimed;
    public event Action? ConnectionsChanged;
    public event Action? LoginNeeded;

    public Miner(Settings settings)
    {
        _settings = settings;
        Images = new ImageCache(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
    }

    public double MinuteProgress => _minuteStartedAt is { } t ? Math.Clamp((Clock.Now - t).TotalSeconds / 60, 0, 1) : 0;

    // ---------------- Жизненный цикл ----------------

    public async Task RunAsync(CancellationToken appCt)
    {
        var errorBackoff = new Backoff(300, 3);
        while (!appCt.IsCancellationRequested)
        {
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
            try
            {
                await RunOnceAsync(_runCts.Token);
                break;
            }
            catch (ReloadRequest)
            {
                errorBackoff.Reset();
            }
            catch (OperationCanceledException) when (appCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
                LastError = ex.Message;
                Activity = MinerActivity.Error;
                // Сессию отклонили: сразу возвращаемся на экран входа
                var delay = _auth is { AccessToken: null } ? TimeSpan.FromSeconds(1) : errorBackoff.Next();
                if (_auth is { AccessToken: null }) LoggedIn = false;
                Status = Loc.F("Status.Error", (int)delay.TotalSeconds);
                await ShutdownAsync();
                try { await Task.Delay(delay, appCt); } catch (OperationCanceledException) { break; }
                continue;
            }
            await ShutdownAsync();
        }
        await ShutdownAsync();
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        _config = TwitchConfig.Load();
        var session = TwitchAuth.ReadSession();
        _http = new TwitchHttp(_config, _config.Client(session?.Client), _settings.Proxy, _settings.ConnectionQuality);
        _http.Notice += msg => Log.Warn(msg);
        _auth = new TwitchAuth(_http);
        _auth.Restore(session);
        _auth.CodeRequired += code =>
        {
            LoginCode = code;
            if (code is not null) LoginNeeded?.Invoke();
        };
        _auth.BrowserLoginActive += active => BrowserLoginActive = active;
        _gql = new GqlClient(_http, _auth);
        _pubsub = new PubSubPool(_http, _auth);
        _pubsub.Message += OnPubSubMessage;
        _pubsub.StatusChanged += () => ConnectionsChanged?.Invoke();

        _ = _config.TryUpdateAsync(_http.Client, ct);

        Activity = MinerActivity.LoggingIn;
        Status = Loc.T("Status.LoggingIn");
        await _auth.EnsureAsync(ct);
        LoggedIn = true;
        LastError = null;
        UserId = _auth.UserId;
        ClientName = _auth.Profile.Key;

        _pubsub.Start();
        _watchTask = WatchLoopAsync(ct);
        _pubsub.AddTopics([Topics.UserDrops(_auth.UserId), Topics.Notifications(_auth.UserId)]);
        _fullCleanup = false;
        ChangeState(MinerState.InventoryFetch);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            switch (_state)
            {
                case MinerState.Idle:
                    Activity = MinerActivity.Idle;
                    Status = Loc.T("Status.Idle");
                    StopWatching();
                    _stateChange.Clear();
                    break;

                case MinerState.InventoryFetch:
                    Activity = MinerActivity.Maintenance;
                    await FetchInventoryAsync(ct);
                    _settings.Save();
                    ChangeState(MinerState.GamesUpdate);
                    break;

                case MinerState.GamesUpdate:
                    await GamesUpdateAsync(ct);
                    break;

                case MinerState.ChannelsCleanup:
                    ChannelsCleanup();
                    break;

                case MinerState.ChannelsFetch:
                    await ChannelsFetchAsync(ct);
                    break;

                case MinerState.ChannelSwitch:
                    ChannelSwitch();
                    break;

                case MinerState.Restart:
                    throw new ReloadRequest();

                case MinerState.Exit:
                    return;
            }
            await _stateChange.WaitAsync(ct);
        }
    }

    public void ChangeState(MinerState state)
    {
        if (_state != MinerState.Exit) _state = state;
        _stateChange.Set();
    }

    public void Reload() => ChangeState(MinerState.InventoryFetch);

    public void Restart() => ChangeState(MinerState.Restart);

    public void SwitchTo(Channel channel)
    {
        RequestedChannel = channel;
        ChangeState(MinerState.ChannelSwitch);
    }

    public void LoginWithBrowser() => _auth?.RequestBrowserLogin();

    public async Task LogoutAsync()
    {
        if (_auth is null) return;
        await _auth.LogoutAsync(CancellationToken.None);
        LoggedIn = false;
        UserId = 0;
        Restart();
    }

    private async Task ShutdownAsync()
    {
        StopWatching();
        _maintenanceCts?.Cancel();
        _runCts?.Cancel();
        if (_watchTask is not null)
        {
            try { await _watchTask; } catch { }
            _watchTask = null;
        }
        if (_pubsub is not null) await _pubsub.StopAsync();
        foreach (var ch in _channels.Values) ch.PendingOnline?.Cancel();
        _channels.Clear();
        Channels.Clear();
        _drops.Clear();
        _wantedGames.Clear();
        _triggers.Clear();
        _http?.Dispose();
        _state = MinerState.InventoryFetch;
        _stateChange.Clear();
        ConnectionsChanged?.Invoke();
    }

    // ---------------- Инвентарь ----------------

    private async Task FetchInventoryAsync(CancellationToken ct)
    {
        Status = Loc.T("Status.FetchingInventory");

        var inventoryResponse = await _gql.RequestAsync(_gql.Op("Inventory"), ct);
        var inv = inventoryResponse["data"]?["currentUser"]?["inventory"];
        var inventoryData = new Dictionary<string, JsonObject>();
        foreach (var c in (inv?["dropCampaignsInProgress"] as JsonArray) ?? [])
            if (c is JsonObject o) inventoryData[o.Str("id")] = o;

        // Twitch отдаёт полученные награды в двух форматах, в зависимости от версии запроса
        var claimedBenefits = new Dictionary<string, DateTimeOffset>();
        var eventDrops = (inv?["gameEventDrops"] as JsonArray)?.AsEnumerable()
            ?? (inv?["gameEventDropsConnection"]?["edges"] as JsonArray)?.Select(e => e?["node"]) ?? [];
        foreach (var b in eventDrops)
            if (b is not null) claimedBenefits[b.Str("id")] = b.Time("lastAwardedAt");

        var available = new Dictionary<string, JsonObject>();
        try
        {
            var dashboard = await _gql.RequestAsync(_gql.Op("Campaigns"), ct);
            foreach (var c in (dashboard["data"]?["currentUser"]?["dropCampaigns"] as JsonArray) ?? [])
                if (c is JsonObject o && o.Str("status") is "ACTIVE" or "UPCOMING")
                    available[o.Str("id")] = o;
        }
        catch (GqlException ex) when (ex.IsIntegrity)
        {
            // Веб-токену список кампаний не отдают без integrity, обходимся поиском по играм
            Log.Warn("Campaign list requires integrity check for this client");
        }

        // Для некоторых клиентов дашборд пуст. Тогда берём каталог кампаний с канала twitch:
        // он приходит одним запросом и сразу со всеми дропами
        var complete = new HashSet<string>();
        if (available.Count == 0)
        {
            foreach (var c in await FetchCatalogAsync(ct))
            {
                if (c.Str("status") is not ("ACTIVE" or "UPCOMING")) continue;
                available[c.Str("id")] = c;
                complete.Add(c.Str("id"));
            }
            Log.Info($"Campaign catalog: {complete.Count} campaigns");
        }
        if (available.Count == 0)
        {
            Log.Warn($"Campaign list is empty for '{_auth.Profile.Key}' client, searching campaigns by priority games");
            foreach (var id in await DiscoverCampaignsAsync(ct))
                if (!available.ContainsKey(id) && !inventoryData.ContainsKey(id))
                    available[id] = new JsonObject { ["id"] = id };
        }

        Status = Loc.T("Status.FetchingCampaigns");
        var ids = available.Keys.Concat(inventoryData.Keys).Distinct().ToList();
        var details = await _gql.RequestBatchAsync(
            ids.Where(id => !complete.Contains(id))
                .Select(id => _gql.Op("CampaignDetails", new JsonObject { ["channelLogin"] = _auth.UserId.ToString(), ["dropID"] = id })).ToList(), ct);
        var fetched = new Dictionary<string, JsonObject>();
        foreach (var r in details)
            if (r["data"]?["user"]?["dropCampaign"] is JsonObject dc) fetched[dc.Str("id")] = dc;

        // Приоритет данных: прогресс из инвентаря > дашборд > детали
        var merged = new List<JsonObject>();
        foreach (var id in ids)
        {
            var obj = fetched.TryGetValue(id, out var f) ? (JsonObject)f.DeepClone() : new JsonObject();
            if (available.TryGetValue(id, out var a)) obj = MergeData(a, obj);
            if (inventoryData.TryGetValue(id, out var i)) obj = MergeData(i, obj);
            if (obj["game"] is JsonObject && obj["timeBasedDrops"] is JsonArray) merged.Add(obj);
        }

        var campaigns = merged.Select(m => new Campaign(m, claimedBenefits, _settings))
            .OrderByDescending(c => c.Eligible)
            .ThenBy(c => c.Upcoming ? c.StartsAt : c.EndsAt)
            .ThenByDescending(c => c.Active)
            .ToList();

        _drops.Clear();
        var triggers = new HashSet<DateTimeOffset>();
        var nextHour = Clock.Now.AddHours(1);
        foreach (var c in campaigns)
        {
            foreach (var d in c.Drops) _drops[d.Id] = d;
            if (c.CanEarnWithin(nextHour)) triggers.UnionWith(c.TimeTriggers);
            _campaigns[c.Id] = c;
            KnownGames.Add(c.Game.Name);
        }
        _inventory = campaigns;

        Images.Trim();
        Inventory.Clear();
        foreach (var c in campaigns) Inventory.Add(c);
        _ = LoadImagesAsync(campaigns, ct);

        var now = Clock.Now;
        _triggers = triggers.Where(t => t > now).Order().ToList();
        RestartMaintenance(ct);
        Log.Info($"Inventory loaded: {campaigns.Count} campaigns");
    }

    // Сначала обложки игр, потом награды; по 8 загрузок одновременно
    private async Task LoadImagesAsync(List<Campaign> campaigns, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(8);

        async Task Load(string url, Action<Avalonia.Media.Imaging.Bitmap?> apply)
        {
            await gate.WaitAsync(ct);
            try { apply(await Images.GetAsync(url, 96, ct)); }
            finally { gate.Release(); }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.WhenAll(campaigns.Where(c => c.Image is null).Select(c => Load(c.ImageUrl, img => c.Image = img)));
            var benefits = campaigns.SelectMany(c => c.Drops).SelectMany(d => d.Benefits).Where(b => b.Image is null);
            await Task.WhenAll(benefits.Select(b => Load(b.ImageUrl, img => b.Image = img)));
            Log.Debug($"Images loaded in {sw.Elapsed.TotalSeconds:F1}s");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static JsonObject MergeData(JsonObject primary, JsonObject secondary)
    {
        var result = (JsonObject)secondary.DeepClone();
        foreach (var (key, value) in primary)
        {
            if (value is JsonObject p && result[key] is JsonObject s)
                result[key] = MergeData(p, s);
            else
                result[key] = value?.DeepClone();
        }
        return result;
    }

    private async Task<List<JsonObject>> FetchCatalogAsync(CancellationToken ct)
    {
        try
        {
            var r = await _gql.RequestAsync(_gql.Raw("CampaignCatalog"), ct);
            return ((r["data"]?["user"]?["channel"]?["dropCampaigns"] as JsonArray) ?? []).OfType<JsonObject>().ToList();
        }
        catch (MinerException ex)
        {
            Log.Warn("Campaign catalog failed: " + ex.Message);
            return [];
        }
    }

    // Запасной путь, когда дашборд пуст: ищем кампании через каналы с дропами по играм из приоритета
    private async Task<List<string>> DiscoverCampaignsAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>();
        foreach (var gameName in _settings.PriorityGames)
        {
            try
            {
                var redirect = await _gql.RequestAsync(_gql.Op("SlugRedirect", new JsonObject { ["name"] = gameName }), ct);
                var slug = redirect["data"]?["game"].Str("slug");
                if (string.IsNullOrEmpty(slug)) continue;

                var streams = await GetLiveStreamsAsync(slug, ct);
                var ops = streams.Take(10)
                    .Select(ch => _gql.Op("AvailableDrops", new JsonObject { ["channelID"] = ch.Id.ToString() }))
                    .ToList();
                foreach (var r in await _gql.RequestBatchAsync(ops, ct))
                    foreach (var c in (r["data"]?["channel"]?["viewerDropCampaigns"] as JsonArray) ?? [])
                        ids.Add(c.Str("id"));
            }
            catch (MinerException ex)
            {
                Log.Warn($"Campaign discovery for {gameName} failed: {ex.Message}");
            }
        }
        return ids.Where(i => i.Length > 0).ToList();
    }

    private void RestartMaintenance(CancellationToken ct)
    {
        _maintenanceCts?.Cancel();
        _maintenanceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = MaintenanceAsync(new Queue<DateTimeOffset>(_triggers), _maintenanceCts.Token);
    }

    // Раз в час полная перезагрузка, плюс чистка каналов в моменты старта и конца кампаний
    private async Task MaintenanceAsync(Queue<DateTimeOffset> triggers, CancellationToken ct)
    {
        try
        {
            var nextPeriod = Clock.Now.AddHours(1);
            while (Clock.Now < nextPeriod)
            {
                var next = nextPeriod;
                while (triggers.Count > 0 && triggers.Peek() <= next) next = triggers.Dequeue();
                Log.Debug($"Maintenance waits until {next.ToLocalTime():HH:mm:ss} ({(next == nextPeriod ? "reload" : "cleanup")})");
                var wait = next - Clock.Now;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                if (Clock.Now >= nextPeriod) break;
                if (next != nextPeriod) ChangeState(MinerState.ChannelsCleanup);
            }
            ChangeState(MinerState.InventoryFetch);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---------------- Выбор игр и каналов ----------------

    private int GamePriority(string name)
    {
        var idx = _settings.PriorityGames.IndexOf(name);
        return idx < 0 ? int.MaxValue : idx;
    }

    private async Task GamesUpdateAsync(CancellationToken ct)
    {
        foreach (var c in _inventory.Where(c => !c.Upcoming))
            foreach (var d in c.Drops.Where(d => d.CanClaim).ToList())
                await ClaimAsync(d, ct);

        IEnumerable<Campaign> ordered = _settings.PriorityMode switch
        {
            PriorityMode.EndingSoonest => _inventory.OrderBy(c => GamePriority(c.Game.Name)).ThenBy(c => c.EndsAt),
            PriorityMode.LowAvailFirst => _inventory.OrderBy(c => GamePriority(c.Game.Name)).ThenBy(c => c.Availability),
            _ => _inventory.OrderBy(c => GamePriority(c.Game.Name)),
        };
        _inventory = ordered.ToList();

        _wantedGames.Clear();
        var nextHour = Clock.Now.AddHours(1);
        foreach (var c in _inventory)
        {
            var name = c.Game.Name;
            if (_wantedGames.Contains(c.Game) || _settings.ExcludedGames.Contains(name)) continue;
            if (_settings.PriorityMode == PriorityMode.PriorityOnly && !_settings.PriorityGames.Contains(name)) continue;
            if (c.CanEarnWithin(nextHour)) _wantedGames.Add(c.Game);
        }
        Log.Info(_wantedGames.Count > 0 ? "Wanted games: " + string.Join(", ", _wantedGames) : "No wanted games");

        _fullCleanup = true;
        RestartWatching();
        ChangeState(MinerState.ChannelsCleanup);
    }

    private void ChannelsCleanup()
    {
        Status = Loc.T("Status.Cleanup");
        var toRemove = _wantedGames.Count == 0 || _fullCleanup
            ? _channels.Values.ToList()
            : _channels.Values.Where(c => !c.AclBased && (c.Offline || c.Game is null || !_wantedGames.Contains(c.Game))).ToList();
        _fullCleanup = false;

        if (toRemove.Count > 0)
        {
            _pubsub.RemoveTopics(toRemove.SelectMany(c => new[] { Topics.Playback(c.Id), Topics.Settings(c.Id) }));
            foreach (var ch in toRemove)
            {
                ch.PendingOnline?.Cancel();
                _channels.Remove(ch.Id);
                Channels.Remove(ch);
            }
        }

        if (_wantedGames.Count > 0)
        {
            ChangeState(MinerState.ChannelsFetch);
        }
        else
        {
            Log.Info(Loc.T("Status.NoCampaign"));
            ChangeState(MinerState.Idle);
        }
    }

    private async Task ChannelsFetchAsync(CancellationToken ct)
    {
        Status = Loc.T("Status.Gathering");
        var result = _channels.Values.ToDictionary(c => c.Id);
        _channels.Clear();
        Channels.Clear();

        var nextHour = Clock.Now.AddHours(1);
        var aclChannels = new Dictionary<long, Channel>();
        var noAcl = new List<Game>();
        foreach (var c in _inventory.Where(c => _wantedGames.Contains(c.Game) && c.CanEarnWithin(nextHour)))
        {
            if (c.AllowedChannels.Count > 0)
            {
                foreach (var ch in c.AllowedChannels)
                    if (!result.ContainsKey(ch.Id)) aclChannels.TryAdd(ch.Id, ch);
            }
            else if (!noAcl.Contains(c.Game))
            {
                noAcl.Add(c.Game);
            }
        }

        await BulkCheckOnlineAsync(aclChannels.Values.ToList(), ct);
        foreach (var ch in aclChannels.Values) result.TryAdd(ch.Id, ch);

        foreach (var game in noAcl)
        {
            try
            {
                foreach (var ch in await GetLiveStreamsAsync(game.Slug, ct))
                    result.TryAdd(ch.Id, ch);
            }
            catch (MinerException ex)
            {
                Log.Warn($"Game directory failed for {game}: {ex.Message}");
            }
        }

        var ordered = result.Values
            .OrderBy(ChannelPriority)
            .ThenByDescending(c => c.AclBased)
            .ThenByDescending(c => c.Viewers)
            .ToList();
        var kept = ordered.Take(MaxChannels).ToList();
        var trimmed = ordered.Skip(MaxChannels).ToList();
        if (trimmed.Count > 0)
            _pubsub.RemoveTopics(trimmed.SelectMany(c => new[] { Topics.Playback(c.Id), Topics.Settings(c.Id) }));

        foreach (var ch in kept)
        {
            _channels[ch.Id] = ch;
            Channels.Add(ch);
        }
        _pubsub.AddTopics(kept.SelectMany(c => new[] { Topics.Playback(c.Id), Topics.Settings(c.Id) }));

        if (Watching is { } w && _channels.TryGetValue(w.Id, out var same) && CanWatch(same))
            Watch(same, updateStatus: false);
        else
            StopWatching();

        foreach (var ch in _channels.Values)
        {
            if (!CanWatch(ch)) continue;
            if (GetActiveCampaign(ch)?.FirstDrop is { } drop) Display(drop, countdown: false);
            break;
        }
        ChangeState(MinerState.ChannelSwitch);
    }

    private void ChannelSwitch()
    {
        Status = Loc.T("Status.Switching");
        Channel? chosen = null;
        if (RequestedChannel is { } req && _channels.TryGetValue(req.Id, out var selected) && CanWatch(selected))
            chosen = selected;
        RequestedChannel = null;

        chosen ??= _channels.Values.OrderBy(ChannelPriority).FirstOrDefault(ShouldSwitch);

        if (chosen is not null)
        {
            Watch(chosen);
            _stateChange.Clear();
        }
        else if (Watching is { } w && CanWatch(w))
        {
            Status = Loc.F("Status.Watching", w.Name);
            _stateChange.Clear();
        }
        else
        {
            Log.Info(Loc.T("Status.NoChannel"));
            ChangeState(MinerState.Idle);
        }
    }

    private int ChannelPriority(Channel ch)
    {
        if (ch.Game is null) return int.MaxValue;
        var idx = _wantedGames.IndexOf(ch.Game);
        return idx < 0 ? int.MaxValue : idx;
    }

    private bool CanWatch(Channel ch)
    {
        if (!ch.Online) return false;
        return _inventory.Any(c => c.CanEarn(ch) && (
            (ch.Game is not null && ch.DropsEnabled && _wantedGames.Contains(ch.Game)) || c.Game.IsSpecial));
    }

    private bool ShouldSwitch(Channel ch)
    {
        if (!CanWatch(ch)) return false;
        if (Watching is not { } w || !CanWatch(w)) return true;
        int c = ChannelPriority(ch), cur = ChannelPriority(w);
        return c < cur || (c == cur && ch.AclBased && !w.AclBased);
    }

    private Campaign? GetActiveCampaign(Channel? channel = null)
    {
        if (_wantedGames.Count == 0) return null;
        var target = Watching ?? channel;
        if (target is null) return null;
        return _inventory.Where(c => c.CanEarn(target)).OrderBy(c => c.RemainingMinutes).FirstOrDefault();
    }

    private void Watch(Channel ch, bool updateStatus = true)
    {
        Activity = MinerActivity.Watching;
        if (Watching is { } old && !ReferenceEquals(old, ch)) old.IsWatching = false;
        ch.IsWatching = true;
        Watching = ch;
        _watchingSet.Set();
        if (updateStatus)
        {
            Log.Info(Loc.F("Status.Watching", ch.Name));
            Status = Loc.F("Status.Watching", ch.Name);
        }
        if (GetActiveCampaign(ch)?.FirstDrop is { } drop && CurrentDrop is null) Display(drop, countdown: false);
    }

    private void StopWatching()
    {
        StopHls();
        if (Watching is { } w) w.IsWatching = false;
        Watching = null;
        _watchingSet.Clear();
        CurrentDrop = null;
        _minuteStartedAt = null;
        OnPropertyChanged(nameof(MinuteProgress));
    }

    private void RestartWatching()
    {
        _minuteStartedAt = null;
        _watchRestart.Set();
    }

    private void Display(TimedDrop? drop, bool countdown = true)
    {
        CurrentDrop = drop;
        if (countdown) _minuteStartedAt = Clock.Now;
        OnPropertyChanged(nameof(MinuteProgress));
    }

    // ---------------- Каналы и стримы ----------------

    private async Task<List<Channel>> GetLiveStreamsAsync(string slug, CancellationToken ct)
    {
        JsonNode response;
        try
        {
            response = await _gql.RequestAsync(_gql.Op("GameDirectory", new JsonObject
            {
                ["limit"] = DirectoryLimit,
                ["slug"] = slug,
                ["options"] = new JsonObject
                {
                    ["includeRestricted"] = new JsonArray("SUB_ONLY_LIVE"),
                    ["systemFilters"] = new JsonArray("DROPS_ENABLED"),
                },
            }), ct);
        }
        catch (GqlException ex)
        {
            throw new MinerException("Game: " + slug, ex);
        }

        var list = new List<Channel>();
        foreach (var edge in (response["data"]?["game"]?["streams"]?["edges"] as JsonArray) ?? [])
            if (edge?["node"] is JsonObject node && node["broadcaster"] is JsonObject)
                list.Add(Channel.FromDirectory(node));
        return list;
    }

    private async Task<StreamInfo?> GetStreamAsync(Channel ch, CancellationToken ct)
    {
        var response = await _gql.RequestAsync(_gql.Op("GetStreamInfo", new JsonObject { ["channel"] = ch.Login }), ct);
        var user = response["data"]?["user"];
        if (user is null) return null;
        if (string.IsNullOrEmpty(ch.DisplayName)) ch.DisplayName = user.StrOrNull("displayName");
        if (user["stream"] is not JsonObject) return null;

        var stream = StreamInfo.FromStreamInfo(user, !_settings.AvailableDropsCheck);
        if (!stream.DropsEnabled)
        {
            try
            {
                var ad = await _gql.RequestAsync(_gql.Op("AvailableDrops", new JsonObject { ["channelID"] = ch.Id.ToString() }), ct);
                stream.DropsEnabled = CheckDropsEnabled(ch, ad["data"]?["channel"]?["viewerDropCampaigns"] as JsonArray);
            }
            catch (MinerException ex)
            {
                Log.Debug($"AvailableDrops failed for {ch.Name}: {ex.Message}");
            }
        }
        return stream;
    }

    private bool CheckDropsEnabled(Channel ch, JsonArray? campaigns) =>
        campaigns?.Any(c => _campaigns.TryGetValue(c.Str("id"), out var camp) && camp.CanEarn(ch, ignoreChannelStatus: true)) ?? false;

    private async Task BulkCheckOnlineAsync(List<Channel> channels, CancellationToken ct)
    {
        if (channels.Count == 0) return;
        var responses = await _gql.RequestBatchAsync(
            channels.Select(c => _gql.Op("GetStreamInfo", new JsonObject { ["channel"] = c.Login })).ToList(), ct);

        var users = new Dictionary<long, JsonNode>();
        foreach (var r in responses)
            if (r["data"]?["user"] is JsonObject u) users[u.Long("id")] = u;

        var available = new Dictionary<long, JsonArray?>();
        if (_settings.AvailableDropsCheck)
        {
            var online = users.Where(kv => kv.Value["stream"] is JsonObject).Select(kv => kv.Key).ToList();
            var ad = await _gql.RequestBatchAsync(
                online.Select(id => _gql.Op("AvailableDrops", new JsonObject { ["channelID"] = id.ToString() })).ToList(), ct);
            foreach (var r in ad)
                if (r["data"]?["channel"] is JsonObject chData)
                    available[chData.Long("id")] = chData["viewerDropCampaigns"] as JsonArray;
        }

        foreach (var ch in channels)
        {
            if (!users.TryGetValue(ch.Id, out var user)) continue;
            if (string.IsNullOrEmpty(ch.DisplayName)) ch.DisplayName = user.StrOrNull("displayName");
            if (user["stream"] is not JsonObject) continue;
            var stream = StreamInfo.FromStreamInfo(user, !_settings.AvailableDropsCheck);
            if (!stream.DropsEnabled) stream.DropsEnabled = CheckDropsEnabled(ch, available.GetValueOrDefault(ch.Id));
            ch.Stream = stream;
        }
    }

    private async Task UpdateStreamAsync(Channel ch, CancellationToken ct)
    {
        var before = ch.Stream;
        ch.Stream = await GetStreamAsync(ch, ct);
        OnChannelUpdate(ch, before, ch.Stream);
    }

    // stream-up приходит раньше, чем стрим реально доступен, поэтому проверяем через 2 минуты
    private void CheckOnline(Channel ch, CancellationToken ct)
    {
        if (ch.PendingOnline is not null) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ch.PendingOnline = cts;
        ch.NotifyStream();
        _ = DelayedCheckAsync(ch, cts);
    }

    private async Task DelayedCheckAsync(Channel ch, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(OnlineDelay, cts.Token);
            ch.PendingOnline = null;
            await UpdateStreamAsync(ch, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Stream check failed for {ch.Name}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(ch.PendingOnline, cts)) ch.PendingOnline = null;
            ch.NotifyStream();
        }
    }

    private void SetOffline(Channel ch)
    {
        if (ch.PendingOnline is { } p)
        {
            p.Cancel();
            ch.PendingOnline = null;
        }
        if (ch.Online)
        {
            var before = ch.Stream;
            ch.Stream = null;
            OnChannelUpdate(ch, before, null);
        }
        ch.NotifyStream();
    }

    private void OnChannelUpdate(Channel ch, StreamInfo? before, StreamInfo? after)
    {
        if (before is null && after is not null)
        {
            if (ShouldSwitch(ch))
            {
                Log.Info(Loc.F("Status.GoesOnline", ch.Name));
                Watch(ch);
            }
        }
        else if (before is not null && ReferenceEquals(Watching, ch))
        {
            if (!CanWatch(ch))
            {
                if (after is null) Log.Info(Loc.F("Status.GoesOffline", ch.Name));
                ChangeState(MinerState.ChannelSwitch);
            }
        }
        else if (before is not null && after is not null && ShouldSwitch(ch))
        {
            Watch(ch);
        }
        ch.NotifyStream();
    }

    // ---------------- Просмотр ----------------

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _watchingSet.WaitAsync(ct);
                var ch = Watching;
                if (ch is null) continue;
                if (!ch.Online)
                {
                    StopWatching();
                    continue;
                }

                var sent = await SendWatchAsync(ch, ct);
                var lastSent = Clock.Now;
                if (!sent) Log.Info($"Stream is stalled for channel {ch.Name}");

                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                if (MinuteAlmostDone())
                {
                    // Twitch иногда перестаёт присылать прогресс, спрашиваем его сами
                    var handled = false;
                    try
                    {
                        var r = await _gql.RequestAsync(_gql.Op("CurrentDrop", new JsonObject { ["channelID"] = ch.Id.ToString() }), ct);
                        var session = r["data"]?["currentUser"]?["dropCurrentSession"];
                        if (session is JsonObject && _drops.TryGetValue(session.Str("dropID"), out var drop) && drop.CanEarn(ch))
                        {
                            drop.UpdateMinutes(session.Int("currentMinutesWatched"));
                            Display(drop.Campaign.FirstDrop ?? drop);
                            handled = true;
                        }
                    }
                    catch (GqlException ex)
                    {
                        Log.Debug("CurrentDrop failed: " + ex.Message);
                    }

                    if (!handled)
                    {
                        if (GetActiveCampaign(ch) is { } campaign)
                        {
                            if (campaign.BumpMinutes(ch))
                            {
                                Log.Warn($"Campaign \"{campaign.Name}\" ({campaign.Game}) reached the limit of unconfirmed minutes");
                                ChangeState(MinerState.ChannelSwitch);
                            }
                            Display(campaign.FirstDrop);
                        }
                        else
                        {
                            Log.Debug("No active drop could be determined");
                        }
                    }
                }

                _watchRestart.Clear();
                var remaining = WatchInterval - (Clock.Now - lastSent);
                if (remaining > TimeSpan.Zero) await _watchRestart.WaitAsync(remaining, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool MinuteAlmostDone() =>
        _minuteStartedAt is not { } t || Clock.Now - t >= TimeSpan.FromSeconds(50);

    public void Tick() => OnPropertyChanged(nameof(MinuteProgress));

    // Twitch засчитывает минуты только тем, кто реально тянет поток: события spade
    // он принимает, но не считает. Поэтому держим поток канала в режиме "только звук"
    // и непрерывно скачиваем сегменты в никуда (около 100 МБ в час).
    private sealed class HlsSession(Channel channel)
    {
        public Channel Channel { get; } = channel;
        public CancellationTokenSource Cts { get; } = new();
        public Task? Task { get; set; }
        public DateTimeOffset LastSegment { get; set; } = Clock.Now;
    }

    private HlsSession? _hls;

    private Task<bool> SendWatchAsync(Channel ch, CancellationToken ct)
    {
        if (ch.Stream is null) return Task.FromResult(false);
        if (_hls is null || !ReferenceEquals(_hls.Channel, ch) || _hls.Task is { IsCompleted: true })
        {
            StopHls();
            var session = new HlsSession(ch);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);
            session.Task = HlsLoopAsync(session, linked.Token);
            _hls = session;
            return Task.FromResult(true);
        }
        return Task.FromResult(Clock.Now - _hls.LastSegment < TimeSpan.FromSeconds(45));
    }

    private void StopHls()
    {
        _hls?.Cts.Cancel();
        _hls = null;
    }

    private async Task HlsLoopAsync(HlsSession session, CancellationToken ct)
    {
        var backoff = new Backoff(60);
        var ch = session.Channel;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var playlist = await GetAudioPlaylistAsync(ch, ct);
                Log.Debug($"Stream opened: {ch.Name}");
                var seen = new Queue<string>();
                var known = new HashSet<string>();
                var first = true;
                var refreshAt = Clock.Now.AddMinutes(10);

                while (!ct.IsCancellationRequested && Clock.Now < refreshAt)
                {
                    using var list = await _http.Bare.GetAsync(playlist, ct);
                    if (!list.IsSuccessStatusCode) break;
                    var segments = (await list.Content.ReadAsStringAsync(ct))
                        .Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("http", StringComparison.Ordinal)).ToList();

                    // При первом заходе берём только хвост, как плеер с живой точки
                    var fresh = segments.Where(known.Add).ToList();
                    if (first) fresh = fresh.TakeLast(2).ToList();
                    first = false;

                    foreach (var url in fresh)
                    {
                        using var seg = await _http.Bare.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (!seg.IsSuccessStatusCode) continue;
                        await using var body = await seg.Content.ReadAsStreamAsync(ct);
                        await body.CopyToAsync(Stream.Null, ct);
                        session.LastSegment = Clock.Now;
                    }

                    foreach (var url in fresh) seen.Enqueue(url);
                    while (seen.Count > 60) known.Remove(seen.Dequeue());
                    backoff.Reset();
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug($"Stream for {ch.Name} failed: {ex.Message}");
                try { await Task.Delay(backoff.Next(), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task<string> GetAudioPlaylistAsync(Channel ch, CancellationToken ct)
    {
        var r = await _gql.RequestAsync(_gql.Op("PlaybackAccessToken", new JsonObject { ["login"] = ch.Login }), ct);
        var token = r["data"]?["streamPlaybackAccessToken"];
        var value = token.Str("value");
        var signature = token.Str("signature");
        if (value.Length == 0) throw new MinerException("No playback token for " + ch.Login);

        var usher = $"https://usher.ttvnw.net/api/channel/hls/{Uri.EscapeDataString(ch.Login)}.m3u8"
            + $"?sig={signature}&token={Uri.EscapeDataString(value)}&allow_source=true&allow_audio_only=true&p={Random.Shared.Next(1_000_000)}";
        using var master = await _http.Bare.GetAsync(usher, ct);
        if (!master.IsSuccessStatusCode) throw new MinerException($"Usher answered {(int)master.StatusCode} for {ch.Login}");

        var lines = (await master.Content.ReadAsStringAsync(ct)).Split('\n').Select(l => l.Trim()).ToList();
        var audio = lines.FindIndex(l => l.Contains("VIDEO=\"audio_only\"", StringComparison.Ordinal));
        if (audio >= 0 && audio + 1 < lines.Count) return lines[audio + 1];
        // Если звуковой дорожки нет, берём самое низкое качество (последний вариант)
        return lines.LastOrDefault(l => l.StartsWith("http", StringComparison.Ordinal))
            ?? throw new MinerException("Empty playlist for " + ch.Login);
    }

    // ---------------- Получение наград ----------------

    private async Task ClaimAsync(TimedDrop drop, CancellationToken ct)
    {
        if (await TryClaimAsync(drop, ct))
        {
            drop.MarkClaimed();
            var c = drop.Campaign;
            var text = $"{c.Game}: {drop.RewardsText} ({c.ClaimedDrops}/{c.TotalDrops})";
            Log.Info(Loc.F("Status.ClaimedDrop", text));
            if (_settings.Notifications) DropClaimed?.Invoke(c.Game.Name, $"{drop.RewardsText} ({c.ClaimedDrops}/{c.TotalDrops})");
        }
        else
        {
            Log.Error($"Drop claim has potentially failed! Drop ID: {drop.Id}");
        }
    }

    private async Task<bool> TryClaimAsync(TimedDrop drop, CancellationToken ct)
    {
        if (drop.IsClaimed) return true;
        if (!drop.CanClaim) return false;
        try
        {
            var r = await _gql.RequestAsync(_gql.Op("ClaimDrop", new JsonObject
            {
                ["input"] = new JsonObject { ["dropInstanceID"] = drop.ClaimId },
            }), ct);
            var data = r["data"];
            if (data?["errors"] is JsonArray { Count: > 0 }) return false;
            return data?["claimDropRewards"].Str("status") is "ELIGIBLE_FOR_ALL" or "DROP_INSTANCE_ALREADY_CLAIMED";
        }
        catch (GqlException ex)
        {
            Log.Debug("Claim failed: " + ex.Message);
            if (ex.IsIntegrity) LastError = Loc.T("Error.ClaimIntegrity");
            return false;
        }
    }

    // ---------------- PubSub ----------------

    private void OnPubSubMessage(string topic, JsonNode message)
    {
        var dot = topic.LastIndexOf('.');
        if (dot < 0 || !long.TryParse(topic[(dot + 1)..], out var target)) return;
        var name = topic[..dot];
        var ct = _runCts?.Token ?? CancellationToken.None;

        _ = Guard(name switch
        {
            "user-drop-events" => ProcessDropsAsync(message, ct),
            "onsite-notifications" => ProcessNotificationAsync(message, ct),
            "video-playback-by-id" => ProcessStreamState(target, message, ct),
            "broadcast-settings-update" => ProcessStreamUpdate(target, message, ct),
            _ => Task.CompletedTask,
        });
    }

    private static async Task Guard(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("Event handler failed: " + ex.Message); }
    }

    private async Task ProcessDropsAsync(JsonNode msg, CancellationToken ct)
    {
        var type = msg.Str("type");
        if (type is not ("drop-progress" or "drop-claim")) return;
        var data = msg["data"];
        _drops.TryGetValue(data.Str("drop_id"), out var drop);
        var watching = Watching;

        if (type == "drop-claim")
        {
            if (drop is null)
            {
                Log.Error($"Received a drop claim for an unknown drop: {data.Str("drop_instance_id")}");
                return;
            }
            drop.ClaimId = data.Str("drop_instance_id");
            await ClaimAsync(drop, ct);
            Display(drop.Campaign.FirstDrop, countdown: false);

            // Следующий дроп стартует через 4-20 секунд после получения предыдущего
            await Task.Delay(4000, ct);
            if (watching is not null)
            {
                for (var i = 0; i < 8; i++)
                {
                    var r = await _gql.RequestAsync(_gql.Op("CurrentDrop", new JsonObject { ["channelID"] = watching.Id.ToString() }), ct);
                    var session = r["data"]?["currentUser"]?["dropCurrentSession"];
                    if (session is not JsonObject || session.Str("dropID") != drop.Id) break;
                    await Task.Delay(2000, ct);
                }
            }
            if (drop.Campaign.CanEarn(watching)) RestartWatching();
            else ChangeState(MinerState.InventoryFetch);
        }
        else
        {
            int current = data.Int("current_progress_min"), required = data.Int("required_progress_min");
            Log.Debug($"Drop progress: {drop?.Name ?? data.Str("drop_id")} {current}/{required}");
            if (drop is not null && drop.CanEarn(watching))
            {
                drop.UpdateMinutes(current);
                Display(drop.Campaign.FirstDrop ?? drop);
            }
        }
    }

    private async Task ProcessNotificationAsync(JsonNode msg, CancellationToken ct)
    {
        if (msg.Str("type") != "create-notification") return;
        var n = msg["data"]?["notification"];
        if (n.Str("type") is "user_drop_reward_reminder_notification" or "quests_viewer_reward_campaign_earned_emote")
        {
            ChangeState(MinerState.InventoryFetch);
            await _gql.RequestAsync(_gql.Op("NotificationsDelete", new JsonObject { ["input"] = new JsonObject { ["id"] = n.Str("id") } }), ct);
        }
    }

    private Task ProcessStreamState(long channelId, JsonNode msg, CancellationToken ct)
    {
        if (!_channels.TryGetValue(channelId, out var ch)) return Task.CompletedTask;
        switch (msg.Str("type"))
        {
            case "viewcount":
                if (!ch.Online) CheckOnline(ch, ct);
                else ch.Viewers = msg.Int("viewers");
                break;
            case "stream-down":
                SetOffline(ch);
                break;
            case "stream-up":
                CheckOnline(ch, ct);
                break;
        }
        return Task.CompletedTask;
    }

    private Task ProcessStreamUpdate(long channelId, JsonNode msg, CancellationToken ct)
    {
        if (!_channels.TryGetValue(channelId, out var ch)) return Task.CompletedTask;
        if (msg.Str("old_game") != msg.Str("game"))
            Log.Debug($"{ch.Name} changed game: {msg.Str("old_game")} -> {msg.Str("game")}");
        // Тегов в сообщении нет, поэтому просто перепроверяем канал с задержкой
        CheckOnline(ch, ct);
        return Task.CompletedTask;
    }

    public void RefreshTexts()
    {
        foreach (var c in Inventory) c.NotifyTexts();
        foreach (var ch in Channels) ch.NotifyStream();
        if (Watching is { } w) Status = Loc.F("Status.Watching", w.Name);
    }
}
