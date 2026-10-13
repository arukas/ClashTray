using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class CoreStartFailureTests
{
    [TestMethod]
    public void OwnershipUnknownIsDistinctFromForeignPortAndDiagnosticsAreSanitized()
    {
        Guid operationId = Guid.NewGuid();
        CoreStartOperationResult unknown = CoreStartFailure.FromException(new ManagedCoreOwnershipException()).ForOperation(operationId);
        Assert.AreEqual(CoreStartOutcome.Failed, unknown.Outcome);
        Assert.AreEqual(ServiceErrorCode.ControllerOwnershipUnconfirmed, unknown.ErrorCode);
        Assert.AreEqual(operationId, unknown.OperationId);

        CoreStartOperationResult conflict = CoreStartFailure.FromException(new ServiceCommandException(
            ServiceErrorCode.ProxyPortConflict, "https://example.test/config?token=sensitive" )).ForOperation(operationId);
        Assert.AreEqual(CoreStartOutcome.PortConflict, conflict.Outcome);
        Assert.IsFalse(conflict.ErrorMessage!.Contains("sensitive", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnconfirmedDispatchKeepsCancellationSourceSeparateFromCompletion()
    {
        using CancellationTokenSource caller = new();
        using CancellationTokenSource deadline = new();
        CoreStartFailure unknown = CoreStartFailure.UnconfirmedServiceStart("unknown", caller.Token, deadline.Token);
        Assert.AreEqual(CoreStartOutcome.Failed, unknown.Outcome);
        Assert.AreEqual(ServiceDispatchState.DispatchedAwaitingResult, unknown.DispatchState);

        deadline.Cancel();
        CoreStartFailure timeout = CoreStartFailure.UnconfirmedServiceStart("same wording", caller.Token, deadline.Token);
        Assert.AreEqual(CoreStartOutcome.TimedOut, timeout.Outcome);
        Assert.AreEqual(ServiceErrorCode.OperationTimedOut, timeout.ErrorCode);
        Assert.AreEqual(ServiceDispatchState.DispatchedAwaitingResult, timeout.DispatchState);

        caller.Cancel();
        CoreStartFailure canceled = CoreStartFailure.UnconfirmedServiceStart("same wording", caller.Token, deadline.Token);
        Assert.AreEqual(CoreStartOutcome.Cancelled, canceled.Outcome);
        Assert.AreEqual(ServiceErrorCode.OperationCancelled, canceled.ErrorCode);
        Assert.AreEqual(ServiceDispatchState.DispatchedAwaitingResult, canceled.DispatchState);
    }
}
