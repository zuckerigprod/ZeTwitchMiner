namespace ZeTwitchMiner.Core;

// Событие, которое можно ждать: Set будит всех ждущих, пока не вызовут Clear
public sealed class AsyncSignal
{
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsSet => _tcs.Task.IsCompleted;

    public void Set() => _tcs.TrySetResult();

    public void Clear()
    {
        if (_tcs.Task.IsCompleted)
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task WaitAsync(CancellationToken ct) => _tcs.Task.WaitAsync(ct);

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _tcs.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
