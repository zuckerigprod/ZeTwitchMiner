using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using ZeTwitchMiner.ViewModels;

namespace ZeTwitchMiner.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var titleBar = this.FindControl<Border>("TitleBar")!;
        titleBar.PointerPressed += OnTitlePressed;
        titleBar.DoubleTapped += (_, _) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        // Если Mica не включилась, показываем непрозрачную подложку
        this.GetObservable(ActualTransparencyLevelProperty).Subscribe(new Observer<WindowTransparencyLevel>(level =>
            this.FindControl<Border>("Solid")!.IsVisible = level == WindowTransparencyLevel.None));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
        {
            Width = vm.Settings.WindowWidth;
            Height = vm.Settings.WindowHeight;
        }
    }

    private void OnTitlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            BeginMoveDrag(e);
    }

    private sealed class Observer<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}
