using ClashTray.Contracts;
using System.Text.Json;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class OperationAdmissionTests
{
    private static readonly int[] LatestWinsExpectedExecutionOrder = [1, 2];

    [TestMethod]
    public async Task BooleanSingleFlightSharesSameTargetAndRejectsOppositeTarget()
    {
        BooleanSingleFlight<int> singleFlight = new("TUN");
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int invocationCount = 0;

        Task<int>[] requests = Enumerable.Range(0, 20)
            .Select(_ => singleFlight.RequestAsync(
                target: true,
                async (_, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref invocationCount) == 1)
                    {
                        entered.TrySetResult(true);
                    }

                    await release.Task.WaitAsync(cancellationToken);
                    return 42;
                }))
            .ToArray();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsExactlyAsync<OperationBusyException>(
            () => singleFlight.RequestAsync(
                target: false,
                (_, _) => Task.FromResult(7)));

        release.TrySetResult(true);
        int[] results = await Task.WhenAll(requests);

        Assert.AreEqual(1, Volatile.Read(ref invocationCount));
        CollectionAssert.AreEqual(Enumerable.Repeat(42, 20).ToArray(), results);
        Assert.IsFalse(singleFlight.IsBusy);
    }

    [TestMethod]
    public async Task BooleanSingleFlightReleasesAfterFailureAndCancellation()
    {
        BooleanSingleFlight<int> singleFlight = new("operation");
        int invocationCount = 0;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => singleFlight.RequestAsync(
                target: true,
                (_, _) =>
                {
                    Interlocked.Increment(ref invocationCount);
                    return Task.FromException<int>(new InvalidOperationException("failed"));
                }));
        Assert.IsFalse(singleFlight.IsBusy);

        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<bool> cancellationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> cancelled = singleFlight.RequestAsync(
            target: false,
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref invocationCount);
                cancellationEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            },
            cancellation.Token);
        await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        // Caller-side cancellation is observed immediately; the operation's own
        // teardown completes asynchronously afterwards, so wait for idle before
        // asserting the released state.
        await singleFlight.WaitForIdleAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(singleFlight.IsBusy);

        int result = await singleFlight.RequestAsync(
            target: true,
            (_, _) =>
            {
                Interlocked.Increment(ref invocationCount);
                return Task.FromResult(9);
            });

        Assert.AreEqual(9, result);
        Assert.AreEqual(3, Volatile.Read(ref invocationCount));
    }

    [TestMethod]
    public async Task LatestWinsKeepsOnlyFirstInFlightAndLastPendingIntent()
    {
        LatestWinsOperation<ProxyMode> latest = new("mode");
        TaskCompletionSource<bool> firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ProxyMode> executed = [];
        int active = 0;
        int maximumActive = 0;

        async Task<ProxyMode> ExecuteAsync(ProxyMode mode, CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximumActive, current);
            lock (executed)
            {
                executed.Add(mode);
            }

            if (executed.Count == 1)
            {
                firstEntered.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }

            Interlocked.Decrement(ref active);
            return mode;
        }

        Task<ProxyMode> first = latest.RequestAsync(ProxyMode.Rule, ExecuteAsync);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ProxyMode> second = latest.RequestAsync(ProxyMode.Global, ExecuteAsync);
        Task<ProxyMode> third = latest.RequestAsync(ProxyMode.Direct, ExecuteAsync);
        Task<ProxyMode> fourth = latest.RequestAsync(ProxyMode.Rule, ExecuteAsync);

        releaseFirst.TrySetResult(true);
        Assert.AreEqual(ProxyMode.Rule, await first);
        await Assert.ThrowsExactlyAsync<OperationSupersededException>(() => second);
        await Assert.ThrowsExactlyAsync<OperationSupersededException>(() => third);
        Assert.AreEqual(ProxyMode.Rule, await fourth);

        CollectionAssert.AreEqual(new[] { ProxyMode.Rule, ProxyMode.Rule }, executed);
        Assert.AreEqual(1, Volatile.Read(ref maximumActive));
        Assert.IsFalse(latest.IsBusy);
    }

    [TestMethod]
    public async Task CanceledPendingIntentDoesNotCancelTheInFlightCaller()
    {
        LatestWinsOperation<int> latest = new("mode");
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> first = latest.RequestAsync(
            1,
            async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return 1;
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using CancellationTokenSource pendingCancellation = new();
        Task<int> pending = latest.RequestAsync(
            2,
            (_, _) => Task.FromResult(2),
            pendingCancellation.Token);
        await pendingCancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);

        release.TrySetResult(true);
        Assert.AreEqual(1, await first);
        Assert.IsTrue(SpinWait.SpinUntil(() => !latest.IsBusy, TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task LatestWinsPendingIntentDoesNotInheritSupersededCancellation()
    {
        LatestWinsOperation<int> latest = new("intent");
        using CancellationTokenSource firstCancellation = new();
        TaskCompletionSource<bool> firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> executed = [];

        async Task<int> ExecuteAsync(int value, CancellationToken cancellationToken)
        {
            executed.Add(value);
            if (value == 1)
            {
                firstEntered.TrySetResult(true);
                await releaseFirst.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }

            return value;
        }

        Task<int> first = latest.RequestAsync(1, ExecuteAsync, firstCancellation.Token);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<int> pending = latest.RequestAsync(2, ExecuteAsync, CancellationToken.None);
        await firstCancellation.CancelAsync();
        releaseFirst.TrySetResult(true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        Assert.AreEqual(2, await pending);
        CollectionAssert.AreEqual(LatestWinsExpectedExecutionOrder, executed);
        Assert.IsTrue(SpinWait.SpinUntil(() => !latest.IsBusy, TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task LatestWinsDrainsNewIntentAfterCanceledPendingPromotion()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            LatestWinsOperation<int> latest = new("intent");
            TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<int> first = latest.RequestAsync(
                1,
                async (value, _) =>
                {
                    entered.TrySetResult(true);
                    await release.Task;
                    return value;
                });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            using CancellationTokenSource pendingCancellation = new();
            Task<int> canceledPending = latest.RequestAsync(
                2,
                static (value, _) => Task.FromResult(value),
                pendingCancellation.Token);
            await pendingCancellation.CancelAsync();
            release.TrySetResult(true);

            Task<int> newest = Task.Run(async () =>
            {
                await Task.Yield();
                return await latest.RequestAsync(
                    3,
                    static (value, _) => Task.FromResult(value));
            });

            Assert.AreEqual(1, await first.WaitAsync(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<OperationCanceledException>(() => canceledPending);
            Assert.AreEqual(3, await newest.WaitAsync(TimeSpan.FromSeconds(2)));
            // The caller task completes before the operation teardown clears
            // _running; wait for the teardown instead of asserting instantly.
            Assert.IsTrue(SpinWait.SpinUntil(() => !latest.IsBusy, TimeSpan.FromSeconds(2)));
        }
    }

    [TestMethod]
    public async Task CleanupOwnershipWaitsForAdmittedOperationAndRejectsNewWork()
    {
        using OperationGate gate = new();
        await gate.WaitAsync();
        gate.BeginQuiescing();

        Task cleanupOwnership = gate.WaitForCleanupOwnershipAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        Assert.IsFalse(cleanupOwnership.IsCompleted);

        gate.Exit();
        await cleanupOwnership.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsExactlyAsync<RuntimeQuiescingException>(() => gate.WaitAsync());
        gate.Exit();
    }

    [TestMethod]
    public async Task SharedAdmissionsRunConcurrently()
    {
        using OperationGate gate = new();
        OperationGate.Lease first = await gate.AcquireSharedAsync();
        Task<OperationGate.Lease> second = gate.AcquireSharedAsync();
        OperationGate.Lease secondLease = await second.WaitAsync(TimeSpan.FromSeconds(2));
        secondLease.Dispose();
        first.Dispose();
    }

    [TestMethod]
    public async Task ExclusiveAdmissionWaitsForSharedHolders()
    {
        using OperationGate gate = new();
        OperationGate.Lease shared = await gate.AcquireSharedAsync();
        Task<OperationGate.Lease> exclusive = gate.AcquireAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(exclusive.IsCompleted);

        shared.Dispose();
        using OperationGate.Lease exclusiveLease = await exclusive.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task SharedAdmissionWaitsForExclusiveHolder()
    {
        using OperationGate gate = new();
        OperationGate.Lease exclusive = await gate.AcquireAsync();
        Task<OperationGate.Lease> shared = gate.AcquireSharedAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(shared.IsCompleted);

        exclusive.Dispose();
        using OperationGate.Lease sharedLease = await shared.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task WaitingExclusiveBlocksNewSharedAdmissions()
    {
        using OperationGate gate = new();
        OperationGate.Lease firstShared = await gate.AcquireSharedAsync();
        Task<OperationGate.Lease> exclusive = gate.AcquireAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(exclusive.IsCompleted);

        Task<OperationGate.Lease> secondShared = gate.AcquireSharedAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(secondShared.IsCompleted);

        firstShared.Dispose();
        OperationGate.Lease exclusiveLease = await exclusive.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(secondShared.IsCompleted);

        exclusiveLease.Dispose();
        using OperationGate.Lease secondSharedLease = await secondShared.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task QuiescingWakesPendingSharedAdmission()
    {
        using OperationGate gate = new();
        OperationGate.Lease exclusive = await gate.AcquireAsync();
        Task<OperationGate.Lease> pendingShared = gate.AcquireSharedAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(pendingShared.IsCompleted);

        gate.BeginQuiescing();
        await Assert.ThrowsExactlyAsync<RuntimeQuiescingException>(
            () => pendingShared.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsExactlyAsync<RuntimeQuiescingException>(() => gate.AcquireSharedAsync());
        exclusive.Dispose();
    }

    [TestMethod]
    public async Task CleanupOwnershipWaitsForSharedHolders()
    {
        using OperationGate gate = new();
        OperationGate.Lease shared = await gate.AcquireSharedAsync();
        gate.BeginQuiescing();

        Task<OperationGate.Lease> cleanup = gate.AcquireCleanupOwnershipAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(cleanup.IsCompleted);

        shared.Dispose();
        using OperationGate.Lease cleanupLease = await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CleanupOwnershipTimeoutCoversSharedDrainAndReleasesAdmissionLane()
    {
        using OperationGate gate = new();
        OperationGate.Lease shared = await gate.AcquireSharedAsync();
        gate.BeginQuiescing();

        Task<OperationGate.Lease> cleanup = gate.AcquireCleanupOwnershipAsync(
            TimeSpan.FromMilliseconds(75));

        try
        {
            Task completed = await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.AreSame(cleanup, completed, "The deadline must include draining admitted readers.");
            await Assert.ThrowsExactlyAsync<TimeoutException>(async () =>
            {
                using OperationGate.Lease unexpectedLease = await cleanup;
            });
        }
        finally
        {
            shared.Dispose();

            // On the pre-fix implementation the wait survives its advertised
            // timeout and acquires ownership after this reader leaves. Release
            // that late lease so the failing regression test does not strand
            // the gate or poison the next assertion.
            if (!cleanup.IsCompleted)
            {
                try
                {
                    using OperationGate.Lease lateLease = await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    Assert.Fail("The cleanup waiter did not settle after its reader was released.");
                }
            }
            else if (cleanup.IsCompletedSuccessfully)
            {
                (await cleanup).Dispose();
            }
        }

        using OperationGate.Lease nextCleanup = await gate.AcquireCleanupOwnershipAsync(
            TimeSpan.FromSeconds(1));
    }
    [TestMethod]
    public async Task CleanupOwnershipTimeoutWhileWaitingForExclusiveHolderReleasesWaiter()
    {
        using OperationGate gate = new();
        OperationGate.Lease exclusive = await gate.AcquireAsync();
        gate.BeginQuiescing();

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => gate.AcquireCleanupOwnershipAsync(
            TimeSpan.FromMilliseconds(75)));

        exclusive.Dispose();
        using OperationGate.Lease retry = await gate.AcquireCleanupOwnershipAsync(
            TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task CleanupOwnershipExternalCancellationWhileDrainingReleasesAdmissionLane()
    {
        using OperationGate gate = new();
        OperationGate.Lease shared = await gate.AcquireSharedAsync();
        gate.BeginQuiescing();
        using CancellationTokenSource cancellation = new();
        Task<OperationGate.Lease> cleanup = gate.AcquireCleanupOwnershipAsync(
            TimeSpan.FromSeconds(2),
            cancellation.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(25));
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cleanup);

        shared.Dispose();
        using OperationGate.Lease retry = await gate.AcquireCleanupOwnershipAsync(
            TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task BoundedCleanupStepReportsUnresponsiveWorkWithoutReleasingItsResource()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromMilliseconds(75));
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int resourceReleased = 0;

        BoundedCleanupStepResult result = await BoundedCleanupStepRunner.RunAsync(
            async _ =>
            {
                try
                {
                    await release.Task;
                }
                finally
                {
                    Interlocked.Exchange(ref resourceReleased, 1);
                }
            },
            deadline.Token);

        Assert.IsFalse(result.IsSettled);
        Assert.IsInstanceOfType<OperationCanceledException>(result.Failure);
        Assert.IsFalse(result.IncompleteOperation!.IsCompleted);
        Assert.AreEqual(0, Volatile.Read(ref resourceReleased));

        release.TrySetResult(true);
        await result.IncompleteOperation;
        Assert.AreEqual(1, Volatile.Read(ref resourceReleased));
    }

    [TestMethod]
    public async Task WaitForIdleTracksSharedHolders()
    {
        using OperationGate gate = new();
        OperationGate.Lease shared = await gate.AcquireSharedAsync();
        Task idle = gate.WaitForIdleAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(idle.IsCompleted);

        shared.Dispose();
        await idle;
    }

    [TestMethod]
    public async Task SingleFlightSharesDuplicateDelayProbe()
    {
        SingleFlightOperation<int> singleFlight = new("delay");
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int invocationCount = 0;

        Task<int> first = singleFlight.RequestAsync(async cancellationToken =>
        {
            Interlocked.Increment(ref invocationCount);
            entered.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            return 123;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<int> duplicate = singleFlight.RequestAsync(_ => Task.FromResult(999));

        release.TrySetResult(true);
        Assert.AreEqual(123, await first);
        Assert.AreEqual(123, await duplicate);
        Assert.AreEqual(1, Volatile.Read(ref invocationCount));
    }

    [TestMethod]
    public void TunConfigurationValidationUsesEffectiveAddressSources()
    {
        using JsonDocument explicitInvalid = JsonDocument.Parse(
            "{\"tun\":{\"enable\":true,\"inet4-address\":[\"not-a-cidr\"]}} ");
        MihomoTunConfiguration invalid = MihomoDataParser.ParseTunConfiguration(explicitInvalid);
        Assert.IsTrue(invalid.HasExplicitAddress);
        Assert.IsFalse(invalid.HasValidAddress);
        Assert.IsFalse(invalid.CanProduceInterfaceAddress);

        using JsonDocument fakeIpDerived = JsonDocument.Parse(
            "{\"tun\":{\"enable\":true,\"inet4-address\":[\"not-a-cidr\"],\"dns-hijack\":[\"any:53\"]},\"dns\":{\"fake-ip-range\":\"198.18.0.1/16\"}} ");
        MihomoTunConfiguration derived = MihomoDataParser.ParseTunConfiguration(fakeIpDerived);
        Assert.IsTrue(derived.HasFakeIpRange);
        Assert.IsTrue(derived.CanProduceInterfaceAddress);
        Assert.IsTrue(derived.RequiresDnsHealth);

        using JsonDocument manualRoute = JsonDocument.Parse(
            "{\"tun\":{\"enable\":true,\"auto-route\":false,\"dns-hijack\":[\"any:53\"]}} ");
        MihomoTunConfiguration manual = MihomoDataParser.ParseTunConfiguration(manualRoute);
        Assert.IsFalse(manual.RequiresDnsHealth);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            while (true)
            {
                int current = Volatile.Read(ref location);
                if (value <= current
                    || Interlocked.CompareExchange(ref location, value, current) == current)
                {
                    return;
                }
            }
        }
    }
}
