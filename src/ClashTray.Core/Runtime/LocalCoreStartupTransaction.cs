using System.Net;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed record LocalCoreStartupRequest(
    AppPaths Paths, AppSettings Settings, string ExecutablePath, string SourceConfigurationPath,
    string RuntimeConfigurationPath, int ControllerPort, MihomoEffectiveListenerPlan ListenerPlan,
    IReadOnlyList<LocalPortBinding> FixedListeners, HashSet<int> AttemptedControllerPorts,
    int InspectedFallbackCandidates, bool UseAvailableControllerPortOnce);

// Owns one complete local start transaction, including retries and cleanup.
// Runtime publishes only its confirmed result while retaining operation admission.
internal sealed class LocalCoreStartupTransaction(
    MihomoProcessManager process, ICoreLifecycleExecutor executor,
    Func<ICoreOwnershipObservationSource> ownership)
{
    public async Task<CoreRuntimeBinding?> StartAsync(LocalCoreStartupRequest request,
        HttpClient httpClient, Func<MihomoApiClient>? apiFactory, Action<Exception> cleanupFailure,
        CancellationToken cancellationToken)
    {
        int controllerPort = request.ControllerPort;
        int inspected = request.InspectedFallbackCandidates;
        MihomoEffectiveListenerPlan plan = request.ListenerPlan;
        bool started = false;
        bool confirmed = false;
        try
        {
            for (int attempt = 0; attempt < ControllerPortAllocator.MaximumStartAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt > 0)
                {
                    await RuntimeConfigBuilder.BuildForCoreStartAsync(request.SourceConfigurationPath,
                        request.RuntimeConfigurationPath, request.Settings with { ControllerPort = controllerPort, TunEnabled = false },
                        externalUiPath: request.Paths.ExternalUiRoot, cancellationToken: cancellationToken).ConfigureAwait(false);
                    plan = await MihomoListenerPlanAnalyzer.AnalyzeEffectiveFileAsync(request.RuntimeConfigurationPath, cancellationToken).ConfigureAwait(false);
                    if (!plan.ProxyPlanComplete)
                    {
                        throw new ServiceCommandException(ServiceErrorCode.InvalidConfiguration,
                            plan.ProxyPlanWarning ?? "无法确认最终 Mihomo 代理监听计划。");
                    }
                }
                // The local executor binds from the rebuilt runtime configuration,
                // so this payload field is unread on this path today; still
                // carry the per-attempt port so a future service-executor
                // consumer cannot silently advertise the stale preferred port.
                ServiceCorePayload payload = new(request.RuntimeConfigurationPath,
                    Path.Combine(request.Paths.RuntimeRoot, "mihomo"), controllerPort, string.Empty);
                CoreLaunchResult launch = await executor.StartAsync(
                    new CoreLaunchRequest(request.ExecutablePath, payload, request.Paths.ExternalUiRoot), cancellationToken).ConfigureAwait(false);
                if (!launch.Succeeded) { return null; }
                started = true;
                LocalCoreProcessIdentity identity = process.CaptureRunningProcessIdentity()
                    ?? throw new InvalidOperationException("Mihomo 启动后立即退出，无法确认受管进程身份。");
                long generation = process.Generation;
                Guid instanceId = process.InstanceId;
                Func<bool> isCurrent = () => IsCurrent(identity, generation, instanceId);
                MihomoApiClient api = apiFactory?.Invoke() ?? new MihomoApiClient(httpClient,
                    new Uri($"http://127.0.0.1:{controllerPort}/"), string.Empty,
                    controllerOwnershipValidator: () => isCurrent() && WindowsListenerOwnerTable.IsOwnedBy(
                        IPAddress.Loopback, controllerPort, PortTransport.Tcp, identity));
                CoreRuntimeBinding binding;
                try
                {
                    binding = await CoreStartupReadiness.WaitAsync(
                        new(request.Settings.ControllerPort, controllerPort, request.Settings.HttpPort,
                            request.Settings.SocksPort, request.Settings.MixedPort, instanceId, process.OwnerInstanceId,
                            identity, generation, plan),
                        new(isCurrent,
                            () => WindowsListenerOwnerTable.InspectListener(IPAddress.Loopback, controllerPort, PortTransport.Tcp, identity),
                            token => api.GetConfigurationAsync(false, token),
                            () => ownership().CreateListenerSnapshot(identity, plan.AllBindings)),
                        TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                }
                catch (ServiceCommandException exception) when (exception.ErrorCode == ServiceErrorCode.ControllerOwnershipUnconfirmed
                    && attempt + 1 < ControllerPortAllocator.MaximumStartAttempts)
                {
                    await StopUnreadyAsync(cleanupFailure).ConfigureAwait(false);
                    started = false;
                    ControllerPortAllocationResult next = CoreControllerPortPlanning.Allocate(request.Settings,
                        request.UseAvailableControllerPortOnce, request.FixedListeners, request.AttemptedControllerPorts,
                        ControllerPortAllocator.MaximumFallbackCandidates - inspected, cancellationToken);
                    inspected += next.FallbackCandidatesExamined;
                    controllerPort = CoreControllerPortPlanning.Require(next);
                    continue;
                }
                if (!process.TryMarkReady(generation))
                { throw new InvalidOperationException("Mihomo 启动代际已变化，未提交运行状态。"); }
                confirmed = true;
                return binding;
            }
            throw new ServiceCommandException(ServiceErrorCode.ControllerOwnershipUnconfirmed,
                "Mihomo 控制器端口在预检后持续被占用，已达到 3 次启动上限。");
        }
        finally
        {
            if (started && !confirmed) { await StopUnreadyAsync(cleanupFailure).ConfigureAwait(false); }
        }
    }

    private bool IsCurrent(LocalCoreProcessIdentity identity, long generation, Guid instanceId)
    {
        try
        {
            return process.State is CoreState.Starting or CoreState.Running && process.Generation == generation
                && process.InstanceId == instanceId && process.CaptureRunningProcessIdentity() == identity;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or IOException) { return false; }
    }

    private async Task StopUnreadyAsync(Action<Exception> cleanupFailure)
    {
        try
        {
            if (process.State != CoreState.Stopped) { await executor.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        { cleanupFailure(exception); }
    }
}
