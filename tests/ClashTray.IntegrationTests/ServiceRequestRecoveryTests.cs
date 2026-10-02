using ClashTray.Contracts;
using ClashTray.Core;
using ClashTray.Service;
using ClashTray.Testing;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class ServiceRequestRecoveryTests
{
    [TestMethod]
    public async Task SuccessfulDispatchWithDroppedResponseReturnsCachedResultExactlyOnce()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            int executions = 0;
            await using ServiceRuntimeController service = Create(root, observed: _ => executions++);
            DroppedResponseTransport transport = new(service);
            ServicePipeClient client = new(transport);
            ServiceResponse response = await client.SendAsync(ServiceCommand.StopCore);
            Assert.IsTrue(response.Succeeded, response.Error);
            Assert.AreEqual(1, executions);
            Assert.AreEqual(service.ServiceInstanceId, response.ServiceInstanceId);
            Assert.AreEqual(2, transport.Attempts.Count);
            Assert.AreEqual(transport.Attempts[0].RequestId, transport.Attempts[1].RequestId);
            Assert.IsTrue(transport.Attempts[1].RecoveryOnly);
        });
    }

    [TestMethod]
    public async Task ExpiredResultAndChangedServiceInstanceNeverReplayTheOperation()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            Clock clock = new();
            int executions = 0;
            await using ServiceRuntimeController service = Create(root, clock, observed: _ => executions++);
            ServiceRequest request = Request(ServiceCommand.StopCore);
            ServiceResponse original = await service.HandleAsync(request, CancellationToken.None);
            Assert.IsTrue(original.Succeeded);
            clock.Advance(TimeSpan.FromMinutes(3));
            ServiceResponse expired = await service.HandleAsync(request with { RecoveryOnly = true }, CancellationToken.None);
            Assert.AreEqual(ServiceErrorCode.RequestResultUnavailable, expired.ErrorCode);
            Assert.AreEqual(ServiceDispatchState.DispatchedAwaitingResult, expired.DispatchState);
            Assert.AreEqual(1, executions);
            await using ServiceRuntimeController restarted = Create(root, observed: _ => executions++);
            ServiceResponse changed = await restarted.HandleAsync(request with { RecoveryOnly = true, ExpectedServiceInstanceId = original.ServiceInstanceId }, CancellationToken.None);
            Assert.AreEqual(ServiceErrorCode.RequestResultUnavailable, changed.ErrorCode);
            Assert.AreEqual(1, executions);
        });
    }

    [TestMethod]
    public async Task SaturatedCachePreservesRecoverableResultsAndRejectsNewOperations()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            int executions = 0;
            await using ServiceRuntimeController service = Create(root, capacity: 1, observed: _ => executions++);
            ServiceRequest request = Request(ServiceCommand.StopCore);
            ServiceResponse first = await service.HandleAsync(request, CancellationToken.None);
            ServiceResponse rejected = await service.HandleAsync(Request(ServiceCommand.StopCore), CancellationToken.None);
            Assert.AreEqual(ServiceErrorCode.OperationBusy, rejected.ErrorCode);
            ServiceResponse recovered = await service.HandleAsync(request with { RecoveryOnly = true }, CancellationToken.None);
            Assert.AreEqual(first, recovered);
            Assert.AreEqual(1, executions);
        });
    }

    [TestMethod]
    public async Task ConcurrentDuplicatesJoinWhileCancelledWaiterDoesNotCancelExecution()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            int executions = 0;
            using ManualResetEventSlim release = new();
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using ServiceRuntimeController service = Create(root, observed: _ =>
            {
                Interlocked.Increment(ref executions);
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Test execution barrier was not released."); }
            });
            ServiceRequest request = Request(ServiceCommand.StopCore);
            Task<ServiceResponse> owner = Task.Run(() => service.HandleAsync(request, CancellationToken.None));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<ServiceResponse>[] duplicates = Enumerable.Range(0, 8).Select(_ => service.HandleAsync(request, CancellationToken.None)).ToArray();
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();
            try
            {
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => service.HandleAsync(request with { RecoveryOnly = true }, cancelled.Token));
                Assert.IsFalse(owner.IsCompleted);
            }
            finally { release.Set(); }
            ServiceResponse[] responses = await Task.WhenAll(duplicates.Append(owner));
            Assert.IsTrue(responses.All(response => response == responses[0]));
            Assert.IsTrue((await service.HandleAsync(request with { RecoveryOnly = true }, CancellationToken.None)).Succeeded);
            ServiceResponse conflict = await service.HandleAsync(request with { Payload = "{}", RecoveryOnly = true }, CancellationToken.None);
            Assert.AreEqual(ServiceErrorCode.RequestIdentityConflict, conflict.ErrorCode);
            Assert.AreEqual(1, executions);
        });
    }

    private static ServiceRequest Request(ServiceCommand command) => new(Guid.NewGuid(), command, ProtocolVersion: ServiceProtocol.CurrentVersion);
    private static ServiceRuntimeController Create(string root, TimeProvider? clock = null, int capacity = 128, Action<ServiceRequest>? observed = null) =>
        new(new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program")), null, null,
            restoreOwnedProxyStates: static () => { }, requestTimeProvider: clock, requestCacheCapacity: capacity, requestExecutionObserved: observed);
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
    private sealed class DroppedResponseTransport(ServiceRuntimeController service) : IServiceRequestTransport
    {
        public List<ServiceRequest> Attempts { get; } = [];
        public async Task<ServiceResponse> SendAsync(ServiceRequest request, CancellationToken cancellationToken)
        {
            Attempts.Add(request);
            ServiceResponse response = await service.HandleAsync(request, cancellationToken);
            if (Attempts.Count == 1) { throw new ServiceRequestUnknownException("response dropped after dispatch", null, request.RequestId, ServiceDispatchState.DispatchedAwaitingResult); }
            return response;
        }
    }
}
