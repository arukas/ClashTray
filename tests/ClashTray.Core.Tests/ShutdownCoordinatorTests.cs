using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ClashTray.App;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ShutdownCoordinatorTests
{
    [TestMethod]
    public async Task ExitActionRunsOnOwnerThreadAfterAsynchronousCleanup()
    {
        using DedicatedSynchronizationContext ui = new();
        TaskCompletionSource cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int exitThread = 0;
        ShutdownCoordinator coordinator = ui.Invoke(() => new ShutdownCoordinator(
            ui.DispatchAsync,
            () => { },
            () => cleanup.Task,
            () => exitThread = Environment.CurrentManagedThreadId));

        Task quit = ui.Invoke(coordinator.RequestQuitAsync);
        cleanup.SetResult();
        await quit;

        Assert.AreEqual(ui.ThreadId, exitThread);
    }

    [TestMethod]
    public async Task BackgroundQuitMarshalsBeginShutdownToOwnerThread()
    {
        using DedicatedSynchronizationContext ui = new();
        TaskCompletionSource cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int beginThread = 0;
        int exitThread = 0;
        ShutdownCoordinator coordinator = ui.Invoke(() => new ShutdownCoordinator(
            ui.DispatchAsync,
            () => beginThread = Environment.CurrentManagedThreadId,
            () => cleanup.Task,
            () => exitThread = Environment.CurrentManagedThreadId));

        Task quit = Task.Run(coordinator.RequestQuitAsync);
        cleanup.SetResult();
        await quit;

        Assert.AreEqual(ui.ThreadId, beginThread);
        Assert.AreEqual(ui.ThreadId, exitThread);
    }

    [TestMethod]
    public async Task RepeatedQuitCallsSharePendingCompletionAndExitOnlyAfterCleanup()
    {
        TaskCompletionSource cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int beginCalls = 0;
        int disposeCalls = 0;
        int exitCalls = 0;
        ShutdownCoordinator coordinator = new(
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            () => beginCalls++,
            () =>
            {
                disposeCalls++;
                return cleanup.Task;
            },
            () => exitCalls++);

        Task first = coordinator.RequestQuitAsync();
        Task second = coordinator.RequestQuitAsync();

        try
        {
            Assert.AreSame(first, second);
            Assert.AreEqual(1, beginCalls);
            Assert.AreEqual(1, disposeCalls);
            Assert.AreEqual(0, exitCalls);
            Assert.IsFalse(first.IsCompleted);
        }
        finally
        {
            cleanup.TrySetResult();
            await first;
        }

        Assert.AreEqual(1, exitCalls);
        Assert.IsTrue(first.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task DispatcherRejectionCompletesSharedQuitWithExplicitFailure()
    {
        ShutdownCoordinator coordinator = new(
            _ => Task.FromException(new ShutdownUiDispatcherRejectedException()),
            () => Assert.Fail("Begin-shutdown must not run when the dispatcher rejects it."),
            () => Task.CompletedTask,
            () => Assert.Fail("Exit must not run when the dispatcher rejects it."));

        Task quit = coordinator.RequestQuitAsync();
        ShutdownUiDispatcherRejectedException exception =
            await Assert.ThrowsExactlyAsync<ShutdownUiDispatcherRejectedException>(() => quit);

        Assert.IsTrue(quit.IsFaulted);
        Assert.IsTrue(exception.Message.Contains("dispatcher is unavailable", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UiCallbackExceptionCompletesQuitWithoutLeavingWaiterPending()
    {
        using DedicatedSynchronizationContext ui = new();
        InvalidOperationException expected = new("window close failed");
        ShutdownCoordinator coordinator = ui.Invoke(() => new ShutdownCoordinator(
            ui.DispatchAsync,
            () => throw expected,
            () => Task.CompletedTask,
            () => Assert.Fail("Exit must not run after a UI callback fails.")));

        Task quit = ui.Invoke(coordinator.RequestQuitAsync);
        InvalidOperationException actual =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => quit);

        Assert.AreSame(expected, actual);
        Assert.IsTrue(quit.IsFaulted);
    }

    private sealed class DedicatedSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;
        private readonly TaskCompletionSource<int> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DedicatedSynchronizationContext()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Shutdown test UI thread" };
            _thread.Start();
            ThreadId = _started.Task.GetAwaiter().GetResult();
        }

        public int ThreadId { get; }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The test dispatcher forwards arbitrary callback failures to the awaiting test caller.")]
        public T Invoke<T>(Func<T> callback)
        {
            if (Environment.CurrentManagedThreadId == ThreadId)
            {
                return callback();
            }

            TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try
                {
                    completion.TrySetResult(callback());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }, null);
            return completion.Task.GetAwaiter().GetResult();
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The test dispatcher forwards arbitrary UI callback failures to the awaiting test caller.")]
        public Task DispatchAsync(Action callback)
        {
            if (Environment.CurrentManagedThreadId == ThreadId)
            {
                callback();
                return Task.CompletedTask;
            }

            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try
                {
                    callback();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }, null);
            return completion.Task;
        }

        public override void Post(SendOrPostCallback callback, object? state) =>
            _queue.Add((callback, state));

        public override void Send(SendOrPostCallback callback, object? state) =>
            Invoke(() =>
            {
                callback(state);
                return true;
            });

        public void Dispose()
        {
            _queue.CompleteAdding();
            Assert.IsTrue(_thread.Join(TimeSpan.FromSeconds(2)), "UI test thread did not stop.");
            _queue.Dispose();
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            _started.TrySetResult(Environment.CurrentManagedThreadId);
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }
}
