namespace IPKeep.Core;

/// <summary>Coalesces interface event bursts. It never performs an update itself.</summary>
public sealed class NetworkChangeDebouncer : IDisposable
{
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan quietPeriod;
    private readonly Action wake;
    private readonly ITimer timer;
    private long lastSignal;
    private bool pending, disposed;

    public NetworkChangeDebouncer(Action wake, TimeProvider? clock = null, TimeSpan? quietPeriod = null)
    {
        this.wake = wake;
        this.clock = clock ?? TimeProvider.System;
        this.quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(10);
        timer = this.clock.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Signal()
    {
        lock (sync)
        {
            if (disposed) return;
            lastSignal = clock.GetTimestamp(); pending = true;
            timer.Change(quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (sync)
        {
            if (disposed || !pending) return;
            var remaining = quietPeriod - clock.GetElapsedTime(lastSignal);
            // An already queued timer callback can race a newer network event.
            if (remaining > TimeSpan.Zero) { timer.Change(remaining, Timeout.InfiniteTimeSpan); return; }
            pending = false;
            wake();
        }
    }

    public void Dispose()
    {
        lock (sync) { disposed = true; pending = false; timer.Dispose(); }
    }
}
