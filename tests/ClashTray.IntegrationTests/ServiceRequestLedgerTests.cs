using ClashTray.Contracts;
using ClashTray.Service;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class ServiceRequestLedgerTests
{
    [TestMethod]
    public void InFlightRequestCannotExpireAndCompletionStartsRetentionWindow()
    {
        Clock clock = new();
        ServiceRequestLedger ledger = new(clock, 1);
        ServiceRequest request = Request();
        RequestAdmission first = ledger.Admit(request, () => RequestRetentionKind.Mutation);
        clock.Advance(TimeSpan.FromDays(1));
        RequestAdmission join = ledger.Admit(request with { RecoveryOnly = true }, () => throw new InvalidOperationException());
        Assert.AreEqual(RequestAdmissionKind.Join, join.Kind);
        Assert.AreSame(first.Entry, join.Entry);
        ledger.Complete(first.Entry!, new(request.RequestId, true, TunState.Off));
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.AreEqual(RequestAdmissionKind.Join, ledger.Admit(request with { RecoveryOnly = true }, () => RequestRetentionKind.Mutation).Kind);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(RequestAdmissionKind.ResultUnavailable, ledger.Admit(request with { RecoveryOnly = true }, () => RequestRetentionKind.Mutation).Kind);
    }

    [TestMethod]
    public void SeparateQuotasKeepObservationAndConfirmedCleanupAvailable()
    {
        ServiceRequestLedger ledger = new(TimeProvider.System, 1);
        foreach (RequestRetentionKind kind in Enum.GetValues<RequestRetentionKind>())
        {
            Assert.AreEqual(RequestAdmissionKind.Execute, ledger.Admit(Request(), () => kind).Kind);
            Assert.AreEqual(RequestAdmissionKind.Saturated, ledger.Admit(Request(), () => kind).Kind);
        }
    }

    [TestMethod]
    public async Task CancelledWaiterCannotCancelTheRetainedExecution()
    {
        ServiceRequestLedger ledger = new(TimeProvider.System);
        ServiceRequest request = Request();
        RequestAdmission admitted = ledger.Admit(request, () => RequestRetentionKind.Mutation);
        using CancellationTokenSource waiter = new();
        Task<ServiceResponse> waiting = admitted.Entry!.Completion.Task.WaitAsync(waiter.Token);
        await waiter.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => waiting);
        Assert.IsFalse(admitted.Entry.Completion.Task.IsCompleted);
        ServiceResponse response = new(request.RequestId, true, TunState.Off);
        ledger.Complete(admitted.Entry, response);
        RequestAdmission recovered = ledger.Admit(request with { RecoveryOnly = true }, () => RequestRetentionKind.Mutation);
        Assert.AreEqual(response, await recovered.Entry!.Completion.Task);
    }

    [TestMethod]
    public void RequestFingerprintRejectsDifferentPayloadOrServiceInstance()
    {
        ServiceRequestLedger ledger = new(TimeProvider.System);
        ServiceRequest request = Request();
        Assert.AreEqual(RequestAdmissionKind.Execute, ledger.Admit(request, () => RequestRetentionKind.Mutation).Kind);
        Assert.AreEqual(RequestAdmissionKind.IdentityConflict, ledger.Admit(request with { Payload = "{}" }, () => RequestRetentionKind.Cleanup).Kind);
        Assert.AreEqual(RequestAdmissionKind.IdentityConflict, ledger.Admit(request with { ExpectedServiceInstanceId = Guid.NewGuid() }, () => RequestRetentionKind.Cleanup).Kind);
        Assert.AreEqual(RequestAdmissionKind.Join, ledger.Admit(request with { RecoveryOnly = true }, () => throw new InvalidOperationException()).Kind);
    }

    private static ServiceRequest Request() => new(Guid.NewGuid(), ServiceCommand.StopCore, ProtocolVersion: ServiceProtocol.CurrentVersion);
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
