using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;
using ClashTray.Testing;
using ClashTray.Service;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class OfficialMihomoServiceInteropTests
{
    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task SaturatedObservationAndMutationQuotasStillConfirmTunOffAndStopTheOwnedOfficialProcess()
    {
        string? executable = FindMihomoExecutable();
        if (executable is null) { Assert.Inconclusive("Official pinned Mihomo is required for real cleanup admission."); return; }
        (string root, AppPaths paths, string runtimeDirectory) = await CreateOfficialCoreFixtureAsync(executable);
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            (string configuration, ServiceCorePayload payload) = await WriteCleanupConfigurationAsync(runtimeDirectory);
            DisabledTunNetworkHealthProbe probe = new();
            await using ServiceRuntimeController controller = new(paths, null, probe, restoreOwnedProxyStates: static () => { });
            ServiceRequest startRequest = new(Guid.NewGuid(), ServiceCommand.StartCore, JsonSerializer.Serialize(payload), ProtocolVersion: ServiceProtocol.CurrentVersion);
            ServiceResponse start = await controller.HandleAsync(startRequest, CancellationToken.None);
            Assert.IsTrue(start.Succeeded, start.Error);
            Assert.IsNotNull(start.RuntimeBinding);
            CoreRuntimeBinding binding = start.RuntimeBinding;
            using System.Diagnostics.Process ownedProcess = System.Diagnostics.Process.GetProcessById(binding.ProcessId);
            ServiceTunPayload tun = new(binding.ControllerPort, string.Empty, false, binding.InstanceId, binding.OwnerInstanceId);
            int probesBeforeStale = probe.DisabledProbeCalls;
            ServiceResponse stale = await controller.HandleAsync(new(Guid.NewGuid(), ServiceCommand.DisableTun,
                JsonSerializer.Serialize(tun with { InstanceId = Guid.NewGuid() }), ProtocolVersion: ServiceProtocol.CurrentVersion), CancellationToken.None);
            Assert.IsFalse(stale.Succeeded);
            Assert.AreEqual(ServiceErrorCode.ControllerOwnershipUnconfirmed, stale.ErrorCode);
            Assert.AreEqual(probesBeforeStale, probe.DisabledProbeCalls, "A stale binding never reaches the TUN transaction.");
            for (int index = 0; index < 128; index++)
            {
                Assert.IsTrue((await controller.HandleAsync(new(Guid.NewGuid(), ServiceCommand.GetStatus, ProtocolVersion: ServiceProtocol.CurrentVersion), CancellationToken.None)).Succeeded);
            }
            // The start and stale request occupy two ordinary result slots.
            for (int index = 0; index < 126; index++)
            {
                ServiceResponse invalid = await controller.HandleAsync(new(Guid.NewGuid(), ServiceCommand.StartCore, ProtocolVersion: ServiceProtocol.CurrentVersion), CancellationToken.None);
                Assert.IsFalse(invalid.Succeeded);
                Assert.AreNotEqual(ServiceErrorCode.OperationBusy, invalid.ErrorCode);
            }
            Assert.AreEqual(ServiceErrorCode.OperationBusy, (await controller.HandleAsync(new(Guid.NewGuid(), ServiceCommand.StartCore, ProtocolVersion: ServiceProtocol.CurrentVersion), CancellationToken.None)).ErrorCode);
            int probesBeforeDisable = probe.DisabledProbeCalls;
            ServiceRequest disableRequest = new(Guid.NewGuid(), ServiceCommand.DisableTun, JsonSerializer.Serialize(tun), ProtocolVersion: ServiceProtocol.CurrentVersion);
            ServiceResponse disabled = await controller.HandleAsync(disableRequest, CancellationToken.None);
            Assert.IsTrue(disabled.Succeeded, disabled.Error);
            Assert.AreEqual(TunState.Off, disabled.Tun);
            Assert.IsGreaterThan(probesBeforeDisable, probe.DisabledProbeCalls, "The service must execute the real controller/probe confirmation, not merely accept the request.");
            ServiceRequest stopRequest = new(Guid.NewGuid(), ServiceCommand.StopCore, ProtocolVersion: ServiceProtocol.CurrentVersion);
            ServiceResponse stopped = await controller.HandleAsync(stopRequest, CancellationToken.None);
            Assert.IsTrue(stopped.Succeeded, stopped.Error);
            Assert.AreEqual(CoreState.Stopped, stopped.Core);
            using CancellationTokenSource exitDeadline = new(TimeSpan.FromSeconds(5));
            await ownedProcess.WaitForExitAsync(exitDeadline.Token);
            Assert.IsTrue(ownedProcess.HasExited);
            Assert.AreEqual(start, await controller.HandleAsync(startRequest with { RecoveryOnly = true }, CancellationToken.None));
            Assert.AreEqual(disabled, await controller.HandleAsync(disableRequest with { RecoveryOnly = true }, CancellationToken.None));
            Assert.AreEqual(stopped, await controller.HandleAsync(stopRequest with { RecoveryOnly = true }, CancellationToken.None));
            Console.WriteLine($"R2 actual official process: PID={binding.ProcessId}, HasExited={ownedProcess.HasExited}, disabledProbes={probe.DisabledProbeCalls}, preserved start/disable/stop results=True; config={Path.GetFileName(configuration)}");
        });
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task WaitingCleanupGetsExecutionAfterHeldStartDespiteContinuedOrdinaryRequests()
    {
        string? executable = FindMihomoExecutable();
        if (executable is null) { Assert.Inconclusive("Official pinned Mihomo is required for cleanup scheduling."); return; }
        (string root, AppPaths paths, string runtimeDirectory) = await CreateOfficialCoreFixtureAsync(executable);
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            (_, ServiceCorePayload payload) = await WriteCleanupConfigurationAsync(runtimeDirectory);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using ServiceRuntimeController controller = new(paths, null, new DisabledTunNetworkHealthProbe(), restoreOwnedProxyStates: static () => { },
                afterCoreStartForTest: async (_, token) => { started.TrySetResult(); await release.Task.WaitAsync(token); });
            Task<ServiceResponse> start = StartServiceCoreAsync(controller, payload);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            Task<ServiceResponse> cleanup = controller.HandleAsync(new(Guid.NewGuid(), ServiceCommand.StopCore, ProtocolVersion: ServiceProtocol.CurrentVersion), CancellationToken.None);
            try
            {
                Assert.IsFalse(cleanup.IsCompleted, "Accepted cleanup waits for the conflicting start rather than caching OperationBusy.");
                for (int index = 0; index < 256; index++)
                {
                    await controller.HandleAsync(new(Guid.NewGuid(), ServiceCommand.GetStatus, ProtocolVersion: ServiceProtocol.CurrentVersion), CancellationToken.None);
                }
                for (int index = 0; index < 16; index++)
                {
                    ServiceResponse ordinary = await controller.HandleAsync(new(Guid.NewGuid(), ServiceCommand.RollbackCore, ProtocolVersion: ServiceProtocol.CurrentVersion), CancellationToken.None);
                    Assert.AreEqual(ServiceErrorCode.OperationBusy, ordinary.ErrorCode);
                }
                Assert.IsFalse(cleanup.IsCompleted);
            }
            finally { release.TrySetResult(); }
            Assert.IsTrue((await start).Succeeded);
            ServiceResponse stop = await cleanup.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsTrue(stop.Succeeded, stop.Error);
            Assert.AreEqual(CoreState.Stopped, controller.CoreState);
            Assert.IsLessThan(TimeSpan.FromSeconds(30), elapsed.Elapsed);
            Console.WriteLine($"R2 cleanup completed under continued pressure within original 30s deadline: {elapsed.Elapsed}; state={controller.CoreState}");
        });
    }

    private static async Task<(string Configuration, ServiceCorePayload Payload)> WriteCleanupConfigurationAsync(string runtimeDirectory)
    {
        int controllerPort = GetAvailableLoopbackPort();
        int mixedPort = GetAvailableLoopbackPort();
        string configuration = Path.Combine(runtimeDirectory, "cleanup-admission.yaml");
        await File.WriteAllTextAsync(configuration,
            $"mixed-port: {mixedPort}\nexternal-controller: 127.0.0.1:{controllerPort}\nsecret: \"\"\nallow-lan: false\nipv6: false\nmode: rule\nlog-level: info\nproxies: []\nproxy-groups: []\nrules: []\ntun:\n  enable: false\n");
        return (configuration, new(configuration, runtimeDirectory, controllerPort, string.Empty, MixedPort: mixedPort));
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task QueuedCleanupDeadlineReturnsRecoverableTimeoutWithoutClaimingProcessStopped()
    {
        string? executable = FindMihomoExecutable();
        if (executable is null) { Assert.Inconclusive("Official pinned Mihomo is required for queued cleanup expiry."); return; }
        (string root, AppPaths paths, string runtimeDirectory) = await CreateOfficialCoreFixtureAsync(executable);
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            (_, ServiceCorePayload payload) = await WriteCleanupConfigurationAsync(runtimeDirectory);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<CancellationTokenSource> cleanupDeadline = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int deadlines = 0;
            await using ServiceRuntimeController controller = new(paths, null, new DisabledTunNetworkHealthProbe(), restoreOwnedProxyStates: static () => { },
                afterCoreStartForTest: async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); },
                operationDeadlineForTest: lifetime =>
                {
                    CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    if (Interlocked.Increment(ref deadlines) == 2) { cleanupDeadline.TrySetResult(deadline); }
                    return deadline;
                });
            Task<ServiceResponse> start = StartServiceCoreAsync(controller, payload);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            ServiceRequest request = new(Guid.NewGuid(), ServiceCommand.StopCore, ProtocolVersion: ServiceProtocol.CurrentVersion);
            Task<ServiceResponse> cleanup = controller.HandleAsync(request, CancellationToken.None);
            try
            {
                Assert.IsFalse(cleanup.IsCompleted);
                CancellationTokenSource deadline = await cleanupDeadline.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await deadline.CancelAsync();
                ServiceResponse timedOut = await cleanup;
                Assert.IsFalse(timedOut.Succeeded);
                Assert.AreEqual(ServiceErrorCode.OperationTimedOut, timedOut.ErrorCode);
                Assert.AreNotEqual(CoreState.Stopped, controller.CoreState);
                Assert.AreEqual(timedOut, await controller.HandleAsync(request with { RecoveryOnly = true }, CancellationToken.None));
            }
            finally { release.TrySetResult(); }
            Assert.IsTrue((await start).Succeeded);
            ServiceResponse stopped = await controller.HandleAsync(new(Guid.NewGuid(), ServiceCommand.StopCore, ProtocolVersion: ServiceProtocol.CurrentVersion), CancellationToken.None);
            Assert.IsTrue(stopped.Succeeded, stopped.Error);
            Assert.AreEqual(CoreState.Stopped, stopped.Core);
        });
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task ServiceAvoidsForeignCompatibleControllerWithoutSendingAnyWriteRequest()
    {
        string? executablePath = FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive("Official pinned Mihomo is required for the controller ownership regression.");
            return;
        }

        string root = TestFixtureDirectory.Create();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureProgramDataDirectories();
        string runtimeDirectory = Path.Combine(paths.RuntimeRoot, "mihomo");
        Directory.CreateDirectory(runtimeDirectory);
        File.Copy(executablePath, paths.ManagedCoreExecutable);
        string executableSha256 = await ComputeSha256Async(paths.ManagedCoreExecutable);
        await File.WriteAllTextAsync(
            paths.ManagedCoreMetadata,
            JsonSerializer.Serialize(new ManagedCoreMetadata(
                BundledMihomo.Version,
                new Uri($"https://github.com/MetaCubeX/mihomo/releases/download/{BundledMihomo.Version}/mihomo-windows-amd64-{BundledMihomo.Version}.zip"),
                OfficialMihomoTestSupport.PinnedArchiveSha256,
                executableSha256)));

        int controllerPort = GetAvailableLoopbackPort();
        int mixedPort = GetAvailableLoopbackPort();
        await using ForeignCompatibleController foreignController = new(controllerPort);
        string configurationPath = Path.Combine(runtimeDirectory, "active.yaml");
        await File.WriteAllTextAsync(
            configurationPath,
            $"mixed-port: {mixedPort}{Environment.NewLine}"
            + $"external-controller: 127.0.0.1:{controllerPort}{Environment.NewLine}"
            + $"secret: \"\"{Environment.NewLine}"
            + $"allow-lan: false{Environment.NewLine}"
            + $"ipv6: false{Environment.NewLine}"
            + $"mode: rule{Environment.NewLine}"
            + $"log-level: info{Environment.NewLine}"
            + $"proxies: []{Environment.NewLine}"
            + $"proxy-groups: []{Environment.NewLine}"
            + $"rules: []{Environment.NewLine}"
            + $"tun:{Environment.NewLine}"
            + $"  enable: false{Environment.NewLine}");

        ServiceCorePayload payload = new(
            configurationPath,
            runtimeDirectory,
            controllerPort,
            string.Empty,
            MixedPort: mixedPort);
        ServiceRequest request = new(
            Guid.NewGuid(),
            ServiceCommand.StartCore,
            JsonSerializer.Serialize(payload),
            ProtocolVersion: ServiceProtocol.CurrentVersion);

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ServiceRuntimeController controller = new(
                paths,
                managedUserSid: null,
                tunHealthProbe: new DisabledTunNetworkHealthProbe(),
                restoreOwnedProxyStates: static () => { });

            ServiceResponse response = await controller.HandleAsync(request, CancellationToken.None);

            Assert.IsTrue(response.Succeeded, response.Error);
            Assert.IsFalse(
                foreignController.Requests.Any(IsControllerWriteMethod),
                "A listener owned by another process must receive zero controller writes.");
            Assert.IsNotNull(response.RuntimeBinding);
            Assert.AreEqual(controllerPort, response.RuntimeBinding.PreferredControllerPort);
            Assert.AreNotEqual(controllerPort, response.RuntimeBinding.ControllerPort);

            ServiceResponse stop = await controller.HandleAsync(
                new ServiceRequest(Guid.NewGuid(), ServiceCommand.StopCore, ProtocolVersion: ServiceProtocol.CurrentVersion),
                CancellationToken.None);
            Assert.IsTrue(stop.Succeeded, stop.Error);
        }, async () =>
        {
            await DeleteTemporaryDirectoryAsync(root);
        });
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task ServiceConfirmsProxyListenersOnTheConfiguredBindAddress()
    {
        string? executablePath = FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive("Official pinned Mihomo is required for bind-address listener ownership coverage.");
            return;
        }

        (string root, AppPaths paths, string runtimeDirectory) = await CreateOfficialCoreFixtureAsync(executablePath);
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            int controllerPort = GetAvailableLoopbackPort();
            int mixedPort = GetAvailableLoopbackPort();
            string configurationPath = Path.Combine(runtimeDirectory, "specified-bind-address.yaml");
            await File.WriteAllTextAsync(
                configurationPath,
                $"mixed-port: {mixedPort}{Environment.NewLine}"
                + $"external-controller: 127.0.0.1:{controllerPort}{Environment.NewLine}"
                + $"secret: \"\"{Environment.NewLine}"
                + $"allow-lan: true{Environment.NewLine}"
                + $"bind-address: 127.0.0.2{Environment.NewLine}"
                + $"ipv6: false{Environment.NewLine}"
                + $"mode: rule{Environment.NewLine}"
                + $"log-level: info{Environment.NewLine}"
                + $"proxies: []{Environment.NewLine}"
                + $"proxy-groups: []{Environment.NewLine}"
                + $"rules: []{Environment.NewLine}"
                + $"tun:{Environment.NewLine}"
                + $"  enable: false{Environment.NewLine}");

            await using ServiceRuntimeController controller = new(
                paths,
                managedUserSid: null,
                tunHealthProbe: new DisabledTunNetworkHealthProbe(),
                restoreOwnedProxyStates: static () => { });

            ServiceResponse start = await StartServiceCoreAsync(
                controller,
                new ServiceCorePayload(
                    configurationPath,
                    runtimeDirectory,
                    controllerPort,
                    string.Empty,
                    MixedPort: mixedPort,
                    AllowLan: true));

            Assert.IsTrue(start.Succeeded, start.Error);
            Assert.IsNotNull(start.RuntimeBinding);
            Assert.AreEqual(mixedPort, start.RuntimeBinding.MixedPort);
            Assert.IsNotNull(start.RuntimeBinding.ListenerBindings);
            RuntimeListenerBinding[] mixedBindings = start.RuntimeBinding.ListenerBindings
                .Where(binding => binding.Name.StartsWith("mixed-", StringComparison.Ordinal))
                .ToArray();
            Assert.AreEqual(2, mixedBindings.Length);
            Assert.IsTrue(mixedBindings.All(binding => binding.Address == "127.0.0.2"));
            Assert.IsTrue(mixedBindings.Any(binding => binding.Transport == RuntimeListenerTransport.Tcp));
            Assert.IsTrue(mixedBindings.Any(binding => binding.Transport == RuntimeListenerTransport.Udp));
            Assert.IsTrue(start.RuntimeBinding.ListenerBindings.Any(binding =>
                binding.Name == "controller"
                && binding.Address == "127.0.0.1"
                && binding.Port == start.RuntimeBinding.ControllerPort
                && binding.Transport == RuntimeListenerTransport.Tcp));

            ServiceResponse stop = await controller.HandleAsync(
                new ServiceRequest(Guid.NewGuid(), ServiceCommand.StopCore, ProtocolVersion: ServiceProtocol.CurrentVersion),
                CancellationToken.None);
            Assert.IsTrue(stop.Succeeded, stop.Error);
        }, async () =>
        {
            if (Directory.Exists(root))
            {
                await DeleteTemporaryDirectoryAsync(root);
            }
        });
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task ServiceConfirmsWildcardProxyListenersWithoutChangingLoopbackController()
    {
        string? executablePath = FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive("Official pinned Mihomo is required for wildcard listener ownership coverage.");
            return;
        }

        (string root, AppPaths paths, string runtimeDirectory) = await CreateOfficialCoreFixtureAsync(executablePath);
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            int controllerPort = GetAvailableLoopbackPort();
            int mixedPort = GetAvailableLoopbackPort();
            string configurationPath = Path.Combine(runtimeDirectory, "wildcard-bind-address.yaml");
            await File.WriteAllTextAsync(
                configurationPath,
                $"mixed-port: {mixedPort}{Environment.NewLine}"
                + $"external-controller: 127.0.0.1:{controllerPort}{Environment.NewLine}"
                + $"secret: \"\"{Environment.NewLine}"
                + $"allow-lan: true{Environment.NewLine}"
                + $"bind-address: \"*\"{Environment.NewLine}"
                + $"ipv6: false{Environment.NewLine}"
                + $"mode: rule{Environment.NewLine}"
                + $"log-level: info{Environment.NewLine}"
                + $"proxies: []{Environment.NewLine}"
                + $"proxy-groups: []{Environment.NewLine}"
                + $"rules: []{Environment.NewLine}"
                + $"tun:{Environment.NewLine}"
                + $"  enable: false{Environment.NewLine}");

            await using ServiceRuntimeController controller = new(
                paths,
                managedUserSid: null,
                tunHealthProbe: new DisabledTunNetworkHealthProbe(),
                restoreOwnedProxyStates: static () => { });

            ServiceResponse start = await StartServiceCoreAsync(
                controller,
                new ServiceCorePayload(
                    configurationPath,
                    runtimeDirectory,
                    controllerPort,
                    string.Empty,
                    MixedPort: mixedPort,
                    AllowLan: true));

            Assert.IsTrue(start.Succeeded, start.Error);
            Assert.IsNotNull(start.RuntimeBinding);
            Assert.IsNotNull(start.RuntimeBinding.ListenerBindings);
            RuntimeListenerBinding[] mixedBindings = start.RuntimeBinding.ListenerBindings
                .Where(binding => binding.Name.StartsWith("mixed-", StringComparison.Ordinal))
                .ToArray();
            Assert.AreEqual(2, mixedBindings.Length);
            Assert.IsTrue(mixedBindings.All(binding => binding.Address == IPAddress.IPv6Any.ToString()));
            Assert.IsTrue(mixedBindings.All(binding => binding.DualMode));
            Assert.IsTrue(mixedBindings.Any(binding => binding.Transport == RuntimeListenerTransport.Tcp));
            Assert.IsTrue(mixedBindings.Any(binding => binding.Transport == RuntimeListenerTransport.Udp));
            Assert.IsTrue(start.RuntimeBinding.ListenerBindings.Any(binding =>
                binding.Name == "controller"
                && binding.Address == IPAddress.Loopback.ToString()
                && binding.Port == start.RuntimeBinding.ControllerPort
                && binding.Transport == RuntimeListenerTransport.Tcp
                && !binding.DualMode));

            ServiceResponse stop = await controller.HandleAsync(
                new ServiceRequest(Guid.NewGuid(), ServiceCommand.StopCore, ProtocolVersion: ServiceProtocol.CurrentVersion),
                CancellationToken.None);
            Assert.IsTrue(stop.Succeeded, stop.Error);
        }, async () =>
        {
            if (Directory.Exists(root))
            {
                await DeleteTemporaryDirectoryAsync(root);
            }
        });
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task ServiceRetriesWhenControllerPortIsTakenAfterPreflightWithoutControllingForeignMihomo()
    {
        string? executablePath = FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive("Official pinned Mihomo is required for the controller preflight race regression.");
            return;
        }

        (string root, AppPaths paths, string runtimeDirectory) = await CreateOfficialCoreFixtureAsync(executablePath);
        ForeignCompatibleController? competingController = null;
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            int preferredControllerPort = GetAvailableLoopbackPort();
            int mixedPort = GetAvailableLoopbackPort();
            string configurationPath = Path.Combine(runtimeDirectory, "controller-race.yaml");
            string configuration = BuildConflictConfiguration(preferredControllerPort, mixedPort);
            await File.WriteAllTextAsync(configurationPath, configuration);
            ServiceCorePayload payload = new(
                configurationPath,
                runtimeDirectory,
                preferredControllerPort,
                string.Empty,
                ControllerPortConflictPolicy.AutomaticFallback,
                MixedPort: mixedPort);

            await using ServiceRuntimeController controller = new(
                paths,
                managedUserSid: null,
                tunHealthProbe: new DisabledTunNetworkHealthProbe(),
                restoreOwnedProxyStates: static () => { },
                beforeCoreStartForTest: port => competingController ??= new ForeignCompatibleController(port));

            ServiceResponse start = await StartServiceCoreAsync(controller, payload);
            Assert.IsTrue(start.Succeeded, start.Error);
            Assert.IsNotNull(start.RuntimeBinding);
            Assert.AreEqual(preferredControllerPort, start.RuntimeBinding.PreferredControllerPort);
            Assert.AreNotEqual(preferredControllerPort, start.RuntimeBinding.ControllerPort);
            Assert.IsNotNull(competingController);

            ServiceResponse stop = await controller.HandleAsync(
                new ServiceRequest(Guid.NewGuid(), ServiceCommand.StopCore, ProtocolVersion: ServiceProtocol.CurrentVersion),
                CancellationToken.None);
            Assert.IsTrue(stop.Succeeded, stop.Error);

            using HttpClient foreignClient = new();
            using HttpResponseMessage foreignResponse = await foreignClient.GetAsync(
                new Uri($"http://127.0.0.1:{competingController.Port}/version"));
            Assert.AreEqual(HttpStatusCode.OK, foreignResponse.StatusCode, "Stopping the managed core must leave the other controller alive.");
            Assert.IsFalse(competingController.Requests.Any(IsControllerWriteMethod));
        }, async () =>
        {
            if (competingController is not null)
            {
                await competingController.DisposeAsync();
            }

            if (Directory.Exists(root))
            {
                await DeleteTemporaryDirectoryAsync(root);
            }
        });
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task ServiceReclaimsOwnedCoreWhenStartupIsCanceledBeforeReadyCommit()
    {
        string? executablePath = FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive("Official pinned Mihomo is required for startup cancellation cleanup coverage.");
            return;
        }

        (string root, AppPaths paths, string runtimeDirectory) = await CreateOfficialCoreFixtureAsync(executablePath);
        TaskCompletionSource<int> coreStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ServiceRuntimeController? controller = null;
        bool disposed = false;
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            int controllerPort = GetAvailableLoopbackPort();
            int mixedPort = GetAvailableLoopbackPort();
            string configurationPath = Path.Combine(runtimeDirectory, "startup-cancel.yaml");
            await File.WriteAllTextAsync(configurationPath, BuildConflictConfiguration(controllerPort, mixedPort));
            ServiceCorePayload payload = new(
                configurationPath,
                runtimeDirectory,
                controllerPort,
                string.Empty,
                MixedPort: mixedPort);
            controller = new ServiceRuntimeController(
                paths,
                managedUserSid: null,
                tunHealthProbe: new DisabledTunNetworkHealthProbe(),
                restoreOwnedProxyStates: static () => { },
                afterCoreStartForTest: async (port, cancellationToken) =>
                {
                    coreStarted.TrySetResult(port);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                });

            Task<ServiceResponse> start = StartServiceCoreAsync(controller, payload);
            Task first = await Task.WhenAny(coreStarted.Task, start, Task.Delay(TimeSpan.FromSeconds(10)));
            if (ReferenceEquals(first, start))
            {
                ServiceResponse earlyResponse = await start;
                Assert.Fail($"Core start completed before the cancellation checkpoint: {earlyResponse.Error}");
            }

            Assert.IsTrue(ReferenceEquals(first, coreStarted.Task), "Core start did not reach the cancellation checkpoint within 10 seconds.");
            Assert.AreEqual(controllerPort, await coreStarted.Task);

            await controller.DisposeAsync();
            disposed = true;
            ServiceResponse canceledStart = await start;
            Assert.IsFalse(canceledStart.Succeeded, canceledStart.Error);
            Assert.AreEqual(CoreState.Stopped, controller.CoreState);
            Assert.IsFalse(WindowsListenerOwnerTable.HasListener(controllerPort, PortTransport.Tcp));
            Assert.IsFalse(WindowsListenerOwnerTable.HasListener(mixedPort, PortTransport.Tcp));
        }, async () =>
        {
            if (controller is not null && !disposed)
            {
                await controller.DisposeAsync();
            }

            if (Directory.Exists(root))
            {
                await DeleteTemporaryDirectoryAsync(root);
            }
        });
    }

    private static bool IsControllerWriteMethod(string requestLine)
    {
        string method = requestLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return method is "POST" or "PUT" or "PATCH" or "DELETE";
    }

    private sealed class ForeignCompatibleController : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _serverTask;
        private bool _tunEnabled = true;

        public ForeignCompatibleController(int port)
        {
            Port = port;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _serverTask = ServeAsync(_lifetime.Token);
        }

        public int Port { get; }

        public ConcurrentQueue<string> Requests { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _listener.Stop();
            _listener.Dispose();
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }

            _lifetime.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                using NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                string requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
                Requests.Enqueue(requestLine);
                string[] parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
                {
                }

                if (parts.Length < 2)
                {
                    continue;
                }

                if (parts[0] == "PATCH" && parts[1] == "/configs")
                {
                    _tunEnabled = false;
                }

                string body = parts[1] switch
                {
                    "/version" => "{\"version\":\"v1.19.31\"}",
                    "/configs" => $"{{\"tun\":{{\"enable\":{(_tunEnabled ? "true" : "false")} }},\"mixed-port\":0 }}",
                    _ => "{}"
                };
                byte[] responseBody = Encoding.UTF8.GetBytes(body);
                byte[] header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBody.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(responseBody, cancellationToken);
            }
        }
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task ServiceStartsRestartsAndStopsPinnedMihomoWithTunPermanentlyDisabled()
    {
        string? executablePath = FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive(
                "Official Mihomo payload not found. Set CLASHTRAY_MIHOMO_PATH or build the packaging payload to run this test.");
            return;
        }

        ManagedCoreVerifier.ValidateWindowsAmd64Executable(executablePath);
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new(
            Path.Combine(root, "local"),
            Path.Combine(root, "program"));
        paths.EnsureProgramDataDirectories();
        string runtimeDirectory = Path.Combine(paths.RuntimeRoot, "mihomo");
        Directory.CreateDirectory(runtimeDirectory);
        File.Copy(executablePath, paths.ManagedCoreExecutable);
        string executableSha256 = await ComputeSha256Async(paths.ManagedCoreExecutable);
        await File.WriteAllTextAsync(
            paths.ManagedCoreMetadata,
            JsonSerializer.Serialize(new ManagedCoreMetadata(
                BundledMihomo.Version,
                new Uri(
                    $"https://github.com/MetaCubeX/mihomo/releases/download/{BundledMihomo.Version}/mihomo-windows-amd64-{BundledMihomo.Version}.zip"),
                OfficialMihomoTestSupport.PinnedArchiveSha256,
                executableSha256)));

        int controllerPort = GetAvailableLoopbackPort();
        int mixedPort = GetAvailableLoopbackPort();
        string configurationPath = Path.Combine(runtimeDirectory, "active.yaml");
        await File.WriteAllTextAsync(
            configurationPath,
            $"mixed-port: {mixedPort}{Environment.NewLine}"
            + $"external-controller: 127.0.0.1:{controllerPort}{Environment.NewLine}"
            + $"secret: \"\"{Environment.NewLine}"
            + $"allow-lan: false{Environment.NewLine}"
            + $"ipv6: false{Environment.NewLine}"
            + $"mode: rule{Environment.NewLine}"
            + $"log-level: info{Environment.NewLine}"
            + $"proxies: []{Environment.NewLine}"
            + $"proxy-groups: []{Environment.NewLine}"
            + $"rules: []{Environment.NewLine}"
            + $"tun:{Environment.NewLine}"
            + $"  enable: false{Environment.NewLine}");

        ServiceCorePayload payload = new(
            configurationPath,
            runtimeDirectory,
            controllerPort,
            string.Empty,
            MixedPort: mixedPort);
        ServiceRequest startRequest = new(
            Guid.NewGuid(),
            ServiceCommand.StartCore,
            JsonSerializer.Serialize(payload),
            ProtocolVersion: ServiceProtocol.CurrentVersion);
        DisabledTunNetworkHealthProbe healthProbe = new();

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ServiceRuntimeController controller = new(
                paths,
                managedUserSid: null,
                tunHealthProbe: healthProbe,
                restoreOwnedProxyStates: static () => { });

            ServiceResponse startResponse = await controller.HandleAsync(
                startRequest,
                CancellationToken.None);
            Assert.IsTrue(startResponse.Succeeded, startResponse.Error);
            Assert.AreEqual(CoreState.Running, startResponse.Core);
            Assert.IsNotNull(startResponse.RuntimeBinding);
            Assert.AreEqual(controllerPort, startResponse.RuntimeBinding.ControllerPort);
            Assert.AreEqual(mixedPort, startResponse.RuntimeBinding.MixedPort);
            Assert.IsTrue(startResponse.RuntimeBinding.ControllerReady);
            Assert.IsTrue(startResponse.RuntimeBinding.MixedReady);

            ServiceResponse statusResponse = await WaitForSafeStatusAsync(controller);
            Assert.IsTrue(statusResponse.Succeeded, statusResponse.Error);
            Assert.AreEqual(CoreState.Running, statusResponse.Core);
            Assert.AreEqual(TunState.Off, statusResponse.Tun);
            Assert.AreEqual(startResponse.RuntimeBinding, statusResponse.RuntimeBinding);

            using HttpClient httpClient = new();
            MihomoApiClient api = new(
                httpClient,
                new Uri($"http://127.0.0.1:{controllerPort}/"),
                string.Empty);
            string version = await WaitForVersionAsync(api);
            Assert.AreEqual(BundledMihomo.Version, version);

            ServiceResponse restartResponse = await controller.HandleAsync(
                new ServiceRequest(
                    Guid.NewGuid(),
                    ServiceCommand.RestartCore,
                    JsonSerializer.Serialize(payload),
                    ProtocolVersion: ServiceProtocol.CurrentVersion),
                CancellationToken.None);
            Assert.IsTrue(restartResponse.Succeeded, restartResponse.Error);
            Assert.AreEqual(CoreState.Running, restartResponse.Core);
            Assert.IsNotNull(restartResponse.RuntimeBinding);
            Assert.AreNotEqual(startResponse.RuntimeBinding.InstanceId, restartResponse.RuntimeBinding.InstanceId);

            ServiceResponse restartedStatus = await WaitForSafeStatusAsync(controller);
            Assert.IsTrue(restartedStatus.Succeeded, restartedStatus.Error);
            Assert.AreEqual(CoreState.Running, restartedStatus.Core);
            Assert.AreEqual(TunState.Off, restartedStatus.Tun);
            string restartedVersion = await WaitForVersionAsync(api);
            Assert.AreEqual(BundledMihomo.Version, restartedVersion);

            ServiceResponse stopResponse = await controller.HandleAsync(
                new ServiceRequest(Guid.NewGuid(), ServiceCommand.StopCore, ProtocolVersion: ServiceProtocol.CurrentVersion),
                CancellationToken.None);
            Assert.IsTrue(stopResponse.Succeeded, stopResponse.Error);
            Assert.AreEqual(CoreState.Stopped, stopResponse.Core);
            Assert.AreEqual(TunState.Off, stopResponse.Tun);
            Assert.AreEqual(0, healthProbe.EnabledProbeCalls);
            Assert.IsGreaterThan(0, healthProbe.DisabledProbeCalls);
        }, async () =>
        {
            if (Directory.Exists(root))
            {
                await DeleteTemporaryDirectoryAsync(root);
            }
        });
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task ServicePreflightsFixedControllerMixedAndDnsConflictsWithoutLaunchingMihomo()
    {
        string? executablePath = FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive("Official pinned Mihomo is required for listener conflict integration coverage.");
            return;
        }

        (string root, AppPaths paths, string runtimeDirectory) = await CreateOfficialCoreFixtureAsync(executablePath);
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ServiceRuntimeController controller = new(
                paths,
                managedUserSid: null,
                tunHealthProbe: new DisabledTunNetworkHealthProbe(),
                restoreOwnedProxyStates: static () => { });

            int httpControllerPort = GetAvailableLoopbackPort();
            int httpMixedPort = GetAvailableLoopbackPort();
            int httpPort = GetAvailableLoopbackPort();
            using TcpListener httpConflict = new(IPAddress.Loopback, httpPort);
            httpConflict.Start();
            string httpConfiguration = Path.Combine(runtimeDirectory, "http-conflict.yaml");
            await File.WriteAllTextAsync(
                httpConfiguration,
                BuildConflictConfiguration(httpControllerPort, httpMixedPort)
                + $"port: {httpPort}{Environment.NewLine}");
            ServiceResponse httpResponse = await StartServiceCoreAsync(
                controller,
                new ServiceCorePayload(
                    httpConfiguration,
                    runtimeDirectory,
                    httpControllerPort,
                    string.Empty,
                    HttpPort: httpPort,
                    MixedPort: httpMixedPort));
            Assert.IsFalse(httpResponse.Succeeded);
            Assert.AreEqual(ServiceErrorCode.ProxyPortConflict, httpResponse.ErrorCode);
            StringAssert.Contains(httpResponse.Error, "http", StringComparison.OrdinalIgnoreCase);

            int socksControllerPort = GetAvailableLoopbackPort();
            int socksMixedPort = GetAvailableLoopbackPort();
            int socksPort = GetAvailableLoopbackPort();
            using TcpListener socksConflict = new(IPAddress.Loopback, socksPort);
            socksConflict.Start();
            string socksConfiguration = Path.Combine(runtimeDirectory, "socks-conflict.yaml");
            await File.WriteAllTextAsync(
                socksConfiguration,
                BuildConflictConfiguration(socksControllerPort, socksMixedPort)
                + $"socks-port: {socksPort}{Environment.NewLine}");
            ServiceResponse socksResponse = await StartServiceCoreAsync(
                controller,
                new ServiceCorePayload(
                    socksConfiguration,
                    runtimeDirectory,
                    socksControllerPort,
                    string.Empty,
                    SocksPort: socksPort,
                    MixedPort: socksMixedPort));
            Assert.IsFalse(socksResponse.Succeeded);
            Assert.AreEqual(ServiceErrorCode.ProxyPortConflict, socksResponse.ErrorCode);
            StringAssert.Contains(socksResponse.Error, "socks", StringComparison.OrdinalIgnoreCase);

            int mixedPort = GetAvailableLoopbackPort();
            int mixedControllerPort = GetAvailableLoopbackPort();
            using TcpListener mixedConflict = new(IPAddress.Loopback, mixedPort);
            mixedConflict.Start();
            string mixedConfiguration = Path.Combine(runtimeDirectory, "mixed-conflict.yaml");
            await File.WriteAllTextAsync(mixedConfiguration, BuildConflictConfiguration(mixedControllerPort, mixedPort));
            ServiceResponse mixedResponse = await StartServiceCoreAsync(
                controller,
                new ServiceCorePayload(
                    mixedConfiguration,
                    runtimeDirectory,
                    mixedControllerPort,
                    string.Empty,
                    MixedPort: mixedPort));
            Assert.IsFalse(mixedResponse.Succeeded);
            Assert.AreEqual(ServiceErrorCode.ProxyPortConflict, mixedResponse.ErrorCode);
            StringAssert.Contains(mixedResponse.Error, "mixed", StringComparison.OrdinalIgnoreCase);

            mixedConflict.Stop();
            int fixedControllerPort = GetAvailableLoopbackPort();
            int fixedMixedPort = GetAvailableLoopbackPort();
            using TcpListener controllerConflict = new(IPAddress.Loopback, fixedControllerPort);
            controllerConflict.Start();
            string fixedConfiguration = Path.Combine(runtimeDirectory, "fixed-controller-conflict.yaml");
            await File.WriteAllTextAsync(fixedConfiguration, BuildConflictConfiguration(fixedControllerPort, fixedMixedPort));
            ServiceResponse fixedResponse = await StartServiceCoreAsync(
                controller,
                new ServiceCorePayload(
                    fixedConfiguration,
                    runtimeDirectory,
                    fixedControllerPort,
                    string.Empty,
                    ControllerPortConflictPolicy.Fixed,
                    MixedPort: fixedMixedPort));
            Assert.IsFalse(fixedResponse.Succeeded);
            Assert.AreEqual(ServiceErrorCode.ControllerPortConflict, fixedResponse.ErrorCode);

            controllerConflict.Stop();
            int dnsPort = GetAvailableLoopbackPort();
            int dnsControllerPort = GetAvailableLoopbackPort();
            int dnsMixedPort = GetAvailableLoopbackPort();
            using TcpListener dnsConflict = new(IPAddress.Loopback, dnsPort);
            dnsConflict.Start();
            string dnsConfiguration = Path.Combine(runtimeDirectory, "dns-conflict.yaml");
            string dnsContent = BuildConflictConfiguration(dnsControllerPort, dnsMixedPort)
                + $"dns:{Environment.NewLine}"
                + $"    enable: true{Environment.NewLine}"
                + $"    listen: 127.0.0.1:{dnsPort}{Environment.NewLine}";
            await File.WriteAllTextAsync(dnsConfiguration, dnsContent);
            ServiceResponse dnsResponse = await StartServiceCoreAsync(
                controller,
                new ServiceCorePayload(
                    dnsConfiguration,
                    runtimeDirectory,
                    dnsControllerPort,
                    string.Empty,
                    MixedPort: dnsMixedPort));
            Assert.IsFalse(dnsResponse.Succeeded);
            Assert.AreEqual(ServiceErrorCode.ProxyPortConflict, dnsResponse.ErrorCode);
            StringAssert.Contains(dnsResponse.Error, "dns", StringComparison.OrdinalIgnoreCase);
            Assert.AreEqual(dnsContent, await File.ReadAllTextAsync(dnsConfiguration));

            ServiceResponse status = await controller.HandleAsync(
                new ServiceRequest(Guid.NewGuid(), ServiceCommand.GetStatus, ProtocolVersion: ServiceProtocol.CurrentVersion),
                CancellationToken.None);
            Assert.IsTrue(status.Succeeded, status.Error);
            Assert.AreEqual(CoreState.Stopped, status.Core);
            Assert.IsNull(status.RuntimeBinding);
        }, async () =>
        {
            if (Directory.Exists(root))
            {
                await DeleteTemporaryDirectoryAsync(root);
            }
        });
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task ServiceRejectsEscapedDnsTcpConflictWithoutLaunchingMihomo()
    {
        string? executablePath = FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive("Official pinned Mihomo is required for escaped DNS conflict coverage.");
            return;
        }

        (string root, AppPaths paths, string runtimeDirectory) = await CreateOfficialCoreFixtureAsync(executablePath);
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            int controllerPort = GetAvailableLoopbackPort();
            int mixedPort = GetAvailableLoopbackPort();
            using TcpListener conflict = new(IPAddress.Loopback, 0);
            conflict.Start();
            int dnsPort = ((IPEndPoint)conflict.LocalEndpoint).Port;
            string configurationPath = Path.Combine(runtimeDirectory, "escaped-dns.yaml");
            string yaml = BuildConflictConfiguration(controllerPort, mixedPort)
                + $"\"d\\u006es\":{Environment.NewLine}"
                + $"    enable: true{Environment.NewLine}"
                + $"    listen: 127.0.0.1:{dnsPort}{Environment.NewLine}";
            await File.WriteAllTextAsync(configurationPath, yaml);
            await using ServiceRuntimeController controller = new(paths, managedUserSid: null,
                tunHealthProbe: new DisabledTunNetworkHealthProbe(), restoreOwnedProxyStates: static () => { });

            ServiceResponse response = await StartServiceCoreAsync(controller,
                new ServiceCorePayload(configurationPath, runtimeDirectory, controllerPort, string.Empty, MixedPort: mixedPort));

            Assert.IsFalse(response.Succeeded);
            Assert.AreEqual(ServiceErrorCode.ProxyPortConflict, response.ErrorCode);
            StringAssert.Contains(response.Error, "dns", StringComparison.OrdinalIgnoreCase);
            Assert.AreEqual(yaml, await File.ReadAllTextAsync(configurationPath));
            ServiceResponse status = await controller.HandleAsync(new ServiceRequest(Guid.NewGuid(), ServiceCommand.GetStatus,
                ProtocolVersion: ServiceProtocol.CurrentVersion), CancellationToken.None);
            Assert.AreEqual(CoreState.Stopped, status.Core);
            Assert.IsNull(status.RuntimeBinding);
        }, async () =>
        {
            await DeleteTemporaryDirectoryAsync(root);
        });
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task ServicePreservesBusyAndDeadlineCodesThroughCachedDispatch()
    {
        string? executablePath = FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive("Official pinned Mihomo is required for cached dispatch code coverage.");
            return;
        }

        (string root, AppPaths paths, string runtimeDirectory) = await CreateOfficialCoreFixtureAsync(executablePath);
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            int controllerPort = GetAvailableLoopbackPort();
            int mixedPort = GetAvailableLoopbackPort();
            string configurationPath = Path.Combine(runtimeDirectory, "deadline.yaml");
            await File.WriteAllTextAsync(configurationPath, BuildConflictConfiguration(controllerPort, mixedPort));
            ServiceCorePayload payload = new(configurationPath, runtimeDirectory, controllerPort, string.Empty, MixedPort: mixedPort);
            TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using ServiceRuntimeController controller = new(paths, managedUserSid: null,
                tunHealthProbe: new DisabledTunNetworkHealthProbe(), restoreOwnedProxyStates: static () => { },
                afterCoreStartForTest: async (_, token) =>
                {
                    entered.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }, operationTimeoutForTest: TimeSpan.FromSeconds(2));
            ServiceRequest firstRequest = new(Guid.NewGuid(), ServiceCommand.StartCore, JsonSerializer.Serialize(payload),
                ProtocolVersion: ServiceProtocol.CurrentVersion);
            Task<ServiceResponse> first = controller.HandleAsync(firstRequest, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            ServiceResponse busy = await StartServiceCoreAsync(controller, payload);
            Assert.IsFalse(busy.Succeeded);
            Assert.AreEqual(ServiceErrorCode.OperationBusy, busy.ErrorCode);
            ServiceResponse timeout = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(timeout.Succeeded);
            Assert.AreEqual(ServiceErrorCode.OperationTimedOut, timeout.ErrorCode);
            Assert.AreEqual(CoreState.Stopped, timeout.Core);
            ServiceResponse repeated = await controller.HandleAsync(firstRequest, CancellationToken.None);
            Assert.AreEqual(timeout, repeated, "A retry must observe the same typed cached result.");
            Assert.IsNull(repeated.RuntimeBinding);
        }, async () =>
        {
            await DeleteTemporaryDirectoryAsync(root);
        });
    }

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public Task ServiceReadinessTimeoutRetainsMissingUdpObservation() =>
        VerifyServiceReadinessTimeoutAsync(ListenerOwnerState.Missing);

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public Task ServiceReadinessTimeoutRetainsUnknownUdpObservation() =>
        VerifyServiceReadinessTimeoutAsync(ListenerOwnerState.Unknown);

    private static async Task VerifyServiceReadinessTimeoutAsync(ListenerOwnerState ownerState)
    {
        string? executablePath = FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive("Official pinned Mihomo is required for readiness timeout diagnostics.");
            return;
        }

        (string root, AppPaths paths, string runtimeDirectory) = await CreateOfficialCoreFixtureAsync(executablePath);
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            int controllerPort = GetAvailableLoopbackPort();
            int mixedPort = GetAvailableLoopbackPort();
            string configurationPath = Path.Combine(runtimeDirectory, "readiness-timeout.yaml");
            await File.WriteAllTextAsync(configurationPath, BuildConflictConfiguration(controllerPort, mixedPort));
            CancellationTokenSource? operationDeadline = null;
            bool observedUdp = false;
            await using ServiceRuntimeController controller = new(paths, managedUserSid: null,
                tunHealthProbe: new DisabledTunNetworkHealthProbe(), restoreOwnedProxyStates: static () => { },
                operationDeadlineForTest: lifetime => operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime),
                listenerInspectorForTest: (listener, identity) =>
                {
                    if (listener.Name != "mixed-udp")
                    {
                        return WindowsListenerOwnerTable.InspectListener(listener.Address, listener.Port,
                            listener.Transport, identity, listener.DualMode);
                    }

                    // Expire the command only after the relevant observation exists.
                    // Cold CI startup must not consume a short real-time fixture budget.
                    observedUdp = true;
                    operationDeadline!.Cancel();
                    return new ListenerOwnerObservation(ownerState, "deterministic UDP observation");
                });

            ServiceResponse response = await StartServiceCoreAsync(controller,
                new ServiceCorePayload(configurationPath, runtimeDirectory, controllerPort, string.Empty, MixedPort: mixedPort));

            Assert.IsFalse(response.Succeeded);
            Assert.IsTrue(observedUdp, "The diagnostic test must reach the injected UDP observation before expiry.");
            Assert.AreEqual(ServiceErrorCode.OperationTimedOut, response.ErrorCode);
            StringAssert.Contains(response.Error, "mixed-udp", StringComparison.Ordinal);
            StringAssert.Contains(response.Error, $"127.0.0.1:{mixedPort}", StringComparison.Ordinal);
            StringAssert.Contains(response.Error, "Udp", StringComparison.Ordinal);
            StringAssert.Contains(response.Error, ownerState.ToString(), StringComparison.Ordinal);
            StringAssert.Contains(response.Error, "deterministic UDP observation", StringComparison.Ordinal);
            Assert.AreEqual(CoreState.Stopped, controller.CoreState);
        }, async () =>
        {
            await DeleteTemporaryDirectoryAsync(root);
        });
    }

    private static async Task<ServiceResponse> StartServiceCoreAsync(
        ServiceRuntimeController controller,
        ServiceCorePayload payload) => await controller.HandleAsync(
            new ServiceRequest(
                Guid.NewGuid(),
                ServiceCommand.StartCore,
                JsonSerializer.Serialize(payload),
                ProtocolVersion: ServiceProtocol.CurrentVersion),
            CancellationToken.None);

    private static string BuildConflictConfiguration(int controllerPort, int mixedPort) =>
        $"mixed-port: {mixedPort}{Environment.NewLine}"
        + $"external-controller: 127.0.0.1:{controllerPort}{Environment.NewLine}"
        + $"secret: \"\"{Environment.NewLine}"
        + $"allow-lan: false{Environment.NewLine}"
        + $"ipv6: false{Environment.NewLine}"
        + $"mode: rule{Environment.NewLine}"
        + $"log-level: info{Environment.NewLine}"
        + $"proxies: []{Environment.NewLine}"
        + $"proxy-groups: []{Environment.NewLine}"
        + $"rules: []{Environment.NewLine}"
        + $"tun:{Environment.NewLine}"
        + $"  enable: false{Environment.NewLine}";

    private static async Task<(string Root, AppPaths Paths, string RuntimeDirectory)> CreateOfficialCoreFixtureAsync(
        string executablePath)
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureProgramDataDirectories();
        string runtimeDirectory = Path.Combine(paths.RuntimeRoot, "mihomo");
        Directory.CreateDirectory(runtimeDirectory);
        File.Copy(executablePath, paths.ManagedCoreExecutable);
        string executableSha256 = await ComputeSha256Async(paths.ManagedCoreExecutable);
        await File.WriteAllTextAsync(
            paths.ManagedCoreMetadata,
            JsonSerializer.Serialize(new ManagedCoreMetadata(
                BundledMihomo.Version,
                new Uri($"https://github.com/MetaCubeX/mihomo/releases/download/{BundledMihomo.Version}/mihomo-windows-amd64-{BundledMihomo.Version}.zip"),
                OfficialMihomoTestSupport.PinnedArchiveSha256,
                executableSha256)));
        return (root, paths, runtimeDirectory);
    }

    private static Task DeleteTemporaryDirectoryAsync(string directoryPath) => TestFixtureDirectory.DeleteAsync(directoryPath);

    private static async Task<ServiceResponse> WaitForSafeStatusAsync(
        ServiceRuntimeController controller)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        ServiceResponse? lastResponse = null;
        while (!timeout.IsCancellationRequested)
        {
            lastResponse = await controller.HandleAsync(
                new ServiceRequest(Guid.NewGuid(), ServiceCommand.GetStatus, ProtocolVersion: ServiceProtocol.CurrentVersion),
                timeout.Token);
            if (lastResponse.Succeeded
                && lastResponse.Core == CoreState.Running
                && lastResponse.Tun == TunState.Off)
            {
                return lastResponse;
            }

            await Task.Delay(100, timeout.Token);
        }

        throw new TimeoutException(
            $"Service did not confirm a running core with TUN off. Last response: {lastResponse?.Error ?? "none"}.");
    }

    private static async Task<string> WaitForVersionAsync(MihomoApiClient api)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Exception? lastException = null;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using JsonDocument version = await api.GetVersionAsync(timeout.Token);
                string? value = MihomoDataParser.ParseVersion(version);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                lastException = new InvalidDataException("Mihomo returned an empty version.");
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
            }
            catch (SocketException exception)
            {
                lastException = exception;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(100, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException(
            "Mihomo loopback controller did not become ready within 10 seconds.",
            lastException);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static int GetAvailableLoopbackPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? FindMihomoExecutable() => OfficialMihomoTestSupport.FindMihomoExecutable();

    private sealed class DisabledTunNetworkHealthProbe : ITunNetworkHealthProbe
    {
        public int DisabledProbeCalls { get; private set; }

        public int EnabledProbeCalls { get; private set; }

        public Task<TunNetworkHealth> ProbeAsync(
            MihomoTunConfiguration configuration,
            TunNetworkExpectation expectation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectation == TunNetworkExpectation.Enabled)
            {
                EnabledProbeCalls++;
                throw new InvalidOperationException("This integration test must never probe an enabled TUN.");
            }

            DisabledProbeCalls++;
            return Task.FromResult(new TunNetworkHealth(
                InterfaceFound: false,
                HasValidAddress: false,
                HasRequiredRoute: false,
                HasRequiredDns: false,
                InterfaceName: null,
                Diagnostic: null,
                HasActiveRoute: false,
                HasActiveDns: false,
                ProbeSucceeded: true,
                RouteStateKnown: true));
        }
    }
}
