using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.Twitch;

public sealed partial class PointsChannel : ObservableObject
{
    public PointsChannel(PointsEntry entry)
    {
        Login = entry.Login;
        _displayName = string.IsNullOrEmpty(entry.Name) ? entry.Login : entry.Name;
        AvatarUrl = entry.Avatar;
    }

    public string Login { get; }
    public string AvatarUrl { get; set; }
    public long ChannelId { get; set; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private bool _isWatching;
    [ObservableProperty] private int _number;
    [ObservableProperty] private long? _balance;
    [ObservableProperty] private string _pointsName = "";

    public string BalanceText => Balance is { } b ? $"{b:N0} {PointsName}".Trim() : "";

    partial void OnBalanceChanged(long? value) => OnPropertyChanged(nameof(BalanceText));
    partial void OnPointsNameChanged(string value) => OnPropertyChanged(nameof(BalanceText));
}

public sealed partial class FollowChannel : ObservableObject
{
    public required string Login { get; init; }
    public required string DisplayName { get; init; }
    public string AvatarUrl { get; init; } = "";

    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private bool _inList;
}

// Сбор баллов каналов: когда дропов нет, смотрим каналы из списка по порядку
public sealed partial class Miner
{
    private static readonly TimeSpan PointsLivePeriod = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PointsCheckPeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PointsBalancesPeriod = TimeSpan.FromMinutes(10);
    // Дропов нет хотя бы столько, чтобы не дёргать браузер при коротком переключении каналов
    private static readonly TimeSpan PointsIdleGrace = TimeSpan.FromSeconds(30);

    private readonly PointsBrowser _pointsBrowser = new();
    private readonly AsyncSignal _pointsWake = new();
    private DateTimeOffset _noDropsSince = DateTimeOffset.MinValue;
    private bool _pointsLiveDirty = true;

    public ObservableCollection<PointsChannel> PointsChannels { get; } = [];
    public ObservableCollection<FollowChannel> Follows { get; } = [];

    [ObservableProperty] private string _pointsStatus = "";
    [ObservableProperty] private bool _pointsActive;
    [ObservableProperty] private bool _followsLoading;

    public bool PointsEnabled => _settings.PointsEnabled;
    public bool PointsUseModule => _settings.PointsUseModule;

    public void SetPointsUseModule(bool useModule)
    {
        if (_settings.PointsUseModule == useModule) return;
        _settings.PointsUseModule = useModule;
        _settings.Save();
        // Браузер сменился, перезапускаем просмотр уже в нём
        _pointsBrowser.Stop();
        OnPropertyChanged(nameof(PointsUseModule));
        WakePoints();
    }

    private void InitPoints()
    {
        PointsBrowser.KillOrphans();
        foreach (var e in _settings.PointsChannels) PointsChannels.Add(new PointsChannel(e));
        Renumber();
        _ = LoadAvatarsAsync(PointsChannels.Select(c => (c.AvatarUrl, (Action<Bitmap?>)(b => c.Avatar = b))).ToList());
    }

    public void SetPointsEnabled(bool enabled)
    {
        _settings.PointsEnabled = enabled;
        _settings.Save();
        OnPropertyChanged(nameof(PointsEnabled));
        WakePoints();
    }

    public void AddPointsChannel(FollowChannel f)
    {
        if (PointsChannels.Any(c => c.Login == f.Login)) return;
        var channel = new PointsChannel(new PointsEntry { Login = f.Login, Name = f.DisplayName, Avatar = f.AvatarUrl }) { Avatar = f.Avatar };
        PointsChannels.Add(channel);
        f.InList = true;
        SavePointsList();
        _pointsLiveDirty = true;
        WakePoints();
    }

    public void RemovePointsChannel(PointsChannel channel)
    {
        PointsChannels.Remove(channel);
        foreach (var f in Follows.Where(f => f.Login == channel.Login)) f.InList = false;
        SavePointsList();
        WakePoints();
    }

    public void MovePointsChannel(PointsChannel channel, int delta)
    {
        var i = PointsChannels.IndexOf(channel);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= PointsChannels.Count) return;
        PointsChannels.Move(i, j);
        SavePointsList();
        WakePoints();
    }

    private void SavePointsList()
    {
        _settings.PointsChannels = PointsChannels
            .Select(c => new PointsEntry { Login = c.Login, Name = c.DisplayName, Avatar = c.AvatarUrl })
            .ToList();
        _settings.Save();
        Renumber();
    }

    private void Renumber()
    {
        for (var i = 0; i < PointsChannels.Count; i++) PointsChannels[i].Number = i + 1;
    }

    public void WakePoints() => _pointsWake.Set();

    // Вызывается из Watch и StopWatching: дропы важнее баллов
    private void OnDropWatchingChanged(bool watching)
    {
        _noDropsSince = watching ? DateTimeOffset.MaxValue : Clock.Now;
        WakePoints();
    }

    public async Task LoadFollowsAsync()
    {
        if (_gql is null || !LoggedIn || FollowsLoading) return;
        FollowsLoading = true;
        try
        {
            var list = new List<FollowChannel>();
            string? cursor = null;
            for (var page = 0; page < 20; page++)
            {
                var vars = new JsonObject();
                if (cursor is not null) vars["cursor"] = cursor;
                var r = await _gql.RequestAsync(_gql.Op("ChannelFollows", vars), _runCts?.Token ?? CancellationToken.None);
                var follows = r["data"]?["user"]?["follows"];
                var edges = follows?["edges"] as JsonArray ?? [];
                foreach (var e in edges)
                {
                    var n = e?["node"];
                    if (n is null) continue;
                    var login = n.Str("login");
                    list.Add(new FollowChannel
                    {
                        Login = login,
                        DisplayName = n.StrOrNull("displayName") ?? login,
                        AvatarUrl = n.Str("profileImageURL"),
                        InList = PointsChannels.Any(c => c.Login == login),
                    });
                    cursor = e.Str("cursor");
                }
                if (follows?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() != true || edges.Count == 0) break;
            }

            Follows.Clear();
            foreach (var f in list.OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase)) Follows.Add(f);
            Log.Info($"Follows loaded: {list.Count}");

            // У каналов из списка без картинки берём аватарку из подписок
            var missing = new List<(string, Action<Bitmap?>)>();
            foreach (var c in PointsChannels.Where(c => c.AvatarUrl.Length == 0))
            {
                if (list.FirstOrDefault(f => f.Login == c.Login) is not { AvatarUrl.Length: > 0 } f) continue;
                c.AvatarUrl = f.AvatarUrl;
                missing.Add((f.AvatarUrl, b => c.Avatar = b));
            }
            if (missing.Count > 0)
            {
                SavePointsList();
                _ = LoadAvatarsAsync(missing);
            }
            _ = LoadAvatarsAsync(Follows.Select(f => (f.AvatarUrl, (Action<Bitmap?>)(b => f.Avatar = b))).ToList());
        }
        catch (Exception ex) when (ex is MinerException or HttpRequestException)
        {
            Log.Warn("Follows not loaded: " + ex.Message);
        }
        finally
        {
            FollowsLoading = false;
        }
    }

    private async Task LoadAvatarsAsync(List<(string Url, Action<Bitmap?> Apply)> items)
    {
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(items.Where(i => i.Url.Length > 0).Select(async i =>
        {
            await gate.WaitAsync();
            try { i.Apply(await Images.GetAsync(i.Url, 64, CancellationToken.None)); }
            finally { gate.Release(); }
        }));
    }

    private async Task PointsLoopAsync(CancellationToken ct)
    {
        var lastLive = DateTimeOffset.MinValue;
        var lastCheck = DateTimeOffset.MinValue;
        var lastBalances = DateTimeOffset.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _pointsWake.WaitAsync(TimeSpan.FromSeconds(20), ct);
                _pointsWake.Clear();
                try
                {
                    var now = Clock.Now;
                    if (PointsChannels.Count > 0 && (_pointsLiveDirty || now - lastLive > PointsLivePeriod))
                    {
                        await RefreshPointsLiveAsync(ct);
                        lastLive = now;
                        _pointsLiveDirty = false;
                    }
                    if (PointsChannels.Count > 0 && now - lastBalances > PointsBalancesPeriod)
                    {
                        foreach (var c in PointsChannels.ToList()) await CheckPointsAsync(c, claim: false, ct);
                        lastBalances = now;
                    }

                    var exe = BrowserModule.Resolve(_settings.PointsUseModule);
                    var target = PointsChannels.FirstOrDefault(c => c.IsLive);
                    var blocked = !_settings.PointsEnabled ? "" // выключено, статус не нужен
                        : PointsChannels.Count == 0 ? Loc.T("Points.StatusEmpty")
                        : exe is null ? Loc.T("Points.StatusNoBrowser")
                        : Watching is not null || now - _noDropsSince < PointsIdleGrace ? Loc.T("Points.StatusDrops")
                        : target is null ? Loc.T("Points.StatusNobodyLive")
                        : null;

                    if (blocked is not null)
                    {
                        StopPoints(blocked);
                        continue;
                    }

                    await _pointsBrowser.WatchAsync(exe!, target!.Login, _auth.AccessToken!, _auth.DeviceId ?? "", ct);
                    foreach (var c in PointsChannels) c.IsWatching = ReferenceEquals(c, target);
                    PointsActive = true;
                    PointsStatus = Loc.F("Points.StatusWatching", target.DisplayName);

                    if (now - lastCheck > PointsCheckPeriod)
                    {
                        await CheckPointsAsync(target, claim: true, ct);
                        lastCheck = now;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warn("Points: " + ex.Message);
                    StopPoints(Loc.T("Points.StatusError"));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            StopPoints("");
        }
    }

    private void StopPoints(string status)
    {
        _pointsBrowser.Stop();
        foreach (var c in PointsChannels) c.IsWatching = false;
        PointsActive = false;
        PointsStatus = status;
    }

    private async Task RefreshPointsLiveAsync(CancellationToken ct)
    {
        var channels = PointsChannels.ToList();
        var responses = await _gql.RequestBatchAsync(
            channels.Select(c => _gql.Op("GetStreamInfo", new JsonObject { ["channel"] = c.Login })).ToList(), ct);
        for (var i = 0; i < channels.Count && i < responses.Count; i++)
        {
            var user = responses[i]["data"]?["user"];
            channels[i].IsLive = user?["stream"] is JsonObject;
            if (user is not null)
            {
                channels[i].ChannelId = user.Long("id");
                if (user.StrOrNull("displayName") is { Length: > 0 } name) channels[i].DisplayName = name;
            }
        }
    }

    private async Task CheckPointsAsync(PointsChannel channel, bool claim, CancellationToken ct)
    {
        var r = await _gql.RequestAsync(_gql.Op("ChannelPointsContext", new JsonObject { ["channelLogin"] = channel.Login }), ct);
        var community = r["data"]?["community"]?["channel"];
        if (community is null) return;

        channel.ChannelId = community.Long("id") is > 0 and var id ? id : channel.ChannelId;
        channel.PointsName = community["communityPointsSettings"].Str("name");
        var points = community["self"]?["communityPoints"];
        channel.Balance = points.Long("balance");

        // Бонус появляется раз в ~15 минут просмотра, забираем сразу
        if (claim && points?["availableClaim"] is JsonObject available)
        {
            var result = await _gql.RequestAsync(_gql.Op("ClaimCommunityPoints", new JsonObject
            {
                ["input"] = new JsonObject { ["claimID"] = available.Str("id"), ["channelID"] = channel.ChannelId.ToString() },
            }), ct);
            var payload = result["data"]?["claimCommunityPoints"];
            if (payload?["error"] is JsonObject error)
            {
                Log.Warn($"Points bonus not claimed on {channel.DisplayName}: {error.Str("code")}");
                return;
            }
            var after = payload.Long("currentPoints");
            if (after > 0)
            {
                Log.Info(Loc.F("Points.Claimed", channel.DisplayName, after - (channel.Balance ?? after)));
                channel.Balance = after;
            }
        }
    }
}
