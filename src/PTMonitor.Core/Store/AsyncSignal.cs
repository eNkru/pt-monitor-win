namespace PTMonitor.Core.Store;

/// <summary>
/// A wake-up that is not lost. <see cref="Signal"/> completes the current
/// waiters and remembers the signal, so a signal that arrives while nobody is
/// waiting still ends the next wait immediately. A signal ends **one** wait:
/// the next wait is a real wait again.
/// </summary>
public sealed class AsyncSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _waiters = NewWaiters();
    private int _pending;

    private static TaskCompletionSource NewWaiters() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes every current waiter and remembers the signal.</summary>
    public void Signal()
    {
        TaskCompletionSource waiters;
        lock (_gate)
        {
            _pending++;
            waiters = _waiters;
            _waiters = NewWaiters();
        }
        waiters.TrySetResult();
    }

    /// <summary>
    /// Waits until the timeout elapses or <see cref="Signal"/> is called.
    /// A cancellation ends the wait with an <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task waiters;
        lock (_gate)
        {
            if (_pending > 0)
            {
                // A signal that arrived between two waits: it belongs to this one.
                _pending--;
                return;
            }
            waiters = _waiters.Task;
        }

        var delay = Task.Delay(timeout, cancellationToken);
        // The delay may fault after a signal ended the wait; observe it so the
        // exception never surfaces as an unobserved task exception.
        _ = delay.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var completed = await Task.WhenAny(waiters, delay).ConfigureAwait(false);
        if (completed == delay)
        {
            // A timeout returns quietly; a cancellation throws.
            await delay.ConfigureAwait(false);
            return;
        }

        // Woken by the signal: consume it, so the next wait is a real wait and
        // one signal does not cause two poll cycles.
        lock (_gate)
        {
            if (_pending > 0) _pending--;
        }
    }
}
