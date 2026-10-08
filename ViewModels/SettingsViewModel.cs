using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    public const string AuthorUrl = "https://github.com/zuckerigprod";
    public const string RepoUrl = "https://github.com/zuckerigprod/ZeTwitchMiner";

    private readonly MainViewModel _main;
    private readonly Action<ThemeMode> _applyTheme;
    private Settings S => _main.Settings;
    private bool _restartNeeded;

    public ObservableCollection<string> Priority => _main.Queue.Games;
    public ObservableCollection<string> Excluded { get; }
    public ObservableCollection<string> GameSuggestions { get; } = [];

    [ObservableProperty] private string _newPriority = "";
    [ObservableProperty] private string _newExcluded = "";
    [ObservableProperty] private string? _selectedPriority;
    [ObservableProperty] private bool _needsReload;

    public SettingsViewModel(MainViewModel main, Action<ThemeMode> applyTheme)
    {
        _main = main;
        _applyTheme = applyTheme;
        Excluded = new ObservableCollection<string>(S.ExcludedGames.Order());
        main.Queue.Changed += SyncExcluded;
        main.Miner.Inventory.CollectionChanged += (_, _) => UpdateSuggestions();
        Loc.Instance.Changed += () =>
        {
            OnPropertyChanged(nameof(Languages));
            OnPropertyChanged(nameof(Themes));
            OnPropertyChanged(nameof(PriorityModes));
            OnPropertyChanged(nameof(DataDirText));
        };
    }

    public string[] Languages => ["Русский", "English"];
    public string[] Themes => [Loc.T("Set.ThemeSystem"), Loc.T("Set.ThemeDark"), Loc.T("Set.ThemeLight")];
    public string[] PriorityModes => [Loc.T("Set.ModePriority"), Loc.T("Set.ModeEnding"), Loc.T("Set.ModeLowAvail")];

    public string Version => "v" + (typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0");
    public string DataDirText => (AppPaths.IsPortable ? Loc.T("Set.Portable") : Loc.T("Set.Installed")) + ": " + AppPaths.DataDir;
    public MainViewModel Main => _main;

    public int LanguageIndex
    {
        get => S.Language == "en" ? 1 : 0;
        set
        {
            S.Language = value == 1 ? "en" : "ru";
            Save();
            Loc.Instance.SetLanguage(S.Language);
            OnPropertyChanged();
        }
    }

    public int ThemeIndex
    {
        get => (int)S.Theme;
        set
        {
            S.Theme = (ThemeMode)value;
            _applyTheme(S.Theme);
            Save();
            OnPropertyChanged();
        }
    }

    public int PriorityModeIndex
    {
        get => (int)S.PriorityMode;
        set
        {
            S.PriorityMode = (PriorityMode)value;
            Changed();
            OnPropertyChanged();
        }
    }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            S.StartWithWindows = value;
            Autostart.Apply(value, S.StartInTray);
            Save();
            OnPropertyChanged();
        }
    }

    public bool StartInTray
    {
        get => S.StartInTray;
        set
        {
            S.StartInTray = value;
            if (S.StartWithWindows) Autostart.Apply(true, value);
            Save();
            OnPropertyChanged();
        }
    }

    public bool MinimizeToTray
    {
        get => S.MinimizeToTray;
        set { S.MinimizeToTray = value; Save(); OnPropertyChanged(); }
    }

    public bool Notifications
    {
        get => S.Notifications;
        set { S.Notifications = value; Save(); OnPropertyChanged(); }
    }

    public bool EnableBadgesEmotes
    {
        get => S.EnableBadgesEmotes;
        set { S.EnableBadgesEmotes = value; Changed(); OnPropertyChanged(); }
    }

    public bool AvailableDropsCheck
    {
        get => S.AvailableDropsCheck;
        set { S.AvailableDropsCheck = value; Changed(); OnPropertyChanged(); }
    }

    public string Proxy
    {
        get => S.Proxy;
        set
        {
            if (S.Proxy == value.Trim()) return;
            S.Proxy = value.Trim();
            _restartNeeded = true;
            Changed();
            OnPropertyChanged();
        }
    }

    private void Save() => S.Save();

    // Изменения, влияющие на выбор кампаний, применяются после перезагрузки
    private void Changed()
    {
        Save();
        NeedsReload = true;
    }

    private void UpdateSuggestions()
    {
        GameSuggestions.Clear();
        foreach (var g in _main.Miner.KnownGames.Order(StringComparer.CurrentCultureIgnoreCase))
            GameSuggestions.Add(g);
    }

    private void SyncExcluded()
    {
        var actual = S.ExcludedGames.Order().ToList();
        if (Excluded.SequenceEqual(actual)) return;
        Excluded.Clear();
        foreach (var g in actual) Excluded.Add(g);
    }

    [RelayCommand]
    private void AddPriority()
    {
        _main.Queue.Add(NewPriority);
        NewPriority = "";
    }

    [RelayCommand]
    private void AddExcluded()
    {
        var name = NewExcluded.Trim();
        if (name.Length == 0) return;
        _main.Queue.Exclude(name);
        NewExcluded = "";
    }

    [RelayCommand]
    private void RemovePriority(string name) => _main.Queue.Remove(name);

    [RelayCommand]
    private void RemoveExcluded(string name) => _main.Queue.Unexclude(name);

    [RelayCommand]
    private void MoveUp(string name) => _main.Queue.Move(name, -1);

    [RelayCommand]
    private void MoveDown(string name) => _main.Queue.Move(name, 1);

    [RelayCommand]
    private void ApplyReload()
    {
        NeedsReload = false;
        // Прокси применяется только при пересоздании соединений
        if (_restartNeeded) _main.Miner.Restart();
        else _main.Miner.Reload();
        _restartNeeded = false;
    }

    [RelayCommand]
    private async Task Logout() => await _main.Miner.LogoutAsync();

    [RelayCommand]
    private void OpenUrl(string url) => MainViewModel.OpenUrl(url);

    [RelayCommand]
    private void OpenDataDir() => MainViewModel.OpenUrl(AppPaths.DataDir);
}
