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
        Assert.IsFalse(latest.IsBusy);
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
        Assert.IsFalse(latest.IsBusy);
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
