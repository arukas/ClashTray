namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SnapshotPublishThrottleTests
{
    [TestMethod]
    public async Task BurstRequestsAreCoalescedToOnePublishPerInterval()
    {
        ManualClock clock = new();
        int publishCount = 0;
        await using SnapshotPublishThrottle throttle = new(
            () => Interlocked.Increment(ref publishCount), CancellationToken.None,
            TimeSpan.FromMilliseconds(150), clock);
        await throttle.RequestAsync().WaitAsync(TimeSpan.FromSeconds(2));
        List<Task> waiters = [];
        for (int index = 0; index < 100; index++)
        {
            throttle.Queue();
            waiters.Add(throttle.RequestAsync());
        }
        Assert.AreEqual(TimeSpan.FromMilliseconds(150), await clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.AreEqual(1, Volatile.Read(ref publishCount));
        Assert.IsFalse(waiters.Any(waiter => waiter.IsCompleted));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, Volatile.Read(ref publishCount));
    }

    [TestMethod]
    [DataRow(-365)]
    [DataRow(365)]
    public async Task WallClockChangesDoNotAlterTheMonotonicPublishInterval(int days)
    {
        ManualClock clock = new();
        int publishCount = 0;
        await using SnapshotPublishThrottle throttle = new(
            () => Interlocked.Increment(ref publishCount), CancellationToken.None,
            TimeSpan.FromSeconds(1), clock);
        await throttle.RequestAsync().WaitAsync(TimeSpan.FromSeconds(2));
        clock.MoveWallClock(TimeSpan.FromDays(days));
        Task pending = throttle.RequestAsync();
        await clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.IsFalse(pending.IsCompleted);
        Assert.AreEqual(1, Volatile.Read(ref publishCount));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, Volatile.Read(ref publishCount));
    }

    [TestMethod]
    public async Task RequestAsyncCompletesAfterTheQueuedPublish()
    {
        int publishCount = 0;
        await using SnapshotPublishThrottle throttle = new(
            () => Interlocked.Increment(ref publishCount), CancellationToken.None,
            timeProvider: new ManualClock());
        await throttle.RequestAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, Volatile.Read(ref publishCount));
    }

    [TestMethod]
    public async Task CancellingOneWaiterDoesNotCancelTheSharedPublish()
    {
        ManualClock clock = new();
        int publishCount = 0;
        await using SnapshotPublishThrottle throttle = new(
            () => Interlocked.Increment(ref publishCount), CancellationToken.None,
            TimeSpan.FromSeconds(1), clock);
        await throttle.RequestAsync().WaitAsync(TimeSpan.FromSeconds(2));
        using CancellationTokenSource cancellation = new();
        Task canceled = throttle.RequestAsync(cancellation.Token);
        Task survivor = throttle.RequestAsync();
        await clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
        Assert.IsFalse(survivor.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await survivor.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, Volatile.Read(ref publishCount));
    }

    [TestMethod]
    public async Task PublishCallbackFailureDoesNotLeaveFutureRequestWaiting()
    {
        int publishCount = 0;
        await using SnapshotPublishThrottle throttle = new(
            () =>
            {
                Interlocked.Increment(ref publishCount);
                throw new InvalidOperationException("synthetic publish failure");
            }, CancellationToken.None, timeProvider: new ManualClock());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => throttle.RequestAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => throttle.RequestAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, Volatile.Read(ref publishCount));
        Assert.IsNotNull(throttle.Fault);
    }

    [TestMethod]
    public async Task DisposeCancelsPendingAndFutureRequestsWithoutAnOrphanWaiter()
    {
        ManualClock clock = new();
        int publishCount = 0;
        await using SnapshotPublishThrottle throttle = new(
            () => Interlocked.Increment(ref publishCount), CancellationToken.None,
            TimeSpan.FromSeconds(10), clock);
        await throttle.RequestAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Task pending = throttle.RequestAsync();
        await clock.DelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await throttle.DisposeAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<OperationCanceledException>(() => throttle.RequestAsync());
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, Volatile.Read(ref publishCount));
    }

    // One-shot timer scheduler for Task.Delay. Advancing time fires due timers
    // synchronously; the tests await completion signals rather than real sleeps.
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        private DateTimeOffset _utc = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        public TaskCompletionSource<TimeSpan> DelayScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) { return _timestamp; } }
        public override DateTimeOffset GetUtcNow() { lock (_gate) { return _utc; } }
        public void MoveWallClock(TimeSpan change) { lock (_gate) { _utc += change; } }

        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _timestamp += elapsed.Ticks;
                _utc += elapsed;
                due = _timers.Where(timer => timer.TakeDue(_timestamp)).ToList();
            }
            foreach (ManualTimer timer in due) { timer.Fire(); }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(Timeout.InfiniteTimeSpan, period);
            ManualTimer timer = new(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate) { _timers.Add(timer); }
            DelayScheduled.TrySetResult(dueTime);
            return timer;
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            private long? _due;
            public bool TakeDue(long now)
            {
                if (_disposed || _due is not long due || due > now) { return false; }
                _due = null;
                return true;
            }
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (_disposed) { return false; }
                    _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._timestamp + dueTime.Ticks;
                    return true;
                }
            }
            public void Dispose() { lock (clock._gate) { _disposed = true; _due = null; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
