using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeTwitchMiner.Core;
using ZeTwitchMiner.Twitch;

namespace ZeTwitchMiner.ViewModels;

public enum CampaignFilter { Active, Upcoming, All }

public sealed partial class InventoryViewModel : ObservableObject
{
    private readonly Miner _miner;
    private readonly Settings _settings;

    public ObservableCollection<Campaign> Items { get; } = [];
    public string[] Filters => [Loc.T("Inv.Active"), Loc.T("Inv.Upcoming"), Loc.T("Inv.All")];

    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private bool _showNotLinked = true;
    [ObservableProperty] private bool _showFinished;
    [ObservableProperty] private string _search = "";

    public InventoryViewModel(Miner miner, Settings settings)
    {
        _miner = miner;
        _settings = settings;
        miner.Inventory.CollectionChanged += (_, _) => Refresh();
        Loc.Instance.Changed += () =>
        {
            var idx = FilterIndex;
            OnPropertyChanged(nameof(Filters));
            FilterIndex = idx;
        };
    }

    public bool IsEmpty => Items.Count == 0;

    partial void OnFilterIndexChanged(int value) => Refresh();
    partial void OnShowNotLinkedChanged(bool value) => Refresh();
    partial void OnShowFinishedChanged(bool value) => Refresh();
    partial void OnSearchChanged(string value) => Refresh();

    public void Refresh()
    {
        var filter = (CampaignFilter)Math.Max(FilterIndex, 0);
        var q = Search.Trim();
        var list = _miner.Inventory.Where(c =>
            c.RequiredMinutes > 0
            && (ShowNotLinked || c.Eligible)
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

        Items.Clear();
        foreach (var c in list) Items.Add(c);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void OpenLink(Campaign campaign)
    {
        if (!string.IsNullOrEmpty(campaign.LinkUrl)) MainViewModel.OpenUrl(campaign.LinkUrl);
    }

    [RelayCommand]
    private void AddPriority(Campaign campaign)
    {
        if (_settings.PriorityGames.Contains(campaign.Game.Name)) return;
        _settings.PriorityGames.Add(campaign.Game.Name);
        _settings.ExcludedGames.Remove(campaign.Game.Name);
        _settings.Save();
        _miner.Reload();
    }

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
