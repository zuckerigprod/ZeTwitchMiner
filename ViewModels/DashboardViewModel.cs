using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeTwitchMiner.Core;
using ZeTwitchMiner.Twitch;

namespace ZeTwitchMiner.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private TimedDrop? _trackedDrop;

    public DashboardViewModel(MainViewModel main)
    {
        _main = main;
        main.Miner.PropertyChanged += OnMinerChanged;
        Loc.Instance.Changed += NotifyAll;
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
    private void OpenChannel()
    {
        if (Miner.Watching is { } ch) MainViewModel.OpenUrl(ch.Url);
    }
}
