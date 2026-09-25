using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed partial class ClashTrayRuntime
{

    public Task UpdateNetworkSwitchRulesAsync(
        NetworkSwitchRuleSet rules,
        CancellationToken cancellationToken = default) =>
        UpdateNetworkSwitchRulesCoreAsync(rules, cancellationToken);

    private async Task UpdateNetworkSwitchRulesCoreAsync(
        NetworkSwitchRuleSet rules,
        CancellationToken cancellationToken)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            await _networkSwitchRuntimeController.SetRulesAsync(rules, cancellationToken);
        }
    }

    public async Task ClearNetworkSwitchManualOverrideAsync(
        CancellationToken cancellationToken = default)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            if (_networkSwitchRuntimeController.IsInitialized)
            {
                _networkSwitchRuntimeController.ClearManualOverride();
            }
        }
    }

    public Task SetModeAsync(ProxyMode mode, CancellationToken cancellationToken = default) =>
        _proxyOps.SetModeAsync(mode, CaptureCurrentEndpointCommandTarget(), cancellationToken);

    public Task SetModeAsync(
        ProxyMode mode,
        EndpointCommandTarget? expectedTarget,
        CancellationToken cancellationToken = default) =>
        _proxyOps.SetModeAsync(
            mode,
            expectedTarget ?? CaptureCurrentEndpointCommandTarget(),
            cancellationToken);

    public Task SetLocalModeAsync(ProxyMode mode, CancellationToken cancellationToken = default) =>
        _proxyOps.SetLocalModeAsync(mode, CaptureLocalEndpointCommandTarget(), cancellationToken);

    public Task SetLocalModeAsync(
        ProxyMode mode,
        EndpointCommandTarget? expectedTarget,
        CancellationToken cancellationToken = default) =>
        _proxyOps.SetLocalModeAsync(
            mode,
            expectedTarget ?? CaptureLocalEndpointCommandTarget(),
            cancellationToken);

    public Task SelectProxyAsync(
        string group,
        string proxy,
        CancellationToken cancellationToken = default) =>
        _proxyOps.SelectProxyAsync(
            group,
            proxy,
            CaptureCurrentEndpointCommandTarget(),
            cancellationToken);

    public Task SelectProxyAsync(
        string group,
        string proxy,
        EndpointCommandTarget? expectedTarget,
        CancellationToken cancellationToken = default) =>
        _proxyOps.SelectProxyAsync(
            group,
            proxy,
            expectedTarget ?? CaptureCurrentEndpointCommandTarget(),
            cancellationToken);

    public Task<int?> TestProxyDelayAsync(string proxy, CancellationToken cancellationToken = default) =>
        _proxyOps.TestProxyDelayAsync(proxy, cancellationToken);

    public Task<IReadOnlyDictionary<string, int?>> TestProxyGroupDelayAsync(
        string group,
        CancellationToken cancellationToken = default) =>
        _proxyOps.TestProxyGroupDelayAsync(group, cancellationToken);

    private async Task CommitGroupDelayResultsAsync(
        MihomoApiClient api,
        long generation,
        IReadOnlyDictionary<string, int?> delays,
        CancellationToken cancellationToken)
    {
        await _dataRefreshLock.WaitAsync(cancellationToken);
        try
        {
            // Refresh now/history from the core and apply the confirmed batch result atomically.
            ProxyDataResult proxies = await _dataRefresh.TryGetProxyDataAsync(api, cancellationToken);
            EnsureControllerSession(api, generation, "测速期间核心会话已切换，请重新测速。");

            string? LatestDelay(string name, string? previous) => delays.TryGetValue(name, out int? delay)
                ? delay?.ToString(System.Globalization.CultureInfo.InvariantCulture) : previous;
            _stateStore.Update(snapshot => snapshot with
            {
                ProxyGroups = proxies.Groups.Select(item => item with { Delay = LatestDelay(item.Name, item.Delay) }).ToArray(),
                ProxyNodes = proxies.Nodes.Select(item => item with { Delay = LatestDelay(item.Name, item.Delay) }).ToArray()
            });
            Publish();
        }
        finally { _dataRefreshLock.Release(); }
    }

    public Task SetSystemProxyAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetSystemProxyCoreAsync(
            enabled,
            persistPreference: true,
            cancellationToken: cancellationToken,
            operationLease: null);

    private async Task SetSystemProxyCoreAsync(
        bool enabled,
        bool persistPreference,
        CancellationToken cancellationToken,
        OperationGate.Lease? operationLease = null)
    {
        OperationGate.Lease? ownedLease = null;
        if (operationLease is null)
        {
            ownedLease = await _operationLock.AcquireAsync(cancellationToken);
        }

        AppSettings previousSettings = _settings;
        bool preferenceChanged = persistPreference && previousSettings.SystemProxyEnabled != enabled;
        try
        {
            if (persistPreference)
            {
                Interlocked.Increment(ref _proxyIntentRevision);
            }

            if (preferenceChanged)
            {
                await SaveSettingsForOperationAsync(
                    previousSettings with { SystemProxyEnabled = enabled },
                    cancellationToken);
            }

            if (enabled && !CoreHealthConfirmed)
            {
                await RevokeSystemProxyForCoreLossAsync(operationLease ?? ownedLease!);
                _stateStore.Update(snapshot => snapshot with { SystemProxy = _localDevice.SystemProxyState });
                Publish();
                return;
            }

            _stateStore.Update(snapshot => snapshot with { SystemProxy = enabled ? SystemProxyState.Enabling : SystemProxyState.Disabling });
            Publish();
            if (enabled)
            {
                await _localDevice.EnableSystemProxyAsync(_settings.MixedPort, _settings.BypassList, cancellationToken);
            }
            else
            {
                await _localDevice.DisableSystemProxyAsync(cancellationToken);
            }

            Interlocked.Increment(ref _proxyOwnershipRevision);
            _stateStore.Update(snapshot => snapshot with { SystemProxy = _localDevice.SystemProxyState, ErrorMessage = null });
            Publish();
        }
        catch
        {
            Interlocked.Increment(ref _proxyOwnershipRevision);
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _stateStore.Update(snapshot => snapshot with { SystemProxy = _localDevice.SystemProxyState });
            Publish();
            throw;
        }
        finally
        {
            ownedLease?.Dispose();
        }
    }

    public async Task SetTunAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await RequestTunOperationAsync(
                enabled,
                persistPreference: true,
                operationLease: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<TunState> RequestTunOperationAsync(
        bool enabled,
        bool persistPreference,
        OperationGate.Lease? operationLease,
        CancellationToken cancellationToken) =>
        _tunOperation.RequestAsync(
            enabled,
            (target, token) => SetTunCoreAsync(
                target,
                persistPreference,
                operationLease,
                token),
            cancellationToken);

    private async Task<TunState> SetTunCoreAsync(
        bool enabled,
        bool persistPreference,
        OperationGate.Lease? operationLease,
        CancellationToken cancellationToken)
    {
        OperationGate.Lease? ownedLease = null;
        if (operationLease is null)
        {
            ownedLease = _operationLock.TryAcquire();
            if (ownedLease is null)
            {
                throw new OperationBusyException("TUN");
            }
        }

        AppSettings previousSettings = _settings;
        bool preferenceChanged = persistPreference && previousSettings.TunEnabled != enabled;
        TunState? serviceResponseState = null;
        try
        {
            _stateStore.Update(snapshot => snapshot with { Tun = enabled ? TunState.Enabling : TunState.Disabling });
            Publish();
            ServiceTunPayload payload = new(
                _settings.ControllerPort,
                string.Empty,
                enabled);
            ServiceResponse response = await _localDevice.SetTunAsync(payload, cancellationToken)
                .ConfigureAwait(false);
            serviceResponseState = response.Tun;
            if (!response.Succeeded)
            {
                _confirmedTunState = response.Tun;
                _stateStore.Update(snapshot => snapshot with
                {
                    Tun = response.Tun,
                    ErrorMessage = response.Error ?? "TUN 操作失败。"
                });
                Publish();
                throw new ServiceCommandException(
                    response.ErrorCode,
                    response.Error ?? "TUN 操作失败。");
            }

            if (response.Tun != (enabled ? TunState.On : TunState.Off))
            {
                _confirmedTunState = response.Tun;
                _stateStore.Update(snapshot => snapshot with { Tun = response.Tun, ErrorMessage = response.Error });
                Publish();
                throw new ServiceCommandException(
                    response.ErrorCode == ServiceErrorCode.None
                        ? ServiceErrorCode.TunStateUnknown
                        : response.ErrorCode,
                    response.Error ?? "TUN 状态无法确认。");
            }

            if (preferenceChanged)
            {
                await SaveSettingsForOperationAsync(
                    previousSettings with { TunEnabled = enabled },
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            _confirmedTunState = response.Tun;
            _stateStore.Update(snapshot => snapshot with { Tun = response.Tun, ErrorMessage = null });
            Publish();
            return response.Tun;
        }
        catch (OperationCanceledException)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            TunState state = serviceResponseState is TunState confirmedState
                ? confirmedState
                : TunState.Unknown;
            _confirmedTunState = state;
            _stateStore.Update(snapshot => snapshot with
            {
                Tun = state,
                ErrorMessage = serviceResponseState is TunState
                    ? null
                    : "TUN 操作已取消，状态无法确认。"
            });
            Publish();
            throw;
        }
        catch (TimeoutException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _confirmedTunState = TunState.Unavailable;
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable, ErrorMessage = ErrorSanitizer.Sanitize(exception) });
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch (ServiceRequestUnknownException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _confirmedTunState = TunState.Failed;
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Failed, ErrorMessage = ErrorSanitizer.Sanitize(exception) });
            Publish();
            throw new InvalidOperationException("TUN 操作结果无法确认，请检查服务状态后重试。", exception);
        }
        catch (IOException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _confirmedTunState = TunState.Unavailable;
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable, ErrorMessage = ErrorSanitizer.Sanitize(exception) });
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _confirmedTunState = TunState.Unavailable;
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable, ErrorMessage = ErrorSanitizer.Sanitize(exception) });
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            if (serviceResponseState is TunState responseState)
            {
                _confirmedTunState = responseState;
                _stateStore.Update(snapshot => snapshot with
                {
                    Tun = responseState,
                    ErrorMessage = snapshot.ErrorMessage ?? "TUN 操作结果无法确认。"
                });
            }
            else
            {
                _confirmedTunState = TunState.Failed;
                _stateStore.Update(snapshot => snapshot with { Tun = TunState.Failed });
            }
            Publish();
            throw;
        }
        finally
        {
            ownedLease?.Dispose();
        }
    }

    private NetworkSwitchPolicyInput CreateNetworkSwitchPolicyInput(
        NetworkContextSnapshot context,
        NetworkSwitchRuleSet rules) =>
        new(
            rules.AutomaticSwitchingEnabled,
            context,
            rules.Rules,
            rules.DefaultConfigurationId,
            GetActiveConfiguration()?.Id,
            Snapshot.Configurations
                .Select(configuration => configuration.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private void OnNetworkSwitchStatusChanged(object? sender, NetworkSwitchStatus status)
    {
        _stateStore.Update(snapshot => snapshot with { NetworkSwitch = status });
        Publish();
    }

    private void QueueSystemProxyRecovery(CoreLossContext context)
    {
        if (_runtimeCts.IsCancellationRequested)
        {
            return;
        }

        lock (_proxyRecoveryGate)
        {
            if (_proxyRecoveryTask is { IsCompleted: false })
            {
                return;
            }

            _proxyRecoveryTask = Task.Run(() => RevokeSystemProxyForCoreLossWithLeaseAsync(
                context,
                CancellationToken.None));
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Proxy revocation on core loss is safety-critical and must complete even when individual steps fail.")]
    private async Task RevokeSystemProxyForCoreLossAsync(
        OperationGate.Lease operationLease,
        CoreLossContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (context is { } initialContext && !CanApplyCoreLossRecovery(initialContext))
        {
            return;
        }

        if (context is { } committedContext && !CanApplyCoreLossRecovery(committedContext))
        {
            return;
        }

        try
        {
            await ReconcileSystemProxyAsync(coreRunning: false, cancellationToken);
        }
        catch (Exception exception)
        {
            _logs.AddApplicationLog(new LogEntry(
                DateTimeOffset.UtcNow,
                "ClashTray",
                "error",
                $"核心不可用时撤销系统代理失败：{ErrorSanitizer.Sanitize(exception)}"));
            _stateStore.Update(snapshot => snapshot with
            {
                SystemProxy = _localDevice.SystemProxyState,
                ErrorMessage = "核心不可用时撤销系统代理失败，系统代理状态需要恢复。",
                Logs = _logs.Snapshot()
            });
            Publish();
        }
    }

    private async Task RevokeSystemProxyForCoreLossWithLeaseAsync(
        CoreLossContext? context = null,
        CancellationToken cancellationToken = default)
    {
        using OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await RevokeSystemProxyForCoreLossAsync(operationLease, context, cancellationToken).ConfigureAwait(false);
    }

    private bool CanApplyCoreLossRecovery(CoreLossContext context) =>
        context.LifecycleEpoch == Volatile.Read(ref _coreLifecycleEpoch)
        && context.ProcessGeneration == _processManager.Generation
        && context.ControllerGeneration == ControllerGeneration
        && context.ProxyOwnershipRevision == Volatile.Read(ref _proxyOwnershipRevision)
        && context.ProxyIntentRevision == Volatile.Read(ref _proxyIntentRevision)
        && !context.CoreHealthConfirmed
        && Snapshot.Core.State != CoreState.Running
        && !_runtimeCts.IsCancellationRequested;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Queued proxy recovery is observed best-effort; the awaiting operation must not fail because recovery observation failed.")]
    private async Task AwaitQueuedProxyRecoveryAsync()
    {
        Task? recoveryTask;
        lock (_proxyRecoveryGate)
        {
            recoveryTask = _proxyRecoveryTask;
        }

        if (recoveryTask is null)
        {
            return;
        }

        try
        {
            await recoveryTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logs.AddApplicationLog(new LogEntry(
                DateTimeOffset.UtcNow,
                "ClashTray",
                "error",
                $"等待系统代理恢复任务失败：{ErrorSanitizer.Sanitize(exception)}"));
            Publish();
        }
    }
}
