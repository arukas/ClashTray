using System.Net;
using System.Text;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeEndpointTests
{
    [TestMethod]
    public async Task RuntimeExposesLocalAndSavedRemoteEndpointSummaries()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new(paths);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            EndpointCatalogLoadResult saved = await runtime.SaveRemoteEndpointAsync(new EndpointRecord(
                remote,
                SecretReference: "office-secret"));

            Assert.AreEqual(EndpointStoreLoadStatus.Loaded, saved.RemoteStoreStatus);
            Assert.AreEqual(2, runtime.Endpoints.Count);
            Assert.AreEqual(EndpointId.Local, runtime.Endpoints[0].Id);
            Assert.AreEqual(new EndpointId("office"), runtime.Endpoints[1].Id);
            Assert.AreEqual(EndpointStoreLoadStatus.Loaded, runtime.EndpointStoreStatus);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RuntimeTestsRemoteEndpointWithoutChangingActiveTarget()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        StaticConnector connector = new();
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            null,
            null,
            null,
            null,
            connector,
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(remote));

            EndpointHandshakeResult result = await runtime.TestRemoteEndpointAsync(remote.Id);

            Assert.AreEqual(EndpointSessionState.Connected, result.State);
            Assert.AreEqual(EndpointId.Local, runtime.EndpointSessionStatus.Endpoint.Id);
            Assert.AreEqual(EndpointSessionState.Disconnected, runtime.EndpointSessionStatus.State);
            Assert.AreEqual(1, connector.CallCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RuntimeKeepsLocalEndpointWhenRemoteMetadataIsCorrupt()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.EndpointStoreFile, "{not-json");
        await using ClashTrayRuntime runtime = new(paths);

        try
        {
            EndpointCatalogLoadResult loaded = await runtime.LoadEndpointCatalogAsync();

            Assert.AreEqual(EndpointStoreLoadStatus.Recovered, loaded.RemoteStoreStatus);
            Assert.AreEqual(1, runtime.Endpoints.Count);
            Assert.AreEqual(EndpointId.Local, runtime.Endpoints[0].Id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(runtime.EndpointStoreMessage));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RuntimeRemovesRemoteEndpointWithoutTouchingLocalTarget()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new(paths);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(remote));

            EndpointRemovalResult result = await runtime.RemoveRemoteEndpointAsync(remote.Id);

            Assert.IsTrue(result.Removed);
            Assert.AreEqual(1, runtime.Endpoints.Count);
            Assert.AreEqual(EndpointId.Local, runtime.Endpoints[0].Id);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RuntimeUpdatesRemoteEndpointWithoutChangingItsIdentity()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new(paths);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(remote));

            EndpointCatalogLoadResult updated = await runtime.UpdateRemoteEndpointAsync(
                remote.Id,
                remote with
                {
                    DisplayName = "Office renamed",
                    BaseUri = new Uri("https://new-office.example.test")
                },
                secret: null,
                customCaCertificate: null,
                insecureHttpAcknowledgedAtUtc: null);

            Assert.AreEqual(EndpointStoreLoadStatus.Loaded, updated.RemoteStoreStatus);
            Assert.AreEqual(2, runtime.Endpoints.Count);
            Assert.AreEqual(new EndpointId("office"), runtime.Endpoints[1].Id);
            Assert.AreEqual("Office renamed", runtime.Endpoints[1].DisplayName);
            Assert.AreEqual("https://new-office.example.test/", runtime.Endpoints[1].BaseUri.AbsoluteUri);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RuntimePublishesUnifiedAppSnapshotWhenEndpointCatalogChanges()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new(paths);
        TaskCompletionSource<AppSnapshot> changed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<RuntimeSnapshot> legacyChanged = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.AppSnapshotChanged += (_, snapshot) => changed.TrySetResult(snapshot);
        runtime.SnapshotChanged += (_, snapshot) => legacyChanged.TrySetResult(snapshot);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(remote));

            AppSnapshot published = await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            RuntimeSnapshot publishedLegacy = await legacyChanged.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(2, published.Endpoints.Count);
            Assert.AreEqual(EndpointId.Local, published.ActiveController.Endpoint.Id);
            Assert.AreEqual(EndpointId.Local, published.Endpoints[0].Id);
            Assert.AreEqual(new EndpointId("office"), published.Endpoints[1].Id);
            Assert.AreEqual(EndpointCapabilityDefaults.Local, published.ActiveController.Capabilities);
            Assert.AreEqual(runtime.Snapshot.Core.State, publishedLegacy.Core.State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RuntimePublishesRemoteSessionStateAndDisconnectsStaleMetadata()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        StaticConnector connector = new();
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            null,
            null,
            null,
            null,
            connector,
            (_, cancellationToken) => Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken),
            remoteLogStreamRunner: (_, _, _) => Task.CompletedTask);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(remote));
            List<EndpointSessionState> publishedStates = [];
            TaskCompletionSource<AppSnapshot> refreshed = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.AppSnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.ActiveController.Endpoint.Id == remote.Id)
                {
                    publishedStates.Add(snapshot.ActiveController.State);
                    if (snapshot.ActiveController.Status?.Mode == ProxyMode.Global)
                    {
                        refreshed.TrySetResult(snapshot);
                    }
                }
            };

            EndpointSession? session = await runtime.SelectEndpointAsync(remote.Id);
            AppSnapshot remoteSnapshot = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsNotNull(session);
            Assert.AreEqual(CoreState.Missing, runtime.AppSnapshot.LocalDevice.CoreState);
            Assert.AreEqual(remote.Id, runtime.AppSnapshot.ActiveController.Endpoint.Id);
            Assert.AreEqual(EndpointSessionState.Connected, runtime.AppSnapshot.ActiveController.State);
            Assert.AreEqual(EndpointCapabilityDefaults.Remote, runtime.AppSnapshot.ActiveController.Capabilities);
            Assert.AreEqual(ProxyMode.Global, remoteSnapshot.ActiveController.Status?.Mode);
            Assert.AreEqual(11, remoteSnapshot.ActiveController.Status?.UploadBytes);
            Assert.AreEqual(22, remoteSnapshot.ActiveController.Status?.MemoryBytes);
            Assert.AreEqual(1, remoteSnapshot.ActiveController.ProxyGroups.Count);
            CollectionAssert.Contains(publishedStates, EndpointSessionState.Connecting);
            CollectionAssert.Contains(publishedStates, EndpointSessionState.Connected);

            TaskCompletionSource<AppSnapshot> republished = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.AppSnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.ActiveController.Status?.UploadBytes == 21)
                {
                    republished.TrySetResult(snapshot);
                }
            };
            connector.SetTraffic(21, 22);
            AppSnapshot updatedSnapshot = await republished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(21, updatedSnapshot.ActiveController.Status?.UploadBytes);

            TaskCompletionSource<AppSnapshot> modeChanged = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.AppSnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.ActiveController.Status?.Mode == ProxyMode.Direct)
                {
                    modeChanged.TrySetResult(snapshot);
                }
            };
            await runtime.SetModeAsync(ProxyMode.Direct);
            AppSnapshot modeSnapshot = await modeChanged.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(ProxyMode.Direct, modeSnapshot.ActiveController.Status?.Mode);
            Assert.AreEqual(1, connector.ModePatchCount);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => runtime.SetLocalModeAsync(ProxyMode.Rule));
            Assert.AreEqual(1, connector.ModePatchCount);

            TaskCompletionSource<AppSnapshot> proxyChanged = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.AppSnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.ActiveController.ProxyGroups
                    .FirstOrDefault(item => item.Name == "Auto")
                    ?.Current == "backup")
                {
                    proxyChanged.TrySetResult(snapshot);
                }
            };
            await runtime.SelectProxyAsync("Auto", "backup");
            AppSnapshot proxySnapshot = await proxyChanged.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(
                "backup",
                proxySnapshot.ActiveController.ProxyGroups
                    .Single(item => item.Name == "Auto")
                    .Current);
            Assert.AreEqual(1, connector.ProxySelectionCount);

            IReadOnlyDictionary<string, int?> groupDelays =
                await runtime.TestProxyGroupDelayAsync("Auto");
            Assert.AreEqual(123, groupDelays["node"]);
            Assert.AreEqual(456, groupDelays["backup"]);
            Assert.AreEqual(1, connector.DelayRequestCount);

            int? nodeDelay = await runtime.TestProxyDelayAsync("backup");
            Assert.AreEqual(123, nodeDelay);
            Assert.AreEqual(2, connector.DelayRequestCount);

            Assert.AreEqual(2, runtime.AppSnapshot.ActiveController.Connections.Count);
            await runtime.CloseConnectionAsync("c1");
            Assert.AreEqual(1, runtime.AppSnapshot.ActiveController.Connections.Count);
            Assert.AreEqual(1, connector.SingleConnectionCloseCount);

            await runtime.CloseAllConnectionsAsync();
            Assert.AreEqual(0, runtime.AppSnapshot.ActiveController.Connections.Count);
            Assert.AreEqual(1, connector.CloseAllConnectionsCount);

            await runtime.RefreshProviderAsync("RemoteProxy", rules: false);
            Assert.AreEqual(1, connector.ProxyProviderRefreshCount);
            await runtime.RefreshProviderAsync("RemoteRules", rules: true);
            Assert.AreEqual(1, connector.RuleProviderRefreshCount);

            await runtime.ClearFakeIpCacheAsync();
            Assert.AreEqual(1, connector.FakeIpCacheClearCount);
            await runtime.ClearDnsCacheAsync();
            Assert.AreEqual(1, connector.DnsCacheClearCount);

            await runtime.UpdateGeoAsync();
            Assert.AreEqual(1, connector.GeoUpdateCount);

            await runtime.UpdateRemoteEndpointAsync(
                remote.Id,
                remote with
                {
                    DisplayName = "Office renamed",
                    BaseUri = new Uri("https://new-office.example.test")
                },
                secret: null,
                customCaCertificate: null,
                insecureHttpAcknowledgedAtUtc: null);

            Assert.AreEqual(EndpointId.Local, runtime.AppSnapshot.ActiveController.Endpoint.Id);
            Assert.AreEqual(EndpointSessionState.Disconnected, runtime.EndpointSessionStatus.State);
            Assert.ThrowsExactly<ObjectDisposedException>(() => session!.BuildWebSocketUri("/logs"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RuntimeRefreshesActiveRemoteSnapshotOnDemand()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        StaticConnector connector = new();
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            null,
            null,
            null,
            null,
            connector,
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            remoteLogStreamRunner: (_, _, _) => Task.CompletedTask);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(remote));
            await runtime.SelectEndpointAsync(remote.Id);
            Assert.AreEqual(11, runtime.AppSnapshot.ActiveController.Status?.UploadBytes);

            connector.SetTraffic(31, 32);
            await runtime.RefreshDataAsync();

            Assert.AreEqual(31, runtime.AppSnapshot.ActiveController.Status?.UploadBytes);
            Assert.AreEqual(32, runtime.AppSnapshot.ActiveController.Status?.DownloadBytes);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RemoteSnapshotMarksPartialRefreshAsReconnectingAndRecovers()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        StaticConnector connector = new();
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            null,
            null,
            null,
            null,
            connector,
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            remoteLogStreamRunner: (_, _, _) => Task.CompletedTask);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(remote));
            await runtime.SelectEndpointAsync(remote.Id);

            AppSnapshot confirmed = runtime.AppSnapshot;
            Assert.AreEqual(EndpointSessionState.Connected, confirmed.ActiveController.State);
            DateTimeOffset confirmedAt = confirmed.ActiveController.LastConfirmedAt!.Value;

            connector.FailTraffic = true;
            await runtime.RefreshDataAsync();

            AppSnapshot stale = runtime.AppSnapshot;
            Assert.AreEqual(EndpointSessionState.Reconnecting, stale.ActiveController.State);
            Assert.AreEqual(confirmedAt, stale.ActiveController.LastConfirmedAt);
            Assert.IsFalse(string.IsNullOrWhiteSpace(stale.ActiveController.ErrorMessage));

            connector.FailTraffic = false;
            await runtime.RefreshDataAsync();

            AppSnapshot recovered = runtime.AppSnapshot;
            Assert.AreEqual(EndpointSessionState.Connected, recovered.ActiveController.State);
            Assert.IsNull(recovered.ActiveController.ErrorMessage);
            Assert.IsNotNull(recovered.ActiveController.LastConfirmedAt);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ActiveRemoteLogStreamIsCancelledWhenEndpointChanges()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        StaticConnector connector = new();
        TaskCompletionSource<bool> streamStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> streamCancelled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunRemoteLogStreamAsync(
            EndpointSession session,
            EndpointSessionStatusEventArgs status,
            CancellationToken cancellationToken)
        {
            Assert.IsNotNull(session);
            Assert.AreEqual(new EndpointId("office"), status.Endpoint.Id);
            streamStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                streamCancelled.TrySetResult(true);
            }
        }

        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            null,
            null,
            null,
            null,
            connector,
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            RunRemoteLogStreamAsync);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(remote));
            await runtime.SelectEndpointAsync(remote.Id);
            await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await runtime.SelectEndpointAsync(EndpointId.Local);
            await streamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(EndpointId.Local, runtime.EndpointSessionStatus.Endpoint.Id);
            Assert.AreEqual(EndpointSessionState.Disconnected, runtime.EndpointSessionStatus.State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task EndpointSelectionWaitsForControllerMutationToFinish()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using BlockingMutationHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(
            httpClient,
            new Uri("http://127.0.0.1:9090/"),
            string.Empty);
        StaticConnector connector = new();
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            null,
            null,
            null,
            null,
            connector,
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            remoteLogStreamRunner: (_, _, _) => Task.CompletedTask);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));
        Task? modeChange = null;
        Task<EndpointSession?>? endpointSelection = null;

        try
        {
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(remote));
            runtime.AttachControllerForTesting(api, usingServiceCore: false);

            modeChange = runtime.SetModeAsync(ProxyMode.Direct);
            await handler.MutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            endpointSelection = runtime.SelectEndpointAsync(remote.Id);
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            Assert.IsFalse(connector.ConnectEntered.Task.IsCompleted);

            handler.ReleaseMutation();
            await Task.WhenAll(modeChange, endpointSelection);

            Assert.IsTrue(connector.ConnectEntered.Task.IsCompleted);
            Assert.AreEqual(remote.Id, runtime.EndpointSessionStatus.Endpoint.Id);
        }
        finally
        {
            handler.ReleaseMutation();
            if (modeChange is not null)
            {
                await modeChange.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (endpointSelection is not null)
            {
                await endpointSelection.WaitAsync(TimeSpan.FromSeconds(2));
            }

            DeleteRoot(root);
        }
    }

    private static string CreateRoot() => Path.Combine(
        Path.GetTempPath(),
        "ClashTrayTests",
        Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticConnector : IEndpointSessionConnector
    {
        private SnapshotHandler? _handler;
        private int _callCount;
        private int _failTraffic;

        public TaskCompletionSource<bool> ConnectEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public bool FailTraffic
        {
            get => Volatile.Read(ref _failTraffic) == 1;
            set
            {
                Volatile.Write(ref _failTraffic, value ? 1 : 0);
                Volatile.Read(ref _handler)?.SetFailTraffic(value);
            }
        }

        public int ModePatchCount => Volatile.Read(ref _handler)?.ModePatchCount ?? 0;

        public int ProxySelectionCount => Volatile.Read(ref _handler)?.ProxySelectionCount ?? 0;

        public int DelayRequestCount => Volatile.Read(ref _handler)?.DelayRequestCount ?? 0;

        public int SingleConnectionCloseCount =>
            Volatile.Read(ref _handler)?.SingleConnectionCloseCount ?? 0;

        public int CloseAllConnectionsCount =>
            Volatile.Read(ref _handler)?.CloseAllConnectionsCount ?? 0;

        public int ProxyProviderRefreshCount =>
            Volatile.Read(ref _handler)?.ProxyProviderRefreshCount ?? 0;

        public int RuleProviderRefreshCount =>
            Volatile.Read(ref _handler)?.RuleProviderRefreshCount ?? 0;

        public int FakeIpCacheClearCount =>
            Volatile.Read(ref _handler)?.FakeIpCacheClearCount ?? 0;

        public int DnsCacheClearCount =>
            Volatile.Read(ref _handler)?.DnsCacheClearCount ?? 0;

        public int GeoUpdateCount =>
            Volatile.Read(ref _handler)?.GeoUpdateCount ?? 0;

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The EndpointSession takes ownership of the transport and disposes it asynchronously.")]
        public Task<EndpointSession> ConnectAsync(
            EndpointDescriptor endpoint,
            long generation,
            long selectionRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            ConnectEntered.TrySetResult(true);
            SnapshotHandler handler = new();
            handler.SetFailTraffic(Volatile.Read(ref _failTraffic) == 1);
            Volatile.Write(ref _handler, handler);
            HttpClient client = new(handler, disposeHandler: true)
            {
                BaseAddress = endpoint.BaseUri
            };
            Uri webSocketUri = new UriBuilder(endpoint.BaseUri)
            {
                Scheme = endpoint.BaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? "wss"
                    : "ws"
            }.Uri;
            EndpointTransport transport = new(
                endpoint,
                endpoint.BaseUri,
                webSocketUri,
                client,
                authorizationValue: null,
                customCaCertificate: null,
                bypassesSystemProxy: true);
            return Task.FromResult(new EndpointSession(
                transport,
                EndpointCapabilityDefaults.Remote,
                generation,
                selectionRevision));
        }

        public void SetTraffic(long uploadBytes, long downloadBytes)
        {
            Volatile.Read(ref _handler)?.SetTraffic(uploadBytes, downloadBytes);
        }

        private sealed class SnapshotHandler : HttpMessageHandler
        {
            private long _uploadBytes = 11;
            private long _downloadBytes = 12;
            private string _mode = "global";
            private int _modePatchCount;
            private string _proxy = "node";
            private int _proxySelectionCount;
            private int _delayRequestCount;
            private int _remainingConnections = 2;
            private int _singleConnectionCloseCount;
            private int _closeAllConnectionsCount;
            private int _proxyProviderRefreshCount;
            private int _ruleProviderRefreshCount;
            private int _fakeIpCacheClearCount;
            private int _dnsCacheClearCount;
            private int _geoUpdateCount;
            private int _failTraffic;

            public bool FailTraffic => Volatile.Read(ref _failTraffic) == 1;

            public int ModePatchCount => Volatile.Read(ref _modePatchCount);

            public int ProxySelectionCount => Volatile.Read(ref _proxySelectionCount);

            public int DelayRequestCount => Volatile.Read(ref _delayRequestCount);

            public int SingleConnectionCloseCount =>
                Volatile.Read(ref _singleConnectionCloseCount);

            public int CloseAllConnectionsCount =>
                Volatile.Read(ref _closeAllConnectionsCount);

            public int ProxyProviderRefreshCount =>
                Volatile.Read(ref _proxyProviderRefreshCount);

            public int RuleProviderRefreshCount =>
                Volatile.Read(ref _ruleProviderRefreshCount);

            public int FakeIpCacheClearCount =>
                Volatile.Read(ref _fakeIpCacheClearCount);

            public int DnsCacheClearCount =>
                Volatile.Read(ref _dnsCacheClearCount);

            public int GeoUpdateCount =>
                Volatile.Read(ref _geoUpdateCount);

            public void SetTraffic(long uploadBytes, long downloadBytes)
            {
                Interlocked.Exchange(ref _uploadBytes, uploadBytes);
                Interlocked.Exchange(ref _downloadBytes, downloadBytes);
            }

            public void SetFailTraffic(bool fail) => Volatile.Write(ref _failTraffic, fail ? 1 : 0);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long uploadBytes = Interlocked.Read(ref _uploadBytes);
                long downloadBytes = Interlocked.Read(ref _downloadBytes);
                if (request.RequestUri?.AbsolutePath == "/traffic" && FailTraffic)
                {
                    throw new HttpRequestException("simulated traffic refresh failure");
                }
                if (request.Method == HttpMethod.Patch
                    && request.RequestUri?.AbsolutePath == "/configs")
                {
                    using JsonDocument payload = JsonDocument.Parse(
                        await request.Content!.ReadAsStringAsync(cancellationToken));
                    string? requestedMode = payload.RootElement
                        .GetProperty("mode")
                        .GetString();
                    if (!string.IsNullOrWhiteSpace(requestedMode))
                    {
                        Volatile.Write(ref _mode, requestedMode);
                    }

                    Interlocked.Increment(ref _modePatchCount);
                }

                if (request.Method == HttpMethod.Put
                    && request.RequestUri?.AbsolutePath == "/proxies/Auto")
                {
                    using JsonDocument payload = JsonDocument.Parse(
                        await request.Content!.ReadAsStringAsync(cancellationToken));
                    string? requestedProxy = payload.RootElement
                        .GetProperty("name")
                        .GetString();
                    if (!string.IsNullOrWhiteSpace(requestedProxy))
                    {
                        Volatile.Write(ref _proxy, requestedProxy);
                    }

                    Interlocked.Increment(ref _proxySelectionCount);
                }

                if (request.RequestUri?.AbsolutePath is "/group/Auto/delay"
                    or "/proxies/backup/delay")
                {
                    Interlocked.Increment(ref _delayRequestCount);
                }

                if (request.Method == HttpMethod.Delete
                    && request.RequestUri?.AbsolutePath == "/connections/c1")
                {
                    Volatile.Write(ref _remainingConnections, 1);
                    Interlocked.Increment(ref _singleConnectionCloseCount);
                }
                else if (request.Method == HttpMethod.Delete
                    && request.RequestUri?.AbsolutePath == "/connections")
                {
                    Volatile.Write(ref _remainingConnections, 0);
                    Interlocked.Increment(ref _closeAllConnectionsCount);
                }

                if (request.Method == HttpMethod.Put
                    && request.RequestUri?.AbsolutePath == "/providers/proxies/RemoteProxy")
                {
                    Interlocked.Increment(ref _proxyProviderRefreshCount);
                }
                else if (request.Method == HttpMethod.Put
                    && request.RequestUri?.AbsolutePath == "/providers/rules/RemoteRules")
                {
                    Interlocked.Increment(ref _ruleProviderRefreshCount);
                }

                if (request.Method == HttpMethod.Post
                    && request.RequestUri?.AbsolutePath == "/cache/fakeip/flush")
                {
                    Interlocked.Increment(ref _fakeIpCacheClearCount);
                }
                else if (request.Method == HttpMethod.Post
                    && request.RequestUri?.AbsolutePath == "/cache/dns/flush")
                {
                    Interlocked.Increment(ref _dnsCacheClearCount);
                }
                else if (request.Method == HttpMethod.Post
                    && request.RequestUri?.AbsolutePath == "/configs/geo")
                {
                    Interlocked.Increment(ref _geoUpdateCount);
                }

                int remainingConnections = Volatile.Read(ref _remainingConnections);
                string body = request.RequestUri?.AbsolutePath switch
                {
                    "/configs" => "{\"mode\":\""
                        + Volatile.Read(ref _mode)
                        + "\",\"tun\":{\"enable\":false}}",
                    "/proxies" => "{\"proxies\":{\"Auto\":{\"type\":\"Selector\",\"now\":\""
                        + Volatile.Read(ref _proxy)
                        + "\",\"all\":[\"node\",\"backup\"]},\"node\":{\"type\":\"Direct\"},\"backup\":{\"type\":\"Direct\"}}}",
                    "/proxies/backup/delay" => """{"delay":123}""",
                    "/group/Auto/delay" => """{"node":123,"backup":456}""",
                    "/traffic" => $$"""{"upTotal":{{uploadBytes}},"downTotal":{{downloadBytes}},"up":1,"down":2}"""
                        + "\n",
                    "/memory" => """{"inuse":22}"""
                        + "\n",
                    "/connections" when remainingConnections == 2 => """{"connections":[{"id":"c1","metadata":{"network":"tcp","sourceIP":"10.0.0.2","destinationIP":"example.test"},"chains":["Auto"],"rule":"MATCH","upload":1,"download":2,"start":"2026-09-15T00:00:00Z"},{"id":"c2","metadata":{"network":"tcp","sourceIP":"10.0.0.3","destinationIP":"example.test"},"chains":["Auto"],"rule":"MATCH","upload":3,"download":4,"start":"2026-09-15T00:00:01Z"}]}""",
                    "/connections" when remainingConnections == 1 => """{"connections":[{"id":"c2","metadata":{"network":"tcp","sourceIP":"10.0.0.3","destinationIP":"example.test"},"chains":["Auto"],"rule":"MATCH","upload":3,"download":4,"start":"2026-09-15T00:00:01Z"}]}""",
                    "/connections" => """{"connections":[]}""",
                    "/rules" => """{"rules":[]}""",
                    "/providers/proxies" => """{"providers":{}}""",
                    "/providers/rules" => """{"providers":{}}""",
                    "/logs" => "[]",
                    _ => "{}"
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }
        }
    }

    private sealed class BlockingMutationHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _releaseMutation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> MutationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseMutation() => _releaseMutation.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Patch
                && path == "/configs"
                && !MutationEntered.Task.IsCompleted)
            {
                MutationEntered.TrySetResult(true);
                await _releaseMutation.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NoContent)
                {
                    Content = new StringContent(string.Empty)
                };
            }

            string body = path switch
            {
                "/version" => """{"version":"v1.19.30"}""",
                "/configs" => """{"mode":"direct","tun":{"enable":false}}""",
                "/traffic" => """{"upTotal":0,"downTotal":0,"up":0,"down":0}""",
                "/memory" => """{"inuse":0}""",
                "/connections" => """{"connections":[]}""",
                "/rules" => """{"rules":[]}""",
                "/providers/proxies" => """{"providers":{}}""",
                "/providers/rules" => """{"providers":{}}""",
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
        }
    }
}
