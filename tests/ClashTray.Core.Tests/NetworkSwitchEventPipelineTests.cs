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
        ManualTimeProvider time = new ManualTimeProvider();
        // Context timestamps use the same virtual clock as the pipeline so the
        // policy engine's cooldown comparison is deterministic.
        FakeNetworkContextSource source = new FakeNetworkContextSource(CreateContext(0, stable: false, observedAtUtc: time.GetUtcNow()));
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
            },
            time);

        await pipeline.StartAsync();
        await AdvanceUntilAsync(time, () => pipeline.LastDecision?.NetworkRevision == 0);

        // Both events land before any virtual debounce elapses, so the first
        // revision can never reach execution on any machine speed.
        source.Publish(CreateContext(1, "Home", observedAtUtc: time.GetUtcNow()));
        source.Publish(CreateContext(2, "Office", observedAtUtc: time.GetUtcNow()));
        await AdvanceUntilAsync(time, () => switching.Task.IsCompleted);

        NetworkSwitchDecision decision = await switching.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, decision.NetworkRevision);
        Assert.AreEqual("office", decision.TargetConfigurationId);
        Assert.AreEqual(1, executedRevisions.Count);
        Assert.AreEqual(2L, executedRevisions[0]);
    }

    [TestMethod]
    public async Task AttemptedSwitchStartsCooldownAndBlocksTheNextRevision()
    {
        ManualTimeProvider time = new ManualTimeProvider();
        FakeNetworkContextSource source = new FakeNetworkContextSource(CreateContext(0, stable: false, observedAtUtc: time.GetUtcNow()));
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
            },
            time);
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
        await AdvanceUntilAsync(time, () => pipeline.LastDecision?.NetworkRevision == 0);
        source.Publish(CreateContext(1, "Home", observedAtUtc: time.GetUtcNow()));
        await AdvanceUntilAsync(time, () => firstSwitch.Task.IsCompleted);
        await firstSwitch.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.Publish(CreateContext(2, "Office", observedAtUtc: time.GetUtcNow()));
        await AdvanceUntilAsync(time, () => cooldown.Task.IsCompleted);

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
        Func<NetworkSwitchDecision, CancellationToken, Task<NetworkSwitchExecutionResult>> handler,
        TimeProvider? timeProvider = null) =>
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
            cooldown: TimeSpan.FromMilliseconds(200),
            timeProvider: timeProvider);

    private static async Task AdvanceUntilAsync(ManualTimeProvider time, Func<bool> condition)
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (time.HasPendingTimers)
            {
                // The virtual clock only moves while the worker has actually
                // scheduled a debounce/cooldown timer, so advancing can never
                // overrun a state the worker has not reached yet.
                time.Advance(TimeSpan.FromMilliseconds(10));
            }

            await Task.Yield();
        }
    }

    private static NetworkContextSnapshot CreateContext(
        long revision,
        string? ssid = null,
        bool stable = true,
        DateTimeOffset? observedAtUtc = null) =>
        new(
            revision,
            observedAtUtc ?? DateTimeOffset.UtcNow,
            NetworkConnectivityKind.WiFi,
            ssid,
            "wifi-interface",
            NetworkPermissionState.Allowed,
            false,
            stable);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ScheduledCallback> _callbacks = [];
        private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public bool HasPendingTimers
        {
            get
            {
                lock (_gate)
                {
                    return _callbacks.Count > 0;
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public void Advance(TimeSpan delta)
        {
            List<ScheduledCallback> due;
            lock (_gate)
            {
                _now += delta;
                due = _callbacks.Where(callback => callback.DueAt <= _now).ToList();
                _callbacks.RemoveAll(callback => callback.DueAt <= _now);
            }

            foreach (ScheduledCallback callback in due)
            {
                callback.Fire();
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ScheduledCallback scheduled = new ScheduledCallback(this, callback, state, GetUtcNow().Add(dueTime));
            lock (_gate)
            {
                _callbacks.Add(scheduled);
            }

            return scheduled;
        }

        private void Remove(ScheduledCallback callback)
        {
            lock (_gate)
            {
                _callbacks.Remove(callback);
            }
        }

        private sealed class ScheduledCallback : ITimer
        {
            private readonly ManualTimeProvider _provider;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private bool _disposed;

            public ScheduledCallback(
                ManualTimeProvider provider,
                TimerCallback callback,
                object? state,
                DateTimeOffset dueAt)
            {
                _provider = provider;
                _callback = callback;
                _state = state;
                DueAt = dueAt;
            }

            public DateTimeOffset DueAt { get; }

            public void Fire()
            {
                if (_disposed)
                {
                    return;
                }

                _callback(_state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
                _disposed = true;
                _provider.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

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
