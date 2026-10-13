using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RemoteControllerRefreshCoordinatorTests
{
    [TestMethod]
    public async Task BuildActiveControllerSnapshotReturnsNullWithoutRemoteSession()
    {
        EndpointSessionManager sessions = CreateSessionManager();
        RemoteControllerRefreshCoordinator coordinator = CreateCoordinator(sessions);

        Assert.IsNull(coordinator.BuildActiveControllerSnapshot());

        coordinator.Dispose();
        await sessions.DisposeAsync();
    }

    [TestMethod]
    public async Task CaptureActiveRemoteSessionReturnsNullWhenDisconnected()
    {
        EndpointSessionManager sessions = CreateSessionManager();
        RemoteControllerRefreshCoordinator coordinator = CreateCoordinator(sessions);

        Assert.IsNull(coordinator.CaptureActiveRemoteSession(EndpointCommand.ObserveStatus, "stale"));

        coordinator.Dispose();
        await sessions.DisposeAsync();
    }

    [TestMethod]
    public async Task LocalDisconnectStatusDoesNotStartRefresh()
    {
        EndpointSessionManager sessions = CreateSessionManager();
        RemoteControllerRefreshCoordinator coordinator = CreateCoordinator(sessions);

        coordinator.HandleSessionStatusChanged(null, sessions.Status);
        Assert.IsNull(coordinator.BuildActiveControllerSnapshot());

        await coordinator.StopRefreshAsync().WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Dispose();
        await sessions.DisposeAsync();
    }

    [TestMethod]
    public async Task StopRefreshAndDisposeWithoutActivityAreNoOps()
    {
        EndpointSessionManager sessions = CreateSessionManager();
        RemoteControllerRefreshCoordinator coordinator = CreateCoordinator(sessions);

        await coordinator.StopRefreshAsync().WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Dispose();

        await sessions.DisposeAsync();
    }

    private static EndpointSessionManager CreateSessionManager() =>
        new(ControllerEndpointFactory.CreateLocal(9090), new ThrowingConnector());

    private static RemoteControllerRefreshCoordinator CreateCoordinator(EndpointSessionManager sessions) =>
        new(
            sessions,
            static () => "info",
            static () => { },
            static () => { },
            static (_, _, _, _) => { },
            delayAsync: null,
            logStreamRunner: null,
            CancellationToken.None);

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
