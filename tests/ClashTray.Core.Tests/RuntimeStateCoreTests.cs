using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeStateCoreTests
{
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
    public async Task LocalCacheCommandsPublishFreshSnapshotAfterCompletion()
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

            int published = 0;
            runtime.SnapshotChanged += (_, _) => published++;
            int beforeCommands = published;

            await runtime.ClearFakeIpCacheAsync();
            int afterFakeIp = published;
            await runtime.ClearDnsCacheAsync();
            int afterDns = published;

            Assert.IsGreaterThan(beforeCommands, afterFakeIp);
            Assert.IsGreaterThan(afterFakeIp, afterDns);
            Assert.IsTrue(handler.RequestedPaths.Any(path => path == "/version"));
            Assert.IsTrue(handler.RequestedPaths.Any(path => path == "/configs"));
            Assert.IsTrue(handler.RequestedPaths.Any(path => path == "/traffic"));
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
    public async Task PublishDoesNotHoldGateWhileInvokingSubscribers()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);
        TaskCompletionSource<bool> firstHandlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseHandler = new();
        int invocations = 0;

        try
        {
            runtime.SnapshotChanged += (_, _) =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                {
                    firstHandlerEntered.TrySetResult(true);
                    releaseHandler.Wait();
                }
            };

            Task first = Task.Run(() => runtime.ClearLogs());
            await firstHandlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The first subscriber is still blocked; a concurrent publish must not
            // wait on it. Pre-fix this second publish deadlocked on _publishGate.
            await Task.Run(() => runtime.ClearLogs()).WaitAsync(TimeSpan.FromSeconds(10));

            releaseHandler.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseHandler.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task MutationsWithoutControllerSessionFailInsteadOfReportingSuccess()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => runtime.ClearDnsCacheAsync());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => runtime.ClearFakeIpCacheAsync());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => runtime.CloseAllConnectionsAsync());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => runtime.CloseConnectionAsync("connection-id"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
