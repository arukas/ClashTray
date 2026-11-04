using System.Text.Json;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClashTray.Service;

/// <summary>
/// The privileged boundary accepts only typed, allow-listed lifecycle and TUN requests.
/// It never accepts a shell command or an arbitrary executable path.
/// </summary>
internal sealed class ServiceRuntimeController : IAsyncDisposable
{
    private static readonly TimeSpan StatusQueryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TunOperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CoreUpdateOperationTimeout = TimeSpan.FromMinutes(6);

    private readonly Action<ServiceRequest>? _requestExecutionObserved;
    public Guid ServiceInstanceId { get; } = Guid.NewGuid();
    private readonly HttpClient _coreUpdateHttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ServiceCoreUpdateHandler _coreUpdates;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "DisposeAsync calls the safety-aware DisposeAsync(bool) overload after confirmed TUN shutdown; transport disposal also runs in finally when observation fails. The analyzer does not recognize that overload.")]
    private readonly ServiceCoreLifecycle _core;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly BooleanSingleFlight<TunTransactionResult> _tunSingleFlight =
        new("TUN", cancelWhenNoWaiters: false);
    private readonly object _tunLogGate = new();
    private readonly ITunNetworkHealthProbe _tunHealthProbe;
    private readonly TunTransactionCoordinator _tunTransactions;
    private readonly ILogger _logger;
    private readonly Action _restoreOwnedProxyStates;
    private readonly TimeSpan _defaultOperationTimeout;
    private readonly Func<CancellationToken, CancellationTokenSource>? _operationDeadlineForTest;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private readonly ServiceRequestLedger _requests;
    private int _cleanupWaiters;
    private TunState _tunState = TunState.Off;
    private long _tunStateRevision;
    private int _tunDesired;
    private long _tunListenerErrorSequence;
    private long _tunConfirmedListenerErrorSequence;
    private string? _lastTunListenerError;

    public ServiceRuntimeController(AppPaths? paths = null, string? managedUserSid = null, ILoggerFactory? loggerFactory = null)
        : this(paths, managedUserSid, null, loggerFactory)
    {
    }

    internal ServiceRuntimeController(
        AppPaths? paths,
        string? managedUserSid,
        ITunNetworkHealthProbe? tunHealthProbe,
        ILoggerFactory? loggerFactory = null,
        Action? restoreOwnedProxyStates = null,
        Action<int>? beforeCoreStartForTest = null,
        Func<int, CancellationToken, Task>? afterCoreStartForTest = null,
        TimeSpan? operationTimeoutForTest = null,
        Func<LocalPortBinding, LocalCoreProcessIdentity, ListenerOwnerObservation>? listenerInspectorForTest = null,
        Func<CancellationToken, CancellationTokenSource>? operationDeadlineForTest = null,
        TimeProvider? requestTimeProvider = null,
        int requestCacheCapacity = ServiceRequestLedger.MaximumCapacity,
        Action<ServiceRequest>? requestExecutionObserved = null)
    {
        AppPaths runtimePaths = paths ?? new AppPaths();
        _requests = new ServiceRequestLedger(requestTimeProvider ?? TimeProvider.System, requestCacheCapacity);
        _requestExecutionObserved = requestExecutionObserved;
        _logger = loggerFactory?.CreateLogger<ServiceRuntimeController>() ?? NullLogger<ServiceRuntimeController>.Instance;
        _restoreOwnedProxyStates = restoreOwnedProxyStates ?? SystemProxyRecovery.RestoreOwnedStatesForLoadedUsers;
        _operationDeadlineForTest = operationDeadlineForTest;
        _defaultOperationTimeout = operationTimeoutForTest ?? DefaultOperationTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_defaultOperationTimeout, TimeSpan.Zero);
        _core = new ServiceCoreLifecycle(runtimePaths, managedUserSid, _logger, _defaultOperationTimeout,
            beforeCoreStartForTest, afterCoreStartForTest, listenerInspectorForTest);
        _core.TunStateObserved += SetTunState;
        _coreUpdates = new ServiceCoreUpdateHandler(runtimePaths, _core.ProcessManager, _coreUpdateHttpClient, managedUserSid);
        _tunHealthProbe = tunHealthProbe ?? new WindowsTunNetworkHealthProbe();
        _tunTransactions = new TunTransactionCoordinator(
            new ControllerTunTransactionBackend(this),
            _tunHealthProbe,
            shouldRetryEnable: () => Volatile.Read(ref _tunDesired) != 0,
            logger: loggerFactory?.CreateLogger<TunTransactionCoordinator>());
        _core.ProcessManager.LogLineReceived += OnProcessLogLine;
    }

    public CoreState CoreState => _core.ProcessManager.State;

    public Task<ServiceResponse> HandleAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return HandleWithRequestCacheAsync(request, cancellationToken);
    }

    private async Task<ServiceResponse> HandleWithRequestCacheAsync(
        ServiceRequest request,
        CancellationToken callerCancellationToken)
    {
        if (request.ProtocolVersion != ServiceProtocol.CurrentVersion)
        {
            _logger.LogWarning(
                "Rejected service request {RequestId}: unsupported protocol version {ProtocolVersion}.",
                request.RequestId,
                request.ProtocolVersion);
            return Failure(
                request,
                $"不支持的 ClashTray 服务协议版本：{request.ProtocolVersion}。",
                _core.ProcessManager.State);
        }

        if (request.RequestId == Guid.Empty)
        {
            return Failure(request, "服务请求必须提供非空操作 ID。", errorCode: ServiceErrorCode.RequestIdentityConflict);
        }

        if (request.ExpectedServiceInstanceId != Guid.Empty && request.ExpectedServiceInstanceId != ServiceInstanceId)
        { return UnknownResult(request, "服务实例已更换，原请求结果无法确认。"); }

        RequestAdmission admission = _requests.Admit(request, () => ClassifyRetention(request));
        switch (admission.Kind)
        {
            case RequestAdmissionKind.IdentityConflict:
                _logger.LogWarning("Rejected service request {RequestId}: request ID reused with a different command.", request.RequestId);
                return Failure(request, "服务请求 ID 已用于不同的命令，拒绝重复执行。", _core.ProcessManager.State, ServiceErrorCode.RequestIdentityConflict);
            case RequestAdmissionKind.ResultUnavailable:
                return UnknownResult(request, "原请求结果不存在或已过期；不会重新执行命令。");
            case RequestAdmissionKind.Saturated:
                _logger.LogWarning("Rejected service request {RequestId}: request cache is saturated.", request.RequestId);
                return Failure(request, "服务请求的有界结果保留配额已满，请稍后重试。", _core.ProcessManager.State, ServiceErrorCode.OperationBusy);
        }
        ServiceRequestLedger.Entry cached = admission.Entry!;
        if (admission.Kind == RequestAdmissionKind.Execute) { _ = ExecuteCachedRequestAsync(request, cached); }
        return await cached.Completion.Task.WaitAsync(callerCancellationToken).ConfigureAwait(false);
    }

    private RequestRetentionKind ClassifyRetention(ServiceRequest request)
    {
        if (request.Command == ServiceCommand.GetStatus) { return RequestRetentionKind.Observation; }
        // Stop can only stop this process manager's own child, including a
        // stopped child. Invalid TUN targets cannot consume the cleanup reserve.
        if (request.Command == ServiceCommand.StopCore) { return RequestRetentionKind.Cleanup; }
        if (request.Command == ServiceCommand.DisableTun)
        {
            try
            {
                ServiceTunPayload payload = Deserialize<ServiceTunPayload>(request.Payload);
                if (!payload.Enabled && IsCurrentTunTarget(payload)) { return RequestRetentionKind.Cleanup; }
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                // Keep malformed requests in the ordinary quota. The original
                // handler reports their validation error through its cache.
            }
        }
        return RequestRetentionKind.Mutation;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Cached command execution converts any failure into a failure ServiceResponse so concurrent joiners always observe a completed result.")]
    private async Task ExecuteCachedRequestAsync(ServiceRequest request, ServiceRequestLedger.Entry cached)
    {
        CancellationToken operationToken = default;
        try
        {
            using CancellationTokenSource operationTimeout = CreateOperationTimeout(request.Command);
            operationToken = operationTimeout.Token;
            _requestExecutionObserved?.Invoke(request);
            ServiceResponse response = await HandleCoreAsync(request, operationTimeout.Token)
                .ConfigureAwait(false);
            response = response with { ServiceInstanceId = ServiceInstanceId };
            if (JsonSerializer.Serialize(response, _jsonOptions).Length > ServiceProtocol.MaximumResponseCharacters)
            { response = UnknownResult(request, "服务响应超过大小限制，必须核对已确认状态。"); }
            _requests.Complete(cached, response);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            _requests.Cancel(cached, _lifetimeCts.Token);
        }
        catch (OperationCanceledException exception) when (operationToken.IsCancellationRequested)
        {
            string detail = exception is ListenerReadinessCancelledException readinessCancelled
                ? $" {readinessCancelled.Diagnostic}" : string.Empty;
            _requests.Complete(cached, Failure(request, $"ClashTray 服务操作超过执行期限。{detail}",
                _core.ProcessManager.State, ServiceErrorCode.OperationTimedOut));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Service command {Command} ({RequestId}) failed: {Error}",
                request.Command,
                request.RequestId,
                ErrorSanitizer.Sanitize(exception));
            _requests.Complete(cached,
                Failure(request, ErrorSanitizer.Sanitize(exception), _core.ProcessManager.State,
                    CoreStartFailure.ClassifyException(exception)));
        }

    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The command dispatch boundary converts any handler failure into a failure ServiceResponse instead of throwing across the IPC boundary.")]
    private async Task<ServiceResponse> HandleCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Command))
        {
            _logger.LogWarning(
                "Rejected unknown service command {Command} ({RequestId}).",
                request.Command,
                request.RequestId);
        }
        else if (request.Command != ServiceCommand.GetStatus)
        {
            _logger.LogInformation("Executing service command {Command} ({RequestId}).", request.Command, request.RequestId);
        }

        try
        {
            return request.Command switch
            {
                ServiceCommand.GetStatus => await GetStatusAsync(request, cancellationToken),
                ServiceCommand.StartCore => await RunExclusiveAsync(
                    request,
                    token => StartCoreAsync(request, token),
                    cancellationToken),
                ServiceCommand.StopCore => await RunExclusiveAsync(
                    request,
                    token => StopCoreAsync(request, token),
                    cancellationToken),
                ServiceCommand.RestartCore => await RunExclusiveAsync(
                    request,
                    token => RestartCoreAsync(request, token),
                    cancellationToken),
                ServiceCommand.InstallCore => await RunExclusiveAsync(
                    request,
                    token => InstallCoreAsync(request, token),
                    cancellationToken),
                ServiceCommand.RollbackCore => await RunExclusiveAsync(
                    request,
                    token => RollbackCoreAsync(request, token),
                    cancellationToken),
                ServiceCommand.EnableTun => await SetTunAsync(request, enabled: true, cancellationToken),
                ServiceCommand.DisableTun => await SetTunAsync(request, enabled: false, cancellationToken),
                _ => Failure(request, "未知服务命令。")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(request, ErrorSanitizer.Sanitize(exception), errorCode: CoreStartFailure.ClassifyException(exception));
        }
    }

    private TimeSpan GetOperationTimeout(ServiceCommand command) => command switch
    {
        ServiceCommand.GetStatus => StatusQueryTimeout,
        ServiceCommand.EnableTun or ServiceCommand.DisableTun => TunOperationTimeout,
        ServiceCommand.InstallCore or ServiceCommand.RollbackCore => CoreUpdateOperationTimeout,
        _ => _defaultOperationTimeout
    };

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed TUN status probe degrades to a bounded failure response; status queries must never throw across the IPC boundary.")]
    private async Task<ServiceResponse> GetStatusAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (_core.ProcessManager.State != CoreState.Running)
        {
            if (_tunState is not TunState.Off and not TunState.Unavailable)
            {
                return Failure(request, "TUN 状态无法确认。为避免影响网络，未继续重试；请检查服务和诊断日志。", _core.ProcessManager.State);
            }

            return Success(request);
        }

        if (_core.Api is null || _core.ActiveCore is null || _core.RuntimeBinding is null || _core.ProcessIdentity is null)
        {
            return Failure(
                request,
                "服务无法恢复经过确认的 Mihomo 运行绑定。",
                CoreState.Running,
                ServiceErrorCode.ControllerOwnershipUnconfirmed);
        }

        if (!_core.IsRuntimeBindingOwned(_core.RuntimeBinding, _core.ProcessIdentity))
        {
            return Failure(
                request,
                "无法确认控制器端口属于当前受管 Mihomo 核心；已阻止控制器请求。",
                CoreState.Running,
                ServiceErrorCode.ControllerOwnershipUnconfirmed);
        }

        if (_tunState is TunState.Enabling or TunState.Disabling)
        {
            return Success(request);
        }

        using CancellationTokenSource timeout = CreateTimeout(StatusQueryTimeout, cancellationToken);
        try
        {
            using JsonDocument document = await _core.Api.GetConfigurationAsync(force: false, timeout.Token);
            if (!ServiceCoreLifecycle.TryValidateReadyListeners(
                document,
                _core.ActiveCore,
                out MihomoListenerPorts statusPorts,
                out ServiceErrorCode listenerError,
                out string? listenerMessage))
            {
                return Failure(request, listenerMessage ?? "Mihomo 监听状态无法确认。", CoreState.Running, listenerError);
            }

            if (!ServiceCoreLifecycle.AreRuntimePortsMatch(_core.RuntimeBinding, statusPorts))
            {
                return Failure(
                    request,
                    "Mihomo 运行端口与已确认的有效监听绑定不一致。",
                    CoreState.Running,
                    ServiceErrorCode.ProxyPortConflict);
            }

            bool? value = MihomoDataParser.ParseTunEnabled(document);
            if (value is not bool enabled)
            {
                return Failure(request, "无法从 Mihomo 控制器确认 TUN 状态。", CoreState.Running);
            }

            MihomoTunConfiguration configuration = MihomoDataParser.ParseTunConfiguration(document);
            if (!enabled)
            {
                TunNetworkHealth disabledHealth = await _tunHealthProbe.ProbeAsync(
                    configuration,
                    TunNetworkExpectation.Disabled,
                    timeout.Token).ConfigureAwait(false);
                if (!disabledHealth.MeetsDisabled)
                {
                    return Failure(
                        request,
                        "TUN 状态无法确认。为避免影响网络，未继续重试；请检查服务和诊断日志。",
                        CoreState.Running);
                }

                return Success(request);
            }

            lock (_tunLogGate)
            {
                if (_tunListenerErrorSequence > _tunConfirmedListenerErrorSequence)
                {
                    return Failure(
                        request,
                        "TUN 状态无法确认。为避免影响网络，未继续重试；请检查服务和诊断日志。",
                        CoreState.Running);
                }
            }

            if (_tunState != TunState.On)
            {
                return Failure(
                    request,
                    "TUN 状态无法确认。为避免影响网络，未继续重试；请检查服务和诊断日志。",
                    CoreState.Running);
            }

            TunNetworkHealth enabledHealth = await _tunHealthProbe.ProbeAsync(
                configuration,
                TunNetworkExpectation.Enabled,
                timeout.Token).ConfigureAwait(false);
            if (!enabledHealth.MeetsEnabled)
            {
                return Failure(
                    request,
                    "TUN 状态无法确认。为避免影响网络，未继续重试；请检查服务和诊断日志。",
                    CoreState.Running);
            }

            return Success(request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(request, "查询 Mihomo TUN 状态超时，状态暂时无法确认。", CoreState.Running);
        }
        catch (Exception exception)
        {
            return Failure(request, $"查询 Mihomo TUN 状态失败，状态暂时无法确认：{DescribeControllerError(exception)}", CoreState.Running);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        // Cleanup runs on its own bounded token: it must not inherit a caller
        // token that may already be canceled, and it must not hang forever.
        using CancellationTokenSource cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cleanupToken = cleanupTimeout.Token;
        bool safeToStopCore = _core.ProcessManager.State != CoreState.Running
            && (_tunState is TunState.Off or TunState.Unavailable);
        try
        {
            if (_core.Api is not null)
            {
                try
                {
                    TunShutdownResult tunShutdown = await TunShutdownGuard.EnsureDisabledAsync(
                        _core.Api,
                        _tunHealthProbe,
                        cleanupToken);
                    SetTunState(tunShutdown.State);
                    safeToStopCore = tunShutdown.Succeeded;
                }
                catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
                {
                    SetTunState(TunState.Unknown);
                    safeToStopCore = false;
                }
            }

            _core.ProcessManager.LogLineReceived -= OnProcessLogLine;
            await _core.DisposeAsync(safeToStopCore).ConfigureAwait(false);
            if (!IsDesktopProcessRunning())
            {
                _restoreOwnedProxyStates();
            }
        }
        finally
        {
            _operationGate.Dispose();
            _coreUpdates.Dispose();
            _core.DisposeTransport();
            _coreUpdateHttpClient.Dispose();
            _lifetimeCts.Dispose();
        }
    }

    private async Task<ServiceResponse> RunExclusiveAsync(
        ServiceRequest request,
        Func<CancellationToken, Task<ServiceResponse>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        await EnterOperationAsync(request.Command == ServiceCommand.StopCore, cancellationToken).ConfigureAwait(false);

        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task EnterOperationAsync(bool cleanup, CancellationToken cancellationToken)
    {
        if (!cleanup)
        {
            // Once cleanup is waiting, subsequent ordinary mutations must not
            // repeatedly win the semaphore. The accepted cleanup set is bounded
            // by its retained-result quota; its original deadline includes wait.
            if (Volatile.Read(ref _cleanupWaiters) != 0 || !_operationGate.Wait(0, cancellationToken))
            {
                throw new OperationBusyException("ClashTray 服务");
            }
            return;
        }
        Interlocked.Increment(ref _cleanupWaiters);
        try { await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _cleanupWaiters); }
    }

    private static bool IsDesktopProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("ClashTray.App").Any(process =>
            {
                try
                {
                    return process.SessionId != 0;
                }
                finally
                {
                    process.Dispose();
                }
            });
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<ServiceResponse> StartCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        ServiceCorePayload payload = Deserialize<ServiceCorePayload>(request.Payload);
        _core.ValidatePayload(payload);
        if (_core.ProcessManager.State == CoreState.Running)
        { return await GetStatusAsync(request, cancellationToken).ConfigureAwait(false); }

        ServiceCoreStartResult result = await _core.StartAsync(payload, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? Success(request) : Failure(request, result.Error!, result.Core, result.ErrorCode);
    }
    internal static CoreUpdateManifest ValidateCoreUpdatePayload(ServiceCoreUpdatePayload payload) =>
        ServiceCoreUpdateHandler.ValidateCoreUpdatePayload(payload);

    private async Task<ServiceResponse> InstallCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (_core.ProcessManager.State == CoreState.Running)
        { return Failure(request, "请先停止 Mihomo 核心再安装更新。", CoreState.Running); }
        ServiceCoreUpdateResult result = await _coreUpdates.InstallAsync(Deserialize<ServiceCoreUpdatePayload>(request.Payload), cancellationToken).ConfigureAwait(false);
        return CreateCoreUpdateResponse(request, result);
    }

    private async Task<ServiceResponse> RollbackCoreAsync(ServiceRequest request, CancellationToken cancellationToken) =>
        CreateCoreUpdateResponse(request, await _coreUpdates.RollbackAsync(cancellationToken).ConfigureAwait(false));

    private ServiceResponse CreateCoreUpdateResponse(ServiceRequest request, ServiceCoreUpdateResult result) => result.Succeeded
        ? new(request.RequestId, true, _tunState, Payload: result.Path, Core: _core.ProcessManager.State, ProtocolVersion: ServiceProtocol.CurrentVersion)
        : Failure(request, result.Error!, _core.ProcessManager.State);

    private async Task<ServiceResponse> StopCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (_core.ProcessManager.State == CoreState.Running && _core.Api is null)
        {
            SetTunState(TunState.Unknown);
            return Failure(request, "停止核心前无法确认 TUN 状态，核心保持运行。", CoreState.Running);
        }

        if (_core.Api is not null)
        {
            TunShutdownResult tunShutdown = await TunShutdownGuard.EnsureDisabledAsync(
                _core.Api,
                _tunHealthProbe,
                cancellationToken);
            if (!tunShutdown.Succeeded)
            {
                SetTunState(tunShutdown.State);
                return Failure(
                    request,
                    tunShutdown.Error ?? "停止核心前无法确认 TUN 已关闭，核心保持运行。",
                    CoreState.Running);
            }

            SetTunState(tunShutdown.State);
        }

        await _core.StopOwnedProcessAsync(cancellationToken).ConfigureAwait(false);
        SetTunState(TunState.Off);
        return Success(request);
    }

    private async Task<ServiceResponse> RestartCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        ServiceCorePayload payload = Deserialize<ServiceCorePayload>(request.Payload);
        _core.ValidatePayload(payload);
        ServiceResponse stopResponse = await StopCoreAsync(request, cancellationToken);
        if (!stopResponse.Succeeded)
        {
            return stopResponse;
        }

        return await StartCoreAsync(request with { Payload = JsonSerializer.Serialize(payload, _jsonOptions) }, cancellationToken);
    }

    private async Task<ServiceResponse> SetTunAsync(
        ServiceRequest request,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ServiceTunPayload payload = Deserialize<ServiceTunPayload>(request.Payload);
        if (payload.ControllerPort is < 1 or > 65535
            || payload.ControllerSecret is null
            || payload.Enabled != enabled)
        {
            _logger.LogWarning("Rejected TUN request {RequestId}: invalid payload parameters.", request.RequestId);
            return Failure(request, "TUN 请求参数无效。", _core.ProcessManager.State);
        }

        if (_core.ProcessManager.State != CoreState.Running || _core.Api is null || _core.ActiveCore is null)
        {
            SetTunState(TunState.Unavailable);
            return Failure(request, "Mihomo 核心尚未运行。", _core.ProcessManager.State);
        }
        if (!IsCurrentTunTarget(payload))
        {
            return Failure(request, "TUN 请求与当前受管 Mihomo 核心不匹配。", _core.ProcessManager.State, ServiceErrorCode.ControllerOwnershipUnconfirmed);
        }

        TunTransactionResult result = await _tunSingleFlight.RequestAsync(
                enabled,
                async (target, token) =>
                {
                    Volatile.Write(ref _tunDesired, target ? 1 : 0);
                    // BooleanSingleFlight owns execution independently of its
                    // waiters. Keep the original service operation deadline for
                    // the underlying transaction and queued cleanup as well.
                    using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                    return await ExecuteTunTransactionAsync(payload, target, linked.Token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
        SetTunState(result.State);
        return result.Succeeded
            ? Success(request)
            : Failure(
                request,
                result.Error ?? "TUN 状态无法确认。",
                _core.ProcessManager.State,
                result.ErrorCode);
    }

    private bool IsCurrentTunTarget(ServiceTunPayload payload) =>
        _core.ProcessManager.State == CoreState.Running
        && _core.Api is not null && _core.ActiveCore is not null && _core.RuntimeBinding is not null
        && _core.RuntimeBinding.ControllerPort == payload.ControllerPort
        && _core.RuntimeBinding.InstanceId == payload.InstanceId
        && _core.RuntimeBinding.OwnerInstanceId == payload.OwnerInstanceId
        && string.Equals(_core.ActiveCore.ControllerSecret, payload.ControllerSecret, StringComparison.Ordinal);

    private async Task<TunTransactionResult> ExecuteTunTransactionAsync(
        ServiceTunPayload payload,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await EnterOperationAsync(cleanup: !enabled, cancellationToken).ConfigureAwait(false);

        SetTunState(enabled ? TunState.Enabling : TunState.Disabling);
        try
        {
            if (_core.ProcessManager.State != CoreState.Running
                || _core.Api is null
                || _core.ActiveCore is null)
            {
                SetTunState(TunState.Unavailable);
                throw new InvalidOperationException("Mihomo 核心尚未运行。");
            }

            if (_core.RuntimeBinding is null
                || _core.RuntimeBinding.ControllerPort != payload.ControllerPort
                || _core.RuntimeBinding.InstanceId != payload.InstanceId
                || _core.RuntimeBinding.OwnerInstanceId != payload.OwnerInstanceId
                || !string.Equals(_core.ActiveCore.ControllerSecret, payload.ControllerSecret, StringComparison.Ordinal))
            {
                SetTunState(TunState.Unknown);
                throw new InvalidOperationException("TUN 请求与当前 Mihomo 核心不匹配。");
            }

            TunTransactionResult result = await _tunTransactions.ExecuteAsync(enabled, cancellationToken)
                .ConfigureAwait(false);
            SetTunState(result.State);
            if (result.Succeeded)
            {
                lock (_tunLogGate)
                {
                    _tunConfirmedListenerErrorSequence = _tunListenerErrorSequence;
                }
            }
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private CancellationTokenSource CreateOperationTimeout(ServiceCommand command)
    {
        if (_operationDeadlineForTest is { } factory)
        {
            return factory(_lifetimeCts.Token);
        }

        return CreateTimeout(GetOperationTimeout(command), _lifetimeCts.Token);
    }

    private static CancellationTokenSource CreateTimeout(TimeSpan timeout, CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static string DescribeControllerError(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } statusCode } => $"HTTP {(int)statusCode}",
        MihomoStreamException streamException => $"{streamException.Path} {streamException.Kind}",
        TimeoutException => "超时",
        _ => exception.GetType().Name
    };

    /// <summary>
    /// Monotonic revision of the service-owned TUN state. Every mutation goes
    /// through <see cref="SetTunState"/> so the log listener, lifecycle
    /// commands and shutdown cleanup share one write boundary.
    /// </summary>
    internal long TunStateRevision
    {
        get
        {
            lock (_tunLogGate)
            {
                return _tunStateRevision;
            }
        }
    }

    private void SetTunState(TunState state)
    {
        lock (_tunLogGate)
        {
            _tunState = state;
            _tunStateRevision++;
        }
    }

    private void OnProcessLogLine(string line, bool _)
    {
        if (!line.Contains("Start TUN listening error:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_tunLogGate)
        {
            _tunListenerErrorSequence++;
            _lastTunListenerError = ErrorSanitizer.Sanitize(line);
            if (_tunState == TunState.On)
            {
                SetTunState(TunState.Unknown);
            }
        }
    }

    private sealed class ControllerTunTransactionBackend : ITunTransactionBackend
    {
        private readonly ServiceRuntimeController _controller;

        public ControllerTunTransactionBackend(ServiceRuntimeController controller)
        {
            _controller = controller;
        }

        public long CurrentGeneration => _controller._core.ProcessManager.Generation;

        public bool IsControllerHealthy =>
            _controller._core.ProcessManager.State == CoreState.Running
            && _controller._core.Api is not null
            && _controller._core.ActiveCore is not null;

        public long CaptureListenerErrorMarker()
        {
            lock (_controller._tunLogGate)
            {
                return _controller._tunListenerErrorSequence;
            }
        }

        public bool HasListenerErrorSince(long marker, out string? error)
        {
            lock (_controller._tunLogGate)
            {
                if (_controller._tunListenerErrorSequence <= marker)
                {
                    error = null;
                    return false;
                }

                error = _controller._lastTunListenerError;
                return true;
            }
        }

        public async Task<TunObservation> ReadAsync(CancellationToken cancellationToken)
        {
            MihomoApiClient api = _controller._core.Api
                ?? throw new InvalidOperationException("Mihomo 控制器尚未连接。");
            using JsonDocument document = await api.GetConfigurationAsync(force: false, cancellationToken)
                .ConfigureAwait(false);
            MihomoTunConfiguration configuration = MihomoDataParser.ParseTunConfiguration(document);
            return new TunObservation(
                configuration.Enabled,
                configuration,
                CurrentGeneration,
                IsControllerHealthy);
        }

        public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
        {
            MihomoApiClient api = _controller._core.Api
                ?? throw new InvalidOperationException("Mihomo 控制器尚未连接。");
            await api.SetTunAsync(enabled, cancellationToken).ConfigureAwait(false);
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Recovery restart is best-effort and reports success through its return value; it must not throw into the TUN shutdown guard.")]
        public async Task<bool> RestartCoreWithTunDisabledAsync(CancellationToken cancellationToken)
        {
            ServiceCorePayload? payload = _controller._core.ActiveCore;
            if (payload is null || _controller._core.ProcessManager.State != CoreState.Running)
            {
                return false;
            }

            try
            {
                // The coordinator has already confirmed controller=false and
                // the Windows probe's disabled convergence. This direct
                // service-owned stop is therefore the bounded, safe recovery
                // path; it does not blind-kill an unconfirmed TUN.
                await _controller._core.StopOwnedProcessAsync(cancellationToken).ConfigureAwait(false);
                _controller.SetTunState(TunState.Off);

                ServiceRequest request = new(
                    Guid.NewGuid(),
                    ServiceCommand.StartCore,
                    JsonSerializer.Serialize(payload, _controller._jsonOptions),
                    ProtocolVersion: ServiceProtocol.CurrentVersion);
                ServiceResponse response = await _controller.StartCoreAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                return response.Succeeded && _controller._core.ProcessManager.State == CoreState.Running;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _controller._logger.LogWarning(
                    "Recovery restart after TUN shutdown failed: {Error}",
                    ErrorSanitizer.Sanitize(exception));
                return false;
            }
        }
    }

    private ServiceResponse Success(ServiceRequest request, string? error = null) =>
        new(
            request.RequestId,
            true,
            _tunState,
            Error: error,
            Core: CoreState,
            ProtocolVersion: ServiceProtocol.CurrentVersion,
            RuntimeBinding: _core.RuntimeBinding,
            ServiceInstanceId: ServiceInstanceId);

    private ServiceResponse Failure(
        ServiceRequest request,
        string error,
        CoreState? core = null,
        ServiceErrorCode errorCode = ServiceErrorCode.None) =>
        new(
            request.RequestId,
            false,
            _tunState,
            Error: error,
            Core: core ?? CoreState,
            ErrorCode: errorCode,
            ProtocolVersion: ServiceProtocol.CurrentVersion,
            RuntimeBinding: _core.RuntimeBinding,
            ServiceInstanceId: ServiceInstanceId);

    private ServiceResponse UnknownResult(ServiceRequest request, string error) =>
        Failure(request, error, errorCode: ServiceErrorCode.RequestResultUnavailable) with
        { DispatchState = ServiceDispatchState.DispatchedAwaitingResult };

    private T Deserialize<T>(string? payload) where T : class =>
        string.IsNullOrWhiteSpace(payload)
            ? throw new InvalidDataException("服务请求缺少参数。")
            : JsonSerializer.Deserialize<T>(payload, _jsonOptions)
              ?? throw new InvalidDataException("服务请求参数无效。");
}
