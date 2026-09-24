using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeLogCoordinatorTests
{
    [TestMethod]
    public void ApplicationLogUpdatesStateSnapshotWithoutPublishing()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        int throttled = 0;
        int published = 0;
        using RuntimeLogCoordinator logs = CreateCoordinator(store, () => throttled++, () => published++);
        LogEntry entry = new(DateTimeOffset.UtcNow, "ClashTray", "info", "hello");

        logs.AddApplicationLog(entry);

        Assert.AreEqual(1, store.Snapshot.Logs.Count);
        Assert.AreEqual("hello", store.Snapshot.Logs[0].Message);
        Assert.AreEqual(0, throttled);
        Assert.AreEqual(0, published);
    }

    [TestMethod]
    public void MihomoLogQueuesThrottledPublish()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        int throttled = 0;
        int published = 0;
        using RuntimeLogCoordinator logs = CreateCoordinator(store, () => throttled++, () => published++);
        LogEntry entry = new(DateTimeOffset.UtcNow, "mihomo", "info", "core line");

        logs.AddMihomoLog(entry);

        Assert.AreEqual(1, store.Snapshot.Logs.Count);
        Assert.AreEqual(1, throttled);
        Assert.AreEqual(0, published);
    }

    [TestMethod]
    public void ProcessLogLineMapsErrorStreamToErrorLevel()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        using RuntimeLogCoordinator logs = CreateCoordinator(store, () => { }, () => { });

        logs.OnProcessLogLine("boom", true);

        LogEntry entry = store.Snapshot.Logs[0];
        Assert.AreEqual("mihomo", entry.Source);
        Assert.AreEqual("error", entry.Level);
        Assert.AreEqual("boom", entry.Message);
    }

    [TestMethod]
    public void ClearLogsEmptiesSnapshotAndPublishes()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        int published = 0;
        using RuntimeLogCoordinator logs = CreateCoordinator(store, () => { }, () => published++);
        logs.AddApplicationLog(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "info", "one"));
        logs.AddApplicationLog(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "info", "two"));

        logs.ClearLogs();

        Assert.AreEqual(0, store.Snapshot.Logs.Count);
        Assert.AreEqual(0, logs.Snapshot().Count);
        Assert.AreEqual(1, published);
    }

    [TestMethod]
    public void LogStreamStaysStoppedWithoutServiceCore()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        using RuntimeLogCoordinator logs = CreateCoordinator(store, () => { }, () => { });

        logs.EnsureLogStreamStarted();

        Assert.AreEqual(0, store.Snapshot.Logs.Count);
    }

    [TestMethod]
    public async Task StopLogStreamAsyncIsIdempotent()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        using RuntimeLogCoordinator logs = CreateCoordinator(store, () => { }, () => { });

        await logs.StopLogStreamAsync();
        await logs.StopLogStreamAsync();
    }

    [TestMethod]
    public async Task StopLogStreamAsyncToleratesWebSocketHandshakeTimeouts()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource acceptCts = new();
        ConcurrentBag<TcpClient> heldClients = [];
        Task acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (!acceptCts.IsCancellationRequested)
                {
                    // Accept connections but never answer the WebSocket upgrade,
                    // so the client-side handshake times out.
                    heldClients.Add(await listener.AcceptTcpClientAsync(acceptCts.Token));
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        try
        {
            using HttpClient httpClient = new();
            MihomoApiClient api = new(
                httpClient,
                new Uri($"http://127.0.0.1:{port}/"),
                string.Empty,
                webSocketHandshakeTimeout: TimeSpan.FromMilliseconds(300));
            using RuntimeLogCoordinator logs = new(
                store,
                () => api,
                () => true,
                () => "info",
                () => { },
                () => { },
                CancellationToken.None);

            logs.EnsureLogStreamStarted();
            await Task.Delay(TimeSpan.FromSeconds(2));

            await logs.StopLogStreamAsync();
        }
        finally
        {
            await acceptCts.CancelAsync();
            foreach (TcpClient client in heldClients)
            {
                client.Dispose();
            }

            await acceptLoop;
        }
    }

    [TestMethod]
    public async Task DisposeRacingStopLogStreamDoesNotThrow()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        using HttpClient httpClient = new();
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:1/"), string.Empty);

        for (int iteration = 0; iteration < 20; iteration++)
        {
            RuntimeLogCoordinator logs = new(
                store,
                () => api,
                () => true,
                () => "info",
                () => { },
                () => { },
                CancellationToken.None);
            logs.EnsureLogStreamStarted();

            Task stop = logs.StopLogStreamAsync();
            logs.Dispose();
            await stop;
        }
    }

    private static RuntimeLogCoordinator CreateCoordinator(
        RuntimeStateStore store,
        Action queueThrottledPublish,
        Action publish,
        Func<bool>? serviceCore = null) =>
        new(
            store,
            () => null,
            serviceCore ?? (() => false),
            () => "info",
            queueThrottledPublish,
            publish,
            CancellationToken.None);

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
}
