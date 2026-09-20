using System.Net;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeDataRefreshCoordinatorTests
{
    private readonly List<IDisposable> _disposables = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [TestMethod]
    public async Task OptionalDataRefreshSkipsCommitWhenBindingIsStale()
    {
        RuntimeStateStore store = new(CreateSnapshot(CoreState.Running));
        int handlerCalls = 0;
        int throttled = 0;
        int published = 0;
        MihomoApiClient api = CreateApiClient(_ =>
        {
            handlerCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        RuntimeDataRefreshCoordinator coordinator = CreateCoordinator(
            store,
            static (_, _, _, _) => false,
            static (_, _, _, _, _) => { },
            () => throttled++,
            () => published++);
        RuntimeSnapshot before = store.Snapshot;

        await coordinator.RefreshOptionalDataAsync(api, CancellationToken.None);

        Assert.AreEqual(0, handlerCalls);
        Assert.AreSame(before, store.Snapshot);
        Assert.AreEqual(0, throttled);
        Assert.AreEqual(0, published);
    }

    [TestMethod]
    public async Task OptionalDataRefreshCommitsAllFactsWhenBindingIsCurrent()
    {
        RuntimeStateStore store = new(CreateSnapshot(CoreState.Running));
        int throttled = 0;
        MihomoApiClient api = CreateApiClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_.RequestUri?.AbsolutePath switch
            {
                "/proxies" => "{\"proxies\":{}}",
                "/traffic" => "{\"upTotal\":100,\"downTotal\":200,\"up\":3,\"down\":4}\n",
                "/memory" => "{\"inuse\":42}\n",
                "/connections" => "{\"connections\":[]}",
                "/rules" => "{\"rules\":[]}",
                "/providers/proxies" => "{\"providers\":{}}",
                "/providers/rules" => "{\"providers\":{}}",
                _ => "{}"
            })
        });
        RuntimeDataRefreshCoordinator coordinator = CreateCoordinator(
            store,
            static (_, _, _, _) => true,
            static (_, _, _, _, _) => { },
            () => throttled++,
            static () => { });

        await coordinator.RefreshOptionalDataAsync(api, CancellationToken.None);

        RuntimeSnapshot snapshot = store.Snapshot;
        Assert.AreEqual(100, snapshot.Core.UploadBytes);
        Assert.AreEqual(200, snapshot.Core.DownloadBytes);
        Assert.AreEqual(3, snapshot.Core.UploadBytesPerSecond);
        Assert.AreEqual(4, snapshot.Core.DownloadBytesPerSecond);
        Assert.IsTrue(snapshot.Core.TrafficAvailable);
        Assert.AreEqual(42, snapshot.Core.MemoryBytes);
        Assert.IsTrue(snapshot.Core.MemoryAvailable);
        Assert.AreEqual(0, snapshot.Core.ConnectionCount);
        Assert.AreEqual(1, throttled);
    }

    [TestMethod]
    public async Task ProxyDataFallsBackToLastSnapshotOnControllerFailure()
    {
        RuntimeStateStore store = new(CreateSnapshot(CoreState.Running));
        List<string> failures = [];
        MihomoApiClient api = CreateApiClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });
        RuntimeDataRefreshCoordinator coordinator = CreateCoordinator(
            store,
            static (_, _, _, _) => true,
            (phase, _, _, _, _) => failures.Add(phase),
            static () => { },
            static () => { });

        ProxyDataResult result = await coordinator.TryGetProxyDataAsync(api, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, result.Groups.Count);
        Assert.AreEqual(0, result.Nodes.Count);
        CollectionAssert.Contains(failures, "代理数据刷新");
    }

    [TestMethod]
    public async Task ModeSnapshotRefreshCommitsParsedMode()
    {
        RuntimeStateStore store = new(CreateSnapshot(CoreState.Running));
        int published = 0;
        MihomoApiClient api = CreateApiClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"mode\":\"global\"}")
        });
        MihomoControllerSessionRegistry registry = new();
        MihomoControllerSession session = registry.Attach(
            api,
            ControllerEndpointFactory.CreateLocal(9090),
            EndpointCapabilityDefaults.Local);
        RuntimeDataRefreshCoordinator coordinator = CreateCoordinator(
            store,
            static (_, _, _, _) => true,
            static (_, _, _, _, _) => { },
            static () => { },
            () => published++,
            new ControllerSessionGuard(registry));

        await coordinator.RefreshModeSnapshotAsync(api, session.Generation, CancellationToken.None);

        Assert.AreEqual(ProxyMode.Global, store.Snapshot.Core.Mode);
        Assert.AreEqual(1, published);
    }

    [TestMethod]
    public async Task ModeSnapshotRefreshRejectsReplacedSession()
    {
        RuntimeStateStore store = new(CreateSnapshot(CoreState.Running));
        MihomoApiClient api = CreateApiClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"mode\":\"global\"}")
        });
        MihomoControllerSessionRegistry registry = new();
        MihomoControllerSession session = registry.Attach(
            api,
            ControllerEndpointFactory.CreateLocal(9090),
            EndpointCapabilityDefaults.Local);
        registry.Detach();
        RuntimeDataRefreshCoordinator coordinator = CreateCoordinator(
            store,
            static (_, _, _, _) => true,
            static (_, _, _, _, _) => { },
            static () => { },
            static () => { },
            new ControllerSessionGuard(registry));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => coordinator.RefreshModeSnapshotAsync(api, session.Generation, CancellationToken.None));
        Assert.AreEqual(ProxyMode.Rule, store.Snapshot.Core.Mode);
    }

    private MihomoApiClient CreateApiClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        DelegateHandler handler = new(responder);
        _disposables.Add(handler);
        HttpClient client = new(handler, disposeHandler: false);
        _disposables.Add(client);
        return new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), string.Empty);
    }

    private RuntimeDataRefreshCoordinator CreateCoordinator(
        RuntimeStateStore store,
        Func<MihomoApiClient, long, long, long, bool> isCurrentCoreBinding,
        ControllerFailureLogger failureLogger,
        Action requestThrottledPublish,
        Action publish,
        ControllerSessionGuard? controllerGuard = null)
    {
        SemaphoreSlim dataRefreshLock = new(1, 1);
        _disposables.Add(dataRefreshLock);
        return new RuntimeDataRefreshCoordinator(
            store,
            controllerGuard ?? new ControllerSessionGuard(new MihomoControllerSessionRegistry()),
            dataRefreshLock,
            static () => new CoreBindingEpochs(1, 1, 1),
            isCurrentCoreBinding,
            failureLogger,
            _ =>
            {
                requestThrottledPublish();
                return Task.CompletedTask;
            },
            publish);
    }

    private static RuntimeSnapshot CreateSnapshot(CoreState coreState) =>
        new(
            new CoreStatus(
                coreState,
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

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
