using System.Net;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ListenerReadinessDeadlineTests
{
    [TestMethod]
    [DataRow((int)ListenerOwnerState.Missing)]
    [DataRow((int)ListenerOwnerState.Unknown)]
    public async Task CancellationDuringDelayRetainsLastListenerAndCallerToken(int ownerState)
    {
        using CancellationTokenSource caller = new();
        using ListenerReadinessDeadline deadline = new(TimeSpan.FromMinutes(1), caller.Token);
        deadline.Observe(new LocalPortBinding("dns-udp", IPAddress.Parse("127.0.0.2"), 15353, PortTransport.Udp),
            new ListenerOwnerObservation((ListenerOwnerState)ownerState, "owner query failed"));
        Task waiting = Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
        await caller.CancelAsync();
        OperationCanceledException cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);

        ListenerReadinessCancelledException result = deadline.CreateCancellationException(cancelled);
        Assert.AreEqual(caller.Token, result.CancellationToken);
        StringAssert.Contains(result.Message, "dns-udp 127.0.0.2:15353 Udp", StringComparison.Ordinal);
        StringAssert.Contains(result.Diagnostic, ((ListenerOwnerState)ownerState).ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task OwnDeadlineProducesTimeoutWithSanitizedListenerAndControllerError()
    {
        using ListenerReadinessDeadline deadline = new(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        deadline.Observe(new LocalPortBinding("controller", IPAddress.IPv6Loopback, 19090, PortTransport.Tcp),
            new ListenerOwnerObservation(ListenerOwnerState.Owned));
        deadline.RecordError(new IOException("https://example.test/subscription?token=secret-value"));
        OperationCanceledException cancelled = await Assert.ThrowsAsync<OperationCanceledException>(
            () => Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token));
        TimeoutException result = deadline.CreateTimeoutException(cancelled);

        Assert.AreEqual(ClashTray.Contracts.ServiceErrorCode.OperationTimedOut, CoreStartFailure.ClassifyException(result));
        StringAssert.Contains(result.Message, "controller [::1]:19090 Tcp Owned", StringComparison.Ordinal);
        StringAssert.Contains(result.Message, "example.test", StringComparison.Ordinal);
        Assert.IsFalse(result.Message.Contains("secret-value", StringComparison.Ordinal));
    }
}
