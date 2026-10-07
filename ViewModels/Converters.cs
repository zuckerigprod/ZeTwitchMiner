using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.ViewModels;

public static class Converters
{
    public static readonly IValueConverter ClaimedOpacity =
        new FuncValueConverter<bool, double>(claimed => claimed ? 0.55 : 1);

    public static readonly IValueConverter LogBrush = new FuncValueConverter<LogLevel, IBrush?>(level => level switch
    {
        LogLevel.Warn => Resource("Ore"),
        LogLevel.Error => Resource("Danger"),
        _ => Resource("Text"),
    });

    private static IBrush? Resource(string key)
    {
        var app = Application.Current;
        return app is not null && app.TryFindResource(key, app.ActualThemeVariant, out var v) ? v as IBrush : null;
    }
}
