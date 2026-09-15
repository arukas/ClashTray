using System.Text.Json;
using System.Diagnostics;
using System.Text;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Service;

/// <summary>
/// The privileged boundary accepts only typed, allow-listed lifecycle and TUN requests.
/// It never accepts a shell command or an arbitrary executable path.
/// </summary>
internal sealed class ServiceRuntimeController : IAsyncDisposable
{
    private static readonly TimeSpan StatusQueryTimeout = TimeSpan.FromSeconds(2);
    private readonly AppPaths _paths;
    private readonly MihomoProcessManager _processManager = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly HttpClient _coreUpdateHttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CoreUpdater _coreUpdater;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly BooleanSingleFlight<TunTransactionResult> _tunSingleFlight =
        new("TUN");
    private readonly object _tunLogGate = new();
    private readonly ITunNetworkHealthProbe _tunHealthProbe;
    private readonly TunTransactionCoordinator _tunTransactions;
    private MihomoApiClient? _api;
    private TunState _tunState = TunState.Off;
    private int _tunDesired;
    private ServiceCorePayload? _activeCore;
    private long _tunListenerErrorSequence;
    private long _tunConfirmedListenerErrorSequence;
    private string? _lastTunListenerError;

    public ServiceRuntimeController(AppPaths? paths = null, string? managedUserSid = null)
        : this(paths, managedUserSid, null)
    {
    }

    internal ServiceRuntimeController(
        AppPaths? paths,
        string? managedUserSid,
        ITunNetworkHealthProbe? tunHealthProbe)
    {
        _paths = paths ?? new AppPaths();
        _coreUpdater = new CoreUpdater(_paths, _coreUpdateHttpClient, managedUserSid);
        _tunHealthProbe = tunHealthProbe ?? new WindowsTunNetworkHealthProbe();
        _tunTransactions = new TunTransactionCoordinator(
            new ControllerTunTransactionBackend(this),
            _tunHealthProbe,
            shouldRetryEnable: () => Volatile.Read(ref _tunDesired) != 0);
        _processManager.LogLineReceived += OnProcessLogLine;
    }

    public CoreState CoreState => _processManager.State;

    public async Task<ServiceResponse> HandleAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
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
            return Failure(request, ErrorSanitizer.Sanitize(exception));
        }
    }

    private async Task<ServiceResponse> GetStatusAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (_processManager.State != CoreState.Running || _api is null || _activeCore is null)
        {
            if (_tunState is not TunState.Off and not TunState.Unavailable)
            {
                _tunState = TunState.Unknown;
                return Failure(request, "TUN 状态无法确认。为避免影响网络，未继续重试；请检查服务和诊断日志。", _processManager.State);
            }

            return Success(request);
        }

        if (_tunState is TunState.Enabling or TunState.Disabling)
        {
            return Success(request);
        }

        using CancellationTokenSource timeout = CreateTimeout(StatusQueryTimeout, cancellationToken);
        try
        {
            using JsonDocument document = await _api.GetConfigurationAsync(force: false, timeout.Token);
            bool? value = MihomoDataParser.ParseTunEnabled(document);
            if (value is not bool enabled)
            {
                _tunState = TunState.Unknown;
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
                    _tunState = TunState.Unknown;
                    return Failure(
                        request,
                        "TUN 状态无法确认。为避免影响网络，未继续重试；请检查服务和诊断日志。",
                        CoreState.Running);
                }

                _tunState = TunState.Off;
                return Success(request);
            }

            lock (_tunLogGate)
            {
                if (_tunListenerErrorSequence > _tunConfirmedListenerErrorSequence)
                {
                    _tunState = TunState.Unknown;
                    return Failure(
                        request,
                        "TUN 状态无法确认。为避免影响网络，未继续重试；请检查服务和诊断日志。",
                        CoreState.Running);
                }
            }

            if (_tunState != TunState.On)
            {
                _tunState = TunState.Unknown;
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
                _tunState = TunState.Unknown;
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
            _tunState = TunState.Unknown;
            return Failure(request, "查询 Mihomo TUN 状态超时，状态暂时无法确认。", CoreState.Running);
        }
        catch (Exception exception)
        {
            _tunState = TunState.Unknown;
            return Failure(request, $"查询 Mihomo TUN 状态失败，状态暂时无法确认：{DescribeControllerError(exception)}", CoreState.Running);
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool safeToStopCore = _processManager.State != CoreState.Running
            && (_tunState is TunState.Off or TunState.Unavailable);
        try
        {
            if (_api is not null)
            {
                TunShutdownResult tunShutdown = await TunShutdownGuard.EnsureDisabledAsync(
                    _api,
                    _tunHealthProbe,
                    CancellationToken.None);
                _tunState = tunShutdown.State;
                safeToStopCore = tunShutdown.Succeeded;
            }

            _processManager.LogLineReceived -= OnProcessLogLine;
            if (safeToStopCore)
            {
                await _processManager.DisposeAsync();
            }
            if (!IsDesktopProcessRunning())
            {
                SystemProxyRecovery.RestoreOwnedStatesForLoadedUsers();
            }
        }
        finally
        {
            _operationGate.Dispose();
            _httpClient.Dispose();
            _coreUpdateHttpClient.Dispose();
        }
    }

    private async Task<ServiceResponse> RunExclusiveAsync(
        ServiceRequest request,
        Func<CancellationToken, Task<ServiceResponse>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_operationGate.Wait(0, CancellationToken.None))
        {
            throw new OperationBusyException("ClashTray 服务");
        }

        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
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
        ValidateCorePayload(payload);
        if (_processManager.State == CoreState.Running)
        {
            return Success(request);
        }

        try
        {
            await ManagedCoreVerifier.ValidateAsync(_paths, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Failure(request, $"受管 Mihomo 核心校验失败：{ErrorSanitizer.Sanitize(exception)}", CoreState.Failed);
        }

        string executablePath = _paths.ManagedCoreExecutable;
        if (!await _processManager.ValidateAsync(
            executablePath,
            payload.ConfigurationPath,
            workingDirectory: null,
            safePaths: _paths.ExternalUiRoot,
            cancellationToken: cancellationToken))
        {
            return Failure(request, "Mihomo 配置验证失败。", CoreState.Failed);
        }

        await _processManager.StartAsync(
            executablePath,
            payload.ConfigurationPath,
            payload.WorkingDirectory,
            safePaths: _paths.ExternalUiRoot,
            cancellationToken: cancellationToken);
        _activeCore = payload;
        _api = CreateApi(payload.ControllerPort, payload.ControllerSecret);
        _tunState = TunState.Unknown;
        return await RefreshTunStateAfterStartAsync(request, cancellationToken);
    }

    private async Task<ServiceResponse> InstallCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (_processManager.State == CoreState.Running)
        {
            return Failure(request, "请先停止 Mihomo 核心再安装更新。", CoreState.Running);
        }

        ServiceCoreUpdatePayload payload = Deserialize<ServiceCoreUpdatePayload>(request.Payload);
        bool installed = false;
        try
        {
            CoreUpdateManifest manifest = new(payload.Version, payload.DownloadUri, payload.Sha256);
            CoreUpdater.ValidateManifest(manifest);
            string path = await _coreUpdater.DownloadAndInstallAsync(manifest, cancellationToken);
            installed = true;
            if (!await ValidateInstalledCoreAsync(cancellationToken))
            {
                throw new InvalidDataException("新 Mihomo 核心未通过最小配置健康检查。");
            }

            return new ServiceResponse(
                request.RequestId,
                true,
                _tunState,
                Payload: path,
                Core: _processManager.State);
        }
        catch (OperationCanceledException exception)
        {
            if (installed)
            {
                string? rollbackError = await TryRollbackInstalledCoreAsync();
                if (rollbackError is not null)
                {
                    throw new IOException($"核心更新取消，且自动回滚失败：{rollbackError}", exception);
                }
            }

            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or HttpRequestException
            or IOException
            or InvalidOperationException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            string message = $"核心安装失败：{ErrorSanitizer.Sanitize(exception)}";
            if (installed)
            {
                string? rollbackError = await TryRollbackInstalledCoreAsync();
                message = rollbackError is null
                    ? $"{message} 已自动回滚到上一个核心。"
                    : $"{message}；自动回滚失败：{rollbackError}";
            }

            return Failure(request, message, _processManager.State);
        }
    }

    private async Task<ServiceResponse> RollbackCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (_processManager.State == CoreState.Running)
        {
            return Failure(request, "请先停止 Mihomo 核心再回滚。", CoreState.Running);
        }

        try
        {
            string path = await _coreUpdater.RollbackLastInstallAsync(cancellationToken);
            await _processManager.StopAsync(CancellationToken.None);
            return new ServiceResponse(
                request.RequestId,
                true,
                _tunState,
                Payload: path,
                Core: _processManager.State);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            return Failure(request, $"核心回滚失败：{ErrorSanitizer.Sanitize(exception)}", _processManager.State);
        }
    }

    private async Task<bool> ValidateInstalledCoreAsync(CancellationToken cancellationToken)
    {
        string runtimeDirectory = Path.Combine(_paths.RuntimeRoot, "mihomo");
        Directory.CreateDirectory(runtimeDirectory);
        string configurationPath = Path.Combine(
            runtimeDirectory,
            $"core-healthcheck-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(
                configurationPath,
                """
                mixed-port: 7890
                mode: rule
                log-level: silent
                external-controller: 127.0.0.1:19090
                secret: ''
                proxies: []
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            return await _processManager.ValidateAsync(
                _paths.ManagedCoreExecutable,
                configurationPath,
                runtimeDirectory,
                cancellationToken);
        }
        finally
        {
            if (File.Exists(configurationPath))
            {
                File.Delete(configurationPath);
            }
        }
    }

    private async Task<string?> TryRollbackInstalledCoreAsync()
    {
        string? error = null;
        try
        {
            await _coreUpdater.RollbackLastInstallAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            error = ErrorSanitizer.Sanitize(exception);
        }

        try
        {
            await _processManager.StopAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            string sanitized = ErrorSanitizer.Sanitize(exception);
            error = error is null ? sanitized : $"{error}；{sanitized}";
        }

        return error;
    }

    private async Task<ServiceResponse> StopCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (_processManager.State == CoreState.Running && _api is null)
        {
            _tunState = TunState.Unknown;
            return Failure(request, "停止核心前无法确认 TUN 状态，核心保持运行。", CoreState.Running);
        }

        if (_api is not null)
        {
            TunShutdownResult tunShutdown = await TunShutdownGuard.EnsureDisabledAsync(
                _api,
                _tunHealthProbe,
                cancellationToken);
            if (!tunShutdown.Succeeded)
            {
                _tunState = tunShutdown.State;
                return Failure(
                    request,
                    tunShutdown.Error ?? "停止核心前无法确认 TUN 已关闭，核心保持运行。",
                    CoreState.Running);
            }

            _tunState = tunShutdown.State;
        }

        await _processManager.StopAsync(cancellationToken);
        _api = null;
        _activeCore = null;
        _tunState = TunState.Off;
        return Success(request);
    }

    private async Task<ServiceResponse> RestartCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        ServiceCorePayload payload = Deserialize<ServiceCorePayload>(request.Payload);
        ValidateCorePayload(payload);
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
            return Failure(request, "TUN 请求参数无效。", _processManager.State);
        }

        Volatile.Write(ref _tunDesired, enabled ? 1 : 0);
        TunTransactionResult result = await _tunSingleFlight.RequestAsync(
                enabled,
                (target, token) => ExecuteTunTransactionAsync(payload, target, token),
                cancellationToken)
            .ConfigureAwait(false);
        _tunState = result.State;
        return result.Succeeded
            ? Success(request)
            : Failure(
                request,
                result.Error ?? "TUN 状态无法确认。",
                _processManager.State,
                result.ErrorCode);
    }

    private async Task<TunTransactionResult> ExecuteTunTransactionAsync(
        ServiceTunPayload payload,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!_operationGate.Wait(0, CancellationToken.None))
        {
            throw new OperationBusyException("ClashTray 服务");
        }

        _tunState = enabled ? TunState.Enabling : TunState.Disabling;
        try
        {
            if (_processManager.State != CoreState.Running
                || _api is null
                || _activeCore is null)
            {
                _tunState = TunState.Unavailable;
                throw new InvalidOperationException("Mihomo 核心尚未运行。");
            }

            if (_activeCore.ControllerPort != payload.ControllerPort
                || !string.Equals(_activeCore.ControllerSecret, payload.ControllerSecret, StringComparison.Ordinal))
            {
                _tunState = TunState.Unknown;
                throw new InvalidOperationException("TUN 请求与当前 Mihomo 核心不匹配。");
            }

            TunTransactionResult result = await _tunTransactions.ExecuteAsync(enabled, cancellationToken)
                .ConfigureAwait(false);
            _tunState = result.State;
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

    private async Task<ServiceResponse> RefreshTunStateAfterStartAsync(
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Every core start is safe-TUN-off. If an externally supplied
            // runtime file still contains tun.enable=true, use the same
            // service-owned close guard before reporting the start state.
            TunShutdownResult shutdown = await TunShutdownGuard.EnsureDisabledAsync(
                    _api!,
                    _tunHealthProbe,
                    cancellationToken)
                .ConfigureAwait(false);
            _tunState = shutdown.State;
            return shutdown.Succeeded
                ? Success(request)
                : Success(
                    request,
                    shutdown.Error ?? "Mihomo 已启动，但 TUN 状态暂时无法确认；为避免影响网络，TUN 保持关闭。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _tunState = TunState.Unknown;
            return Success(request, $"Mihomo 已启动，但 TUN 状态暂时无法确认：{DescribeControllerError(exception)}");
        }
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
                _tunState = TunState.Unknown;
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

        public long CurrentGeneration => _controller._processManager.Generation;

        public bool IsControllerHealthy =>
            _controller._processManager.State == CoreState.Running
            && _controller._api is not null
            && _controller._activeCore is not null;

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
            MihomoApiClient api = _controller._api
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
            MihomoApiClient api = _controller._api
                ?? throw new InvalidOperationException("Mihomo 控制器尚未连接。");
            await api.SetTunAsync(enabled, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> RestartCoreWithTunDisabledAsync(CancellationToken cancellationToken)
        {
            ServiceCorePayload? payload = _controller._activeCore;
            if (payload is null || _controller._processManager.State != CoreState.Running)
            {
                return false;
            }

            try
            {
                // The coordinator has already confirmed controller=false and
                // the Windows probe's disabled convergence. This direct
                // service-owned stop is therefore the bounded, safe recovery
                // path; it does not blind-kill an unconfirmed TUN.
                await _controller._processManager.StopAsync(cancellationToken).ConfigureAwait(false);
                _controller._api = null;
                _controller._activeCore = null;
                _controller._tunState = TunState.Off;

                ServiceRequest request = new(
                    Guid.NewGuid(),
                    ServiceCommand.StartCore,
                    JsonSerializer.Serialize(payload, _controller._jsonOptions));
                ServiceResponse response = await _controller.StartCoreAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                return response.Succeeded && _controller._processManager.State == CoreState.Running;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }
    }

    private MihomoApiClient CreateApi(int port, string secret) =>
        new(_httpClient, new Uri($"http://127.0.0.1:{port}/"), secret);

    private void ValidateCorePayload(ServiceCorePayload payload)
    {
        if (!IsAllowedRuntimePath(payload.ConfigurationPath, allowYaml: true)
            || !IsAllowedRuntimePath(payload.WorkingDirectory, allowYaml: false)
            || payload.ControllerPort is < 1 or > 65535
            || payload.ControllerSecret is null)
        {
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

    private ServiceResponse Success(ServiceRequest request, string? error = null) =>
        new(request.RequestId, true, _tunState, Error: error, Core: CoreState);

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
            ErrorCode: errorCode);

    private T Deserialize<T>(string? payload) where T : class =>
        string.IsNullOrWhiteSpace(payload)
            ? throw new InvalidDataException("服务请求缺少参数。")
            : JsonSerializer.Deserialize<T>(payload, _jsonOptions)
              ?? throw new InvalidDataException("服务请求参数无效。");
}
