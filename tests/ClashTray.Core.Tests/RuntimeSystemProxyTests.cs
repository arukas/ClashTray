using System.Net;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeSystemProxyTests
{
    [TestMethod]
    public async Task ChangingProxyBindingRestoresAndReappliesOwnedSystemProxy()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new TestSettingsStore(new AppSettings(
            BypassList: "old",
            SystemProxyEnabled: true));
        FakeSystemProxyController proxy = new FakeSystemProxyController(SystemProxyState.On);
        using RuntimeControllerHandler handler = new RuntimeControllerHandler();
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, settings, proxy);

        try
        {
            await runtime.InitializeAsync();
            proxy.SetState(SystemProxyState.On);
            int disableCountBeforeChange = proxy.DisableCount;
            runtime.AttachControllerForTesting(api, usingServiceCore: false);

            await runtime.UpdateSettingsAsync(new AppSettingsPatch(BypassList: SettingPatchValue.Set("new")));

            Assert.AreEqual("new", runtime.Settings.BypassList);
            Assert.AreEqual(disableCountBeforeChange + 1, proxy.DisableCount);
            Assert.AreEqual(1, proxy.EnableCount);
            Assert.AreEqual(SystemProxyState.On, proxy.State);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task EnablingSystemProxyBeforeCoreHealthOnlyStoresPreference()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        FakeSystemProxyController proxy = new FakeSystemProxyController(SystemProxyState.Off);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, null, proxy);

        try
        {
            await runtime.SetSystemProxyAsync(true);

            Assert.IsTrue(runtime.Settings.SystemProxyEnabled);
            Assert.AreEqual(0, proxy.EnableCount);
            Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task MixedListenerNotReadyKeepsSystemProxyOffEvenWhenControllerIsHealthy()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        FakeSystemProxyController proxy = new(SystemProxyState.Off);
        using RuntimeControllerHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:19090/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            null,
            proxy,
            controllerApiFactory: () => api);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            CoreRuntimeBinding binding = runtime.ActiveRuntimeBinding!;
            runtime.SetRuntimeBindingForTesting(binding with
            {
                MixedPort = 0,
                MixedReady = false
            });

            await runtime.SetSystemProxyAsync(true);

            Assert.AreEqual(0, proxy.EnableCount);
            Assert.AreEqual(SystemProxyState.Off, proxy.State);
            Assert.IsTrue(runtime.Settings.SystemProxyEnabled);
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "Mixed", StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ManualSystemProxyEnableUsesConfirmedBindingPortWhenSavedPreferenceDiffers()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        AppSettings settings = RuntimeTestHelpers.CreatePortSafeSettings();
        FakeSystemProxyController proxy = new(SystemProxyState.Off);
        using RuntimeControllerHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri($"http://127.0.0.1:{settings.ControllerPort}/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            new TestSettingsStore(settings),
            proxy,
            controllerApiFactory: () => api);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: true);
            CoreRuntimeBinding previousBinding = runtime.ActiveRuntimeBinding!;
            CoreRuntimeBinding binding = previousBinding with
            {
                MixedPort = 58991,
                MixedReady = true,
                ListenerBindings = previousBinding.ListenerBindings!.Select(listener =>
                    listener.Name.StartsWith("mixed-", StringComparison.Ordinal)
                        ? listener with { Port = 58991 } : listener).ToArray()
            };
            runtime.SetRuntimeBindingForTesting(binding);

            await runtime.SetSystemProxyAsync(true);

            Assert.AreEqual(58991, proxy.EnabledPorts.Single());
            Assert.AreNotEqual(settings.MixedPort, proxy.EnabledPorts.Single());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task AppReopenRestoresSystemProxyToConfirmedServiceBindingAfterSettingsWereSavedBeforeRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        AppSettings persistedSettings = RuntimeTestHelpers.CreatePortSafeSettings() with
        {
            SystemProxyEnabled = true
        };
        const int runningCoreMixedPort = 58991;
        CoreRuntimeBinding runningBinding = RuntimeTestHelpers.CreateRuntimeBinding(
            persistedSettings with { MixedPort = runningCoreMixedPort });
        RunningServiceStatusClient service = new(runningBinding);
        FakeSystemProxyController proxy = new(SystemProxyState.Off);
        using AppReopenControllerHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri($"http://127.0.0.1:{persistedSettings.ControllerPort}/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            service,
            new TestSettingsStore(persistedSettings),
            proxy,
            controllerApiFactory: () => api);

        try
        {
            await runtime.InitializeAsync();

            Assert.AreEqual(0, service.RestartCount, "App recovery should attach to the still-running service core without starting a restart.");
            Assert.AreEqual(persistedSettings.MixedPort, runtime.Settings.MixedPort, "The newer saved preference remains intact.");
            Assert.AreEqual(runningCoreMixedPort, runtime.ActiveRuntimeBinding!.MixedPort);
            Assert.AreEqual(runningCoreMixedPort, proxy.EnabledPorts.Single(), "The proxy must target the confirmed running listener until core reconciliation.");
            Assert.AreNotEqual(persistedSettings.MixedPort, proxy.EnabledPorts.Single());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CoreUnavailableRestoresActualProxyButPreservesPreference()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new TestSettingsStore(new AppSettings(SystemProxyEnabled: true));
        FakeSystemProxyController proxy = new FakeSystemProxyController(SystemProxyState.On);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, settings, proxy);

        try
        {
            await runtime.InitializeAsync();
            proxy.SetState(SystemProxyState.On);
            await runtime.RevokeSystemProxyForTestingAsync();

            Assert.IsTrue(runtime.Settings.SystemProxyEnabled);
            Assert.AreEqual(SystemProxyState.Off, proxy.State);
            Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
            Assert.IsGreaterThanOrEqualTo(2, proxy.DisableCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RunningServiceStatusClient(CoreRuntimeBinding binding) : IServicePipeClient
    {
        public int RestartCount { get; private set; }

        public Task<ServiceResponse> SendAsync(
            ServiceCommand command,
            string? payload = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == ServiceCommand.RestartCore)
            {
                RestartCount++;
            }

            return Task.FromResult(new ServiceResponse(
                Guid.NewGuid(),
                true,
                TunState.Off,
                Core: command == ServiceCommand.StopCore ? CoreState.Stopped : CoreState.Running,
                ProtocolVersion: ServiceProtocol.CurrentVersion,
                RuntimeBinding: command == ServiceCommand.StopCore ? null : binding));
        }
    }

    private sealed class AppReopenControllerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string body = path switch
            {
                "/version" => "{\"version\":\"v1.19.31\"}",
                "/configs" => "{\"mode\":\"rule\",\"allow-lan\":false,\"ipv6\":false,\"tun\":{\"enable\":false}}",
                "/proxies" => "{\"proxies\":{}}",
                "/traffic" => "{\"upTotal\":0,\"downTotal\":0,\"up\":0,\"down\":0}",
                "/memory" => "{\"inuse\":0}",
                "/connections" => "{\"connections\":[]}",
                "/rules" => "{\"rules\":[]}",
                "/providers/proxies" => "{\"providers\":{}}",
                "/providers/rules" => "{\"providers\":{}}",
                _ => "{}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
