namespace ZeTwitchMiner.Core;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _wake;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public SingleInstance(string name)
    {
        _mutex = new Mutex(true, @"Local\" + name, out var created);
        IsFirst = created;
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + ".Wake");
    }

    public void SignalFirst() => _wake.Set();

    public void Listen(Action onWake)
    {
        var thread = new Thread(() =>
        {
            var handles = new[] { _wake, _cts.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
                onWake();
        }) { IsBackground = true, Name = "instance-wake" };
        thread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _wake.Dispose();
    }
}
