using ClashTray.Contracts;
using ClashTray.Core;
using ClashTray.Service;
using ClashTray.Testing;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class ServiceCleanupAdmissionTests
{
    [TestMethod]
    public async Task MutationSaturationPreservesResultsAndLeavesCleanupItsOwnBoundedSlot()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ServiceRuntimeController service = Create(root, capacity: 1);
            ServiceRequest mutation = Request(ServiceCommand.StartCore);
            ServiceResponse original = await service.HandleAsync(mutation, CancellationToken.None);
            Assert.IsFalse(original.Succeeded, "The invalid start is retained as a completed side-effect result.");
            Assert.AreEqual(ServiceErrorCode.OperationBusy, (await service.HandleAsync(Request(ServiceCommand.StartCore), CancellationToken.None)).ErrorCode);
            ServiceRequest cleanup = Request(ServiceCommand.StopCore);
            ServiceResponse stopped = await service.HandleAsync(cleanup, CancellationToken.None);
            Assert.IsTrue(stopped.Succeeded, stopped.Error);
            Assert.AreEqual(original, await service.HandleAsync(mutation with { RecoveryOnly = true }, CancellationToken.None));
            Assert.AreEqual(stopped, await service.HandleAsync(cleanup with { RecoveryOnly = true }, CancellationToken.None));
            Assert.AreEqual(ServiceErrorCode.OperationBusy, (await service.HandleAsync(Request(ServiceCommand.StopCore), CancellationToken.None)).ErrorCode);
        });
    }

    [TestMethod]
    public async Task RepeatedCleanupCannotGrowBeyondReserveAndExpiryNeverReplays()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            Clock clock = new();
            int executions = 0;
            await using ServiceRuntimeController service = Create(root, clock: clock, observed: _ => executions++);
            List<(ServiceRequest Request, ServiceResponse Response)> results = [];
            for (int index = 0; index < 16; index++)
            {
                ServiceRequest request = Request(ServiceCommand.StopCore);
                ServiceResponse response = await service.HandleAsync(request, CancellationToken.None);
                Assert.IsTrue(response.Succeeded);
                results.Add((request, response));
            }
            Assert.AreEqual(ServiceErrorCode.OperationBusy, (await service.HandleAsync(Request(ServiceCommand.StopCore), CancellationToken.None)).ErrorCode);
            foreach ((ServiceRequest request, ServiceResponse response) in results)
            {
                Assert.AreEqual(response, await service.HandleAsync(request with { RecoveryOnly = true }, CancellationToken.None));
            }
            Assert.AreEqual(16, executions);
            clock.Advance(TimeSpan.FromMinutes(3));
            ServiceResponse unknown = await service.HandleAsync(results[0].Request with { RecoveryOnly = true }, CancellationToken.None);
            Assert.AreEqual(ServiceErrorCode.RequestResultUnavailable, unknown.ErrorCode);
            Assert.AreEqual(ServiceDispatchState.DispatchedAwaitingResult, unknown.DispatchState);
            Assert.AreEqual(16, executions);
            Assert.IsTrue((await service.HandleAsync(Request(ServiceCommand.StopCore), CancellationToken.None)).Succeeded);
            await using ServiceRuntimeController restarted = Create(root);
            ServiceResponse changed = await restarted.HandleAsync(results[0].Request with { RecoveryOnly = true, ExpectedServiceInstanceId = service.ServiceInstanceId }, CancellationToken.None);
            Assert.AreEqual(ServiceErrorCode.RequestResultUnavailable, changed.ErrorCode);
        });
    }

    [TestMethod]
    public async Task QueryPressureRetainsSingleCleanupExecutionFingerprintAndCancelledWaiting()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            using ManualResetEventSlim release = new();
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int cleanupExecutions = 0;
            await using ServiceRuntimeController service = Create(root, observed: request =>
            {
                if (request.Command != ServiceCommand.StopCore) { return; }
                Interlocked.Increment(ref cleanupExecutions);
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Cleanup barrier not released."); }
            });
            for (int index = 0; index < 128; index++) { Assert.IsTrue((await service.HandleAsync(Request(ServiceCommand.GetStatus), CancellationToken.None)).Succeeded); }
            ServiceRequest request = Request(ServiceCommand.StopCore);
            Task<ServiceResponse> owner = Task.Run(() => service.HandleAsync(request, CancellationToken.None));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<ServiceResponse>[] joins = Enumerable.Range(0, 8).Select(_ => service.HandleAsync(request, CancellationToken.None)).ToArray();
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();
            try
            {
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => service.HandleAsync(request with { RecoveryOnly = true }, cancelled.Token));
                Assert.IsFalse(owner.IsCompleted);
                Assert.AreEqual(ServiceErrorCode.RequestIdentityConflict, (await service.HandleAsync(request with { Payload = "{}" }, CancellationToken.None)).ErrorCode);
                for (int index = 0; index < 128; index++) { Assert.AreEqual(ServiceErrorCode.OperationBusy, (await service.HandleAsync(Request(ServiceCommand.GetStatus), CancellationToken.None)).ErrorCode); }
            }
            finally { release.Set(); }
            ServiceResponse[] completed = await Task.WhenAll(joins.Append(owner));
            Assert.IsTrue(completed.All(response => response.Succeeded && response == completed[0]));
            Assert.AreEqual(1, cleanupExecutions);
            Assert.AreEqual(completed[0], await service.HandleAsync(request with { RecoveryOnly = true }, CancellationToken.None));
        });
    }

    private static ServiceRuntimeController Create(string root, int capacity = 128, TimeProvider? clock = null, Action<ServiceRequest>? observed = null) =>
        new(new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program")), null, null,
            restoreOwnedProxyStates: static () => { }, requestCacheCapacity: capacity, requestTimeProvider: clock, requestExecutionObserved: observed);

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
    [TestMethod]
    public async Task CompletedStatusQueriesAtDefaultCapacityDoNotPreventCleanupExecution()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            List<ServiceCommand> executions = [];
            await using ServiceRuntimeController service = new(new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program")), null, null,
                restoreOwnedProxyStates: static () => { }, requestExecutionObserved: request => executions.Add(request.Command));
            for (int index = 0; index < 128; index++)
            {
                Assert.IsTrue((await service.HandleAsync(Request(ServiceCommand.GetStatus), CancellationToken.None)).Succeeded);
            }
            ServiceResponse stop = await service.HandleAsync(Request(ServiceCommand.StopCore), CancellationToken.None);
            ServiceResponse disable = await service.HandleAsync(Request(ServiceCommand.DisableTun), CancellationToken.None);
            Console.WriteLine($"R2: Stop={stop.ErrorCode}, DisableTun={disable.ErrorCode}, stop executions={executions.Count(c => c == ServiceCommand.StopCore)}, disable executions={executions.Count(c => c == ServiceCommand.DisableTun)}");
            Assert.AreEqual(1, executions.Count(command => command == ServiceCommand.StopCore));
            Assert.AreEqual(1, executions.Count(command => command == ServiceCommand.DisableTun));
            Assert.IsTrue(stop.Succeeded, stop.Error);
            Assert.AreEqual(CoreState.Stopped, stop.Core);
            Assert.AreNotEqual(ServiceErrorCode.OperationBusy, disable.ErrorCode);
        });
    }

    private static ServiceRequest Request(ServiceCommand command) => new(Guid.NewGuid(), command, ProtocolVersion: ServiceProtocol.CurrentVersion);
}
