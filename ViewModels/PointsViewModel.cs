using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeTwitchMiner.Core;
using ZeTwitchMiner.Twitch;

namespace ZeTwitchMiner.ViewModels;

public sealed partial class PointsViewModel : ObservableObject
{
    private readonly Miner _miner;

    public ObservableCollection<FollowChannel> FilteredFollows { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string? _moduleMessage;

    public PointsViewModel(Miner miner)
    {
        _miner = miner;
        miner.Follows.CollectionChanged += (_, _) => Filter();
        miner.PointsChannels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChannels));
        miner.PropertyChanged += OnMinerChanged;
        Loc.Instance.Changed += () =>
        {
            BuildChoices();
            RefreshBrowserState();
        };
        BuildChoices();
    }

    public Miner Miner => _miner;
    public ObservableCollection<PointsChannel> Channels => _miner.PointsChannels;
    public bool HasChannels => Channels.Count > 0;

    public bool Enabled
    {
        get => _miner.PointsEnabled;
        set
        {
            _miner.SetPointsEnabled(value);
            OnPropertyChanged();
        }
    }

    public bool HasSystemBrowser => BrowserModule.SystemBrowser is not null;
    // Отдельный браузер берём, если его выбрали или если на компьютере нет Edge и Chrome
    private bool UsingModule => !HasSystemBrowser || _miner.PointsUseModule;

    public bool CanEnable => BrowserModule.Resolve(_miner.PointsUseModule) is not null;
    public bool NeedsModule => UsingModule && !BrowserModule.ModuleInstalled;
    public bool CanRemoveModule => BrowserModule.ModuleInstalled;
    public bool BrowserReady => !NeedsModule;

    // Список пересобираем только при смене языка: новый список посреди выбора сбивает ComboBox
    public string[] BrowserChoices { get; private set; } = [];

    private void BuildChoices()
    {
        BrowserChoices =
        [
            Loc.F("Points.UseSystem", BrowserModule.SystemBrowserName ?? "Edge / Chrome"),
            Loc.T("Points.UseModule"),
        ];
        OnPropertyChanged(nameof(BrowserChoices));
        OnPropertyChanged(nameof(BrowserChoiceIndex));
    }

    public int BrowserChoiceIndex
    {
        get => _miner.PointsUseModule ? 1 : 0;
        set
        {
            if (value < 0) return;
            _miner.SetPointsUseModule(value == 1);
            RefreshBrowserState();
        }
    }

    public string BrowserText => !UsingModule ? Loc.F("Points.BrowserFound", BrowserModule.SystemBrowserName ?? "")
        : BrowserModule.ModuleInstalled ? Loc.T("Points.ModuleReady")
        : HasSystemBrowser ? Loc.T("Points.ModuleNotDownloaded")
        : Loc.T("Points.BrowserMissing");

    private void RefreshBrowserState()
    {
        OnPropertyChanged(nameof(HasSystemBrowser));
        OnPropertyChanged(nameof(CanEnable));
        OnPropertyChanged(nameof(NeedsModule));
        OnPropertyChanged(nameof(CanRemoveModule));
        OnPropertyChanged(nameof(BrowserReady));
        OnPropertyChanged(nameof(BrowserText));
    }

    private void OnMinerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Miner.PointsEnabled)) OnPropertyChanged(nameof(Enabled));
        if (e.PropertyName == nameof(Miner.PointsUseModule)) OnPropertyChanged(nameof(BrowserChoiceIndex));
        if (e.PropertyName == nameof(Miner.LoggedIn) && _miner.LoggedIn && _miner.Follows.Count == 0) _ = _miner.LoadFollowsAsync();
    }

    // Подписки грузим, когда вкладку открыли впервые
    public void OnShown()
    {
        RefreshBrowserState();
        if (_miner.Follows.Count == 0) _ = _miner.LoadFollowsAsync();
    }

    partial void OnSearchChanged(string value) => Filter();

    private void Filter()
    {
        var q = Search.Trim();
        FilteredFollows.Clear();
        foreach (var f in _miner.Follows.Where(f => q.Length == 0
                     || f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || f.Login.Contains(q, StringComparison.OrdinalIgnoreCase)))
            FilteredFollows.Add(f);
    }

    [RelayCommand]
    private async Task Download()
    {
        if (IsDownloading) return;
        IsDownloading = true;
        ModuleMessage = Loc.T("Points.Downloading");
        try
        {
            await BrowserModule.DownloadAsync(new Progress<double>(p => DownloadProgress = p), CancellationToken.None);
            ModuleMessage = null;
        }
        catch (Exception ex)
        {
            Log.Error("Browser module download failed: " + ex.Message);
            ModuleMessage = Loc.T("Points.DownloadFailed");
        }
        finally
        {
            IsDownloading = false;
            RefreshBrowserState();
            _miner.WakePoints();
        }
    }

    [RelayCommand]
    private void RemoveModule()
    {
        _miner.StopPointsBrowser();
        BrowserModule.Remove();
        // Без отдельного браузера возвращаемся к системному, если он есть
        if (HasSystemBrowser) _miner.SetPointsUseModule(false);
        RefreshBrowserState();
        if (!CanEnable) Enabled = false;
        _miner.WakePoints();
    }

    [RelayCommand]
    private void Add(FollowChannel follow) => _miner.AddPointsChannel(follow);

    [RelayCommand]
    private void Remove(PointsChannel channel) => _miner.RemovePointsChannel(channel);

    [RelayCommand]
    private void Up(PointsChannel channel) => _miner.MovePointsChannel(channel, -1);

    [RelayCommand]
    private void Down(PointsChannel channel) => _miner.MovePointsChannel(channel, 1);

    [RelayCommand]
    private Task RefreshFollows() => _miner.LoadFollowsAsync();
}
