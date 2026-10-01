using System.Net;
using System.Net.Sockets;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeBindingValidationTests
{
    [TestMethod]
    [DataRow("controller-only")]
    [DataRow("http-missing")]
    [DataRow("socks-udp-missing")]
    [DataRow("mixed-port-mismatch")]
    [DataRow("mixed-address-mismatch")]
    [DataRow("duplicate-name")]
    [DataRow("duplicate-endpoint")]
    [DataRow("null-listener")]
    [DataRow("invalid-address")]
    [DataRow("invalid-transport")]
    [DataRow("invalid-dual-mode")]
    [DataRow("undeclared-listener")]
    [DataRow("additional-missing")]
    [DataRow("additional-mismatch")]
    [DataRow("additional-null")]
    [DataRow("too-many-listeners")]
    [DataRow("readiness-mismatch")]
    [DataRow("relative-path")]
    [DataRow("incomplete-without-warning")]
    [DataRow("primary-port-collision")]
    public void MalformedContractsAreRejectedBeforeAnyOwnershipQuery(string scenario)
    {
        CoreRuntimeBinding original = RuntimeTestHelpers.CreateRuntimeBinding(new AppSettings());
        RuntimeListenerBinding[] listeners = original.ListenerBindings!.ToArray();
        RuntimeListenerBinding extra = new("dns-udp", "127.0.0.1", 15353, RuntimeListenerTransport.Udp);
        CoreRuntimeBinding malformed = scenario switch
        {
            "controller-only" => original with { ListenerBindings = [listeners[0]] },
            "http-missing" => original with { ListenerBindings = listeners.Where(item => item.Name != "http-tcp").ToArray() },
            "socks-udp-missing" => original with { ListenerBindings = listeners.Where(item => item.Name != "socks-udp").ToArray() },
            "mixed-port-mismatch" => original with { ListenerBindings = Change("mixed-tcp", item => item with { Port = 18000 }) },
            "mixed-address-mismatch" => original with { ListenerBindings = Change("mixed-udp", item => item with { Address = "127.0.0.2" }) },
            "duplicate-name" => original with { ListenerBindings = [.. listeners, listeners[1] with { Port = 18000 }] },
            "duplicate-endpoint" => original with { ListenerBindings = [.. listeners, listeners[1] with { Name = "duplicate-http" }] },
            "null-listener" => original with { ListenerBindings = [.. listeners, null!] },
            "invalid-address" => original with { ListenerBindings = Change("mixed-tcp", item => item with { Address = "localhost" }) },
            "invalid-transport" => original with { ListenerBindings = Change("mixed-tcp", item => item with { Transport = (RuntimeListenerTransport)99 }) },
            "invalid-dual-mode" => original with { ListenerBindings = Change("mixed-tcp", item => item with { DualMode = true }) },
            "undeclared-listener" => original with { ListenerBindings = [.. listeners, extra] },
            "additional-missing" => original with { AdditionalListeners = [extra] },
            "additional-mismatch" => original with { ListenerBindings = [.. listeners, extra], AdditionalListeners = [extra with { Port = 15354 }] },
            "additional-null" => original with { AdditionalListeners = [null!] },
            "too-many-listeners" => original with { ListenerBindings = Enumerable.Repeat(listeners[0], 257).ToArray() },
            "readiness-mismatch" => original with { MixedReady = false },
            "relative-path" => original with { ExecutablePath = "mihomo.exe" },
            "incomplete-without-warning" => original with { ListenerPlanComplete = false },
            "primary-port-collision" => original with { HttpPort = original.ControllerPort },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        int queryCount = 0;
        bool owned = RuntimeBindingValidator.AreListenersOwned(malformed, _ =>
        {
            queryCount++;
            return new ListenerOwnerObservation(ListenerOwnerState.Owned);
        });
        Assert.IsFalse(owned);
        Assert.AreEqual(RuntimeBindingValidationFailure.Malformed, RuntimeBindingValidator.Validate(malformed).Failure);
        Assert.AreEqual(0, queryCount);

        RuntimeListenerBinding[] Change(string name, Func<RuntimeListenerBinding, RuntimeListenerBinding> update) =>
            listeners.Select(item => item.Name == name ? update(item) : item).ToArray();
    }

    [TestMethod]
    [DataRow("127.0.0.1", false)]
    [DataRow("0.0.0.0", false)]
    [DataRow("::", true)]
    [DataRow("::1", false)]
    [DataRow("fe80::1%1", false)]
    public void CompleteContractsPreserveAddressFamilyAndScope(string address, bool dualMode)
    {
        CoreRuntimeBinding original = RuntimeTestHelpers.CreateRuntimeBinding(new AppSettings());
        CoreRuntimeBinding binding = original with
        {
            ListenerBindings = original.ListenerBindings!.Select(item => item.Name == "controller" ? item
                : item with { Address = address, DualMode = dualMode }).ToArray()
        };
        RuntimeBindingValidationResult result = RuntimeBindingValidator.Validate(binding);
        Assert.IsTrue(result.IsValid, result.Detail);
        Assert.AreEqual(IPAddress.Parse(address), result.Listeners.Single(item => item.Name == "mixed-udp").Address);
        Assert.IsTrue(RuntimeBindingValidator.AreListenersOwned(binding, _ => new(ListenerOwnerState.Owned)));
    }

    [TestMethod]
    public void DisabledPortsAndDeclaredAdditionalListenersHaveAnExactContract()
    {
        CoreRuntimeBinding original = RuntimeTestHelpers.CreateRuntimeBinding(new AppSettings());
        RuntimeListenerBinding dns = new("dns-udp", "127.0.0.2", 15353, RuntimeListenerTransport.Udp);
        CoreRuntimeBinding binding = original with
        {
            HttpPort = 0, HttpReady = false, SocksPort = 0, SocksReady = false,
            AdditionalListeners = [dns], ListenerPlanComplete = false, ListenerPlanWarning = "unrecognized YAML preserved",
            ListenerBindings = [.. original.ListenerBindings!.Where(item => item.Name is "controller" or "mixed-tcp" or "mixed-udp"), dns]
        };
        Assert.IsTrue(RuntimeBindingValidator.Validate(binding).IsValid);
        Assert.IsFalse(RuntimeBindingValidator.AreListenersOwned(binding, item =>
            new(item.Name == "dns-udp" ? ListenerOwnerState.Unknown : ListenerOwnerState.Owned)));
    }

    [TestMethod]
    public async Task ControllerOnlyMetadataCannotConfirmProxyHealth()
    {
        using TcpListener controller = new(IPAddress.Loopback, 0);
        controller.Start();
        AppSettings settings = RuntimeTestHelpers.CreatePortSafeSettings() with
        {
            ControllerPort = ((IPEndPoint)controller.LocalEndpoint).Port
        };
        CoreRuntimeBinding binding = RuntimeTestHelpers.CreateRuntimeBinding(settings) with
        {
            ListenerBindings = [new RuntimeListenerBinding("controller", "127.0.0.1",
                settings.ControllerPort, RuntimeListenerTransport.Tcp)]
        };
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        using BindingControllerHandler handler = new(settings);
        await using ClashTrayRuntime runtime = new(new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program")),
            null, new RunningBindingService(binding), new TestSettingsStore(settings),
            new FakeSystemProxyController(SystemProxyState.Off), controllerHttpMessageHandler: handler);
        try
        {
            await runtime.InitializeAsync();
            Assert.IsFalse(runtime.IsCoreHealthConfirmedForTesting,
                "Owned controller metadata alone cannot prove the required HTTP/SOCKS/Mixed listeners.");
            Assert.AreEqual(0, handler.WriteCount);
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

    private sealed class RunningBindingService(CoreRuntimeBinding binding) : IServicePipeClient
    {
        public Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ServiceResponse(Guid.NewGuid(), true,
                TunState.Off, Core: command == ServiceCommand.StopCore ? CoreState.Stopped : CoreState.Running,
                ProtocolVersion: ServiceProtocol.CurrentVersion, RuntimeBinding: binding));
    }

    private sealed class BindingControllerHandler(AppSettings settings) : HttpMessageHandler
    {
        private int _writeCount;
        public int WriteCount => Volatile.Read(ref _writeCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                Interlocked.Increment(ref _writeCount);
            }

            string body = request.RequestUri?.AbsolutePath switch
            {
                "/version" => "{\"version\":\"v1.19.31\"}",
                "/configs" => $"{{\"port\":{settings.HttpPort},\"socks-port\":{settings.SocksPort},\"mixed-port\":{settings.MixedPort},\"mode\":\"rule\",\"allow-lan\":false,\"ipv6\":true,\"tun\":{{\"enable\":false}}}}",
                "/proxies" => "{\"proxies\":{}}",
                "/connections" => "{\"connections\":[]}",
                "/rules" => "{\"rules\":[]}",
                _ => "{}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
