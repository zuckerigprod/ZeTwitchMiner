using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using ZeTwitchMiner.Core;

namespace ZeTwitchMiner.Views;

public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        // Держим журнал прокрученным к последней записи
        Log.Entries.CollectionChanged += OnEntriesChanged;
        DetachedFromVisualTree += (_, _) => Log.Entries.CollectionChanged -= OnEntriesChanged;
        AttachedToVisualTree += (_, _) => ScrollToEnd();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    private void ScrollToEnd()
    {
        if (Log.Entries.Count > 0) List.ScrollIntoView(Log.Entries.Count - 1);
    }
}
