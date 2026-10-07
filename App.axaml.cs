using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using ZeTwitchMiner.Core;
using ZeTwitchMiner.Twitch;
using ZeTwitchMiner.ViewModels;
using ZeTwitchMiner.Views;

namespace ZeTwitchMiner;

public sealed class App(SingleInstance? instance, bool startHidden) : Application
{
    private readonly CancellationTokenSource _appCts = new();
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private TrayIcon? _tray;
    private Task? _minerTask;
    private bool _exiting;

    public App() : this(null, false)
    {
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            var settings = Settings.Load();
            if (string.IsNullOrEmpty(settings.Language))
            {
                // Первый запуск: сначала спрашиваем язык, потом запускаем всё остальное
                Loc.Instance.SetLanguage(Loc.DetectLanguage());
                var picker = new LanguageWindow();
                picker.Closed += (_, _) =>
                {
                    settings.Language = picker.Choice ?? Loc.DetectLanguage();
                    settings.Save();
                    Start(settings);
                };
                picker.Show();
            }
            else
            {
                Start(settings);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Start(Settings settings)
    {
        Loc.Instance.SetLanguage(settings.Language);

        var miner = new Miner(settings);
        _vm = new MainViewModel(miner, settings, ApplyTheme, ExitAsync);
        ApplyTheme(settings.Theme);

        CreateTray(miner);
        miner.DropClaimed += (title, text) => Toast.Show(title, text);
        miner.LoginNeeded += ShowWindow;
        miner.PropertyChanged += OnMinerChanged;

        instance?.Listen(() => Dispatcher.UIThread.Post(ShowWindow));

        // --tray добавляет автозапуск, когда включён старт в трее
        if (!startHidden) ShowWindow();

        // Секундный тик нужен только для полоски "следующая минута", пока окно видно
        var tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_window is { IsVisible: true }) miner.Tick();
        });
        tick.Start();

        _minerTask = miner.RunAsync(_appCts.Token);
    }

    private void ApplyTheme(ThemeMode mode) => RequestedThemeVariant = mode switch
    {
        ThemeMode.Dark => ThemeVariant.Dark,
        ThemeMode.Light => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    private void CreateTray(Miner miner)
    {
        var show = new NativeMenuItem(Loc.T("Tray.Show"));
        show.Click += (_, _) => ShowWindow();
        var reload = new NativeMenuItem(Loc.T("Tray.Reload"));
        reload.Click += (_, _) => miner.Reload();
        var quit = new NativeMenuItem(Loc.T("Tray.Quit"));
        quit.Click += async (_, _) => await ExitAsync();

        Loc.Instance.Changed += () =>
        {
            show.Header = Loc.T("Tray.Show");
            reload.Header = Loc.T("Tray.Reload");
            quit.Header = Loc.T("Tray.Quit");
        };

        _tray = new TrayIcon
        {
            Icon = LoadIcon("tray-idle.ico"),
            ToolTipText = "ZeTwitchMiner",
            Menu = [show, reload, new NativeMenuItemSeparator(), quit],
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [_tray]);
    }

    private static WindowIcon LoadIcon(string name) =>
        new(AssetLoader.Open(new Uri("avares://ZeTwitchMiner/Assets/" + name)));

    private void OnMinerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tray is null || sender is not Miner miner) return;
        if (e.PropertyName == nameof(Miner.Activity))
        {
            _tray.Icon = LoadIcon(miner.Activity switch
            {
                MinerActivity.Watching => "tray-active.ico",
                MinerActivity.Error => "tray-error.ico",
                _ => "tray-idle.ico",
            });
        }
        if (e.PropertyName is nameof(Miner.CurrentDrop) or nameof(Miner.Status) or nameof(Miner.Activity))
        {
            var tip = miner.CurrentDrop is { } d
                ? $"ZeTwitchMiner\n{d.Campaign.Game}\n{d.RewardsText} {d.Progress:P0}"
                : "ZeTwitchMiner\n" + miner.Status;
            // Windows обрезает подсказку трея до 127 символов
            _tray.ToolTipText = tip.Length > 127 ? tip[..124] + "..." : tip;
        }
    }

    private MainWindow CreateWindow()
    {
        var window = new MainWindow { DataContext = _vm };
        window.Closing += OnWindowClosing;
        window.Opened += (_, _) => _vm!.WindowVisible = true;
        window.Closed += (_, _) =>
        {
            _vm!.WindowVisible = false;
            if (ReferenceEquals(_window, window)) _window = null;
            if (!_exiting) Dispatcher.UIThread.Post(() =>
            {
                MemoryTrim.Run();
                Log.Debug("Window closed, memory trimmed");
            }, DispatcherPriority.Background);
        };
        return window;
    }

    private void ShowWindow()
    {
        if (_exiting) return;
        _window ??= CreateWindow();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting || _vm is null) return;
        // В трее окно не прячется, а уничтожается целиком: так в фоне не висит дерево интерфейса
        if (_vm.Settings.MinimizeToTray && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            _vm.SaveWindowSize(_window);
            return;
        }
        e.Cancel = true;
        _ = ExitAsync();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _vm?.SaveWindowSize(_window);
        _vm?.Settings.Save();
        _appCts.Cancel();
        if (_minerTask is not null)
            await Task.WhenAny(_minerTask, Task.Delay(3000));
        if (_tray is not null) _tray.IsVisible = false;
        _window?.Close();
        _desktop?.Shutdown();
    }
}
