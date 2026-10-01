using System.Net;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeSystemProxyAddressTests
{
    [TestMethod]
    [DataRow("127.0.0.1", false, true)]
    [DataRow("0.0.0.0", false, true)]
    [DataRow("::", true, true)]
    [DataRow("127.0.0.2", false, false)]
    [DataRow("192.0.2.10", false, false)]
    [DataRow("::1", false, false)]
    [DataRow("::", false, false)]
    [DataRow("invalid", false, false)]
    public Task ManualEnableChecksTheActualSystemProxyAddress(string address, bool dualMode, bool eligible) =>
        VerifyAddressAsync(address, dualMode, eligible, recover: false);

    [TestMethod]
    [DataRow("127.0.0.1", false, true)]
    [DataRow("0.0.0.0", false, true)]
    [DataRow("::", true, true)]
    [DataRow("127.0.0.2", false, false)]
    [DataRow("192.0.2.10", false, false)]
    [DataRow("::1", false, false)]
    [DataRow("::", false, false)]
    public Task AppRecoveryChecksTheActualSystemProxyAddress(string address, bool dualMode, bool eligible) =>
        VerifyAddressAsync(address, dualMode, eligible, recover: true);

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task MissingOrMismatchedMixedTcpEvidenceCannotEnableProxy(bool udpOnly, bool wrongPort)
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppSettings settings = RuntimeTestHelpers.CreatePortSafeSettings();
        FakeSystemProxyController proxy = new(SystemProxyState.Off);
        using ControllerHandler handler = new();
        using HttpClient http = new(handler);
        MihomoApiClient api = new(http, new Uri($"http://127.0.0.1:{settings.ControllerPort}/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program")),
            null, null, new TestSettingsStore(settings), proxy);
        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: true);
            CoreRuntimeBinding binding = runtime.ActiveRuntimeBinding!;
            runtime.SetRuntimeBindingForTesting(binding with
            {
                ListenerBindings = udpOnly || wrongPort
                    ? [new RuntimeListenerBinding("mixed-tcp", "127.0.0.1", wrongPort ? settings.HttpPort : settings.MixedPort,
                        udpOnly ? RuntimeListenerTransport.Udp : RuntimeListenerTransport.Tcp)]
                    : null
            });

            await runtime.SetSystemProxyAsync(true);

            Assert.AreEqual(0, proxy.EnableCount);
            Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
            Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task VerifyAddressAsync(string address, bool dualMode, bool eligible, bool recover)
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppSettings settings = RuntimeTestHelpers.CreatePortSafeSettings() with { SystemProxyEnabled = recover };
        CoreRuntimeBinding binding = RuntimeTestHelpers.CreateRuntimeBinding(settings);
        binding = binding with
        {
            ListenerBindings = binding.ListenerBindings!.Select(listener => listener.Name == "controller" ? listener
                : listener with { Address = address, DualMode = dualMode }).ToArray()
        };
        FakeSystemProxyController proxy = new(SystemProxyState.Off);
        using ControllerHandler handler = new();
        using HttpClient http = new(handler);
        MihomoApiClient api = new(http, new Uri($"http://127.0.0.1:{settings.ControllerPort}/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program")),
            null, new RunningService(binding), new TestSettingsStore(settings),
            proxy, controllerApiFactory: () => api);
        try
        {
            if (recover)
            {
                await runtime.InitializeAsync();
            }
            else
            {
                runtime.AttachControllerForTesting(api, usingServiceCore: true);
                runtime.SetRuntimeBindingForTesting(binding);
                await runtime.SetSystemProxyAsync(true);
            }

            Assert.AreEqual(eligible ? 1 : 0, proxy.EnableCount);
            Assert.AreEqual(eligible ? SystemProxyState.On : SystemProxyState.Off, runtime.Snapshot.SystemProxy);
            Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State, "Proxy eligibility must not revoke core health.");
            Assert.IsTrue(runtime.IsCoreHealthConfirmedForTesting);
            Assert.IsTrue(runtime.Settings.SystemProxyEnabled);
            if (eligible)
            {
                Assert.AreEqual(binding.MixedPort, proxy.EnabledPorts.Single());
            }
            else
            {
                StringAssert.Contains(runtime.Snapshot.ErrorMessage, "127.0.0.1", StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RunningService(CoreRuntimeBinding binding) : IServicePipeClient
    {
        public Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ServiceResponse(Guid.NewGuid(), true, TunState.Off,
                Core: CoreState.Running, ProtocolVersion: ServiceProtocol.CurrentVersion, RuntimeBinding: binding));
        }
    }

    private sealed class ControllerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri?.AbsolutePath switch
                {
                    "/version" => "{\"version\":\"v1.19.31\"}",
                    "/configs" => "{\"mode\":\"rule\",\"allow-lan\":false,\"ipv6\":false,\"tun\":{\"enable\":false}}",
                    "/proxies" => "{\"proxies\":{}}",
                    "/rules" => "{\"rules\":[]}",
                    "/connections" => "{\"connections\":[]}",
                    _ => "{}"
                })
            });
    }
}
