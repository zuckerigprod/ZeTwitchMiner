using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeTwitchMiner.Core;
using ZeTwitchMiner.Twitch;

namespace ZeTwitchMiner.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly GameQueue _queue;
    private TimedDrop? _trackedDrop;
    private bool _queuePending;

    public ObservableCollection<QueueItem> Queue { get; } = [];
    public bool HasQueue => Queue.Count > 0;

    public DashboardViewModel(MainViewModel main, GameQueue queue)
    {
        _main = main;
        _queue = queue;
        main.Miner.PropertyChanged += OnMinerChanged;
        main.Miner.Inventory.CollectionChanged += (_, _) => ScheduleQueue();
        queue.Changed += ScheduleQueue;
        Loc.Instance.Changed += () =>
        {
            NotifyAll();
            ScheduleQueue();
        };
    }

    private void ScheduleQueue()
    {
        if (_queuePending) return;
        _queuePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queuePending = false;
            Queue.Clear();
            foreach (var item in QueueItem.Build(_queue.Games, Miner.Inventory, Miner.CurrentDrop?.Campaign))
                Queue.Add(item);
            OnPropertyChanged(nameof(HasQueue));
        }, DispatcherPriority.Background);
    }

    public Miner Miner => _main.Miner;
    public MainViewModel Main => _main;

    public TimedDrop? Drop => Miner.CurrentDrop;
    public Campaign? Campaign => Drop?.Campaign;
    public bool HasDrop => Drop is not null;
    public bool NoDrop => Drop is null;
    public double Progress => Drop?.Progress ?? 0;
    public string PercentText => Drop is null ? "" : $"{Drop.Progress:P0}";
    public string MinutesText => Drop is null ? "" : Loc.F("Dash.Minutes", Drop.CurrentMinutes, Drop.RequiredMinutes);
    public string RemainingText => Drop is null ? "" : FormatDuration(Drop.RemainingMinutes);
    public string CampaignRemainingText => Campaign is null ? "" : FormatDuration(Campaign.RemainingMinutes);
    public string CampaignCountText => Campaign is null ? "" : Loc.F("Dash.DropsCount", Campaign.ClaimedDrops, Campaign.TotalDrops);
    public string Title => Drop is null ? "" : string.IsNullOrEmpty(Drop.RewardsText) ? Drop.Name : Drop.RewardsText;
    public string Subtitle => Campaign is null ? "" : $"{Campaign.Game.Name}  ·  {Campaign.Name}";
    public string EmptyTitle => Miner.Activity == MinerActivity.Idle ? Loc.T("Dash.EmptyIdle") : Miner.Status;
    public bool ShowGoSettings => Miner.Activity == MinerActivity.Idle;

    public static string FormatDuration(int minutes)
    {
        if (minutes <= 0) return Loc.T("Dash.AlmostDone");
        var h = minutes / 60;
        var m = minutes % 60;
        return h > 0 ? Loc.F("Time.HoursMinutes", h, m) : Loc.F("Time.Minutes", m);
    }

    private void OnMinerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Miner.CurrentDrop))
        {
            if (_trackedDrop is not null) _trackedDrop.PropertyChanged -= OnDropChanged;
            _trackedDrop = Miner.CurrentDrop;
            if (_trackedDrop is not null) _trackedDrop.PropertyChanged += OnDropChanged;
            NotifyAll();
            ScheduleQueue();
        }
        else if (e.PropertyName is nameof(Miner.Activity) or nameof(Miner.Status))
        {
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(ShowGoSettings));
        }
    }

    private void OnDropChanged(object? sender, PropertyChangedEventArgs e) => NotifyAll();

    private void NotifyAll() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void QueueUp(QueueItem item) => _queue.Move(item.Name, -1);

    [RelayCommand]
    private void QueueDown(QueueItem item) => _queue.Move(item.Name, 1);

    [RelayCommand]
    private void QueueRemove(QueueItem item) => _queue.Remove(item.Name);

    [RelayCommand]
    private void OpenChannel()
    {
        if (Miner.Watching is { } ch) MainViewModel.OpenUrl(ch.Url);
    }
}
