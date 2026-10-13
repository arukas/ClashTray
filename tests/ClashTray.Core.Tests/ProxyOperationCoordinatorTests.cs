using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ProxyOperationCoordinatorTests
{
    private readonly List<IDisposable> _disposables = [];
    private readonly List<IAsyncDisposable> _asyncDisposables = [];

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (IAsyncDisposable asyncDisposable in _asyncDisposables)
        {
            await asyncDisposable.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ModeSwitchCoalescesToLatestIntent()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        using OperationGate gate = new();
        using SemaphoreSlim entered = new(0);
        using SemaphoreSlim blocker = new(0);
        int executions = 0;
        ControllerMutationExecutor executor = async (_, _, _, _, _, _, _, _, _, cancellationToken) =>
        {
            Interlocked.Increment(ref executions);
            entered.Release();
            await blocker.WaitAsync(cancellationToken);
        };
        ProxyOperationCoordinator coordinator = CreateCoordinator(store, gate, executor);

        Task first = coordinator.SetModeAsync(ProxyMode.Global);
        await entered.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Task superseded = coordinator.SetModeAsync(ProxyMode.Rule);
        Task latest = coordinator.SetModeAsync(ProxyMode.Direct);
        blocker.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await entered.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        blocker.Release();

        await latest.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsExactlyAsync<OperationSupersededException>(
            () => superseded.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(2, executions);
    }

    [TestMethod]
    public void SelectProxyRejectsBlankNames()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        using OperationGate gate = new();
        ProxyOperationCoordinator coordinator = CreateCoordinator(store, gate, NeverExecute);

        Assert.ThrowsExactly<ArgumentException>(() => coordinator.SelectProxyAsync("", "node"));
        Assert.ThrowsExactly<ArgumentException>(() => coordinator.SelectProxyAsync("group", ""));
    }

    [TestMethod]
    public void TestProxyDelayRejectsBlankName()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        using OperationGate gate = new();
        ProxyOperationCoordinator coordinator = CreateCoordinator(store, gate, NeverExecute);

        Assert.ThrowsExactly<ArgumentException>(() => coordinator.TestProxyDelayAsync(""));
        Assert.ThrowsExactly<ArgumentException>(() => coordinator.TestProxyGroupDelayAsync(""));
    }

    [TestMethod]
    public async Task QuiescingGateFaultsDelayTest()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        using OperationGate gate = new();
        ProxyOperationCoordinator coordinator = CreateCoordinator(store, gate, NeverExecute);
        gate.BeginQuiescing();

        await Assert.ThrowsExactlyAsync<RuntimeQuiescingException>(
            () => coordinator.TestProxyDelayAsync("node").WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task SelectionTableRejectsNewGroupWhenAllOperationsBusy()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        using OperationGate gate = new();
        using SemaphoreSlim entered = new(0);
        using SemaphoreSlim blocker = new(0);
        ControllerMutationExecutor executor = async (_, _, _, _, _, _, _, _, _, cancellationToken) =>
        {
            entered.Release();
            await blocker.WaitAsync(cancellationToken);
        };
        ProxyOperationCoordinator coordinator = CreateCoordinator(store, gate, executor);

        List<Task> pending = new(256);
        for (int i = 0; i < 256; i++)
        {
            pending.Add(coordinator.SelectProxyAsync($"group-{i}", "node"));
        }

        for (int i = 0; i < 256; i++)
        {
            await entered.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.ThrowsExactly<OperationBusyException>(
            () => coordinator.SelectProxyAsync("group-overflow", "node"));

        blocker.Release(256);
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static readonly ControllerMutationExecutor NeverExecute =
        static (_, _, _, _, _, _, _, _, _, _) => throw new NotSupportedException();

    private ProxyOperationCoordinator CreateCoordinator(
        RuntimeStateStore store,
        OperationGate gate,
        ControllerMutationExecutor executor)
    {
        EndpointSessionManager sessions = new(
            ControllerEndpointFactory.CreateLocal(9090),
            new ThrowingConnector());
        RemoteControllerRefreshCoordinator remoteRefresh = new(
            sessions,
            static () => "info",
            static () => { },
            static () => { },
            static (_, _, _, _) => { },
            delayAsync: null,
            logStreamRunner: null,
            CancellationToken.None);
        RuntimeLogCoordinator logs = new(
            store,
            static () => null,
            static () => false,
            static () => "info",
            static () => { },
            static () => { },
            CancellationToken.None);
        _asyncDisposables.Add(sessions);
        _disposables.Add(remoteRefresh);
        _disposables.Add(logs);
        return new ProxyOperationCoordinator(
            gate,
            store,
            sessions,
            remoteRefresh,
            logs,
            new ControllerSessionGuard(new MihomoControllerSessionRegistry()),
            static () => new AppSettings(),
            executor,
            static (_, _, _, _) => Task.CompletedTask,
            static () => { },
            CancellationToken.None);
    }

    private static RuntimeSnapshot CreateSnapshot() =>
        new(
            new CoreStatus(
                CoreState.Stopped,
                null,
                null,
                ProxyMode.Rule,
                0,
                0,
                0,
                0,
                0,
                0,
                null),
            SystemProxyState.Off,
            TunState.Unavailable,
            SubscriptionState.Idle,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            null);

    private sealed class ThrowingConnector : IEndpointSessionConnector
    {
        public Task<EndpointSession> ConnectAsync(
            EndpointDescriptor endpoint,
            long generation,
            long selectionRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
