using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class NetworkSwitchRuntimeControllerTests
{
    [TestMethod]
    public async Task InitializeLoadsRulesStartsPipelineAndExecutesConfigurationRequest()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        NetworkRuleStore store = new(paths);
        await store.SaveAsync(new NetworkSwitchRuleSet(
            true,
            null,
            [new NetworkSwitchRule("home-rule", "Home", "home")]));

        await using FakeNetworkContextSource source = new(CreateContext(0, "Home", stable: false));
        List<ConfigurationSwitchRequest> requests = [];
        TaskCompletionSource<ConfigurationSwitchRequest> requestCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<NetworkSwitchStatus> switching = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        NetworkSwitchRuntimeController controller = new(
            store,
            source,
            (context, rules) => new NetworkSwitchPolicyInput(
                rules.AutomaticSwitchingEnabled,
                context,
                rules.Rules,
                rules.DefaultConfigurationId,
                "office",
                new HashSet<string>(["home", "office"], StringComparer.OrdinalIgnoreCase)),
            (request, _) =>
            {
                requests.Add(request);
                requestCompleted.TrySetResult(request);
                return Task.FromResult(new ConfigurationSwitchResult(
                    request.OperationId,
                    ConfigurationSwitchOutcome.Committed,
                    ConfigurationSwitchStage.Committed,
                    ErrorCode.None));
            },
            debounce: TimeSpan.FromMilliseconds(20),
            cooldown: TimeSpan.FromSeconds(1));
        controller.StatusChanged += (_, status) =>
        {
            if (status.LastDecision?.State == NetworkSwitchState.Switching)
            {
                switching.TrySetResult(status);
            }
        };

        try
        {
            await controller.InitializeAsync();
            source.Publish(CreateContext(1, "Home"));
            NetworkSwitchStatus status = await WaitForAsync(switching.Task);

            Assert.IsTrue(status.Available);
            Assert.AreEqual("Home", status.Context!.CurrentSsid);
            Assert.AreEqual(NetworkSwitchReason.MappedRule, status.LastDecision!.Reason);
            ConfigurationSwitchRequest request = await WaitForAsync(requestCompleted.Task);
            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual(ConfigurationSwitchSource.NetworkRule, request.Source);
            Assert.AreEqual("home", request.TargetConfigurationId);
            Assert.AreEqual(1, request.ExpectedNetworkRevision);
        }
        finally
        {
            await controller.DisposeAsync();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ManualOverrideReevaluatesCurrentNetworkWithoutExecutingSwitch()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        NetworkRuleStore store = new(paths);
        await using FakeNetworkContextSource source = new(CreateContext(1, "Home"));
        List<ConfigurationSwitchRequest> requests = [];
        TaskCompletionSource<NetworkSwitchStatus> manual = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        NetworkSwitchRuntimeController controller = new(
            store,
            source,
            (context, rules) => new NetworkSwitchPolicyInput(
                rules.AutomaticSwitchingEnabled,
                context,
                rules.Rules,
                rules.DefaultConfigurationId,
                "office",
                new HashSet<string>(["home", "office", "manual"], StringComparer.OrdinalIgnoreCase)),
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConfigurationSwitchResult(
                    request.OperationId,
                    ConfigurationSwitchOutcome.Committed,
                    ConfigurationSwitchStage.Committed,
                    ErrorCode.None));
            },
            debounce: TimeSpan.FromMilliseconds(20),
            cooldown: TimeSpan.FromSeconds(1));
        controller.StatusChanged += (_, status) =>
        {
            if (status.LastDecision?.State == NetworkSwitchState.ManualOverride)
            {
                manual.TrySetResult(status);
            }
        };

        try
        {
            await controller.InitializeAsync();
            Assert.IsTrue(controller.SetManualOverrideForCurrentNetwork("manual"));
            await controller.SetRulesAsync(new NetworkSwitchRuleSet(
                true,
                null,
                [new NetworkSwitchRule("home-rule", "Home", "home")]));

            NetworkSwitchStatus status = await WaitForAsync(manual.Task);

            Assert.AreEqual(NetworkSwitchReason.ManualOverride, status.LastDecision!.Reason);
            Assert.AreEqual(1, status.LastDecision.NetworkRevision);
            Assert.AreEqual(0, requests.Count);
        }
        finally
        {
            await controller.DisposeAsync();
            DeleteRoot(root);
        }
    }

    private static NetworkContextSnapshot CreateContext(
        long revision,
        string ssid,
        bool stable = true) => new(
        revision,
        DateTimeOffset.UtcNow,
        NetworkConnectivityKind.WiFi,
        ssid,
        "wifi-interface",
        NetworkPermissionState.Allowed,
        false,
        stable);

    private static async Task<T> WaitForAsync<T>(Task<T> task)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        try
        {
            return await task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Timed out waiting for network switch status.");
            throw;
        }
    }

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeNetworkContextSource(NetworkContextSnapshot current) : INetworkContextSource
    {
        private NetworkContextSnapshot _current = current;

        public event EventHandler<NetworkContextSnapshot>? ContextChanged;

        public Task<NetworkContextSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_current);

        public void Publish(NetworkContextSnapshot context)
        {
            _current = context;
            ContextChanged?.Invoke(this, context);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
