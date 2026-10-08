using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lucide.Avalonia;
using ZeTwitchMiner.Core;
using ZeTwitchMiner.Twitch;

namespace ZeTwitchMiner.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    private readonly string _key;

    public NavItem(string key, LucideIconKind icon, ObservableObject page)
    {
        _key = key;
        Icon = icon;
        Page = page;
        Loc.Instance.Changed += () => OnPropertyChanged(nameof(Title));
    }

    public LucideIconKind Icon { get; }
    public ObservableObject Page { get; }
    public string Title => Loc.T(_key);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Func<Task> _exit;

    public Miner Miner { get; }
    public Settings Settings { get; }
    public GameQueue Queue { get; }
    public Updater Updater { get; }
    public DashboardViewModel Dashboard { get; }
    public InventoryViewModel Inventory { get; }
    public ChannelsViewModel Channels { get; }
    public SettingsViewModel SettingsPage { get; }
    public LogViewModel LogPage { get; }
    public List<NavItem> Nav { get; }

    [ObservableProperty] private NavItem _selectedNav;
    [ObservableProperty] private bool _windowVisible = true;

    public MainViewModel(Miner miner, Settings settings, Action<ThemeMode> applyTheme, Func<Task> exit)
    {
        Miner = miner;
        Settings = settings;
        _exit = exit;
        Queue = new GameQueue(settings, miner);
        Updater = new Updater(exit);
        Dashboard = new DashboardViewModel(this, Queue);
        Inventory = new InventoryViewModel(miner, Queue);
        Channels = new ChannelsViewModel(miner);
        SettingsPage = new SettingsViewModel(this, applyTheme);
        LogPage = new LogViewModel();

        Nav =
        [
            new NavItem("Nav.Dashboard", LucideIconKind.Pickaxe, Dashboard),
            new NavItem("Nav.Campaigns", LucideIconKind.Gift, Inventory),
            new NavItem("Nav.Channels", LucideIconKind.Tv, Channels),
            new NavItem("Nav.Log", LucideIconKind.ScrollText, LogPage),
            new NavItem("Nav.Settings", LucideIconKind.Settings2, SettingsPage),
        ];
        _selectedNav = Nav[0];

        miner.PropertyChanged += OnMinerChanged;
        Loc.Instance.Changed += () =>
        {
            miner.RefreshTexts();
            OnPropertyChanged(nameof(StatusText));
        };
    }

    public bool RingActive => WindowVisible && Miner.Activity == MinerActivity.Watching;
    public bool NeedsLogin => !Miner.LoggedIn;
    public bool HasLoginCode => Miner.LoginCode is not null;
    public string StatusText => Miner.Status;

    partial void OnWindowVisibleChanged(bool value) => OnPropertyChanged(nameof(RingActive));

    partial void OnSelectedNavChanged(NavItem value)
    {
        if (value.Page == Inventory) Inventory.Refresh();
    }

    private void OnMinerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Miner.Activity):
                OnPropertyChanged(nameof(RingActive));
                break;
            case nameof(Miner.LoggedIn):
                OnPropertyChanged(nameof(NeedsLogin));
                break;
            case nameof(Miner.LoginCode):
                OnPropertyChanged(nameof(HasLoginCode));
                break;
            case nameof(Miner.Status):
                OnPropertyChanged(nameof(StatusText));
                break;
        }
    }

    public void Navigate(ObservableObject page) => SelectedNav = Nav.First(n => n.Page == page);

    [RelayCommand]
    private void GoSettings() => Navigate(SettingsPage);

    [RelayCommand]
    private void GoCampaigns() => Navigate(Inventory);

    [RelayCommand]
    private void Reload() => Miner.Reload();

    [RelayCommand]
    private async Task OpenActivation(TopLevel? top)
    {
        if (Miner.LoginCode is not { } code) return;
        if (top?.Clipboard is { } clipboard) await clipboard.SetTextAsync(code.UserCode);
        OpenUrl(code.VerificationUri);
    }

    [RelayCommand]
    private void LoginWithBrowser() => Miner.LoginWithBrowser();

    [RelayCommand]
    private void FinishBrowserLogin() => BrowserLogin.Finish();

    [RelayCommand]
    private Task Exit() => _exit();

    [RelayCommand]
    private Task UpdateNow() => Updater.InstallAsync();

    [RelayCommand]
    private void UpdateLater() => Updater.Dismissed = true;

    [RelayCommand]
    private Task CheckUpdates() => Updater.CheckAsync(manual: true);

    [RelayCommand]
    private void OpenRelease() => Updater.OpenReleasePage();

    public void SaveWindowSize(Window? window)
    {
        if (window is null || window.WindowState != WindowState.Normal) return;
        Settings.WindowWidth = window.Width;
        Settings.WindowHeight = window.Height;
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Cannot open link: " + ex.Message);
        }
    }
}
