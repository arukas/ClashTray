using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Service;

internal enum TunOperationPhase
{
    Idle,
    Reading,
    Enabling,
    VerifyingEnable,
    Disabling,
    VerifyingDisable,
    Recovering,
    Failed,
    Unknown
}

internal sealed record TunObservation(
    bool? Enabled,
    MihomoTunConfiguration Configuration,
    long Generation,
    bool ControllerHealthy);

internal interface ITunTransactionBackend
{
    public long CurrentGeneration { get; }

    public bool IsControllerHealthy { get; }

    public long CaptureListenerErrorMarker();

    public bool HasListenerErrorSince(long marker, out string? error);

    public Task<TunObservation> ReadAsync(CancellationToken cancellationToken);

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken);

    public Task<bool> RestartCoreWithTunDisabledAsync(CancellationToken cancellationToken);
}

internal sealed record TunTransactionOptions(
    TimeSpan TransitionTimeout,
    TimeSpan PollInterval,
    int StableSamples,
    int MaxAutomaticRetries)
{
    public static TunTransactionOptions Default { get; } = new(
        TransitionTimeout: TimeSpan.FromSeconds(8),
        PollInterval: TimeSpan.FromMilliseconds(350),
        StableSamples: 2,
        MaxAutomaticRetries: 1);
}

internal sealed record TunTransactionResult(
    bool Succeeded,
    TunState State,
    TunOperationPhase Phase,
    string? Error,
    bool RestartedCore,
    int RetryCount,
    Guid OperationId,
    long CoreGeneration)
{
    /// <summary>
    /// Configuration preflight failures leave a healthy, already-disabled
    /// core untouched. Runtime/listener/probe failures still go through the
    /// bounded recovery path.
    /// </summary>
    public bool NeedsRecovery { get; init; } = true;

    public ServiceErrorCode ErrorCode { get; init; } = ServiceErrorCode.None;
}

/// <summary>
/// Owns the TUN transition state machine. It commits On/Off only after both
/// Mihomo and the read-only Windows network probe agree. A failed enable is
/// taken through a bounded close-and-recover path and can have at most one
/// automatic retry.
/// </summary>
internal sealed class TunTransactionCoordinator
{
    internal const string MissingAddressError =
        "TUN 启动失败，Mihomo 已自动恢复，当前 TUN 已关闭。\n原因：缺少可用的接口地址。";
    internal const string ConfigurationMissingAddressError =
        "TUN 无法开启，当前 TUN 保持关闭。\n原因：配置没有可用的接口地址。";
    internal const string UnknownStateError =
        "TUN 状态无法确认。为避免影响网络，未继续重试；请检查服务和诊断日志。";

    private readonly ITunTransactionBackend _backend;
    private readonly ITunNetworkHealthProbe _healthProbe;
    private readonly TunTransactionOptions _options;
    private readonly Func<bool> _shouldRetryEnable;
    private TunOperationPhase _phase = TunOperationPhase.Idle;

    public TunTransactionCoordinator(
        ITunTransactionBackend backend,
        ITunNetworkHealthProbe healthProbe,
        TunTransactionOptions? options = null,
        Func<bool>? shouldRetryEnable = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
        _options = options ?? TunTransactionOptions.Default;
        if (_options.TransitionTimeout <= TimeSpan.Zero
            || _options.PollInterval <= TimeSpan.Zero
            || _options.StableSamples < 1
            || _options.MaxAutomaticRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _shouldRetryEnable = shouldRetryEnable ?? (() => true);
    }

    public TunOperationPhase Phase => _phase;

    public async Task<TunTransactionResult> ExecuteAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        Guid operationId = Guid.NewGuid();
        if (!enabled)
        {
            TunTransactionResult result = await ExecuteDisableAsync(operationId, cancellationToken).ConfigureAwait(false);
            _phase = result.Succeeded ? TunOperationPhase.Idle :
                result.State == TunState.Unknown ? TunOperationPhase.Unknown : TunOperationPhase.Failed;
            return result;
        }

        TunTransactionResult firstAttempt = await RunAttemptAsync(
            enabled: true,
            operationId,
            retryCount: 0,
            cancellationToken).ConfigureAwait(false);
        if (firstAttempt.Succeeded)
        {
            _phase = TunOperationPhase.Idle;
            return firstAttempt;
        }

        if (!firstAttempt.NeedsRecovery)
        {
            _phase = TunOperationPhase.Failed;
            return firstAttempt;
        }

        _phase = TunOperationPhase.Recovering;
        bool safelyDisabled = await TryConvergeDisabledAsync(cancellationToken).ConfigureAwait(false);
        if (!safelyDisabled)
        {
            _phase = TunOperationPhase.Unknown;
            return firstAttempt with
            {
                State = TunState.Unknown,
                Phase = TunOperationPhase.Unknown,
                Error = UnknownStateError,
                ErrorCode = ServiceErrorCode.TunStateUnknown
            };
        }

        if (!_shouldRetryEnable())
        {
            _phase = TunOperationPhase.Failed;
            return firstAttempt with
            {
                State = TunState.Off,
                Phase = TunOperationPhase.Failed,
                Error = firstAttempt.Error ?? MissingAddressError
            };
        }

        long generationBeforeRestart = _backend.CurrentGeneration;
        bool restarted = await TryRestartWithTunDisabledAsync(cancellationToken).ConfigureAwait(false);
        if (!restarted)
        {
            _phase = TunOperationPhase.Failed;
            return firstAttempt with
            {
                State = TunState.Off,
                Phase = TunOperationPhase.Failed,
                Error = firstAttempt.Error ?? "TUN 启动失败，核心已恢复且 TUN 已关闭。",
                RestartedCore = false
            };
        }

        long generationAfterRestart = _backend.CurrentGeneration;
        if (generationAfterRestart == generationBeforeRestart
            || !await ConfirmDisabledAfterRestartAsync(cancellationToken).ConfigureAwait(false))
        {
            _phase = TunOperationPhase.Unknown;
            return firstAttempt with
            {
                State = TunState.Unknown,
                Phase = TunOperationPhase.Unknown,
                Error = UnknownStateError,
                ErrorCode = ServiceErrorCode.TunStateUnknown,
                RestartedCore = true,
                CoreGeneration = generationAfterRestart
            };
        }

        if (_options.MaxAutomaticRetries < 1 || !_shouldRetryEnable())
        {
            _phase = TunOperationPhase.Failed;
            return firstAttempt with
            {
                State = TunState.Off,
                Phase = TunOperationPhase.Failed,
                Error = firstAttempt.Error ?? "TUN 启动失败，核心已恢复且 TUN 已关闭。",
                RestartedCore = true,
                CoreGeneration = generationAfterRestart
            };
        }

        TunTransactionResult retry = await RunAttemptAsync(
            enabled: true,
            operationId,
            retryCount: 1,
            cancellationToken).ConfigureAwait(false);
        if (retry.Succeeded)
        {
            _phase = TunOperationPhase.Idle;
            return retry with { RestartedCore = true, CoreGeneration = _backend.CurrentGeneration };
        }

        _phase = TunOperationPhase.Recovering;
        bool retrySafelyDisabled = await TryConvergeDisabledAsync(cancellationToken).ConfigureAwait(false);
        _phase = retrySafelyDisabled ? TunOperationPhase.Failed : TunOperationPhase.Unknown;
        return retry with
        {
            State = retrySafelyDisabled ? TunState.Off : TunState.Unknown,
            Phase = retrySafelyDisabled ? TunOperationPhase.Failed : TunOperationPhase.Unknown,
            Error = retrySafelyDisabled ? MissingAddressError : UnknownStateError,
            ErrorCode = retrySafelyDisabled
                ? ServiceErrorCode.TunMissingInterfaceAddress
                : ServiceErrorCode.TunStateUnknown,
            RestartedCore = true,
            CoreGeneration = _backend.CurrentGeneration
        };
    }

    private async Task<TunTransactionResult> ExecuteDisableAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        TunTransactionResult attempt = await RunAttemptAsync(
            enabled: false,
            operationId,
            retryCount: 0,
            cancellationToken).ConfigureAwait(false);
        if (attempt.Succeeded)
        {
            return attempt;
        }

        _phase = TunOperationPhase.Unknown;
        return attempt with
        {
            State = TunState.Unknown,
            Phase = TunOperationPhase.Unknown,
            Error = UnknownStateError,
            ErrorCode = ServiceErrorCode.TunStateUnknown
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "A failed transition is converted to a typed result so recovery can close TUN safely.")]
    private async Task<TunTransactionResult> RunAttemptAsync(
        bool enabled,
        Guid operationId,
        int retryCount,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TransitionTimeout);
        CancellationToken token = timeout.Token;
        long listenerMarker = _backend.CaptureListenerErrorMarker();
        TunObservation initial;
        try
        {
            _phase = TunOperationPhase.Reading;
            initial = await _backend.ReadAsync(token).ConfigureAwait(false);
            if (initial.Enabled is not bool initialEnabled)
            {
                return Failed(
                    operationId,
                    TunState.Unknown,
                    retryCount,
                    "无法确认当前 TUN 状态。",
                    initial.Generation,
                    ServiceErrorCode.TunStateUnknown);
            }

            if (enabled && !initial.Configuration.CanProduceInterfaceAddress)
            {
                return Failed(
                        operationId,
                        TunState.Off,
                        retryCount,
                        ConfigurationMissingAddressError,
                        initial.Generation,
                        ServiceErrorCode.TunConfigurationMissingAddress)
                    with { NeedsRecovery = false };
            }

            _phase = enabled ? TunOperationPhase.Enabling : TunOperationPhase.Disabling;
            if (initialEnabled != enabled)
            {
                await _backend.SetEnabledAsync(enabled, token).ConfigureAwait(false);
            }

            _phase = enabled ? TunOperationPhase.VerifyingEnable : TunOperationPhase.VerifyingDisable;
            bool confirmed = await WaitForConvergenceAsync(
                enabled,
                initial.Generation,
                listenerMarker,
                initial.Configuration,
                token).ConfigureAwait(false);
            if (confirmed)
            {
                return new TunTransactionResult(
                    Succeeded: true,
                    State: enabled ? TunState.On : TunState.Off,
                    Phase: TunOperationPhase.Idle,
                    Error: null,
                    RestartedCore: false,
                    RetryCount: retryCount,
                    OperationId: operationId,
                    CoreGeneration: initial.Generation);
            }

            bool hasListenerError = _backend.HasListenerErrorSince(listenerMarker, out string? listenerError);
            ServiceErrorCode errorCode = !enabled
                ? ServiceErrorCode.TunStateUnknown
                : !hasListenerError
                    || listenerError?.Contains("missing interface address", StringComparison.OrdinalIgnoreCase) == true
                        ? ServiceErrorCode.TunMissingInterfaceAddress
                        : ServiceErrorCode.None;
            return Failed(
                operationId,
                enabled ? TunState.Failed : TunState.Unknown,
                retryCount,
                hasListenerError
                    ? $"Mihomo TUN 监听失败：{ErrorSanitizer.Sanitize(listenerError)}"
                    : enabled ? MissingAddressError : "Mihomo 未确认 TUN 已关闭。",
                _backend.CurrentGeneration,
                errorCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(
                operationId,
                enabled ? TunState.Failed : TunState.Unknown,
                retryCount,
                enabled ? "TUN 状态确认超时。" : "TUN 关闭确认超时。",
                _backend.CurrentGeneration,
                ServiceErrorCode.TunStateUnknown);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(
                operationId,
                enabled ? TunState.Failed : TunState.Unknown,
                retryCount,
                ErrorSanitizer.Sanitize(exception),
                _backend.CurrentGeneration);
        }
    }

    private async Task<bool> WaitForConvergenceAsync(
        bool enabled,
        long expectedGeneration,
        long listenerMarker,
        MihomoTunConfiguration initialConfiguration,
        CancellationToken cancellationToken)
    {
        int stableSamples = 0;
        while (true)
        {
            if (_backend.HasListenerErrorSince(listenerMarker, out _))
            {
                return false;
            }

            TunObservation observation = await _backend.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (observation.Generation != expectedGeneration
                || !observation.ControllerHealthy
                || !_backend.IsControllerHealthy)
            {
                return false;
            }

            MihomoTunConfiguration configuration = observation.Configuration == MihomoTunConfiguration.Empty
                ? initialConfiguration
                : observation.Configuration;
            TunNetworkHealth health = await _healthProbe.ProbeAsync(
                configuration,
                enabled ? TunNetworkExpectation.Enabled : TunNetworkExpectation.Disabled,
                cancellationToken).ConfigureAwait(false);
            bool converged = observation.Enabled == enabled
                && (enabled ? health.MeetsEnabled : health.MeetsDisabled);
            if (converged)
            {
                stableSamples++;
                if (stableSamples >= _options.StableSamples)
                {
                    return true;
                }
            }
            else
            {
                stableSamples = 0;
            }

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Recovery must treat every backend failure as an unsafe-to-confirm state.")]
    private async Task<bool> TryConvergeDisabledAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TransitionTimeout);
        try
        {
            await _backend.SetEnabledAsync(false, timeout.Token).ConfigureAwait(false);
            TunObservation observation = await _backend.ReadAsync(timeout.Token).ConfigureAwait(false);
            return await WaitForConvergenceAsync(
                    enabled: false,
                    observation.Generation,
                    _backend.CaptureListenerErrorMarker(),
                    observation.Configuration,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "ClashTray service: TUN disable convergence failed: {0}",
                ErrorSanitizer.Sanitize(exception));
            return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "A recovery restart failure is represented as false and never triggers a blind kill.")]
    private async Task<bool> TryRestartWithTunDisabledAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _backend.RestartCoreWithTunDisabledAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "ClashTray service: TUN recovery restart failed: {0}",
                ErrorSanitizer.Sanitize(exception));
            return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Failure to confirm a restarted core is an explicit unknown state.")]
    private async Task<bool> ConfirmDisabledAfterRestartAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TransitionTimeout);
        try
        {
            TunObservation observation = await _backend.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (observation.Enabled is not false || !observation.ControllerHealthy || !_backend.IsControllerHealthy)
            {
                return false;
            }

            return await WaitForConvergenceAsync(
                    enabled: false,
                    observation.Generation,
                    _backend.CaptureListenerErrorMarker(),
                    observation.Configuration,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "ClashTray service: TUN post-restart confirmation failed: {0}",
                ErrorSanitizer.Sanitize(exception));
            return false;
        }
    }

    private static TunTransactionResult Failed(
        Guid operationId,
        TunState state,
        int retryCount,
        string error,
        long generation,
        ServiceErrorCode errorCode = ServiceErrorCode.None) => new(
            Succeeded: false,
            State: state,
            Phase: state == TunState.Unknown ? TunOperationPhase.Unknown : TunOperationPhase.Failed,
            Error: error,
            RestartedCore: false,
            RetryCount: retryCount,
        OperationId: operationId,
        CoreGeneration: generation)
        {
            ErrorCode = errorCode
        };
}
