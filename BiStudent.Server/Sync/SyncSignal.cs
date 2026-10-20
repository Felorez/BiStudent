namespace BiStudent.Server.Sync;

public sealed class SyncSignal
{
    private readonly object gate = new();
    private TaskCompletionSource wake = NewSignal();
    private DateTimeOffset? firstNotify;

    public void Notify()
    {
        lock (gate)
        {
            firstNotify ??= DateTimeOffset.UtcNow;
            wake.TrySetResult();
        }
    }

    public async Task WaitAsync(TimeSpan max, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + max;
        try
        {
            while (true)
            {
                Task? signal;
                TimeSpan delay;
                lock (gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    var ready = firstNotify?.AddSeconds(5);
                    var until = ready is not null && ready < deadline ? ready.Value : deadline;
                    if (now >= until) return;
                    delay = until - now;
                    signal = firstNotify is null ? wake.Task : null;
                }
                if (signal is null)
                    await Task.Delay(delay, cancellationToken);
                else
                    await Task.WhenAny(signal, Task.Delay(delay, cancellationToken))
                        .WaitAsync(cancellationToken);
            }
        }
        finally
        {
            lock (gate)
            {
                firstNotify = null;
                wake = NewSignal();
            }
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
