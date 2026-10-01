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
    public async Task SkippingOptionalDataPreservesConfirmedValuesAndAvailability()
    {
        List<string> paths = [];
        RuntimeStateStore store = new(CreateSnapshot(CoreState.Running));
        MihomoApiClient api = CreateApiClient(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return OptionalResponse(path);
        });
        RuntimeDataRefreshCoordinator coordinator = CreateCoordinator(store, static (_, _, _, _) => true,
            static (_, _, _, _, _) => { }, static () => { }, static () => { });
        await coordinator.RefreshOptionalDataAsync(api, CancellationToken.None);
        RuntimeSnapshot confirmed = store.Snapshot;
        paths.Clear();

        await coordinator.RefreshOptionalDataAsync(api, CancellationToken.None, demand: ControllerDataDemand.None);
        Assert.AreEqual(0, paths.Count);
        Assert.AreSame(confirmed, store.Snapshot);
        await coordinator.RefreshOptionalDataAsync(api, CancellationToken.None, demand: ControllerDataDemand.Proxies);
        Assert.AreEqual(1, paths.Count);
        Assert.AreEqual("/proxies", paths[0]);
        Assert.IsTrue(store.Snapshot.Core.TrafficAvailable);
        Assert.IsTrue(store.Snapshot.Core.MemoryAvailable);
        Assert.AreEqual(confirmed.Core.MemoryBytes, store.Snapshot.Core.MemoryBytes);
        Assert.AreSame(confirmed.Connections, store.Snapshot.Connections);
        Assert.AreSame(confirmed.Rules, store.Snapshot.Rules);
        Assert.AreEqual(confirmed.ConnectionsSummary, store.Snapshot.ConnectionsSummary);
        Assert.AreEqual(confirmed.RulesSummary, store.Snapshot.RulesSummary);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.RefreshOptionalDataAsync(api, cancellation.Token, demand: ControllerDataDemand.None));
    }

    [TestMethod]
    public async Task ProviderDemandDoesNotReadOrReplaceRulesInEitherControllerPipeline()
    {
        List<string> paths = [];
        RuntimeStateStore store = new(CreateSnapshot(CoreState.Running));
        MihomoApiClient api = CreateApiClient(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return OptionalResponse(path);
        });
        RuntimeDataRefreshCoordinator coordinator = CreateCoordinator(store, static (_, _, _, _) => true,
            static (_, _, _, _, _) => { }, static () => { }, static () => { });
        await coordinator.RefreshOptionalDataAsync(api, CancellationToken.None);
        RuntimeSnapshot previous = store.Snapshot;
        paths.Clear();
        await coordinator.RefreshOptionalDataAsync(api, CancellationToken.None, demand: ControllerDataDemand.Providers);
        Assert.AreEqual(2, paths.Count);
        Assert.IsTrue(paths.Contains("/providers/proxies", StringComparer.Ordinal));
        Assert.IsTrue(paths.Contains("/providers/rules", StringComparer.Ordinal));
        Assert.AreSame(previous.Rules, store.Snapshot.Rules);
        Assert.AreEqual(previous.RulesSummary, store.Snapshot.RulesSummary);
        MihomoControllerSnapshotData controllerData = await MihomoControllerSnapshotReader.ReadAsync(api, "test", "test", includeLogs: false);
        paths.Clear();
        MihomoControllerSnapshotData current = await MihomoControllerSnapshotReader.ReadWithDemandAsync(
            api, "test", "test", controllerData, includeLogs: false, ControllerDataDemand.Providers);
        Assert.AreEqual(3, paths.Count);
        Assert.IsFalse(paths.Contains("/rules", StringComparer.Ordinal));
        Assert.AreSame(controllerData.Rules, current.Rules);
        Assert.AreEqual(controllerData.RulesSummary, current.RulesSummary);
    }

    [TestMethod]
    public async Task FiveMinutesOfHiddenPollingRequestsThirtyOptionalResourcesInsteadOfSixHundred()
    {
        List<string> paths = [];
        RuntimeStateStore store = new(CreateSnapshot(CoreState.Running));
        MihomoApiClient api = CreateApiClient(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return OptionalResponse(path);
        });
        RuntimeDataRefreshCoordinator coordinator = CreateCoordinator(store, static (_, _, _, _) => true,
            static (_, _, _, _, _) => { }, static () => { }, static () => { });
        PanelRefreshPolicyTests.PollingClock clock = new();
        PanelRefreshPolicy policy = new(clock);
        policy.Set(false, ControllerPanelPage.Proxy);
        for (int poll = 0; poll < 150; poll++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await coordinator.RefreshOptionalDataAsync(api, CancellationToken.None,
                demand: policy.GetPollingDemand(EndpointKind.Local, true));
        }

        Assert.AreEqual(30, paths.Count);
        Assert.AreEqual(10, paths.Count(path => path == "/traffic"));
        Assert.AreEqual(10, paths.Count(path => path == "/memory"));
        Assert.AreEqual(10, paths.Count(path => path == "/connections"));
        Assert.IsFalse(paths.Any(path => path is "/proxies" or "/rules" or "/providers/proxies" or "/providers/rules"));
        Assert.AreEqual(100, store.Snapshot.Core.UploadBytes);
    }

    [TestMethod]
    public async Task DemandRefreshDiscardsResponseAfterBindingChangesDuringRequest()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool current = true;
        using AsyncDelegateHandler handler = new(async (request, cancellation) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return OptionalResponse(request.RequestUri!.AbsolutePath);
        });
        using HttpClient client = new(handler);
        MihomoApiClient api = new(client, new Uri("http://127.0.0.1:9090/"), string.Empty);
        RuntimeStateStore store = new(CreateSnapshot(CoreState.Running));
        RuntimeDataRefreshCoordinator coordinator = CreateCoordinator(store, (_, _, _, _) => current,
            static (_, _, _, _, _) => { }, static () => { }, static () => { });
        RuntimeSnapshot before = store.Snapshot;
        Task refresh = coordinator.RefreshOptionalDataAsync(api, CancellationToken.None, demand: ControllerDataDemand.Connections);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            current = false;
            release.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(before, store.Snapshot);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task ControllerReaderWithoutOptionalDemandStillConfirmsConfigurationAndPreservesCachedLists()
    {
        List<string> paths = [];
        MihomoApiClient api = CreateApiClient(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return path == "/configs"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"mode\":\"global\"}") }
                : OptionalResponse(path);
        });
        MihomoControllerSnapshotData previous = await MihomoControllerSnapshotReader.ReadAsync(api, "test", "test", includeLogs: false);
        paths.Clear();
        MihomoControllerSnapshotData current = await MihomoControllerSnapshotReader.ReadWithDemandAsync(
            api, "test", "test", previous, includeLogs: false, ControllerDataDemand.None);
        Assert.AreEqual(1, paths.Count);
        Assert.AreEqual("/configs", paths[0]);
        Assert.AreEqual(ProxyMode.Global, current.Status.Mode);
        Assert.AreEqual(previous.Status.ConnectionCount, current.Status.ConnectionCount);
        Assert.IsTrue(current.Status.TrafficAvailable);
        Assert.IsTrue(current.Status.MemoryAvailable);
        Assert.AreSame(previous.Connections, current.Connections);
        Assert.AreSame(previous.Rules, current.Rules);
        Assert.AreEqual(previous.ConnectionsSummary, current.ConnectionsSummary);
        Assert.AreEqual(previous.RulesSummary, current.RulesSummary);
        Assert.IsNotNull(current.LastConfirmedAt);
        Assert.IsNull(current.ErrorMessage);
        paths.Clear();
        await MihomoControllerSnapshotReader.ReadWithDemandAsync(
            api, "test", "test", current, includeLogs: false, ControllerDataDemand.Metrics);
        Assert.AreEqual(3, paths.Count);
        Assert.IsTrue(paths.Contains("/configs", StringComparer.Ordinal));
        Assert.IsTrue(paths.Contains("/traffic", StringComparer.Ordinal));
        Assert.IsTrue(paths.Contains("/memory", StringComparer.Ordinal));
    }

    private static HttpResponseMessage OptionalResponse(string path) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(path switch
        {
            "/traffic" => "{\"upTotal\":100,\"downTotal\":200,\"up\":3,\"down\":4}\n",
            "/memory" => "{\"inuse\":42}\n",
            "/connections" => "{\"connections\":[{\"id\":\"kept\"}]}",
            "/rules" => "{\"rules\":[[\"DOMAIN\",\"example.invalid\",\"DIRECT\"]]}",
            _ => "{}"
        })
    };

    private sealed class AsyncDelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    [TestMethod]
    public async Task TruncatedListsKeepReportedCountsAndSummariesThroughFailuresAndAppProjection()
    {
        string connections = "{\"connections\":[" + string.Join(',', Enumerable.Range(0, 2001).Select(i => $"{{\"id\":\"{i}\"}}")) + "]}";
        string rules = "{\"rules\":[" + string.Join(',', Enumerable.Repeat("[\"DOMAIN\",\"example.invalid\",\"DIRECT\"]", 5001)) + "]}";
        bool fail = false;
        RuntimeStateStore store = new(CreateSnapshot(CoreState.Running));
        MihomoApiClient api = CreateApiClient(request => new HttpResponseMessage(fail ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri?.AbsolutePath switch { "/connections" => connections, "/rules" => rules, _ => "{}" })
        });
        RuntimeDataRefreshCoordinator coordinator = CreateCoordinator(store, static (_, _, _, _) => true, static (_, _, _, _, _) => { }, static () => { }, static () => { });
        await coordinator.RefreshOptionalDataAsync(api, CancellationToken.None);
        RuntimeSnapshot accepted = store.Snapshot;
        Assert.AreEqual(2001, accepted.Core.ConnectionCount);
        Assert.AreEqual(2000, accepted.Connections.Count);
        Assert.AreEqual(new ControllerListSummary(2001, true), accepted.ConnectionsSummary);
        Assert.AreEqual(5000, accepted.Rules.Count);
        Assert.AreEqual(new ControllerListSummary(5001, true), accepted.RulesSummary);
        RuntimeSnapshot projected = RuntimeSnapshotAdapter.ToRuntimeSnapshot(RuntimeSnapshotAdapter.ToAppSnapshot(accepted, new AppSettings()));
        Assert.AreEqual(accepted.ConnectionsSummary, projected.ConnectionsSummary);
        Assert.AreEqual(accepted.RulesSummary, projected.RulesSummary);
        MihomoControllerSnapshotData controllerData = await MihomoControllerSnapshotReader.ReadAsync(api, "test", "test", includeLogs: false);
        Assert.AreEqual(2001, controllerData.Status.ConnectionCount);
        Assert.AreEqual(accepted.ConnectionsSummary, controllerData.ConnectionsSummary);
        Assert.AreEqual(accepted.RulesSummary, controllerData.RulesSummary);
        fail = true;
        await coordinator.RefreshOptionalDataAsync(api, CancellationToken.None);
        Assert.AreSame(accepted.Connections, store.Snapshot.Connections);
        Assert.AreSame(accepted.Rules, store.Snapshot.Rules);
        Assert.AreEqual(accepted.ConnectionsSummary, store.Snapshot.ConnectionsSummary);
        Assert.AreEqual(accepted.RulesSummary, store.Snapshot.RulesSummary);
        Assert.AreEqual(2001, store.Snapshot.Core.ConnectionCount);
        MihomoControllerSnapshotData retained = await MihomoControllerSnapshotReader.ReadAsync(api, "test", "test", controllerData, includeLogs: false);
        Assert.AreSame(controllerData.Connections, retained.Connections);
        Assert.AreEqual(controllerData.ConnectionsSummary, retained.ConnectionsSummary);
        Assert.AreEqual(controllerData.RulesSummary, retained.RulesSummary);
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
