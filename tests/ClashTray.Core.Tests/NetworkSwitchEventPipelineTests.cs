using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "NetworkSwitchEventPipeline owns and asynchronously disposes the injected context source.")]
public sealed class NetworkSwitchEventPipelineTests
{
    [TestMethod]
    public async Task RapidNetworkEventsProduceOnlyTheLatestStableDecision()
    {
        FakeNetworkContextSource source = new FakeNetworkContextSource(CreateContext(0, stable: false));
        List<long> executedRevisions = [];
        TaskCompletionSource<NetworkSwitchDecision> switching =
            new TaskCompletionSource<NetworkSwitchDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using NetworkSwitchEventPipeline pipeline = CreatePipeline(
            source,
            (decision, _) =>
            {
                executedRevisions.Add(decision.NetworkRevision);
                switching.TrySetResult(decision);
                return Task.FromResult(new NetworkSwitchExecutionResult(true, true));
            });

        await pipeline.StartAsync();
        source.Publish(CreateContext(1, "Home"));
        await Task.Delay(15);
        source.Publish(CreateContext(2, "Office"));

        NetworkSwitchDecision decision = await switching.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, decision.NetworkRevision);
        Assert.AreEqual("office", decision.TargetConfigurationId);
        Assert.AreEqual(1, executedRevisions.Count);
        Assert.AreEqual(2L, executedRevisions[0]);
    }

    [TestMethod]
    public async Task AttemptedSwitchStartsCooldownAndBlocksTheNextRevision()
    {
        FakeNetworkContextSource source = new FakeNetworkContextSource(CreateContext(0, stable: false));
        List<NetworkSwitchDecision> decisions = [];
        List<long> executedRevisions = [];
        TaskCompletionSource<NetworkSwitchDecision> firstSwitch =
            new TaskCompletionSource<NetworkSwitchDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<NetworkSwitchDecision> cooldown =
            new TaskCompletionSource<NetworkSwitchDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using NetworkSwitchEventPipeline pipeline = CreatePipeline(
            source,
            (decision, _) =>
            {
                executedRevisions.Add(decision.NetworkRevision);
                return Task.FromResult(new NetworkSwitchExecutionResult(true, true));
            });
        pipeline.DecisionChanged += (_, decision) =>
        {
            decisions.Add(decision);
            if (decision.NetworkRevision == 1 && decision.State == NetworkSwitchState.Switching)
            {
                firstSwitch.TrySetResult(decision);
            }

            if (decision.NetworkRevision == 2 && decision.State == NetworkSwitchState.CoolingDown)
            {
                cooldown.TrySetResult(decision);
            }
        };

        await pipeline.StartAsync();
        await Task.Delay(120);
        source.Publish(CreateContext(1, "Home"));
        await firstSwitch.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.Publish(CreateContext(2, "Office"));

        NetworkSwitchDecision cooldownDecision = await cooldown.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(NetworkSwitchReason.CoolingDown, cooldownDecision.Reason);
        Assert.AreEqual(1, executedRevisions.Count);
        Assert.AreEqual(1L, executedRevisions[0]);
        Assert.IsTrue(decisions.Count >= 2);
    }

    [TestMethod]
    public async Task ManualOverrideAppliesOnlyToItsNetworkRevision()
    {
        FakeNetworkContextSource source = new FakeNetworkContextSource(CreateContext(0, stable: false));
        List<NetworkSwitchDecision> decisions = [];
        TaskCompletionSource<NetworkSwitchDecision> manual =
            new TaskCompletionSource<NetworkSwitchDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<NetworkSwitchDecision> switched =
            new TaskCompletionSource<NetworkSwitchDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using NetworkSwitchEventPipeline pipeline = CreatePipeline(
            source,
            (decision, _) =>
            {
                switched.TrySetResult(decision);
                return Task.FromResult(new NetworkSwitchExecutionResult(true, true));
            });
        pipeline.DecisionChanged += (_, decision) =>
        {
            decisions.Add(decision);
            if (decision.NetworkRevision == 1 && decision.State == NetworkSwitchState.ManualOverride)
            {
                manual.TrySetResult(decision);
            }
        };

        await pipeline.StartAsync();
        pipeline.SetManualOverride("manual", 1);
        source.Publish(CreateContext(1, "Home"));
        NetworkSwitchDecision manualDecision = await manual.Task.WaitAsync(TimeSpan.FromSeconds(2));

        source.Publish(CreateContext(2, "Home"));
        NetworkSwitchDecision switchedDecision = await switched.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(NetworkSwitchReason.ManualOverride, manualDecision.Reason);
        Assert.AreEqual(2, switchedDecision.NetworkRevision);
        Assert.AreEqual(NetworkSwitchDecisionKind.SwitchToMappedConfiguration, switchedDecision.Kind);
        Assert.IsTrue(decisions.Any(decision => decision.NetworkRevision == 2));
    }

    private static NetworkSwitchEventPipeline CreatePipeline(
        FakeNetworkContextSource source,
        Func<NetworkSwitchDecision, CancellationToken, Task<NetworkSwitchExecutionResult>> handler) =>
        new NetworkSwitchEventPipeline(
            source,
            context => new NetworkSwitchPolicyInput(
                true,
                context,
                [
                    new NetworkSwitchRule("home-rule", "Home", "home"),
                    new NetworkSwitchRule("office-rule", "Office", "office")
                ],
                null,
                null,
                new HashSet<string>(["home", "office"], StringComparer.OrdinalIgnoreCase)),
            handler,
            debounce: TimeSpan.FromMilliseconds(60),
            cooldown: TimeSpan.FromMilliseconds(200));

    private static NetworkContextSnapshot CreateContext(
        long revision,
        string? ssid = null,
        bool stable = true) =>
        new(
            revision,
            DateTimeOffset.UtcNow,
            NetworkConnectivityKind.WiFi,
            ssid,
            "wifi-interface",
            NetworkPermissionState.Allowed,
            false,
            stable);

    private sealed class FakeNetworkContextSource : INetworkContextSource
    {
        public FakeNetworkContextSource(NetworkContextSnapshot current)
        {
            Current = current;
        }

        public event EventHandler<NetworkContextSnapshot>? ContextChanged;

        public NetworkContextSnapshot Current { get; private set; }

        public Task<NetworkContextSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public void Publish(NetworkContextSnapshot context)
        {
            Current = context;
            ContextChanged?.Invoke(this, context);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
