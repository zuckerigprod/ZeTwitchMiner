using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.Twitch;

internal static partial class J
{
    public static string Str(this JsonNode? n, string key) => n?[key]?.GetValue<string>() ?? "";
    public static string? StrOrNull(this JsonNode? n, string key) => n?[key] is JsonValue v ? v.ToString() : null;
    public static long Long(this JsonNode? n, string key) => n?[key] switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when long.TryParse(v.ToString(), out var p) => p,
        _ => 0,
    };
    public static int Int(this JsonNode? n, string key) => (int)n.Long(key);
    public static bool Bool(this JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static DateTimeOffset Time(this JsonNode? n, string key)
    {
        var s = n.Str(key);
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t : DateTimeOffset.MinValue;
    }

    [GeneratedRegex(@"-\d+x\d+(?=\.(?:jpg|png|gif)$)", RegexOptions.IgnoreCase)]
    public static partial Regex Dimensions();
}

public static class Clock
{
    public static DateTimeOffset Now => DateTimeOffset.UtcNow;
}

public sealed partial class Game : IEquatable<Game>
{
    private static readonly HashSet<long> SpecialIds = [509663, 509672];

    public long Id { get; }
    public string Name { get; }
    public string Slug { get; }

    public Game(JsonNode data)
    {
        Id = data.Long("id");
        Name = data.StrOrNull("displayName") ?? data.Str("name");
        Slug = data.StrOrNull("slug") ?? MakeSlug(Name);
    }

    public bool IsSpecial => SpecialIds.Contains(Id);

    private static string MakeSlug(string name)
    {
        var s = NonWord().Replace(name.ToLowerInvariant().Replace("'", ""), "-");
        return Dashes().Replace(s.Trim('-'), "-");
    }

    [GeneratedRegex(@"\W+")] private static partial Regex NonWord();
    [GeneratedRegex(@"-{2,}")] private static partial Regex Dashes();

    public bool Equals(Game? other) => other is not null && other.Id == Id;
    public override bool Equals(object? obj) => Equals(obj as Game);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => Name;
}

public enum BenefitType { Unknown, Badge, Emote, DirectEntitlement }

public sealed partial class Benefit : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public BenefitType Type { get; }
    public string ImageUrl { get; }

    [ObservableProperty] private Bitmap? _image;

    public Benefit(JsonNode edge)
    {
        var b = edge["benefit"]!;
        Id = b.Str("id");
        Name = b.Str("name");
        ImageUrl = b.Str("imageAssetURL");
        Type = b.Str("distributionType") switch
        {
            "BADGE" => BenefitType.Badge,
            "EMOTE" => BenefitType.Emote,
            "DIRECT_ENTITLEMENT" => BenefitType.DirectEntitlement,
            _ => BenefitType.Unknown,
        };
    }

    public bool IsBadgeOrEmote => Type is BenefitType.Badge or BenefitType.Emote;
}

public sealed partial class TimedDrop : ObservableObject
{
    public const int MaxExtraMinutes = 15;

    public string Id { get; }
    public string Name { get; }
    public Campaign Campaign { get; }
    public List<Benefit> Benefits { get; }
    public DateTimeOffset StartsAt { get; }
    public DateTimeOffset EndsAt { get; }
    public List<string> PreconditionIds { get; }
    public int RequiredMinutes { get; }

    public string? ClaimId { get; set; }
    public bool IsClaimed { get; private set; }
    public int RealMinutes { get; private set; }
    public int ExtraMinutes { get; private set; }

    public TimedDrop(Campaign campaign, JsonNode d, IReadOnlyDictionary<string, DateTimeOffset> claimedBenefits)
    {
        Campaign = campaign;
        Id = d.Str("id");
        Name = d.Str("name");
        Benefits = (d["benefitEdges"] as JsonArray)?.Select(b => new Benefit(b!)).ToList() ?? [];
        StartsAt = d.Time("startAt");
        EndsAt = d.Time("endAt");
        RequiredMinutes = d.Int("requiredMinutesWatched");
        PreconditionIds = (d["preconditionDrops"] as JsonArray)?.Select(p => p.Str("id")).ToList() ?? [];

        if (d["self"] is JsonObject self)
        {
            ClaimId = self.StrOrNull("dropInstanceID");
            IsClaimed = self.Bool("isClaimed");
            RealMinutes = self.Int("currentMinutesWatched");
        }
        else
        {
            // Нет self: смотрим, выдавались ли награды в окне этого дропа
            var stamps = Benefits.Where(b => claimedBenefits.ContainsKey(b.Id)).Select(b => claimedBenefits[b.Id]).ToList();
            IsClaimed = stamps.Count > 0 && stamps.All(t => StartsAt <= t && t < EndsAt);
        }
        // У полученных дропов Twitch иногда отдаёт странные минуты
        if (IsClaimed) RealMinutes = RequiredMinutes;
    }

    public int CurrentMinutes => RealMinutes + ExtraMinutes;
    public int RemainingMinutes => RequiredMinutes - CurrentMinutes;

    private IEnumerable<TimedDrop> Preconditions =>
        PreconditionIds.Select(id => Campaign.Drops.FirstOrDefault(d => d.Id == id)).OfType<TimedDrop>();

    public int TotalRequiredMinutes => RequiredMinutes + Preconditions.Select(p => p.TotalRequiredMinutes).DefaultIfEmpty(0).Max();
    public int TotalRemainingMinutes => RemainingMinutes + Preconditions.Select(p => p.TotalRemainingMinutes).DefaultIfEmpty(0).Max();

    public double Progress => CurrentMinutes <= 0 || RequiredMinutes <= 0 ? 0
        : CurrentMinutes >= RequiredMinutes ? 1 : (double)CurrentMinutes / RequiredMinutes;

    public double Availability
    {
        get
        {
            var now = Clock.Now;
            return RequiredMinutes > 0 && TotalRemainingMinutes > 0 && now < EndsAt
                ? (EndsAt - now).TotalMinutes / TotalRemainingMinutes
                : double.PositiveInfinity;
        }
    }

    public bool PreconditionsMet => Preconditions.All(p => p.IsClaimed);

    private bool BaseEarnConditions =>
        PreconditionsMet && !IsClaimed
        && (Benefits.Count > 0 || Campaign.PreconditionsChain().Contains(Id))
        && RequiredMinutes > 0 && ExtraMinutes < MaxExtraMinutes;

    public bool BaseCanEarn()
    {
        var now = Clock.Now;
        return BaseEarnConditions && StartsAt <= now && now < EndsAt;
    }

    public bool CanEarnWithin(DateTimeOffset stamp) => BaseEarnConditions && EndsAt > Clock.Now && StartsAt < stamp;

    public bool CanEarn(Channel? channel = null, bool ignoreChannelStatus = false) =>
        BaseCanEarn() && Campaign.BaseCanEarn(channel, ignoreChannelStatus);

    public bool CanClaim => ClaimId is not null && !IsClaimed && Clock.Now < Campaign.EndsAt.AddHours(24);

    public string RewardsText => string.Join(", ", Benefits.Select(b => b.Name));
    public Benefit? MainBenefit => Benefits.FirstOrDefault();

    // Текст статуса для карточки дропа
    public string StatusText => IsClaimed ? Loc.T("Drop.Claimed")
        : CanClaim ? Loc.T("Drop.ReadyToClaim")
        : RequiredMinutes <= 0 ? ""
        : CurrentMinutes > 0 || CanEarn() ? Loc.F("Drop.Progress", Progress, RequiredMinutes)
        : Loc.F("Drop.Minutes", RequiredMinutes);

    public DropState State => IsClaimed ? DropState.Claimed
        : CanClaim ? DropState.Ready
        : CurrentMinutes > 0 ? DropState.InProgress
        : DropState.Pending;

    internal void ApplyRealDelta(int delta)
    {
        if (delta == 0 || RealMinutes + delta < 0) return;
        RealMinutes = Math.Min(RealMinutes + delta, RequiredMinutes);
        ExtraMinutes = 0;
        NotifyProgress();
    }

    // Точное значение от Twitch; прогресс у дропов кампании идёт параллельно
    public void UpdateMinutes(int minutes)
    {
        var delta = Math.Clamp(minutes, 0, RequiredMinutes) - RealMinutes;
        if (delta == 0) return;
        Campaign.ApplyRealDelta(delta);
    }

    public bool BumpMinutes(Channel channel)
    {
        if (CanEarn(channel))
        {
            ExtraMinutes++;
            NotifyProgress();
        }
        return ExtraMinutes >= MaxExtraMinutes;
    }

    public void MarkClaimed()
    {
        IsClaimed = true;
        RealMinutes = RequiredMinutes;
        ExtraMinutes = 0;
        NotifyProgress();
    }

    public void NotifyProgress()
    {
        OnPropertyChanged(nameof(CurrentMinutes));
        OnPropertyChanged(nameof(RemainingMinutes));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(State));
        Campaign.NotifyProgress();
    }
}

public enum DropState { Pending, InProgress, Ready, Claimed }

public enum CampaignStatus { Active, Upcoming, Expired }

public sealed partial class Campaign : ObservableObject
{
    private readonly Settings _settings;
    private bool? _hasBadgeOrEmote;

    public string Id { get; }
    public string Name { get; }
    public Game Game { get; }
    public bool Linked { get; }
    public string LinkUrl { get; }
    public string ImageUrl { get; }
    public DateTimeOffset StartsAt { get; }
    public DateTimeOffset EndsAt { get; }
    private readonly bool _valid;
    public List<Channel> AllowedChannels { get; }
    public List<TimedDrop> Drops { get; }

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private bool _inQueue;
    [ObservableProperty] private bool _isMining;

    public bool QueuedOnly => InQueue && !IsMining;

    partial void OnInQueueChanged(bool value) => OnPropertyChanged(nameof(QueuedOnly));
    partial void OnIsMiningChanged(bool value) => OnPropertyChanged(nameof(QueuedOnly));

    public Campaign(JsonNode c, IReadOnlyDictionary<string, DateTimeOffset> claimedBenefits, Settings settings)
    {
        _settings = settings;
        Id = c.Str("id");
        Name = c.Str("name");
        Game = new Game(c["game"]!);
        Linked = c["self"].Bool("isAccountConnected");
        LinkUrl = c.Str("accountLinkURL");
        ImageUrl = BoxArt(c["game"].Str("boxArtURL"));
        StartsAt = c.Time("startAt");
        EndsAt = c.Time("endAt");
        _valid = c.Str("status") != "EXPIRED";

        var allow = c["allow"];
        var aclEnabled = allow?["isEnabled"] is not JsonValue v || !v.TryGetValue<bool>(out var e) || e;
        AllowedChannels = allow?["channels"] is JsonArray acl && acl.Count > 0 && aclEnabled
            ? acl.Select(x => Channel.FromAcl(x!)).ToList()
            : [];

        Drops = [];
        foreach (var d in (c["timeBasedDrops"] as JsonArray) ?? [])
            Drops.Add(new TimedDrop(this, d!, claimedBenefits));
    }

    // Каталог отдаёт шаблон {width}x{height}, остальные запросы уже готовый размер
    private static string BoxArt(string url) => url.Contains("{width}", StringComparison.Ordinal)
        ? url.Replace("{width}", "144").Replace("{height}", "192")
        : J.Dimensions().Replace(url, "");

    public bool Active { get { var now = Clock.Now; return _valid && StartsAt <= now && now < EndsAt; } }
    public bool Upcoming => _valid && Clock.Now < StartsAt;
    public bool Expired => !_valid || EndsAt <= Clock.Now;
    public CampaignStatus Status => Active ? CampaignStatus.Active : Upcoming ? CampaignStatus.Upcoming : CampaignStatus.Expired;

    public bool HasBadgeOrEmote => _hasBadgeOrEmote ??= Drops.Any(d => d.Benefits.Any(b => b.IsBadgeOrEmote));
    // Минуты копятся и без привязки аккаунта игры, привязать можно потом, до получения награды
    public bool Eligible => !HasBadgeOrEmote || _settings.EnableBadgesEmotes;
    public bool Finished => Drops.All(d => d.IsClaimed || d.RequiredMinutes <= 0);

    public int TotalDrops => Drops.Count;
    public int ClaimedDrops => Drops.Count(d => d.IsClaimed);
    public int RemainingDrops => TotalDrops - ClaimedDrops;
    public int RequiredMinutes => Drops.Select(d => d.TotalRequiredMinutes).DefaultIfEmpty(0).Max();
    public int RemainingMinutes => Drops.Select(d => d.TotalRemainingMinutes).DefaultIfEmpty(0).Max();
    public double Progress => Drops.Count == 0 ? 0 : Drops.Average(d => d.Progress);
    public double Availability => Drops.Select(d => d.Availability).DefaultIfEmpty(double.PositiveInfinity).Min();

    public TimedDrop? FirstDrop => Drops.Where(d => d.CanEarn()).OrderBy(d => d.RemainingMinutes).FirstOrDefault();

    public IEnumerable<DateTimeOffset> TimeTriggers =>
        new[] { StartsAt, EndsAt }.Concat(Drops.SelectMany(d => new[] { d.StartsAt, d.EndsAt }));

    public HashSet<string> PreconditionsChain() =>
        Drops.Where(d => !d.IsClaimed).SelectMany(d => d.PreconditionIds).ToHashSet();

    public bool BaseCanEarn(Channel? channel = null, bool ignoreChannelStatus = false) =>
        Eligible && Active && (channel is null || (
            (AllowedChannels.Count == 0 || AllowedChannels.Any(a => a.Id == channel.Id))
            && (ignoreChannelStatus || (channel.Game is not null && channel.Game.Equals(Game)) || Game.IsSpecial)));

    public bool CanEarn(Channel? channel = null, bool ignoreChannelStatus = false) =>
        BaseCanEarn(channel, ignoreChannelStatus) && Drops.Any(d => d.BaseCanEarn());

    public bool CanEarnWithin(DateTimeOffset stamp) =>
        Eligible && _valid && EndsAt > Clock.Now && StartsAt < stamp && Drops.Any(d => d.CanEarnWithin(stamp));

    internal void ApplyRealDelta(int delta)
    {
        foreach (var d in Drops) d.ApplyRealDelta(delta);
    }

    // Возвращает true, если хотя бы один дроп упёрся в лимит "оценочных" минут
    public bool BumpMinutes(Channel channel)
    {
        var results = Drops.Select(d => d.BumpMinutes(channel)).ToList();
        return results.Any(r => r);
    }

    public string ClaimedText => $"{ClaimedDrops}/{TotalDrops}";

    public string EndsText => Upcoming
        ? Loc.F("Campaign.Starts", StartsAt.ToLocalTime().ToString("d MMM, HH:mm"))
        : Loc.F("Campaign.Ends", EndsAt.ToLocalTime().ToString("d MMM, HH:mm"));

    public string AllowedChannelsText => AllowedChannels.Count == 0
        ? Loc.T("Campaign.AllChannels")
        : AllowedChannels.Count <= 5
            ? string.Join(", ", AllowedChannels.Select(c => c.Name))
            : string.Join(", ", AllowedChannels.Take(4).Select(c => c.Name)) + " " + Loc.F("Campaign.AndMore", AllowedChannels.Count - 4);

    public void NotifyProgress()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ClaimedDrops));
        OnPropertyChanged(nameof(RemainingMinutes));
        OnPropertyChanged(nameof(Finished));
        OnPropertyChanged(nameof(FirstDrop));
        OnPropertyChanged(nameof(ClaimedText));
    }

    public void NotifyTexts()
    {
        OnPropertyChanged(nameof(AllowedChannelsText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(EndsText));
        foreach (var d in Drops) d.NotifyProgress();
    }
}

public sealed class StreamInfo
{
    public long BroadcastId { get; init; }
    public int Viewers { get; set; }
    public string Title { get; init; } = "";
    public Game? Game { get; init; }
    public bool DropsEnabled { get; set; }

    public static StreamInfo FromStreamInfo(JsonNode user, bool dropsEnabled) => new()
    {
        BroadcastId = user["stream"].Long("id"),
        Viewers = user["stream"].Int("viewersCount"),
        Title = user["broadcastSettings"].Str("title"),
        Game = user["broadcastSettings"]?["game"] is JsonObject g ? new Game(g) : null,
        DropsEnabled = dropsEnabled,
    };

    public static StreamInfo FromDirectory(JsonNode node) => new()
    {
        BroadcastId = node.Long("id"),
        Viewers = node.Int("viewersCount"),
        Title = node.Str("title"),
        Game = node["game"] is JsonObject g ? new Game(g) : null,
        DropsEnabled = true,
    };
}

public enum ChannelState { Online, Pending, Offline }

public sealed partial class Channel : ObservableObject, IEquatable<Channel>
{
    public long Id { get; }
    public string Login { get; }
    public bool AclBased { get; }

    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private bool _isWatching;

    private StreamInfo? _stream;
    public CancellationTokenSource? PendingOnline { get; set; }

    public Channel(long id, string login, string? displayName, bool aclBased)
    {
        Id = id;
        Login = login;
        _displayName = displayName;
        AclBased = aclBased;
    }

    public static Channel FromAcl(JsonNode x) => new(x.Long("id"), x.Str("name"), x.StrOrNull("displayName"), true);

    public static Channel FromDirectory(JsonNode node)
    {
        var b = node["broadcaster"]!;
        return new Channel(b.Long("id"), b.Str("login"), b.StrOrNull("displayName"), false) { Stream = StreamInfo.FromDirectory(node) };
    }

    public StreamInfo? Stream
    {
        get => _stream;
        set
        {
            _stream = value;
            NotifyStream();
        }
    }

    public string Name => string.IsNullOrEmpty(DisplayName) ? Login : DisplayName;
    public string Url => "https://www.twitch.tv/" + Login;
    public bool Online => _stream is not null;
    public bool PendingOnlineState => _stream is null && PendingOnline is not null;
    public bool Offline => _stream is null && PendingOnline is null;
    public ChannelState State => Online ? ChannelState.Online : PendingOnlineState ? ChannelState.Pending : ChannelState.Offline;
    public Game? Game => _stream?.Game;
    public string GameName => Game?.Name ?? "";
    public int Viewers
    {
        get => _stream?.Viewers ?? -1;
        set
        {
            if (_stream is null) return;
            _stream.Viewers = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ViewersText));
        }
    }
    public string ViewersText => _stream is null ? "" : _stream.Viewers.ToString("N0", CultureInfo.CurrentCulture);
    public bool DropsEnabled => _stream?.DropsEnabled ?? false;
    public string StateText => State switch
    {
        ChannelState.Online => Loc.T("Channel.Online"),
        ChannelState.Pending => Loc.T("Channel.Pending"),
        _ => Loc.T("Channel.Offline"),
    };

    partial void OnDisplayNameChanged(string? value) => OnPropertyChanged(nameof(Name));

    public void NotifyStream()
    {
        OnPropertyChanged(nameof(Online));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Game));
        OnPropertyChanged(nameof(GameName));
        OnPropertyChanged(nameof(Viewers));
        OnPropertyChanged(nameof(ViewersText));
        OnPropertyChanged(nameof(DropsEnabled));
    }

    public bool Equals(Channel? other) => other is not null && other.Id == Id;
    public override bool Equals(object? obj) => Equals(obj as Channel);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => Name;
}
