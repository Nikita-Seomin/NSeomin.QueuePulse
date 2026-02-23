namespace NS.QueuePulse.Application;

/// <summary>
/// Кооперативная пауза: job в коде вызывает WaitIfPausedAsync().
/// </summary>
public sealed class PauseGate
{
    private volatile TaskCompletionSource _tcs = CreateSignaled();

    public bool IsPaused => !_tcs.Task.IsCompleted;

    public void Pause()
    {
        // если уже paused — ничего
        if (IsPaused) return;
        _tcs = CreateUnsignaled();
    }

    public void Resume()
    {
        // если уже resumed — ничего
        if (!IsPaused) return;
        _tcs.TrySetResult();
    }

    public Task WaitIfPausedAsync(CancellationToken ct = default)
    {
        var task = _tcs.Task;
        if (task.IsCompleted) return Task.CompletedTask;

        if (!ct.CanBeCanceled) return task;

        return WaitWithCancellationAsync(task, ct);
    }

    private static async Task WaitWithCancellationAsync(Task task, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var _ = ct.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs);
        await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await task.ConfigureAwait(false);
    }

    private static TaskCompletionSource CreateSignaled()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult();
        return tcs;
    }

    private static TaskCompletionSource CreateUnsignaled()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}