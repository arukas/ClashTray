using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.Extensions.Logging;

namespace ClashTray.Service;

internal sealed record ServiceCoreStartResult(
    bool Succeeded,
    string? Error = null,
    CoreState Core = CoreState.Failed,
    ServiceErrorCode ErrorCode = ServiceErrorCode.None);

// The dispatcher owns operation admission. This component owns the managed
// process/API/binding and startup rollback, and introduces no second gate.
internal sealed class ServiceCoreLifecycle : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly string? _managedUserSid;
    private readonly ILogger _logger;
    private readonly TimeSpan _defaultOperationTimeout;
    private readonly Action<int>? _beforeCoreStartForTest;
    private readonly Func<int, CancellationToken, Task>? _afterCoreStartForTest;
    private readonly Func<LocalPortBinding, LocalCoreProcessIdentity, ListenerOwnerObservation>? _listenerInspectorForTest;
    private readonly MihomoProcessManager _processManager = new();
    private readonly HttpClient _httpClient = EndpointTransportPolicy.CreateControllerHttpClient();
    private MihomoApiClient? _api;
    private CoreRuntimeBinding? _runtimeBinding;
    private LocalCoreProcessIdentity? _activeProcessIdentity;
    private ServiceCorePayload? _activeCore;

    internal ServiceCoreLifecycle(AppPaths paths, string? managedUserSid, ILogger logger, TimeSpan operationTimeout,
        Action<int>? beforeStart = null, Func<int, CancellationToken, Task>? afterStart = null,
        Func<LocalPortBinding, LocalCoreProcessIdentity, ListenerOwnerObservation>? listenerInspector = null)
    {
        _paths = paths;
        _managedUserSid = managedUserSid;
        _logger = logger;
        _defaultOperationTimeout = operationTimeout;
        _beforeCoreStartForTest = beforeStart;
        _afterCoreStartForTest = afterStart;
        _listenerInspectorForTest = listenerInspector;
    }

    internal event Action<TunState>? TunStateObserved;
    internal MihomoProcessManager ProcessManager => _processManager;
    internal MihomoApiClient? Api => _api;
    internal CoreRuntimeBinding? RuntimeBinding => _runtimeBinding;
    internal LocalCoreProcessIdentity? ProcessIdentity => _activeProcessIdentity;
    internal ServiceCorePayload? ActiveCore => _activeCore;

    // Call only after TUN shutdown has been confirmed, or for a failed start
    // whose candidate has TUN disabled. A canceled stop retains cleanup state.
    internal async Task StopOwnedProcessAsync(CancellationToken cancellationToken)
    {
        await _processManager.StopAsync(cancellationToken).ConfigureAwait(false);
        ClearSession();
    }

    private void ClearSession()
    {
        _api = null;
        _runtimeBinding = null;
        _activeProcessIdentity = null;
        _activeCore = null;
    }

    internal async ValueTask DisposeAsync(bool safeToStopProcess)
    {
        try
        {
            if (safeToStopProcess) { await _processManager.DisposeAsync().ConfigureAwait(false); }
        }
        finally { DisposeTransport(); }
    }

    public ValueTask DisposeAsync() => DisposeAsync(safeToStopProcess: true);
    internal void DisposeTransport() => _httpClient.Dispose();

    private void SetTunState(TunState state) => TunStateObserved?.Invoke(state);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A core start owns only the child it creates; on unexpected setup failure it must attempt bounded cleanup and return a typed failed start result.")]
    internal async Task<ServiceCoreStartResult> StartAsync(ServiceCorePayload payload, CancellationToken cancellationToken)
    {
        ValidatePayload(payload);
        if (_processManager.State == CoreState.Running)
        {
            return new(true);
        }

        try
        {
            await ManagedCoreVerifier.ValidateAsync(_paths, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Failure($"受管 Mihomo 核心校验失败：{ErrorSanitizer.Sanitize(exception)}", CoreState.Failed);
        }

        string executablePath = _paths.ManagedCoreExecutable;
        MihomoEffectiveListenerPlan listenerPlan = await MihomoListenerPlanAnalyzer.AnalyzeEffectiveFileAsync(
            payload.ConfigurationPath,
            cancellationToken).ConfigureAwait(false);
        if (!listenerPlan.ProxyPlanComplete)
        {
            return Failure(listenerPlan.ProxyPlanWarning ?? "无法确认有效 Mihomo 代理监听计划。",
                CoreState.Failed,
                ServiceErrorCode.InvalidConfiguration);
        }

        IReadOnlyList<LocalPortBinding> fixedListeners = listenerPlan.AllBindings;
        HashSet<int> attemptedPorts = [];
        int inspectedFallbackPorts = 0;
        ControllerPortAllocationResult allocation = AllocateControllerPort(
            payload,
            fixedListeners,
            attemptedPorts,
            ControllerPortAllocator.MaximumFallbackCandidates,
            cancellationToken);
        inspectedFallbackPorts += allocation.FallbackCandidatesExamined;
        if (!allocation.Succeeded)
        {
            return BuildPortAllocationFailure(allocation);
        }

        int candidatePort = allocation.Port!.Value;
        for (int attempt = 0; attempt < ControllerPortAllocator.MaximumStartAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string candidatePath = Path.Combine(
                payload.WorkingDirectory,
                "active-service-config.yaml");
            try
            {
                _beforeCoreStartForTest?.Invoke(candidatePort);
                await RuntimeConfigBuilder.BuildControllerCandidateAsync(
                    payload.ConfigurationPath,
                    candidatePath,
                    candidatePort,
                    cancellationToken).ConfigureAwait(false);
                MihomoEffectiveListenerPlan candidateListenerPlan = await MihomoListenerPlanAnalyzer.AnalyzeEffectiveFileAsync(
                    candidatePath,
                    cancellationToken).ConfigureAwait(false);
                if (!candidateListenerPlan.ProxyPlanComplete)
                {
                    return Failure(candidateListenerPlan.ProxyPlanWarning ?? "无法确认最终 Mihomo 代理监听计划。",
                        CoreState.Failed,
                        ServiceErrorCode.InvalidConfiguration);
                }

                if (!await _processManager.ValidateAsync(
                    executablePath,
                    candidatePath,
                    workingDirectory: null,
                    safePaths: _paths.ExternalUiRoot,
                    cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    SetTunState(TunState.Off);
                    return Failure("Mihomo 配置验证失败。", CoreState.Failed, ServiceErrorCode.InvalidConfiguration);
                }

                await _processManager.StartAsync(
                    executablePath,
                    candidatePath,
                    payload.WorkingDirectory,
                    safePaths: _paths.ExternalUiRoot,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                _activeCore = payload;
                LocalCoreProcessIdentity processIdentity = _processManager.CaptureRunningProcessIdentity()
                    ?? throw new CoreStartException(
                        ServiceErrorCode.CoreReadinessFailed,
                        "Mihomo 启动后立即退出，无法确认受管进程身份。");
                long processGeneration = _processManager.Generation;
                Guid instanceId = _processManager.InstanceId;
                _activeProcessIdentity = processIdentity;
                _api = CreateApi(candidatePort, payload.ControllerSecret, processIdentity, processGeneration, instanceId);
                SetTunState(TunState.Unknown);
                if (_afterCoreStartForTest is not null)
                {
                    await _afterCoreStartForTest(candidatePort, cancellationToken).ConfigureAwait(false);
                }

                // The non-elevated desktop verifies image/creation-time as well as PID.
                // Bind this query-only grant to the installed user, never an IPC-supplied SID.
                // A failure follows startup rollback and must not publish a ready binding.
                if (_managedUserSid is not null)
                {
                    _processManager.GrantIdentityQueryAccess(_managedUserSid);
                }

                CoreRuntimeBinding binding = await WaitForReadyBindingAsync(
                    payload,
                    candidatePort,
                    processIdentity,
                    processGeneration,
                    instanceId,
                    candidateListenerPlan,
                    cancellationToken).ConfigureAwait(false);
                if (!_processManager.TryMarkReady(processGeneration))
                {
                    throw new CoreStartException(
                        ServiceErrorCode.CoreReadinessFailed,
                        "Mihomo 启动代际已变化，未提交运行状态。");
                }

                _runtimeBinding = binding;
                SetTunState(TunState.Off);
                return new(true);
            }
            catch (CoreStartException exception)
            {
                await CleanupFailedCoreStartAsync().ConfigureAwait(false);
                if (exception.ErrorCode != ServiceErrorCode.ControllerOwnershipUnconfirmed
                    || attempt + 1 >= ControllerPortAllocator.MaximumStartAttempts
                    || payload.ControllerPortConflictPolicy == ControllerPortConflictPolicy.Fixed
                        && !payload.UseAvailableControllerPortOnce)
                {
                    return Failure(exception.Message, CoreState.Failed, exception.ErrorCode);
                }

                ControllerPortAllocationResult next = AllocateControllerPort(
                    payload,
                    fixedListeners,
                    attemptedPorts,
                    ControllerPortAllocator.MaximumFallbackCandidates - inspectedFallbackPorts,
                    cancellationToken);
                inspectedFallbackPorts += next.FallbackCandidatesExamined;
                if (!next.Succeeded)
                {
                    return BuildPortAllocationFailure(next);
                }

                candidatePort = next.Port!.Value;
            }
            catch (OperationCanceledException)
            {
                await CleanupFailedCoreStartAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or TimeoutException)
            {
                await CleanupFailedCoreStartAsync().ConfigureAwait(false);
                ServiceErrorCode code = CoreStartFailure.ClassifyException(exception);
                return Failure($"Mihomo 启动就绪失败：{ErrorSanitizer.Sanitize(exception)}",
                    CoreState.Failed,
                    code != ServiceErrorCode.None ? code : ServiceErrorCode.CoreReadinessFailed);
            }
            catch (Exception exception)
            {
                await CleanupFailedCoreStartAsync().ConfigureAwait(false);
                return Failure($"Mihomo 启动就绪失败：{ErrorSanitizer.Sanitize(exception)}",
                    CoreState.Failed,
                    ServiceErrorCode.CoreReadinessFailed);
            }
        }

        await CleanupFailedCoreStartAsync().ConfigureAwait(false);
        return Failure("控制器端口在预检后持续被抢占，已达到 3 次启动上限。",
            CoreState.Failed,
            ServiceErrorCode.ControllerCandidatesExhausted);
    }

    private static ControllerPortAllocationResult AllocateControllerPort(
        ServiceCorePayload payload,
        IReadOnlyList<LocalPortBinding> fixedListeners,
        HashSet<int> attemptedPorts,
        int remainingFallbackCandidates,
        CancellationToken cancellationToken) => ControllerPortAllocator.Allocate(
            payload.ControllerPort,
            payload.ControllerPortConflictPolicy,
            payload.UseAvailableControllerPortOnce,
            fixedListeners,
            static () => RandomNumberGenerator.GetInt32(
                ControllerPortAllocator.MinimumFallbackPort,
                ControllerPortAllocator.MaximumFallbackPort + 1),
            attemptedPorts: attemptedPorts,
            maximumFallbackCandidates: Math.Max(0, remainingFallbackCandidates),
            cancellationToken: cancellationToken);

    private static ServiceCoreStartResult BuildPortAllocationFailure(
        ControllerPortAllocationResult result)
    {
        PortPlanConflict? conflict = result.Conflict;
        if (conflict is null)
        {
            return Failure("没有找到可用的高位控制器端口（最多探测 16 个候选）。",
                CoreState.Failed,
                ServiceErrorCode.ControllerCandidatesExhausted);
        }

        bool controllerConflict = conflict.Listener.Name == "controller"
            || conflict.ConflictingWith == "controller";
        string message = conflict.IsInternalConflict
            ? $"本地监听配置冲突：{conflict.ConflictingWith} 与 {conflict.Listener.Name} 使用了重叠端口 {conflict.Listener.Port}。"
            : $"{conflict.Listener.Name} 监听端口 {conflict.Listener.Port} 无法使用（{conflict.Probe.Status}）。";
        return Failure(message,
            CoreState.Failed,
            controllerConflict ? ServiceErrorCode.ControllerPortConflict : ServiceErrorCode.ProxyPortConflict);
    }

    private async Task<CoreRuntimeBinding> WaitForReadyBindingAsync(
        ServiceCorePayload payload, int controllerPort, LocalCoreProcessIdentity processIdentity,
        long processGeneration, Guid instanceId, MihomoEffectiveListenerPlan listenerPlan,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CoreStartupReadiness.WaitAsync(
                new CoreStartupTarget(payload.ControllerPort, controllerPort, payload.HttpPort,
                    payload.SocksPort, payload.MixedPort, instanceId, _processManager.OwnerInstanceId,
                    processIdentity, processGeneration, listenerPlan),
                new CoreStartupObservations(
                    () => IsProcessIdentityCurrent(processIdentity, processGeneration, instanceId),
                    () => WindowsListenerOwnerTable.InspectListener(IPAddress.Loopback, controllerPort, PortTransport.Tcp, processIdentity),
                    token => (_api ?? throw new ManagedCoreOwnershipException()).GetConfigurationAsync(false, token),
                    () =>
                    {
                        ListenerOwnerObservationScope observation = WindowsListenerOwnerTable.CreateObservation(processIdentity, listenerPlan.AllBindings);
                        return listener => _listenerInspectorForTest?.Invoke(listener, processIdentity) ?? observation.Inspect(listener);
                    }),
                _defaultOperationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceCommandException exception)
        {
            throw new CoreStartException(exception.ErrorCode, exception.Message, exception);
        }
    }

    internal static bool TryValidateReadyListeners(JsonDocument configuration, ServiceCorePayload payload,
        out MihomoListenerPorts ports, out ServiceErrorCode errorCode, out string? message) =>
        CoreStartupReadiness.TryValidatePorts(configuration, payload.HttpPort, payload.SocksPort,
            payload.MixedPort, out ports, out errorCode, out message);

    internal static bool AreRuntimePortsMatch(CoreRuntimeBinding binding, MihomoListenerPorts ports) =>
        binding.HttpPort == (ports.Http ?? 0)
        && binding.SocksPort == (ports.Socks ?? 0)
        && binding.MixedPort == (ports.Mixed ?? 0);

    private bool IsProcessIdentityCurrent(
        LocalCoreProcessIdentity identity,
        long generation,
        Guid instanceId)
    {
        try
        {
            return _processManager.State is CoreState.Starting or CoreState.Running
                && _processManager.Generation == generation
                && _processManager.InstanceId == instanceId
                && _processManager.CaptureRunningProcessIdentity() == identity
                && WindowsListenerOwnerTable.IsCurrentProcessIdentity(identity);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception
            or IOException)
        {
            return false;
        }
    }

    internal bool IsRuntimeBindingOwned(CoreRuntimeBinding binding, LocalCoreProcessIdentity identity)
    {
        try
        {
            return binding.OwnerInstanceId == _processManager.OwnerInstanceId
                && binding.InstanceId == _processManager.InstanceId
                && binding.ProcessId == identity.ProcessId
                && binding.ProcessStartedUtcTicks == identity.StartTimeUtcTicks
                && binding.ProcessGeneration == _processManager.Generation
                && IsProcessIdentityCurrent(identity, binding.ProcessGeneration, binding.InstanceId)
                && RuntimeBindingValidator.AreListenersOwned(binding, identity);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task CleanupFailedCoreStartAsync()
    {
        ClearSession();
        if (_processManager.State is CoreState.Starting or CoreState.Running or CoreState.Failed)
        {
            try
            {
                await _processManager.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or IOException
                or TimeoutException
                or System.ComponentModel.Win32Exception)
            {
                _logger.LogWarning("Could not clean up failed owned Mihomo startup: {Error}", ErrorSanitizer.Sanitize(exception));
                SetTunState(TunState.Unknown);
                return;
            }
        }

        SetTunState(TunState.Off);
    }

    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "This private service start result is created only at typed lifecycle rejection boundaries.")]
    private sealed class CoreStartException : IOException
    {
        public CoreStartException(ServiceErrorCode errorCode, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }

        public ServiceErrorCode ErrorCode { get; }
    }

    private MihomoApiClient CreateApi(
        int port,
        string secret,
        LocalCoreProcessIdentity processIdentity,
        long processGeneration,
        Guid instanceId) =>
        new(
            _httpClient,
            new Uri($"http://127.0.0.1:{port}/"),
            secret,
            controllerOwnershipValidator: () =>
                IsProcessIdentityCurrent(processIdentity, processGeneration, instanceId)
                && WindowsListenerOwnerTable.IsOwnedBy(
                    IPAddress.Loopback,
                    port,
                    PortTransport.Tcp,
                    processIdentity));

    internal void ValidatePayload(ServiceCorePayload payload)
    {
        if (!IsAllowedRuntimePath(payload.ConfigurationPath, allowYaml: true)
            || !IsAllowedRuntimePath(payload.WorkingDirectory, allowYaml: false)
            || payload.ControllerPort is < 1 or > 65535
            || payload.ControllerSecret is null
            || payload.ControllerSecret.Length != 0
            || !Enum.IsDefined(payload.ControllerPortConflictPolicy)
            || payload.HttpPort is < 0 or > 65535
            || payload.SocksPort is < 0 or > 65535
            || payload.MixedPort is < 0 or > 65535)
        {
            _logger.LogWarning("Rejected core payload: untrusted runtime path or parameters.");
            throw new InvalidOperationException("服务拒绝了不受信任的核心路径或参数。");
        }
    }

    private bool IsAllowedRuntimePath(string path, bool allowYaml)
    {
        string fullPath = Path.GetFullPath(path);
        if (allowYaml)
        {
            return CorePathPolicy.IsManagedRuntimeFile(_paths, fullPath)
                && (Path.GetExtension(fullPath) is ".yaml" or ".yml");
        }

        return CorePathPolicy.IsManagedRuntimeDirectory(_paths, fullPath);
    }

    private static ServiceCoreStartResult Failure(string error, CoreState core, ServiceErrorCode errorCode = ServiceErrorCode.None) =>
        new(false, error, core, errorCode);
}
