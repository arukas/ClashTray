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
            (_, cancellationToken) => Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken));
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

        public int ModePatchCount => Volatile.Read(ref _handler)?.ModePatchCount ?? 0;

        public int ProxySelectionCount => Volatile.Read(ref _handler)?.ProxySelectionCount ?? 0;

        public int DelayRequestCount => Volatile.Read(ref _handler)?.DelayRequestCount ?? 0;

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
            SnapshotHandler handler = new();
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

            public int ModePatchCount => Volatile.Read(ref _modePatchCount);

            public int ProxySelectionCount => Volatile.Read(ref _proxySelectionCount);

            public int DelayRequestCount => Volatile.Read(ref _delayRequestCount);

            public void SetTraffic(long uploadBytes, long downloadBytes)
            {
                Interlocked.Exchange(ref _uploadBytes, uploadBytes);
                Interlocked.Exchange(ref _downloadBytes, downloadBytes);
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long uploadBytes = Interlocked.Read(ref _uploadBytes);
                long downloadBytes = Interlocked.Read(ref _downloadBytes);
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
}
