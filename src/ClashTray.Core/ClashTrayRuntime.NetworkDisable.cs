using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed partial class ClashTrayRuntime
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Storage failures are recorded separately and must not veto owned network cleanup.")]
    private async Task RememberNetworkDisableAsync(bool tun)
    {
        _networkDisableIntent = _networkDisableIntent with
        {
            SystemProxyOff = _networkDisableIntent.SystemProxyOff || !tun,
            TunOff = _networkDisableIntent.TunOff || tun
        };
        _settings = _networkDisableIntent.Apply(_settings);
        if (!tun) { Interlocked.Increment(ref _proxyIntentRevision); }
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            _networkDisableIntent = _networkDisableIntent.Merge(await _settingsRecovery.ReadNetworkDisableIntentAsync(timeout.Token));
            _settings = _networkDisableIntent.Apply(_settings);
            await _settingsRecovery.WriteNetworkDisableIntentAsync(_networkDisableIntent, timeout.Token);
        }
        catch (Exception exception) { RecordSettingsRecoveryFailure(exception); }
    }

    private async Task ClearNetworkDisableIntentAsync(bool tun, CancellationToken cancellationToken)
    {
        NetworkDisableIntent remaining = _networkDisableIntent with
        {
            SystemProxyOff = tun && _networkDisableIntent.SystemProxyOff,
            TunOff = !tun && _networkDisableIntent.TunOff
        };
        await _settingsRecovery.WriteNetworkDisableIntentAsync(remaining, cancellationToken);
        _networkDisableIntent = remaining;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Confirmed Off is retained while preference persistence reports partial success to the caller.")]
    private async Task PersistNetworkDisableAsync(bool tun)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await RecoverPendingSettingsAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            _settings = _networkDisableIntent.Apply(_settings);
            string message = $"{(tun ? "TUN" : "系统代理")}已确认关闭，但关闭偏好尚未保存；本次运行将保持关闭，请恢复设置存储后重试。";
            _stateStore.Update(snapshot => snapshot with { ErrorMessage = message });
            Publish();
            throw new InvalidOperationException(message, exception);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The OS confirmed state must be published even when owned proxy restoration fails.")]
    private async Task DisableSystemProxyCoreAsync(bool persistPreference, CancellationToken cancellationToken)
    {
        if (persistPreference) { await RememberNetworkDisableAsync(tun: false); }
        _stateStore.Update(snapshot => snapshot with { SystemProxy = SystemProxyState.Disabling });
        Publish();
        try
        {
            await _localDevice.DisableSystemProxyAsync(cancellationToken);
            SystemProxyState confirmed = _localDevice.DetectSystemProxyState();
            _stateStore.Update(snapshot => snapshot with { SystemProxy = confirmed, ErrorMessage = null });
            if (confirmed != SystemProxyState.Off)
            {
                throw new InvalidOperationException("系统代理尚未确认关闭；可能存在外部所有权更改，请检查代理恢复状态。");
            }
        }
        catch (Exception exception)
        {
            _stateStore.Update(snapshot => snapshot with { SystemProxy = _localDevice.DetectSystemProxyState(), ErrorMessage = ErrorSanitizer.Sanitize(exception) });
            Publish();
            throw;
        }
        finally { Interlocked.Increment(ref _proxyOwnershipRevision); }
        Publish();
        if (persistPreference) { await PersistNetworkDisableAsync(tun: false); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Service-confirmed Off and failed or unknown cleanup outcomes must not be confused with storage errors.")]
    private async Task<TunState> DisableTunCoreAsync(bool persistPreference, CancellationToken cancellationToken)
    {
        if (persistPreference) { await RememberNetworkDisableAsync(tun: true); }
        TunState? responseState = null;
        _stateStore.Update(snapshot => snapshot with { Tun = TunState.Disabling });
        Publish();
        try
        {
            CoreRuntimeBinding binding = ActiveRuntimeBinding
                ?? throw new InvalidOperationException("TUN 操作需要已经确认的 Mihomo 运行绑定。");
            ServiceResponse response = await _localDevice.DisableTunAsync(new(binding.ControllerPort, string.Empty, false, binding.InstanceId, binding.OwnerInstanceId), cancellationToken);
            responseState = response.Tun;
            _confirmedTunState = response.Tun;
            _stateStore.Update(snapshot => snapshot with { Tun = response.Tun, ErrorMessage = response.Error });
            if (!response.Succeeded || response.Tun != TunState.Off)
            {
                throw new ServiceCommandException(response.ErrorCode == ServiceErrorCode.None ? ServiceErrorCode.TunStateUnknown : response.ErrorCode, response.Error ?? "TUN 未确认关闭。");
            }
        }
        catch (Exception exception)
        {
            _confirmedTunState = responseState ?? (exception is OperationCanceledException ? TunState.Unknown : TunState.Failed);
            _stateStore.Update(snapshot => snapshot with { Tun = _confirmedTunState, ErrorMessage = ErrorSanitizer.Sanitize(exception) });
            Publish();
            throw;
        }
        Publish();
        if (persistPreference) { await PersistNetworkDisableAsync(tun: true); }
        return TunState.Off;
    }
}
