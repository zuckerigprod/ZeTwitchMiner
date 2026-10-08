using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeTwitchMiner.Core;
using ZeTwitchMiner.Twitch;

namespace ZeTwitchMiner.ViewModels;

public enum CampaignFilter { Active, Upcoming, All }

public sealed record CampaignGroup(string Title);

public sealed partial class InventoryViewModel : ObservableObject
{
    private readonly Miner _miner;
    private readonly GameQueue _queue;
    private bool _refreshPending;

    // Заголовки групп и кампании вперемешку: сначала очередь, потом остальные
    public ObservableCollection<object> Items { get; } = [];
    public string[] Filters => [Loc.T("Inv.Active"), Loc.T("Inv.Upcoming"), Loc.T("Inv.All")];

    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private bool _showNotLinked = true;
    [ObservableProperty] private bool _showFinished;
    [ObservableProperty] private string _search = "";

    public InventoryViewModel(Miner miner, GameQueue queue)
    {
        _miner = miner;
        _queue = queue;
        miner.Inventory.CollectionChanged += (_, _) => ScheduleRefresh();
        queue.Changed += ScheduleRefresh;
        miner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Miner.CurrentDrop)) ScheduleRefresh();
        };
        Loc.Instance.Changed += () =>
        {
            var idx = FilterIndex;
            OnPropertyChanged(nameof(Filters));
            FilterIndex = idx;
            ScheduleRefresh();
        };
    }

    public bool IsEmpty => Items.Count == 0;

    partial void OnFilterIndexChanged(int value) => Refresh();
    partial void OnShowNotLinkedChanged(bool value) => Refresh();
    partial void OnShowFinishedChanged(bool value) => Refresh();
    partial void OnSearchChanged(string value) => Refresh();

    // Инвентарь при загрузке добавляет кампании по одной, перестраиваем список один раз
    private void ScheduleRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    public void Refresh()
    {
        var current = _miner.CurrentDrop?.Campaign;
        foreach (var c in _miner.Inventory)
        {
            c.InQueue = _queue.Contains(c.Game.Name);
            c.IsMining = ReferenceEquals(c, current);
        }

        var filter = (CampaignFilter)Math.Max(FilterIndex, 0);
        var q = Search.Trim();
        var list = _miner.Inventory.Where(c =>
            c.RequiredMinutes > 0
            && (ShowNotLinked || c.Linked)
            && (ShowFinished || !c.Finished)
            && filter switch
            {
                CampaignFilter.Active => c.Active,
                CampaignFilter.Upcoming => c.Upcoming,
                _ => true,
            }
            && (q.Length == 0
                || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Game.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var queued = list.Where(c => c.InQueue)
            .OrderByDescending(c => c.IsMining)
            .ThenBy(c => _queue.IndexOf(c.Game.Name))
            .ThenByDescending(c => c.Active)
            .ThenBy(c => c.EndsAt)
            .ToList();
        var rest = list.Where(c => !c.InQueue).ToList();

        Items.Clear();
        if (queued.Count > 0)
        {
            Items.Add(new CampaignGroup(Loc.F("Inv.GroupQueue", queued.Count)));
            foreach (var c in queued) Items.Add(c);
        }
        if (rest.Count > 0)
        {
            if (queued.Count > 0) Items.Add(new CampaignGroup(Loc.F("Inv.GroupOther", rest.Count)));
            foreach (var c in rest) Items.Add(c);
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void OpenLink(Campaign campaign)
    {
        if (!string.IsNullOrEmpty(campaign.LinkUrl)) MainViewModel.OpenUrl(campaign.LinkUrl);
    }

    [RelayCommand]
    private void ToggleQueue(Campaign campaign) => _queue.Toggle(campaign.Game.Name);

    [RelayCommand]
    private void Reload() => _miner.Reload();
}

public sealed partial class ChannelsViewModel(Miner miner) : ObservableObject
{
    public Miner Miner => miner;
    public ObservableCollection<Channel> Items => miner.Channels;

    [RelayCommand]
    private void Watch(Channel channel) => miner.SwitchTo(channel);

    [RelayCommand]
    private void Open(Channel channel) => MainViewModel.OpenUrl(channel.Url);
}

public sealed partial class LogViewModel : ObservableObject
{
    public ObservableCollection<LogEntry> Entries => Log.Entries;

    [RelayCommand]
    private async Task Copy(TopLevel? top)
    {
        if (top?.Clipboard is not { } clipboard) return;
        var sb = new StringBuilder();
        foreach (var e in Entries) sb.AppendLine($"{e.TimeText} [{e.Level}] {e.Message}");
        await clipboard.SetTextAsync(sb.ToString());
    }

    [RelayCommand]
    private void OpenFile() => MainViewModel.OpenUrl(AppPaths.LogFile);

    [RelayCommand]
    private void Clear() => Entries.Clear();
}
