using System.Collections.Concurrent;
using System.Diagnostics;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeDisposeTests
{
    [TestMethod]
    public async Task DisposeUsesBoundedAdmissionWaitAndKeepsGateAliveForOutstandingReader()
    {
        string root = CreateRoot();
        AppPaths paths = CreatePaths(root);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            null,
            disposeCleanupTimeout: TimeSpan.FromMilliseconds(300));
        runtime.SetShutdownStateForTesting(true, CoreState.Running, TunState.Off, SystemProxyState.Off);
        OperationGate.Lease reader = await runtime.AcquireSharedOperationForTestingAsync();

        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            RuntimeShutdownResult result = await runtime.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.AreEqual(ShutdownCleanupStatus.TimedOutUnknown, result.OperationGate.Status);
            Assert.AreEqual(ShutdownCleanupStatus.NotRequired, result.Tun.Status);
            Assert.AreEqual(ShutdownCleanupStatus.TimedOutUnknown, result.Core.Status);
            // If Runtime.DisposeCoreAsync disposed the gate after timing out,
            // releasing this still-admitted operation would throw.
            reader.Dispose();
        }
        finally
        {
            reader.Dispose();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task NoncriticalLogShutdownTimeoutDoesNotConsumeNetworkCleanupResult()
    {
        string root = CreateRoot();
        FakeServicePipeClient service = new((command, _) => Task.FromResult(Response(
            succeeded: true,
            tun: TunState.Off,
            core: command == ServiceCommand.StopCore ? CoreState.Stopped : CoreState.Running)));
        TaskCompletionSource logStopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseLogStop = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task PauseLogStopAsync(string step, CancellationToken _)
        {
            if (step == "停止日志流")
            {
                logStopEntered.TrySetResult();
                await releaseLogStop.Task;
            }
        }

        await using ClashTrayRuntime runtime = CreateRuntime(
            root,
            service,
            new FakeSystemProxyController(SystemProxyState.Off),
            TimeSpan.FromMilliseconds(700),
            PauseLogStopAsync);
        runtime.SetShutdownStateForTesting(
            serviceOwnsCore: true,
            CoreState.Running,
            TunState.Off,
            SystemProxyState.Off);

        try
        {
            Task<RuntimeShutdownResult> shutdownTask = runtime.ShutdownAsync();
            await logStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Stopwatch stopwatch = Stopwatch.StartNew();
            RuntimeShutdownResult result = await shutdownTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.IsTrue(result.NetworkCleanupConfirmed, result.ToDiagnosticSummary());
            Assert.AreEqual(ShutdownCleanupStatus.Completed, result.Core.Status);
            Assert.AreEqual(ShutdownCleanupStatus.NotRequired, result.Tun.Status);
            Assert.AreEqual(ShutdownCleanupStatus.NotRequired, result.SystemProxy.Status);
            Assert.AreEqual(
                ShutdownCleanupStatus.TimedOutUnknown,
                result.AdditionalSteps.Single(step => step.Name == "停止日志流").Status);
            Assert.AreEqual(ShutdownCleanupStatus.TimedOutUnknown, result.RuntimeResources.Status);
            CollectionAssert.Contains(service.Commands.ToArray(), ServiceCommand.StopCore);
        }
        finally
        {
            releaseLogStop.TrySetResult();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UnknownTunResponseNeverReportsOffAndLateServiceResponseRetainsShutdownOwnership()
    {
        string root = CreateRoot();
        TaskCompletionSource tunDisableEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ServiceResponse> releaseTunDisable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeServicePipeClient service = new((command, _) =>
        {
            if (command == ServiceCommand.DisableTun)
            {
                tunDisableEntered.TrySetResult();
                return releaseTunDisable.Task;
            }

            return Task.FromResult(Response(
                succeeded: true,
                tun: TunState.Off,
                core: CoreState.Stopped));
        });
        await using ClashTrayRuntime runtime = CreateRuntime(
            root,
            service,
            new FakeSystemProxyController(SystemProxyState.Off),
            TimeSpan.FromMilliseconds(600));
        runtime.SetShutdownStateForTesting(
            serviceOwnsCore: true,
            CoreState.Running,
            TunState.On,
            SystemProxyState.Off);

        try
        {
            Task<RuntimeShutdownResult> shutdownTask = runtime.ShutdownAsync();
            Assert.AreSame(shutdownTask, runtime.ShutdownAsync());
            await tunDisableEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            RuntimeShutdownResult result = await shutdownTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(ShutdownCleanupStatus.TimedOutUnknown, result.Tun.Status);
            Assert.AreEqual(ShutdownCleanupStatus.TimedOutUnknown, result.Core.Status);
            Assert.IsTrue(runtime.Snapshot.Tun is TunState.Disabling or TunState.Failed);
            Assert.AreNotEqual(TunState.Off, runtime.Snapshot.Tun);
            Assert.IsFalse(service.Commands.Contains(ServiceCommand.StopCore));
            Assert.IsTrue(result.RecoveryResponsibilities.Any(
                item => item.Owner == "Existing Mihomo service"));

            releaseTunDisable.TrySetResult(Response(
                succeeded: true,
                tun: TunState.Off,
                core: CoreState.Running));
            await WaitForTunStateAsync(runtime, TunState.Off, TimeSpan.FromSeconds(2));

            Assert.AreEqual(1, service.Commands.Count(command => command == ServiceCommand.DisableTun));
            Assert.AreEqual(ShutdownCleanupStatus.TimedOutUnknown, result.Tun.Status);
            Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State);
        }
        finally
        {
            releaseTunDisable.TrySetResult(Response(
                succeeded: true,
                tun: TunState.Off,
                core: CoreState.Running));
            await WaitForTunStateAsync(runtime, TunState.Off, TimeSpan.FromSeconds(2));
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UnavailableServiceDoesNotStopCoreWhileTunStateIsUnconfirmed()
    {
        string root = CreateRoot();
        FakeServicePipeClient service = new((command, _) =>
        {
            if (command == ServiceCommand.DisableTun)
            {
                return Task.FromException<ServiceResponse>(new IOException("simulated unavailable service"));
            }

            return Task.FromResult(Response(true, TunState.Off, CoreState.Stopped));
        });
        await using ClashTrayRuntime runtime = CreateRuntime(
            root,
            service,
            new FakeSystemProxyController(SystemProxyState.Off),
            TimeSpan.FromSeconds(2));
        runtime.SetShutdownStateForTesting(
            serviceOwnsCore: true,
            CoreState.Running,
            TunState.On,
            SystemProxyState.Off);

        try
        {
            RuntimeShutdownResult result = await runtime.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(ShutdownCleanupStatus.Failed, result.Tun.Status);
            Assert.AreEqual(ShutdownCleanupStatus.TimedOutUnknown, result.Core.Status);
            Assert.IsFalse(service.Commands.Contains(ServiceCommand.StopCore));
            Assert.AreNotEqual(TunState.Off, runtime.Snapshot.Tun);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ProxyOwnershipConflictIsReportedAndPersistentJournalOwnsLaterRecovery()
    {
        string root = CreateRoot();
        AppPaths paths = CreatePaths(root);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.ProxyOwnershipFile, "simulated ownership record");
        FakeServicePipeClient service = new((command, _) => Task.FromResult(Response(
            true,
            TunState.Off,
            command == ServiceCommand.StopCore ? CoreState.Stopped : CoreState.Running)));
        FakeSystemProxyController proxy = new(SystemProxyState.RestoreRequired);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            service,
            null,
            proxy,
            disposeCleanupTimeout: TimeSpan.FromSeconds(2));
        runtime.SetShutdownStateForTesting(
            serviceOwnsCore: true,
            CoreState.Running,
            TunState.Off,
            SystemProxyState.RestoreRequired);

        try
        {
            RuntimeShutdownResult result = await runtime.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(ShutdownCleanupStatus.Failed, result.SystemProxy.Status);
            Assert.AreEqual(SystemProxyState.RestoreRequired, proxy.State);
            Assert.AreEqual(1, proxy.DisableCount);
            ShutdownRecoveryResponsibility responsibility = result.RecoveryResponsibilities.Single(
                item => item.Owner == "System Proxy ownership journal");
            StringAssert.Contains(responsibility.Trigger, "next app launch", StringComparison.Ordinal);
            StringAssert.Contains(responsibility.Limitation, "not in this exiting process", StringComparison.Ordinal);
            Assert.IsTrue(service.Commands.Contains(ServiceCommand.StopCore));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task LocalCoreGateTimeoutPersistsJournalForNextRuntimeRecovery()
    {
        string root = CreateRoot();
        AppPaths paths = CreatePaths(root);
        LocalCoreProcessIdentity identity = new(
            42_424,
            DateTimeOffset.UnixEpoch.AddDays(100).UtcDateTime.Ticks,
            paths.ManagedCoreExecutable);
        await using ClashTrayRuntime runtime = new(
            paths,
            startupRegistration: null,
            servicePipeClient: null,
            settingsStore: null,
            systemProxy: new FakeSystemProxyController(SystemProxyState.Off),
            disposeCleanupTimeout: TimeSpan.FromSeconds(1.5),
            localCoreProcessIdentityProvider: () => identity);
        runtime.SetShutdownStateForTesting(
            serviceOwnsCore: false,
            CoreState.Running,
            TunState.Off,
            SystemProxyState.Off);
        OperationGate.Lease reader = await runtime.AcquireSharedOperationForTestingAsync();

        try
        {
            RuntimeShutdownResult result = await runtime.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(ShutdownCleanupStatus.TimedOutUnknown, result.OperationGate.Status);
            Assert.AreEqual(ShutdownCleanupStatus.TimedOutUnknown, result.Core.Status);
            Assert.AreEqual(ShutdownCleanupStatus.Completed, result.LocalCoreRecovery.Status);
            Assert.IsTrue(File.Exists(paths.LocalCoreShutdownFile));
            Assert.AreEqual(
                "Local core shutdown journal",
                result.RecoveryResponsibilities.Single().Owner);

            reader.Dispose();
            LocalCoreProcessIdentity? recoveredIdentity = null;
            await using ClashTrayRuntime restartedRuntime = new(
                paths,
                startupRegistration: null,
                servicePipeClient: null,
                settingsStore: null,
                systemProxy: new FakeSystemProxyController(SystemProxyState.Off),
                localCoreRecoveryAction: (pending, _) =>
                {
                    recoveredIdentity = pending;
                    return Task.FromResult(new LocalCoreShutdownJournalResult(
                        Succeeded: true,
                        RecordFound: true,
                        Detail: "fake owned child stopped after previous app process exit"));
                });

            await restartedRuntime.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(identity, recoveredIdentity);
            Assert.IsFalse(File.Exists(paths.LocalCoreShutdownFile));
        }
        finally
        {
            reader.Dispose();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task IAsyncDisposableStillWaitsForTheSameStructuredShutdownTask()
    {
        string root = CreateRoot();
        await using ClashTrayRuntime runtime = CreateRuntime(
            root,
            new FakeServicePipeClient((_, _) => Task.FromResult(Response(
                true,
                TunState.Off,
                CoreState.Stopped))),
            new FakeSystemProxyController(SystemProxyState.Off),
            TimeSpan.FromSeconds(2));

        RuntimeShutdownResult result = await runtime.ShutdownAsync();
        await runtime.DisposeAsync();

        Assert.IsTrue(result.IsFullyClean, result.ToDiagnosticSummary());
        DeleteRoot(root);
    }

    private static ClashTrayRuntime CreateRuntime(
        string root,
        FakeServicePipeClient service,
        FakeSystemProxyController proxy,
        TimeSpan timeout,
        Func<string, CancellationToken, Task>? shutdownStepTestHook = null) =>
        new(
            CreatePaths(root),
            null,
            service,
            null,
            proxy,
            disposeCleanupTimeout: timeout,
            shutdownStepTestHook: shutdownStepTestHook);

    private static AppPaths CreatePaths(string root) =>
        new(Path.Combine(root, "local"), Path.Combine(root, "service"));

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ServiceResponse Response(bool succeeded, TunState tun, CoreState core) =>
        new(Guid.NewGuid(), succeeded, tun, Core: core);

    private static async Task WaitForTunStateAsync(
        ClashTrayRuntime runtime,
        TunState expected,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (runtime.Snapshot.Tun != expected && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(10);
        }

        Assert.AreEqual(expected, runtime.Snapshot.Tun);
    }

    private sealed class FakeServicePipeClient(
        Func<ServiceCommand, CancellationToken, Task<ServiceResponse>> handler) : IServicePipeClient
    {
        private readonly ConcurrentQueue<ServiceCommand> _commands = new();

        public IReadOnlyCollection<ServiceCommand> Commands => _commands.ToArray();

        public Task<ServiceResponse> SendAsync(
            ServiceCommand command,
            string? payload = null,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command);
            return handler(command, cancellationToken);
        }
    }

    private sealed class FakeSystemProxyController(SystemProxyState initialState) : ISystemProxyController
    {
        public SystemProxyState State { get; private set; } = initialState;

        public int DisableCount { get; private set; }

        public SystemProxyState DetectState() => State;

        public Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default)
        {
            State = SystemProxyState.On;
            return Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken = default)
        {
            DisableCount++;
            if (State != SystemProxyState.RestoreRequired)
            {
                State = SystemProxyState.Off;
            }

            return Task.CompletedTask;
        }
    }
}
