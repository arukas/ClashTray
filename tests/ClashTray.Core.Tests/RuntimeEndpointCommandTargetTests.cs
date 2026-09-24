using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeEndpointCommandTargetTests
{
    [TestMethod]
    public async Task CloseAllQueuedForEndpointADoesNotRetargetToEndpointB()
    {
        await AssertQueuedCloseDoesNotRetargetAsync(closeAll: true);
    }

    [TestMethod]
    public async Task CloseSingleQueuedForEndpointADoesNotRetargetToEndpointBWhenIdsMatch()
    {
        await AssertQueuedCloseDoesNotRetargetAsync(closeAll: false);
    }

    [TestMethod]
    public async Task QueuedCloseUsesCurrentEndpointWhenNoSwitchOccurs()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.Runtime.SelectEndpointAsync(fixture.EndpointA.Id);
        EndpointCommandTarget target = CaptureTarget(fixture.Runtime);

        await CloseAsync(fixture.Runtime, closeAll: true, expectedTarget: target);

        Assert.AreEqual(1, fixture.Connector.DeleteCount(fixture.EndpointA.Id, "/connections"));
        Assert.AreEqual(0, fixture.Connector.DeleteCount(fixture.EndpointB.Id));
        Assert.AreEqual(
            target.Generation,
            fixture.Runtime.AppSnapshot.ActiveController.Generation);
    }

    [TestMethod]
    public async Task QueuedCloseCancellationDoesNotSendToEitherEndpoint()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.Runtime.SelectEndpointAsync(fixture.EndpointA.Id);
        using CancellationTokenSource cancellation = new();
        Task closeTask;
        using (await AcquireExclusiveOperationAsync(fixture.Runtime))
        {
            closeTask = fixture.Runtime.CloseAllConnectionsAsync(cancellation.Token);
            await WaitUntilQueuedAsync(closeTask);
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => closeTask.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(0, fixture.Connector.DeleteCount(fixture.EndpointA.Id));
        Assert.AreEqual(0, fixture.Connector.DeleteCount(fixture.EndpointB.Id));
    }

    [TestMethod]
    public async Task QueuedCloseRejectsSameEndpointAfterSessionGenerationChanges()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.Runtime.SelectEndpointAsync(fixture.EndpointA.Id);
        EndpointCommandTarget target = CaptureTarget(fixture.Runtime);
        long originalGeneration = target.Generation;
        Task closeTask;
        using (await AcquireExclusiveOperationAsync(fixture.Runtime))
        {
            closeTask = fixture.Runtime.CloseAllConnectionsAsync(target);
            await WaitUntilQueuedAsync(closeTask);
            await fixture.Runtime.SelectEndpointAsync(fixture.EndpointA.Id);
            Assert.AreNotEqual(originalGeneration, fixture.Runtime.AppSnapshot.ActiveController.Generation);
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => closeTask.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(0, fixture.Connector.DeleteCount(fixture.EndpointA.Id));
        Assert.AreEqual(0, fixture.Connector.DeleteCount(fixture.EndpointB.Id));
    }

    private static async Task AssertQueuedCloseDoesNotRetargetAsync(bool closeAll)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.Runtime.SelectEndpointAsync(fixture.EndpointA.Id);
        Task closeTask;
        using (await AcquireExclusiveOperationAsync(fixture.Runtime))
        {
            EndpointCommandTarget target = CaptureTarget(fixture.Runtime);
            closeTask = CloseAsync(fixture.Runtime, closeAll, target);
            await WaitUntilQueuedAsync(closeTask);
            await fixture.Runtime.SelectEndpointAsync(fixture.EndpointB.Id);
            Assert.AreEqual(fixture.EndpointB.Id, fixture.Runtime.AppSnapshot.ActiveController.Endpoint.Id);
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => closeTask.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(0, fixture.Connector.DeleteCount(fixture.EndpointA.Id));
        Assert.AreEqual(0, fixture.Connector.DeleteCount(fixture.EndpointB.Id));

        await CloseAsync(fixture.Runtime, closeAll, CaptureTarget(fixture.Runtime));
        Assert.AreEqual(0, fixture.Connector.DeleteCount(fixture.EndpointA.Id));
        Assert.AreEqual(
            1,
            fixture.Connector.DeleteCount(
                fixture.EndpointB.Id,
                closeAll ? "/connections" : "/connections/c1"));
    }

    private static EndpointCommandTarget CaptureTarget(ClashTrayRuntime runtime)
    {
        ControllerSessionSnapshot snapshot = runtime.AppSnapshot.ActiveController;
        return new EndpointCommandTarget(snapshot.Endpoint.Id, snapshot.Generation);
    }

    private static Task CloseAsync(
        ClashTrayRuntime runtime,
        bool closeAll,
        EndpointCommandTarget? expectedTarget = null) =>
        closeAll
            ? runtime.CloseAllConnectionsAsync(expectedTarget)
            : runtime.CloseConnectionAsync("c1", expectedTarget);
    private static async Task WaitUntilQueuedAsync(Task operation)
    {
        await Task.Delay(25);
        Assert.IsFalse(operation.IsCompleted, "The close request should still be waiting for operation admission.");
    }

    private static async Task<OperationGate.Lease> AcquireExclusiveOperationAsync(ClashTrayRuntime runtime)
    {
        FieldInfo field = typeof(ClashTrayRuntime).GetField(
            "_operationLock",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("Runtime operation gate was not found.");
        OperationGate gate = (OperationGate)(field.GetValue(runtime)
            ?? throw new AssertFailedException("Runtime operation gate was null."));
        return await gate.AcquireAsync();
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string _root;

        private RuntimeFixture(string root, ClashTrayRuntime runtime, RecordingConnector connector)
        {
            _root = root;
            Runtime = runtime;
            Connector = connector;
            EndpointA = CreateEndpoint("remote-a", "a");
            EndpointB = CreateEndpoint("remote-b", "b");
        }

        public ClashTrayRuntime Runtime { get; }

        public RecordingConnector Connector { get; }

        public EndpointDescriptor EndpointA { get; }

        public EndpointDescriptor EndpointB { get; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned fixture owns and disposes the runtime after the test operation.")]
        public static async Task<RuntimeFixture> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "ClashTrayTests",
                "EndpointCommandTarget",
                Guid.NewGuid().ToString("N"));
            RecordingConnector connector = new();
            ClashTrayRuntime runtime = new(
                new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program")),
                null,
                null,
                null,
                null,
                null,
                null,
                connector,
                (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                remoteLogStreamRunner: (_, _, _) => Task.CompletedTask);
            RuntimeFixture fixture = new(root, runtime, connector);
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(fixture.EndpointA));
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(fixture.EndpointB));
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static EndpointDescriptor CreateEndpoint(string id, string host) =>
            EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId(id),
                id,
                new Uri($"https://{host}.example.test/"));
    }

    private sealed class RecordingConnector : IEndpointSessionConnector
    {
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();

        public int DeleteCount(EndpointId endpointId, string? path = null) =>
            _requests.Count(request => request.EndpointId == endpointId
                && request.Method == HttpMethod.Delete
                && (path is null || request.Path == path));

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned EndpointSession owns the transport, which owns the HttpClient and handler.")]
        public Task<EndpointSession> ConnectAsync(
            EndpointDescriptor endpoint,
            long generation,
            long selectionRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpClient client = new(new RecordingHandler(endpoint.Id, _requests), disposeHandler: true)
            {
                BaseAddress = endpoint.BaseUri
            };
            Uri webSocketUri = new UriBuilder(endpoint.BaseUri)
            {
                Scheme = endpoint.BaseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws"
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

        private sealed record RecordedRequest(EndpointId EndpointId, HttpMethod Method, string Path);

        private sealed class RecordingHandler(
            EndpointId endpointId,
            ConcurrentQueue<RecordedRequest> requests) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                requests.Enqueue(new RecordedRequest(
                    endpointId,
                    request.Method,
                    request.RequestUri?.AbsolutePath ?? string.Empty));
                if (request.Method == HttpMethod.Delete)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }

                string body = request.RequestUri?.AbsolutePath switch
                {
                    "/configs" => """{"mode":"rule","tun":{"enable":false}}""",
                    "/proxies" => """{"proxies":{}}""",
                    "/traffic" => """{"upTotal":0,"downTotal":0,"up":0,"down":0}""",
                    "/memory" => """{"inuse":0}""",
                    "/connections" => """{"connections":[]}""",
                    "/rules" => """{"rules":[]}""",
                    "/providers/proxies" or "/providers/rules" => """{"providers":{}}""",
                    "/logs" => """{"logs":[]}""",
                    _ => "{}"
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }
        }
    }

}
