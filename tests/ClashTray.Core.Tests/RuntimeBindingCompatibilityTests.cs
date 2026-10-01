using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeBindingCompatibilityTests
{
    [TestMethod]
    public void AdditiveAdmissionErrorsKeepExistingWireValuesAndProtocol()
    {
        Assert.AreEqual(12, (int)ServiceErrorCode.OperationCancelled);
        Assert.AreEqual(13, (int)ServiceErrorCode.RuntimeBindingMetadataMissing);
        Assert.AreEqual(14, (int)ServiceErrorCode.RuntimeBindingInvalid);
        Assert.AreEqual(2, ServiceProtocol.CurrentVersion);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OldServiceCannotAdmitControllerRequestsAndExplainsSynchronizedUpgrade(bool missingWholeBinding)
    {
        using TcpListener controller = new(IPAddress.Loopback, 0);
        controller.Start();
        AppSettings settings = RuntimeTestHelpers.CreatePortSafeSettings() with
        {
            ControllerPort = ((IPEndPoint)controller.LocalEndpoint).Port,
            SystemProxyEnabled = true
        };
        CoreRuntimeBinding old = RuntimeTestHelpers.CreateRuntimeBinding(settings) with { ListenerBindings = null };
        FakeSystemProxyController proxy = new(SystemProxyState.Off);
        using CountingControllerHandler handler = new();
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        await using ClashTrayRuntime runtime = new(new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program")),
            null, new OldRunningService(missingWholeBinding ? null : old), new TestSettingsStore(settings), proxy,
            controllerHttpMessageHandler: handler);
        try
        {
            await runtime.InitializeAsync();
            Assert.IsFalse(runtime.IsCoreHealthConfirmedForTesting);
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "同步升级", StringComparison.Ordinal);
            Assert.IsNull(runtime.ActiveRuntimeBinding);
            Assert.AreEqual(0, handler.RequestCount,
                "Missing service metadata must be rejected before even creating a controller session.");
            await runtime.SetSystemProxyAsync(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SetModeAsync(ProxyMode.Direct));
            Assert.AreEqual(0, handler.RequestCount);
            Assert.AreEqual(0, proxy.EnableCount);
        }
        finally
        {
            await runtime.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void OlderServicePayloadsDeserializeWithMissingListenerMetadata()
    {
        CoreRuntimeBinding current = RuntimeTestHelpers.CreateRuntimeBinding(new AppSettings()) with
        {
            ListenerBindings =
            [
                new RuntimeListenerBinding(
                    "mixed-tcp",
                    "::",
                    17890,
                    RuntimeListenerTransport.Tcp,
                    DualMode: true)
            ]
        };
        JsonObject oldServiceJson = JsonSerializer.SerializeToNode(current)!.AsObject();
        oldServiceJson.Remove(nameof(CoreRuntimeBinding.ListenerBindings));

        CoreRuntimeBinding? oldBinding = JsonSerializer.Deserialize<CoreRuntimeBinding>(oldServiceJson.ToJsonString());

        Assert.IsNotNull(oldBinding);
        Assert.IsNull(oldBinding.ListenerBindings);
        Assert.AreEqual(2, ServiceProtocol.CurrentVersion, "The additive binding metadata must not silently bump the IPC protocol.");
    }

    [TestMethod]
    public void OlderListenerRecordDefaultsNewDualModeFieldToFalse()
    {
        JsonObject oldRecordJson = JsonSerializer.SerializeToNode(new RuntimeListenerBinding(
            "controller",
            "127.0.0.1",
            19091,
            RuntimeListenerTransport.Tcp))!.AsObject();
        oldRecordJson.Remove(nameof(RuntimeListenerBinding.DualMode));

        RuntimeListenerBinding? oldRecord = JsonSerializer.Deserialize<RuntimeListenerBinding>(oldRecordJson.ToJsonString());

        Assert.IsNotNull(oldRecord);
        Assert.IsFalse(oldRecord.DualMode);
        Assert.AreEqual("127.0.0.1", oldRecord.Address);
        Assert.AreEqual(RuntimeListenerTransport.Tcp, oldRecord.Transport);
    }

    private sealed class OldRunningService(CoreRuntimeBinding? binding) : IServicePipeClient
    {
        public Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ServiceResponse(Guid.NewGuid(), true,
                TunState.Off, Core: command == ServiceCommand.StopCore ? CoreState.Stopped : CoreState.Running,
                ProtocolVersion: ServiceProtocol.CurrentVersion, RuntimeBinding: binding));
    }

    private sealed class CountingControllerHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri?.AbsolutePath == "/version"
                    ? "{\"version\":\"v1.19.31\"}" : "{\"mode\":\"rule\",\"tun\":{\"enable\":false}}")
            });
        }
    }
}
