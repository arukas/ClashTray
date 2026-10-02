using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed partial class ClashTrayRuntime
{

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Settings application rolls back to the previously persisted settings on any failure, so the rollback must run regardless of failure type.")]
    public async Task UpdateSettingsAsync(AppSettingsPatch patch, bool reconcileStartup = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        using OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken);
        await RecoverPendingSettingsAsync(cancellationToken);
        AppSettings previousSettings = _settings;
        AppSettings settings = patch.Apply(previousSettings);
        ValidateSettings(settings);
        bool networkSettingsChanged = settings.AllowLan != previousSettings.AllowLan
            || settings.Ipv6 != previousSettings.Ipv6;
        bool coreRestartRequired = RequiresCoreRestart(previousSettings, settings);
        bool systemProxyBindingChanged = HasSystemProxyBindingChanged(previousSettings, settings);
        bool systemProxyPreferenceChanged = settings.SystemProxyEnabled != previousSettings.SystemProxyEnabled;
        bool coreWasRunning = IsCoreRunningForSettings();
        bool settingsSaved = false;
        bool restartStarted = false;
        StartupRegistrationChange? startupChange = null;
        try
        {
            if (reconcileStartup)
            {
                startupChange = _startupRegistration.Ensure(
                    settings.StartWithWindows,
                    ResolveStartupExecutablePath(settings.StartWithWindows));
            }

            await _settingsRecovery.PrepareAsync(previousSettings, settings, cancellationToken);
            await _settingsStore.SaveAsync(settings, cancellationToken);
            settingsSaved = true;
            _settings = settings;
            if (systemProxyPreferenceChanged)
            {
                Interlocked.Increment(ref _proxyIntentRevision);
            }

            if (systemProxyBindingChanged
                && _localDevice.SystemProxyState is SystemProxyState.On
                    or SystemProxyState.RestoreRequired
                    or SystemProxyState.Enabling)
            {
                await ReconcileSystemProxyAsync(coreRunning: false, cancellationToken);
            }

            if (coreRestartRequired && coreWasRunning)
            {
                restartStarted = true;
                await RestartCoreCoreAsync(operationLease, cancellationToken);
                if (!IsCoreHealthy())
                {
                    throw new InvalidOperationException("运行中设置已保存，但核心重启健康检查失败。");
                }
            }
            else if (networkSettingsChanged && _api is not null)
            {
                try
                {
                    await ApplyProgramNetworkPreferencesAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogControllerFailure("程序局域网/IPv6 设置覆盖", "/configs", exception, 0);
                    _stateStore.Update(snapshot => snapshot with
                    {
                        ErrorMessage = $"程序局域网/IPv6 设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
                        Logs = _logs.Snapshot()
                    });
                    throw new InvalidOperationException("运行中网络设置应用失败，正在恢复旧设置。", exception);
                }
            }

            if (systemProxyBindingChanged)
            {
                await ReconcileSystemProxyAsync(
                    coreRunning: IsCoreHealthy(),
                    cancellationToken);
            }

            _settingsRecovery.Complete();
            Publish();
        }
        catch (Exception exception)
        {
            if (!settingsSaved)
            {
                // No side effect was admitted. Retain a recovery record if an
                // ambiguous storage failure needs to be reconciled next time.
                SettingsRestorationResult initialRestoration = await RestoreSettingsAfterOperationFailureAsync(previousSettings);
                Exception? startupRollbackException = null;
                if (startupChange is { Changed: true })
                {
                    try
                    {
                        _startupRegistration.Rollback(startupChange);
                    }
                    catch (Exception startupException)
                    {
                        startupRollbackException = startupException;
                    }
                }

                if (startupRollbackException is not null)
                {
                    throw new InvalidOperationException(
                        "设置保存失败，且 Windows 启动项回滚失败。",
                        new AggregateException(new Exception?[] { exception, initialRestoration.Failure, startupRollbackException }.OfType<Exception>()));
                }

                ThrowIfSettingsRecoveryIncomplete(exception);
                throw;
            }

            Exception? rollbackException = null;
            try
            {
                await RollbackSettingsChangeAsync(
                    previousSettings,
                    coreRestartRequired,
                    coreWasRunning,
                    networkSettingsChanged,
                    systemProxyBindingChanged,
                    restartStarted,
                    operationLease);
            }
            catch (Exception restoreException)
            {
                rollbackException = restoreException;
            }

            Exception? startupRollbackFailure = null;
            if (startupChange is { Changed: true })
            {
                try
                {
                    _startupRegistration.Rollback(startupChange);
                }
                catch (Exception restoreException)
                {
                    startupRollbackFailure = restoreException;
                }
            }

            Exception?[] rollbackFailures = [rollbackException, startupRollbackFailure];
            Exception[] failures = rollbackFailures.OfType<Exception>().ToArray();
            string message = failures.Length == 0
                ? "设置应用失败，已恢复旧设置。"
                : "设置应用失败，旧设置或核心状态恢复失败，请检查核心状态。";
            _stateStore.Update(snapshot => snapshot with
            {
                ErrorMessage = message,
                Logs = _logs.Snapshot()
            });
            Publish();

            if (failures.Length > 0)
            {
                throw new InvalidOperationException(
                    message,
                    new AggregateException(new[] { exception }.Concat(failures)));
            }

            throw;
        }
    }

    private async Task ApplyProgramOverridesAsync(
        bool coreRunning,
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        await ApplyProgramOverridesCoreAsync(coreRunning, operationLease, cancellationToken);
    }

    private async Task ApplyProgramOverridesWithLeaseAsync(
        bool coreRunning,
        CancellationToken cancellationToken)
    {
        using OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await ApplyProgramOverridesAsync(coreRunning, operationLease, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyProgramOverridesCoreAsync(
        bool coreRunning,
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        await ApplyControllerProgramOverridesAsync(coreRunning, operationLease, cancellationToken);
        await ApplyLocalDeviceProgramOverridesAsync(coreRunning, cancellationToken);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Program overrides are best-effort; a failing override must not fail the core lifecycle operation that triggered it.")]
    private async Task ApplyControllerProgramOverridesAsync(
        bool coreRunning,
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        if (!coreRunning || _api is null)
        {
            return;
        }

        try
        {
            await ApplyProgramNetworkPreferencesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogControllerFailure("程序局域网/IPv6 设置覆盖", "/configs", exception, 0);
            _stateStore.Update(snapshot => snapshot with
            {
                ErrorMessage = $"程序局域网/IPv6 设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
                Logs = _logs.Snapshot()
            });
            Publish();
        }

        try
        {
            await ApplyProgramTunPreferenceAsync(operationLease, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogControllerFailure("程序 TUN 设置覆盖", "/configs", exception, 0);
            _stateStore.Update(snapshot => snapshot with
            {
                ErrorMessage = $"程序 TUN 设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
                Logs = _logs.Snapshot()
            });
            Publish();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Program overrides are best-effort; a failing override must not fail the core lifecycle operation that triggered it.")]
    private async Task ApplyLocalDeviceProgramOverridesAsync(
        bool coreRunning,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileSystemProxyAsync(coreRunning, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logs.AddApplicationLog(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "error", $"程序系统代理设置应用失败：{ErrorSanitizer.Sanitize(exception)}"));
            _stateStore.Update(snapshot => snapshot with
            {
                SystemProxy = _localDevice.SystemProxyState,
                ErrorMessage = $"程序系统代理设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
                Logs = _logs.Snapshot()
            });
            Publish();
        }
    }

    private async Task ApplyProgramNetworkPreferencesAsync(CancellationToken cancellationToken)
    {
        MihomoControllerSession? session = _controllerSessions.Current;
        if (session is null)
        {
            return;
        }

        MihomoApiClient api = session.Api;
        EnsureControllerCommand(
            api,
            session.Generation,
            EndpointCommand.ControlLocalCore,
            "程序局域网/IPv6 设置期间核心会话已切换，请重试。");
        using JsonDocument configuration = await api.GetConfigurationAsync(force: false, cancellationToken);
        bool? currentAllowLan = MihomoDataParser.ParseAllowLan(configuration);
        bool? currentIpv6 = MihomoDataParser.ParseIpv6(configuration);
        if (currentAllowLan is bool currentAllowLanValue
            && currentAllowLanValue == _settings.AllowLan
            && currentIpv6 is bool currentIpv6Value
            && currentIpv6Value == _settings.Ipv6)
        {
            return;
        }

        try
        {
            using JsonDocument response = await api.SetNetworkSettingsAsync(
                _settings.AllowLan,
                _settings.Ipv6,
                cancellationToken);
            if (!await ConfirmNetworkSettingsAsync(
                    api,
                    _settings.AllowLan,
                    _settings.Ipv6,
                    cancellationToken))
            {
                throw new InvalidOperationException("Mihomo 未确认程序局域网/IPv6 设置。");
            }
        }
        catch
        {
            if (!await TryRestoreNetworkSettingsAsync(api, currentAllowLan, currentIpv6))
            {
                _logs.AddApplicationLog(new LogEntry(
                    DateTimeOffset.UtcNow,
                    "ClashTray",
                    "error",
                    "程序局域网/IPv6 设置应用失败，且无法恢复核心原始设置。"));
            }

            throw;
        }
    }

    private async Task ApplyProgramTunPreferenceAsync(
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        if (!_usingServiceCore)
        {
            _confirmedTunState = TunState.Unavailable;
            if (Snapshot.Tun != TunState.Unavailable)
            {
                _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                Publish();
            }

            return;
        }

        MihomoControllerSession? session = _controllerSessions.Current;
        if (session is null)
        {
            return;
        }

        MihomoApiClient api = session.Api;
        EnsureControllerCommand(
            api,
            session.Generation,
            EndpointCommand.ControlLocalCore,
            "程序 TUN 设置期间核心会话已切换，请重试。");
        using JsonDocument configuration = await api.GetConfigurationAsync(force: false, cancellationToken);
        bool? current = MihomoDataParser.ParseTunEnabled(configuration);
        if (current is not bool currentValue)
        {
            return;
        }

        TunState expectedConfirmedState = currentValue ? TunState.On : TunState.Off;
        bool desiredTun = _settings.TunEnabled && !_networkDisableIntent.TunOff;
        if (desiredTun && _settingsRecoveryFailure is not null) { return; }
        if (currentValue == desiredTun
            && _confirmedTunState == expectedConfirmedState)
        {
            _confirmedTunState = currentValue ? TunState.On : TunState.Off;
            if (!currentValue && ReferenceEquals(_api, api) && Snapshot.Tun is not (TunState.Enabling or TunState.Disabling))
            {
                _stateStore.Update(snapshot => snapshot with { Tun = TunState.Off });
                Publish();
            }

            return;
        }

        // TUN writes belong exclusively to the service. The desktop process
        // may observe /configs here, but it never PATCHes the controller.
        await RequestTunOperationAsync(
                desiredTun,
                persistPreference: false,
                operationLease: operationLease,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReconcileSystemProxyAsync(bool coreRunning, CancellationToken cancellationToken)
    {
        bool listenerConfirmed = TryGetConfirmedMixedPort(out int confirmedMixedPort);
        bool desiredProxy = _settings.SystemProxyEnabled && !_networkDisableIntent.SystemProxyOff;
        string? listenerError = desiredProxy && coreRunning && !listenerConfirmed
            ? SystemProxyListenerUnavailableMessage
            : null;
        if (desiredProxy
            && coreRunning
            && listenerConfirmed)
        {
            if (_settingsRecoveryFailure is null && _localDevice.SystemProxyState is (SystemProxyState.Off or SystemProxyState.Failed))
            {
                await _localDevice.EnableSystemProxyAsync(confirmedMixedPort, _settings.BypassList, cancellationToken);
                Interlocked.Increment(ref _proxyOwnershipRevision);
            }
        }
        else
        {
            SystemProxyState detectedState = _localDevice.DetectSystemProxyState();
            if (detectedState is SystemProxyState.On
                or SystemProxyState.RestoreRequired
                or SystemProxyState.Enabling)
            {
                await _localDevice.DisableSystemProxyAsync(cancellationToken);
                Interlocked.Increment(ref _proxyOwnershipRevision);
            }
        }

        SystemProxyState state = _localDevice.SystemProxyState;
        if (Snapshot.SystemProxy != state
            || listenerError is not null && Snapshot.ErrorMessage != listenerError
            || listenerError is null && Snapshot.ErrorMessage == SystemProxyListenerUnavailableMessage)
        {
            _stateStore.Update(snapshot => snapshot with
            {
                SystemProxy = state,
                ErrorMessage = listenerError ?? (snapshot.ErrorMessage == SystemProxyListenerUnavailableMessage
                    ? null : snapshot.ErrorMessage)
            });
            Publish();
        }
    }

    private async Task RollbackSettingsChangeAsync(
        AppSettings previousSettings,
        bool coreRestartRequired,
        bool coreWasRunning,
        bool networkSettingsChanged,
        bool systemProxyBindingChanged,
        bool restartStarted,
        OperationGate.Lease operationLease)
    {
        SettingsRestorationResult restoration = await RestoreSettingsAfterOperationFailureAsync(previousSettings);
        using CancellationTokenSource recoveryTimeout = new(TimeSpan.FromSeconds(30));
        try
        {

        if (restartStarted)
        {
            await RestartCoreCoreAsync(operationLease, recoveryTimeout.Token);
            if (coreWasRunning && !IsCoreHealthy())
            {
                throw new InvalidOperationException("旧设置已恢复，但核心未能恢复健康。");
            }
        }
        else if (networkSettingsChanged && _api is not null)
        {
            await ApplyProgramNetworkPreferencesAsync(recoveryTimeout.Token);
        }

        if (systemProxyBindingChanged)
        {
            await ReconcileSystemProxyAsync(
                coreRunning: IsCoreHealthy(),
                recoveryTimeout.Token);
        }
        }
        catch (Exception exception) when (restoration.Failure is not null)
        {
            throw new InvalidOperationException("设置和核心恢复均未完成。", new AggregateException(restoration.Failure, exception));
        }
        if (restoration.Failure is not null) { throw new InvalidOperationException("设置恢复未完成。", restoration.Failure); }
    }

    private async Task SaveSettingsForOperationAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _settingsRecovery.PrepareAsync(_settings, settings, cancellationToken);
        await _settingsStore.SaveAsync(settings, cancellationToken);
        _settings = settings;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Failure rollback must run to completion regardless of which restore step fails.")]
    private async Task<SettingsRestorationResult> RestoreSettingsAfterOperationFailureAsync(AppSettings settings)
    {
        if (!_settingsRecovery.Exists && _settings == settings)
        {
            return new SettingsRestorationResult(true);
        }
        _settings = _networkDisableIntent.Apply(settings);
        try
        {
            using CancellationTokenSource recoveryTimeout = new(TimeSpan.FromSeconds(3));
            SettingsRecoveryRecord? record = await _settingsRecovery.ReadAsync(recoveryTimeout.Token);
            if (record is not null)
            {
                SettingsLoadResult loaded = await _settingsStore.LoadWithStatusAsync(recoveryTimeout.Token);
                EnsureSettingsStorageReadable(loaded);
                _settings = _networkDisableIntent.Apply(record.RestoreOnto(loaded.Settings));
            }
            await _settingsStore.SaveAsync(_settings, recoveryTimeout.Token);
            _settingsRecovery.Complete();
            Volatile.Write(ref _settingsRecoveryFailure, null);
            return new SettingsRestorationResult(true);
        }
        catch (Exception exception)
        {
            RecordSettingsRecoveryFailure(exception);
            return new SettingsRestorationResult(false, exception);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Persisted recovery failures remain visible and pending; no network success is inferred.")]
    private async Task RecoverPendingSettingsAsync(CancellationToken cancellationToken, bool initializing = false)
    {
        try
        {
            _networkDisableIntent = _networkDisableIntent.Merge(await _settingsRecovery.ReadNetworkDisableIntentAsync(cancellationToken));
            _settings = _networkDisableIntent.Apply(_settings);
            if (!_settingsRecovery.Exists && _networkDisableIntent.IsEmpty)
            {
                if (_settingsRecoveryFailure is not null)
                {
                    // A transient failure can leave no journal to replay. Only
                    // clear the old failure after confirming storage is readable.
                    EnsureSettingsStorageReadable(await _settingsStore.LoadWithStatusAsync(cancellationToken));
                    Volatile.Write(ref _settingsRecoveryFailure, null);
                }
                return;
            }
            SettingsRecoveryRecord? record = await _settingsRecovery.ReadAsync(cancellationToken);
            SettingsLoadResult loaded = await _settingsStore.LoadWithStatusAsync(cancellationToken);
            EnsureSettingsStorageReadable(loaded);
            AppSettings restored = _networkDisableIntent.Apply(record?.RestoreOnto(loaded.Settings) ?? loaded.Settings);
            // Keep the off-only marker until a successful explicit enable. A
            // late rollback may still contain the old enabled preference.
            await _settingsRecovery.WriteNetworkDisableIntentAsync(_networkDisableIntent, cancellationToken);
            _settings = restored;
            await _settingsStore.SaveAsync(restored, cancellationToken);
            _settingsRecovery.Complete();
            Volatile.Write(ref _settingsRecoveryFailure, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // An unreadable recovery record cannot be an admission for a
            // background enable, including during a subsequent App restart.
            RecordSettingsRecoveryFailure(exception);
            if (!initializing) { throw new InvalidOperationException("设置恢复未完成，无法开始新的设置操作。", exception); }
        }
    }

    private void RecordSettingsRecoveryFailure(Exception exception)
    {
        Volatile.Write(ref _settingsRecoveryFailure, exception);
        _logs.AddApplicationLog(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "error", $"设置恢复未完成：{ErrorSanitizer.Sanitize(exception)}"));
    }

    private static void EnsureSettingsStorageReadable(SettingsLoadResult loaded)
    {
        if (loaded.Status is not (SettingsLoadStatus.Loaded or SettingsLoadStatus.FirstRun))
        {
            throw new InvalidOperationException("持久化设置无法可靠读取；保留恢复记录，请检查设置存储后重试。");
        }
    }

    private void ThrowIfSettingsRecoveryIncomplete(Exception original)
    {
        if (_settingsRecoveryFailure is Exception failure)
        {
            throw new InvalidOperationException("操作失败，且设置恢复未完成。", new AggregateException(original, failure));
        }
    }

    private static async Task<bool> ConfirmNetworkSettingsAsync(
        MihomoApiClient api,
        bool? expectedAllowLan,
        bool? expectedIpv6,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using JsonDocument configuration = await api.GetConfigurationAsync(force: false, cancellationToken);
            bool? allowLan = MihomoDataParser.ParseAllowLan(configuration);
            bool? ipv6 = MihomoDataParser.ParseIpv6(configuration);
            bool allowLanMatches = !expectedAllowLan.HasValue
                || allowLan is bool allowLanValue && allowLanValue == expectedAllowLan.Value;
            bool ipv6Matches = !expectedIpv6.HasValue
                || ipv6 is bool ipv6Value && ipv6Value == expectedIpv6.Value;
            if (allowLanMatches && ipv6Matches)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return false;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Network restore is a safety net that reports success through its return value; it must never throw into the recovery path.")]
    private static async Task<bool> TryRestoreNetworkSettingsAsync(
        MihomoApiClient api,
        bool? allowLan,
        bool? ipv6)
    {
        if (!allowLan.HasValue && !ipv6.HasValue)
        {
            return true;
        }

        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using JsonDocument response = await api.SetNetworkSettingsAsync(allowLan, ipv6, timeout.Token);
            return await ConfirmNetworkSettingsAsync(api, allowLan, ipv6, timeout.Token);
        }
        catch
        {
            return false;
        }
    }

    private bool IsCoreRunningForSettings() =>
        Snapshot.Core.State == CoreState.Running
        || _api is not null
        || _usingServiceCore;

    private static bool RequiresCoreRestart(AppSettings previous, AppSettings next) =>
        previous.HttpPort != next.HttpPort
        || previous.SocksPort != next.SocksPort
        || previous.MixedPort != next.MixedPort
        || previous.ControllerPort != next.ControllerPort
        || previous.ControllerPortConflictPolicy != next.ControllerPortConflictPolicy
        || previous.TcpConcurrent != next.TcpConcurrent
        || !string.Equals(previous.TunStack, next.TunStack, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(previous.LogLevel, next.LogLevel, StringComparison.OrdinalIgnoreCase);

    private static bool HasSystemProxyBindingChanged(AppSettings previous, AppSettings next) =>
        previous.MixedPort != next.MixedPort
        || !string.Equals(previous.BypassList, next.BypassList, StringComparison.Ordinal);

    private static void ValidateSettings(AppSettings settings)
        => SettingsValidator.Validate(settings);

    private static string? ResolveStartupExecutablePath(bool required)
    {
        string? path = Environment.ProcessPath;
        bool valid = !string.IsNullOrWhiteSpace(path)
            && Path.IsPathFullyQualified(path)
            && File.Exists(path)
            && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        if (valid)
        {
            return Path.GetFullPath(path!);
        }

        if (required)
        {
            throw new InvalidOperationException(
                "无法确定 ClashTray 的真实可执行文件路径，未修改 Windows 启动项。请从已安装目录启动应用。");
        }

        return null;
    }
}
