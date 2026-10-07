using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ZeTwitchMiner.Views;

// Уведомление о полученном дропе: своё окно в углу экрана, фокус не забирает
public static class Toast
{
    private static Window? _current;

    public static void Show(string title, string text)
    {
        _current?.Close();

        var res = Application.Current!;
        IBrush Brush(string key) => res.TryFindResource(key, res.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

        var logo = new Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://ZeTwitchMiner/Assets/logo.png"))),
            Width = 40,
            Height = 40,
        };
        var body = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new TextBlock { Text = Core.Loc.T("Toast.Claimed"), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush("Ore"), LetterSpacing = 0.6 });
        body.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap, MaxLines = 2 });

        var row = new DockPanel();
        DockPanel.SetDock(logo, Dock.Left);
        logo.Margin = new Thickness(0, 0, 14, 0);
        row.Children.Add(logo);
        row.Children.Add(body);

        var card = new Border
        {
            Background = Brush("Surface"),
            BorderBrush = Brush("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14),
            Margin = new Thickness(12),
            BoxShadow = BoxShadows.Parse("0 8 24 0 #40000000"),
            Child = row,
            Opacity = 0,
            RenderTransform = TransformOperations.Parse("translateY(16px)"),
            Transitions =
            [
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() },
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = new QuinticEaseOut() },
            ],
        };

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            Topmost = true,
            ShowActivated = false,
            ShowInTaskbar = false,
            CanResize = false,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Content = card,
        };
        card.PointerPressed += (_, _) => window.Close();

        window.Opened += (_, _) =>
        {
            var screen = window.Screens.Primary;
            if (screen is not null)
            {
                var area = screen.WorkingArea;
                var scale = screen.Scaling;
                var w = (int)(window.Bounds.Width * scale);
                var h = (int)(window.Bounds.Height * scale);
                window.Position = new PixelPoint(area.Right - w - 8, area.Bottom - h - 8);
            }
            card.Opacity = 1;
            card.RenderTransform = TransformOperations.Parse("translateY(0px)");
        };

        _current = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_current, window)) _current = null; };
        window.Show();

        DispatcherTimer.RunOnce(() =>
        {
            card.Opacity = 0;
            DispatcherTimer.RunOnce(window.Close, TimeSpan.FromMilliseconds(250));
        }, TimeSpan.FromSeconds(6));
    }
}
