namespace ClashTray.Core.Tests;

[TestClass]
public sealed class DebouncedActionTests
{
    [TestMethod]
    public void NewInputAndCancellationRejectAlreadyDispatchedCallbacks()
    {
        List<Action> queue = [];
        InputClock clock = new();
        int applied = 0;
        using DebouncedAction action = new(queue.Add, () => applied++, clock);
        action.Schedule();
        clock.Fire();
        action.Schedule();
        clock.Fire();
        queue[0]();
        Assert.AreEqual(0, applied);
        Assert.IsTrue(action.IsPending);
        queue[1]();
        queue[1]();
        Assert.AreEqual(1, applied);
        Assert.IsFalse(action.IsPending);
        action.Schedule();
        clock.Fire();
        action.Cancel();
        queue[2]();
        Assert.AreEqual(1, applied);
        action.Schedule();
        clock.Fire();
        action.Dispose();
        queue[3]();
        Assert.AreEqual(1, applied);
        Assert.ThrowsExactly<ObjectDisposedException>(action.Schedule);
    }

    private sealed class InputClock : TimeProvider
    {
        private Action? _fire;
        public void Fire() => _fire!();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(TimeSpan.FromMilliseconds(200), dueTime);
            InputTimer timer = new(callback, state);
            _fire = timer.Fire;
            return timer;
        }

        private sealed class InputTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) { callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
