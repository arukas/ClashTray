using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeStateTests
{
    private static readonly string[] SelectionThenClose = ["select", "close"];

    [TestMethod]
    public async Task MetricFailureKeepsLastValuesAndDoesNotStopConfirmedCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using RuntimeControllerHandler handler = new RuntimeControllerHandler();
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), "test-secret");
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            await runtime.RefreshControllerDataForTestingAsync();

            Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State);
            Assert.IsTrue(runtime.Snapshot.Core.TrafficAvailable);
            Assert.IsTrue(runtime.Snapshot.Core.MemoryAvailable);
            Assert.AreEqual(11, runtime.Snapshot.Core.UploadBytes);
            Assert.AreEqual(22, runtime.Snapshot.Core.MemoryBytes);

            handler.FailMetrics = true;
            await runtime.RefreshControllerDataForTestingAsync();

            Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State);
            Assert.IsFalse(runtime.Snapshot.Core.TrafficAvailable);
            Assert.IsFalse(runtime.Snapshot.Core.MemoryAvailable);
            Assert.AreEqual(11, runtime.Snapshot.Core.UploadBytes);
            Assert.AreEqual(22, runtime.Snapshot.Core.MemoryBytes);
            Assert.IsTrue(runtime.Snapshot.Logs.Any(log => log.Message.Contains("/traffic", StringComparison.Ordinal)));
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
    public void BoundedLogBufferFoldsAdjacentIdenticalLines()
    {
        BoundedLogBuffer buffer = new BoundedLogBuffer(2);
        buffer.Add(new LogEntry(DateTimeOffset.UtcNow, "mihomo", "info", "same"));
        buffer.Add(new LogEntry(DateTimeOffset.UtcNow, "mihomo", "info", "same"));
        buffer.Add(new LogEntry(DateTimeOffset.UtcNow, "mihomo", "error", "different"));

        IReadOnlyList<LogEntry> snapshot = buffer.Snapshot();
        Assert.AreEqual(2, snapshot.Count);
        Assert.AreEqual(2, snapshot[0].RepeatCount);
        Assert.AreEqual("different", snapshot[1].Message);
    }

    [TestMethod]
    public void BoundedLogBufferReusesSnapshotUntilChanged()
    {
        BoundedLogBuffer buffer = new BoundedLogBuffer(2);

        IReadOnlyList<LogEntry> first = buffer.Snapshot();
        IReadOnlyList<LogEntry> second = buffer.Snapshot();

        Assert.AreSame(first, second);
        buffer.Add(new LogEntry(DateTimeOffset.UtcNow, "mihomo", "info", "new"));
        IReadOnlyList<LogEntry> changed = buffer.Snapshot();

        Assert.AreNotSame(first, changed);
        Assert.AreSame(changed, buffer.Snapshot());
    }

    [TestMethod]
    public void MihomoDataParserReadsMemoryAndTunState()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"inuse\": 4096, \"allow-lan\": true, \"ipv6\": false, \"tun\": {\"enable\": true}}");

        Assert.AreEqual(4096, MihomoDataParser.ParseMemoryBytes(document));
        Assert.AreEqual(true, MihomoDataParser.ParseAllowLan(document));
        Assert.AreEqual(false, MihomoDataParser.ParseIpv6(document));
        Assert.AreEqual(true, MihomoDataParser.ParseTunEnabled(document));
    }

    [TestMethod]
    public async Task RunningCoreUsesProgramNetworkSettingsOverControllerConfig()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using RuntimeControllerHandler handler = new RuntimeControllerHandler();
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), "test-secret");
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            await runtime.UpdateSettingsAsync(runtime.Settings with { AllowLan = true, Ipv6 = false });

            Assert.IsTrue(handler.AllowLan);
            Assert.IsFalse(handler.Ipv6);
            Assert.AreEqual(1, handler.NetworkPatchCount);
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
    public async Task RefreshWithoutRuleAndProviderDataSkipsTheirEndpoints()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using RuntimeControllerHandler handler = new RuntimeControllerHandler();
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            await runtime.RefreshControllerDataForTestingAsync();
            while (handler.RequestedPaths.TryDequeue(out _))
            {
            }

            await runtime.RefreshControllerDataForTestingAsync(includeRulesAndProviders: false);

            Assert.IsFalse(
                handler.RequestedPaths.Any(path => path is "/rules" or "/providers/proxies" or "/providers/rules"));
            Assert.IsTrue(handler.RequestedPaths.Any(path => path == "/traffic"));
            Assert.IsTrue(handler.RequestedPaths.Any(path => path == "/connections"));
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
    public async Task UnchangedControllerDataReusesListSnapshots()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using RuntimeControllerHandler handler = new RuntimeControllerHandler();
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            await runtime.RefreshControllerDataForTestingAsync();
            RuntimeSnapshot first = runtime.Snapshot;

            await runtime.RefreshControllerDataForTestingAsync();
            RuntimeSnapshot second = runtime.Snapshot;

            Assert.AreSame(first.ProxyGroups, second.ProxyGroups);
            Assert.AreSame(first.ProxyNodes, second.ProxyNodes);
            Assert.AreSame(first.Connections, second.Connections);
            Assert.AreSame(first.Rules, second.Rules);
            Assert.AreSame(first.Providers, second.Providers);
            Assert.AreSame(first.RuleProviders, second.RuleProviders);
            Assert.AreSame(first.Logs, second.Logs);
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
    public async Task FailedRunningCoreSettingsRestartRestoresPreviousSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new TestSettingsStore(new AppSettings());
        using RuntimeControllerHandler handler = new RuntimeControllerHandler();
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, settings);

        try
        {
            AppSettings previous = runtime.Settings;
            runtime.AttachControllerForTesting(api, usingServiceCore: false);

            InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => runtime.UpdateSettingsAsync(previous with { HttpPort = previous.HttpPort + 1 }));

            Assert.AreEqual(previous, runtime.Settings);
            Assert.AreEqual(previous, settings.Settings);
            StringAssert.Contains(exception.Message, "设置应用失败", StringComparison.Ordinal);

            await runtime.UpdateSettingsAsync(previous with { Theme = "dark" });
            Assert.AreEqual("dark", runtime.Settings.Theme);
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

            await runtime.UpdateSettingsAsync(runtime.Settings with { BypassList = "new" });

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

    [TestMethod]
    public async Task InitializeSurfacesRecoveredSettingsWarning()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.SettingsFile, "{\"logLevel\":\"trace\"}");
        FakeSystemProxyController proxy = new FakeSystemProxyController(SystemProxyState.Off);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, null, proxy);

        try
        {
            await runtime.InitializeAsync();

            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "设置文件已损坏", StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(paths.SettingsFile));
            Assert.AreEqual(1, Directory.GetFiles(paths.LocalRoot, "settings.json.corrupt-*").Length);
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
    public async Task SwitchingProxyOptionDisconnectsOnlyAfterSuccessfulSelection()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using ProxySwitchHandler handler = new ProxySwitchHandler();
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            await runtime.UpdateSettingsAsync(runtime.Settings with
            {
                DisconnectConnectionsAfterProxySwitch = true
            });
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            await runtime.RefreshControllerDataForTestingAsync();

            await runtime.SelectProxyAsync("Auto", "new");

            CollectionAssert.AreEqual(SelectionThenClose, handler.Operations.Take(2).ToArray());
            Assert.AreEqual("new", runtime.Snapshot.ProxyGroups.Single(group => group.Name == "Auto").Current);
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
    public async Task FailedDisconnectReportsPartialSuccessWithoutChangingSelection()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using ProxySwitchHandler handler = new ProxySwitchHandler { FailCloseAll = true };
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            await runtime.UpdateSettingsAsync(runtime.Settings with
            {
                DisconnectConnectionsAfterProxySwitch = true
            });
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            await runtime.RefreshControllerDataForTestingAsync();

            InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                runtime.SelectProxyAsync("Auto", "new"));

            Assert.AreEqual("节点已切换，但未能断开旧连接。", exception.Message);
            CollectionAssert.AreEqual(SelectionThenClose, handler.Operations.Take(2).ToArray());
            Assert.AreEqual("new", runtime.Snapshot.ProxyGroups.Single(group => group.Name == "Auto").Current);
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "节点已切换", StringComparison.Ordinal);
            Assert.IsTrue(runtime.Snapshot.Logs.Any(log => log.Message.Contains("未能断开旧连接", StringComparison.Ordinal)));
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
    public async Task ControllerOperationsAreSerialized()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using DelayControllerHandler handler = new DelayControllerHandler(holdFirstRequest: true);
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            Task<int?> first = runtime.TestProxyDelayAsync("node");
            await handler.FirstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Task<int?> second = runtime.TestProxyDelayAsync("node");
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            Assert.AreEqual(1, handler.MaxInFlight);
            handler.ReleaseFirstRequest();
            int?[] results = await Task.WhenAll(first, second);

            CollectionAssert.AreEqual(new int?[] { 10, 10 }, results);
            Assert.AreEqual(1, handler.MaxInFlight);
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
    public async Task ControllerOperationRejectsResultFromReplacedSession()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using DelayControllerHandler oldHandler = new DelayControllerHandler(holdFirstRequest: false);
        using DelayControllerHandler replacementHandler = new DelayControllerHandler(holdFirstRequest: false);
        using HttpClient oldHttpClient = new HttpClient(oldHandler);
        using HttpClient replacementHttpClient = new HttpClient(replacementHandler);
        MihomoApiClient oldApi = new MihomoApiClient(oldHttpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        MihomoApiClient replacementApi = new MihomoApiClient(
            replacementHttpClient,
            new Uri("http://127.0.0.1:9090/"),
            string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            oldHandler.BeforeFirstRequest = () =>
                runtime.AttachControllerForTesting(replacementApi, usingServiceCore: false);
            runtime.AttachControllerForTesting(oldApi, usingServiceCore: false);

            InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => runtime.TestProxyDelayAsync("node"));

            Assert.AreEqual("测速期间核心会话已切换，请重新测速。", exception.Message);
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
    public void MihomoDataParserPreservesZeroTrafficTotals()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"upTotal\":0,\"downTotal\":0,\"up\":128,\"down\":256}");

        TrafficSnapshot traffic = MihomoDataParser.ParseTraffic(document);

        Assert.AreEqual(0, traffic.UploadBytes);
        Assert.AreEqual(0, traffic.DownloadBytes);
        Assert.AreEqual(128, traffic.UploadBytesPerSecond);
        Assert.AreEqual(256, traffic.DownloadBytesPerSecond);
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderReplacesManagedSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(source, "external-controller: 0.0.0.0:9999\nsecret: old\nmixed-port: 1111\nallow-lan: true\nipv6: true\nproxies: []\n");

        try
        {
            await RuntimeConfigBuilder.BuildAsync(source, destination, new AppSettings(ControllerPort: 9191, MixedPort: 8899));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, "external-controller: 127.0.0.1:9191", StringComparison.Ordinal);
            StringAssert.Contains(generated, "mixed-port: 8899", StringComparison.Ordinal);
            StringAssert.Contains(generated, "allow-lan: false", StringComparison.Ordinal);
            StringAssert.Contains(generated, "ipv6: false", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("0.0.0.0:9999", StringComparison.Ordinal));
            Assert.IsFalse(generated.Contains("secret: old", StringComparison.Ordinal));
            StringAssert.Contains(generated, "secret: ''", StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(Path.Combine(paths.LocalRoot, "controller-secret.bin")));
            Assert.IsFalse(generated.Contains("allow-lan: true", StringComparison.Ordinal));
            Assert.IsFalse(generated.Contains("ipv6: true", StringComparison.Ordinal));
            Assert.AreEqual(
                0,
                Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "*.tmp").Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderPreservesInlineProxyGroupWithoutAddingFilters()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(
            source,
            """
            proxies:
              - { name: US-1, type: direct }
              - { name: US-2, type: direct }
              - { name: JP-1, type: direct }
            proxy-groups:
              - { name: 美国常用, type: select, proxies: [自动选择, US-1] }
            """);

        try
        {
            await RuntimeConfigBuilder.BuildAsync(source, destination, new AppSettings(ControllerPort: 9191));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, "- { name: 美国常用, type: select, proxies: [自动选择, US-1] }", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("include-all:", StringComparison.Ordinal));
            Assert.IsFalse(generated.Contains("filter:", StringComparison.Ordinal));
            string original = await File.ReadAllTextAsync(source);
            StringAssert.StartsWith(generated, original.ReplaceLineEndings(Environment.NewLine), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderPreservesExistingProxyGroupFilter()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(
            source,
            """
            proxies:
              - name: US-1
                type: direct
            proxy-groups:
              - name: 美国常用
                type: select
                include-all: false
                filter: old-filter
                proxies:
                  - 自动选择
            """);

        try
        {
            await RuntimeConfigBuilder.BuildAsync(source, destination, new AppSettings(ControllerPort: 9191));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, "include-all: false", StringComparison.Ordinal);
            StringAssert.Contains(generated, "filter: old-filter", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("include-all: true", StringComparison.Ordinal));
            string original = await File.ReadAllTextAsync(source);
            StringAssert.StartsWith(generated, original.ReplaceLineEndings(Environment.NewLine), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderPreservesNestedPortFields()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(
            source,
            "proxies:\n  - name: node\n    server: example.com\n    port: 443\nport: 1000\n");

        try
        {
            await RuntimeConfigBuilder.BuildAsync(source, destination, new AppSettings(HttpPort: 8899, ControllerPort: 9191));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, "    port: 443", StringComparison.Ordinal);
            StringAssert.Contains(generated, "port: 8899", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("port: 1000", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderProgramTunSettingOverridesProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(
            source,
            "tun:\n  enable: true\n  stack: system\nproxies: []\n");

        try
        {
            await RuntimeConfigBuilder.BuildAsync(
                source,
                destination,
                new AppSettings(ControllerPort: 9191, TunEnabled: false));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, $"tun:{Environment.NewLine}  enable: false{Environment.NewLine}  stack: system", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("enable: true", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderProgramTunSettingOverridesInlineProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(source, "tun: { enable: true, stack: system }\nproxies: []\n");

        try
        {
            await RuntimeConfigBuilder.BuildAsync(
                source,
                destination,
                new AppSettings(ControllerPort: 9191, TunEnabled: false));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, "tun: { enable: false, stack: system }", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("enable: true", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CoreUpdaterRejectsUnapprovedSourceBeforeNetworkAccess()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        CoreUpdater updater = new CoreUpdater(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => updater.DownloadAndInstallAsync(
                new CoreUpdateManifest("v0", new Uri("https://example.com/mihomo.zip"), new string('0', 64))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeRejectsInvalidSubscriptionRefreshInterval()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => runtime.UpdateSettingsAsync(
                runtime.Settings with { SubscriptionRefreshHours = 0 }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeRejectsInvalidPortSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => runtime.UpdateSettingsAsync(
                runtime.Settings with { HttpPort = 0 }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeRejectsDuplicatePorts()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => runtime.UpdateSettingsAsync(
                runtime.Settings with { HttpPort = runtime.Settings.MixedPort }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeRejectsInvalidLogLevel()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => runtime.UpdateSettingsAsync(
                runtime.Settings with { LogLevel = "trace" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeInitializationKeepsOnlyOneActiveConfiguration()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationStore store = new ConfigurationStore(paths);
        ConfigurationProfile first = await store.ImportLocalAsync(
            await WriteConfigAsync(root, "first.yaml"),
            "first");
        ConfigurationProfile second = await store.ImportLocalAsync(
            await WriteConfigAsync(root, "second.yaml"),
            "second");
        await WriteMetadataAsync(paths, first with { IsActive = true });
        await WriteMetadataAsync(paths, second with { IsActive = true });

        try
        {
            await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);
            await runtime.InitializeAsync();

            Assert.AreEqual(1, runtime.Snapshot.Configurations.Count(configuration => configuration.IsActive));
            Assert.AreEqual(first.Id, runtime.Snapshot.Configurations.Single(configuration => configuration.IsActive).Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> WriteConfigAsync(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, $"# {name}\nmixed-port: 7890\nproxies: []\n");
        return path;
    }

    private static async Task WriteMetadataAsync(AppPaths paths, ConfigurationProfile profile)
    {
        await File.WriteAllTextAsync(
            Path.Combine(paths.ConfigurationsRoot, $"{profile.Id}.json"),
            System.Text.Json.JsonSerializer.Serialize(profile));
    }

    private sealed class FakeSystemProxyController : ISystemProxyController
    {
        public FakeSystemProxyController(SystemProxyState initialState)
        {
            State = initialState;
        }

        public SystemProxyState State { get; private set; }

        public int EnableCount { get; private set; }

        public int DisableCount { get; private set; }

        public SystemProxyState DetectState() => State;

        public Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default)
        {
            EnableCount++;
            State = SystemProxyState.On;
            return Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken = default)
        {
            DisableCount++;
            State = SystemProxyState.Off;
            return Task.CompletedTask;
        }

        public void SetState(SystemProxyState state) => State = state;
    }

    private sealed class TestSettingsStore : ISettingsStore
    {
        public TestSettingsStore(AppSettings settings)
        {
            Settings = settings;
        }

        public AppSettings Settings { get; private set; }

        public Task<SettingsLoadResult> LoadWithStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SettingsLoadResult(Settings, SettingsLoadStatus.Loaded, null));

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class ProxySwitchHandler : HttpMessageHandler
    {
        public List<string> Operations { get; } = [];

        public bool FailCloseAll { get; init; }

        public string CurrentProxy { get; private set; } = "old";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Put && path.StartsWith("/proxies/", StringComparison.Ordinal))
            {
                Operations.Add("select");
                CurrentProxy = "new";
                return Task.FromResult(JsonResponse("{}"));
            }

            if (request.Method == HttpMethod.Delete && path == "/connections")
            {
                Operations.Add("close");
                return Task.FromResult(FailCloseAll
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (request.Method == HttpMethod.Get && path == "/proxies")
            {
                string body = "{\"proxies\":{\"Auto\":{\"type\":\"Selector\",\"now\":\""
                    + CurrentProxy
                    + "\",\"all\":[\"old\",\"new\"]},\"old\":{\"type\":\"Direct\"},\"new\":{\"type\":\"Direct\"}}}";
                return Task.FromResult(JsonResponse(body));
            }

            string responseBody = path switch
            {
                "/version" => "{\"version\":\"v1.19.30\"}",
                "/configs" => "{\"mode\":\"rule\",\"tun\":{\"enable\":false}}",
                "/traffic" => "{\"upTotal\":0,\"downTotal\":0,\"up\":0,\"down\":0}",
                "/memory" => "{\"inuse\":0}",
                "/connections" => "{\"connections\":[]}",
                "/rules" => "{\"rules\":[]}",
                "/providers/proxies" => "{\"providers\":{}}",
                "/providers/rules" => "{\"providers\":{}}",
                _ => "{}"
            };
            return Task.FromResult(JsonResponse(responseBody));
        }

        private static HttpResponseMessage JsonResponse(string body) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
    }

    private sealed class RuntimeControllerHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> RequestedPaths { get; } = new ConcurrentQueue<string>();

        public bool FailMetrics { get; set; }

        public bool AllowLan { get; private set; }

        public bool Ipv6 { get; private set; } = true;

        public int NetworkPatchCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? path = request.RequestUri?.AbsolutePath;
            if (path is not null)
            {
                RequestedPaths.Enqueue(path);
            }

            if (FailMetrics && path is "/traffic" or "/memory")
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("redacted")
                };
            }

            if (request.Method == HttpMethod.Patch && path == "/configs")
            {
                using JsonDocument payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (payload.RootElement.TryGetProperty("allow-lan", out JsonElement allowLan))
                {
                    AllowLan = allowLan.GetBoolean();
                }

                if (payload.RootElement.TryGetProperty("ipv6", out JsonElement ipv6))
                {
                    Ipv6 = ipv6.GetBoolean();
                }

                NetworkPatchCount++;
                return new HttpResponseMessage(HttpStatusCode.NoContent)
                {
                    Content = new StringContent(string.Empty)
                };
            }

            string body = path switch
            {
                "/version" => "{\"version\":\"v1.19.30\"}",
                "/configs" => $"{{\"mode\":\"rule\",\"allow-lan\":{(AllowLan ? "true" : "false")},\"ipv6\":{(Ipv6 ? "true" : "false")},\"tun\":{{\"enable\":true}}}}",
                "/proxies" => "{\"proxies\":{}}",
                "/traffic" => "{\"upTotal\":11,\"downTotal\":12,\"up\":1,\"down\":2}\n",
                "/memory" => "{\"inuse\":22}\n",
                "/connections" => "{\"connections\":[]}",
                "/rules" => "{\"rules\":[]}",
                "/providers/proxies" => "{\"providers\":{}}",
                "/providers/rules" => "{\"providers\":{}}",
                _ => "{}"
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
        }
    }

    private sealed class DelayControllerHandler : HttpMessageHandler
    {
        private readonly bool _holdFirstRequest;
        private readonly TaskCompletionSource<bool> _releaseFirstRequest =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _beforeFirstRequest;
        private int _requestCount;
        private int _inFlight;
        private int _maxInFlight;

        public DelayControllerHandler(bool holdFirstRequest)
        {
            _holdFirstRequest = holdFirstRequest;
        }

        public TaskCompletionSource<bool> FirstRequestEntered { get; } =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action? BeforeFirstRequest
        {
            get => _beforeFirstRequest;
            set => _beforeFirstRequest = value;
        }

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public void ReleaseFirstRequest() => _releaseFirstRequest.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.StartsWith("/proxies/", StringComparison.Ordinal) != true)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            int requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber == 1)
            {
                Action? callback = Interlocked.Exchange(ref _beforeFirstRequest, null);
                callback?.Invoke();
            }

            int current = Interlocked.Increment(ref _inFlight);
            UpdateMaximum(current);
            try
            {
                if (_holdFirstRequest && requestNumber == 1)
                {
                    FirstRequestEntered.TrySetResult(true);
                    await _releaseFirstRequest.Task.WaitAsync(cancellationToken);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"delay\":10}")
                };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void UpdateMaximum(int current)
        {
            while (true)
            {
                int previous = Volatile.Read(ref _maxInFlight);
                if (current <= previous
                    || Interlocked.CompareExchange(ref _maxInFlight, current, previous) == previous)
                {
                    return;
                }
            }
        }
    }
}
