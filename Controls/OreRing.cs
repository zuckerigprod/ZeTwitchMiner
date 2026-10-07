using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ZeTwitchMiner.Controls;

// Кольцо из насечек: заполненные показывают прогресс дропа,
// крайняя заполненная мягко пульсирует, пока идёт добыча
public sealed class OreRing : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<OreRing, double>(nameof(Value));

    public static readonly StyledProperty<int> TicksProperty =
        AvaloniaProperty.Register<OreRing, int>(nameof(Ticks), 60);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<OreRing, double>(nameof(Thickness), 14);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<OreRing, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<OreRing, IBrush?>(nameof(Track));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<OreRing, bool>(nameof(IsActive));

    private readonly DispatcherTimer _pulse;
    private double _phase;

    static OreRing()
    {
        AffectsRender<OreRing>(ValueProperty, TicksProperty, ThicknessProperty, FillProperty, TrackProperty, IsActiveProperty);
    }

    public OreRing()
    {
        Transitions =
        [
            new DoubleTransition { Property = ValueProperty, Duration = TimeSpan.FromMilliseconds(600), Easing = new QuinticEaseOut() },
        ];
        _pulse = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, (_, _) =>
        {
            _phase = (_phase + 0.05) % 1;
            InvalidateVisual();
        });
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Ticks { get => GetValue(TicksProperty); set => SetValue(TicksProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty) UpdateTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _pulse.Stop();
    }

    // Таймер крутится только когда кольцо видно и добыча идёт, иначе ноль нагрузки
    private void UpdateTimer()
    {
        if (IsActive && VisualRoot is not null) _pulse.Start();
        else _pulse.Stop();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var outer = size / 2;
        var inner = outer - Thickness;
        var ticks = Math.Max(Ticks, 1);
        var filled = Math.Clamp(Value, 0, 1) * ticks;
        var lead = (int)Math.Ceiling(filled) - 1;

        var width = Math.Max(2, size * 0.012);
        var fillPen = new Pen(Fill ?? Brushes.Orange, width, lineCap: PenLineCap.Round);
        var trackPen = new Pen(Track ?? Brushes.Gray, width, lineCap: PenLineCap.Round);

        for (var i = 0; i < ticks; i++)
        {
            var angle = (i / (double)ticks) * Math.PI * 2 - Math.PI / 2;
            var (sin, cos) = Math.SinCos(angle);
            var p1 = new Point(center.X + cos * inner, center.Y + sin * inner);
            var p2 = new Point(center.X + cos * outer, center.Y + sin * outer);

            if (i == lead && IsActive)
            {
                var opacity = 0.45 + 0.55 * (0.5 + 0.5 * Math.Cos(_phase * Math.PI * 2));
                using (context.PushOpacity(opacity))
                    context.DrawLine(fillPen, p1, p2);
            }
            else
            {
                context.DrawLine(i <= lead ? fillPen : trackPen, p1, p2);
            }
        }
    }
}
