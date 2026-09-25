using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed partial class ClashTrayRuntime
{

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Startup recovery converts journal cleanup, backup restore, and health-confirmation failures into degraded-state messages so initialization always completes.")]
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LocalCoreShutdownJournalResult localCoreRecovery = await _localCoreShutdownJournal.RecoverPendingStopAsync(
            _localCoreRecoveryAction,
            cancellationToken);
        SettingsLoadResult settingsLoad = await _settingsStore.LoadWithStatusAsync(cancellationToken);
        _settings = settingsLoad.Settings;
        await LoadEndpointCatalogAsync(cancellationToken);
        ConfigurationSwitchJournalLoadResult journalLoad =
            await _configurationSwitchJournalStore.LoadAsync(cancellationToken);
        ConfigurationSwitchJournal? recoveryJournal = journalLoad.Journal;
        string? journalRecoveryMessage = journalLoad.Message;
        bool journalContentRestored = true;
        if (recoveryJournal is { Stage: ConfigurationSwitchStage.Committed })
        {
            try
            {
                await ClearRecoveredConfigurationSwitchArtifactsAsync(recoveryJournal);
                recoveryJournal = null;
            }
            catch (Exception exception)
            {
                journalRecoveryMessage = $"配置切换已完成，但无法清理恢复记录：{ErrorSanitizer.Sanitize(exception)}";
                recoveryJournal = null;
            }
        }
        else if (recoveryJournal is not null)
        {
            if (recoveryJournal.ContentBackupId is Guid backupId)
            {
                try
                {
                    journalContentRestored = await _configurationStore.RestorePersistentBackupAsync(
                        backupId,
                        recoveryJournal.CandidateConfigurationId,
                        cancellationToken);
                    if (!journalContentRestored)
                    {
                        journalRecoveryMessage = "配置切换恢复记录缺少旧配置备份，无法恢复订阅内容。";
                    }
                }
                catch (Exception exception)
                {
                    journalContentRestored = false;
                    journalRecoveryMessage = $"订阅配置备份恢复失败：{ErrorSanitizer.Sanitize(exception)}";
                }
            }

            IReadOnlyList<ConfigurationProfile> configurationsBeforeRestore =
                await _configurationStore.ListAsync(cancellationToken);
            string? restoreMessage = await RestoreConfigurationFromJournalAsync(
                recoveryJournal,
                configurationsBeforeRestore,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(restoreMessage))
            {
                journalRecoveryMessage = restoreMessage;
            }
        }

        IReadOnlyList<ConfigurationProfile> storedConfigurations =
            await _configurationStore.ListAsync(cancellationToken);

        string? activeConfigurationId = _settings.ActiveConfigurationId
            ?? storedConfigurations.FirstOrDefault(configuration => configuration.IsActive)?.Id;
        ConfigurationProfile[] configurations = storedConfigurations
            .Select(configuration => configuration with
            {
                IsActive = string.Equals(configuration.Id, activeConfigurationId, StringComparison.OrdinalIgnoreCase)
            })
            .ToArray();
        _stateStore.Update(snapshot => snapshot with
        {
            Configurations = configurations,
            Core = snapshot.Core with
            {
                State = _coreDiscovery.FindExecutable() is null ? CoreState.Missing : CoreState.Stopped,
                Version = FindCoreVersion()
            },
            SystemProxy = _localDevice.DetectSystemProxyState()
        });
        await ApplyProgramOverridesWithLeaseAsync(coreRunning: false, cancellationToken: cancellationToken);
        try
        {
            ServiceResponse serviceStatus = await _localDevice.GetStatusAsync(cancellationToken);
            _stateStore.Update(snapshot => snapshot with
            {
                Tun = AdoptServiceTunState(serviceStatus.Tun),
                Core = snapshot.Core with
                {
                    State = serviceStatus.Core == CoreState.Stopped && snapshot.Core.State == CoreState.Missing
                        ? CoreState.Missing
                        : serviceStatus.Core
                }
            });
            Publish();
            if (serviceStatus.Core == CoreState.Running)
            {
                _usingServiceCore = true;
                SetController(CreateApiClient());
                try
                {
                    await RefreshCoreHealthWithRetryAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    MarkCoreHealthUnconfirmed("初始化", exception);
                }

                await ApplyProgramOverridesWithLeaseAsync(
                    coreRunning: CoreHealthConfirmed,
                    cancellationToken: cancellationToken);
                StartPolling();
                StartOptionalRefreshInBackground(_api);

                if (recoveryJournal is not null)
                {
                    (bool completed, string? message) = await CompleteConfigurationSwitchRecoveryAsync(
                        recoveryJournal,
                        serviceStatus.Core,
                        journalContentRestored,
                        cancellationToken);
                    if (!completed)
                    {
                        journalRecoveryMessage = message;
                    }
                    else
                    {
                        recoveryJournal = null;
                    }
                }
            }
            else if (recoveryJournal is not null)
            {
                (bool completed, string? message) = await CompleteConfigurationSwitchRecoveryAsync(
                    recoveryJournal,
                    serviceStatus.Core,
                    journalContentRestored,
                    cancellationToken);
                if (!completed)
                {
                    journalRecoveryMessage = message;
                }
                else
                {
                    recoveryJournal = null;
                }
            }
        }
        catch (TimeoutException)
        {
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
            if (recoveryJournal is not null)
            {
                if (!recoveryJournal.PreviousCoreWasRunning && journalContentRestored)
                {
                    await ClearRecoveredConfigurationSwitchArtifactsAsync(recoveryJournal);
                    recoveryJournal = null;
                }
                else
                {
                    journalRecoveryMessage = "配置切换恢复记录仍未完成，服务状态暂时无法确认。";
                }
            }
        }
        catch (IOException)
        {
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
            if (recoveryJournal is not null)
            {
                if (!recoveryJournal.PreviousCoreWasRunning && journalContentRestored)
                {
                    await ClearRecoveredConfigurationSwitchArtifactsAsync(recoveryJournal);
                    recoveryJournal = null;
                }
                else
                {
                    journalRecoveryMessage = "配置切换恢复记录仍未完成，服务状态暂时无法确认。";
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
            if (recoveryJournal is not null)
            {
                if (!recoveryJournal.PreviousCoreWasRunning && journalContentRestored)
                {
                    await ClearRecoveredConfigurationSwitchArtifactsAsync(recoveryJournal);
                    recoveryJournal = null;
                }
                else
                {
                    journalRecoveryMessage = "配置切换恢复记录仍未完成，服务状态暂时无法确认。";
                }
            }
        }
        Publish();
        _subscriptionScheduler.Start();

        if (localCoreRecovery.Succeeded
            && recoveryJournal is null
            && ShouldAutomaticallyStartCore(
                _settings,
                configurations.Any(configuration => configuration.IsActive),
                Snapshot.Core.State))
        {
            await StartCoreAsync(cancellationToken);
        }

        await _networkSwitchRuntimeController.InitializeAsync(cancellationToken);

        if (settingsLoad.Status is SettingsLoadStatus.Recovered
            or SettingsLoadStatus.ReadFailed
            or SettingsLoadStatus.RecoveryFailed)
        {
            _stateStore.Update(snapshot => snapshot with { ErrorMessage = settingsLoad.Message });
            Publish();
        }

        if (!localCoreRecovery.Succeeded)
        {
            string message = $"上次退出后的本地核心恢复未确认：{localCoreRecovery.Detail ?? "原因未知"}；本次启动已跳过自动启动。";
            _stateStore.Update(snapshot => snapshot with { ErrorMessage = message });
            Publish();
        }

        if (!string.IsNullOrWhiteSpace(journalRecoveryMessage))
        {
            _stateStore.Update(snapshot => snapshot with { ErrorMessage = journalRecoveryMessage });
            Publish();
        }
    }

    public Task StartCoreAsync(CancellationToken cancellationToken = default) =>
        AdmitCoreLifecycleAsync(
            "核心",
            (operationLease, token) => StartCoreCoreAsync(operationLease, token),
            cancellationToken);

    private async Task AdmitCoreLifecycleAsync(
        string operationName,
        Func<OperationGate.Lease, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        OperationGate.Lease? lease = _operationLock.TryAcquire();
        if (lease is null)
        {
            throw new OperationBusyException(operationName);
        }

        using (lease)
        {
            await operation(lease, cancellationToken).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Core start must end in a typed Failed state with the system proxy revoked; unexpected failures are sanitized into the snapshot instead of escaping the operation boundary.")]
    private async Task StartCoreCoreAsync(
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        try
        {
            if (Snapshot.Core.State == CoreState.Running)
            {
                return;
            }

            Interlocked.Increment(ref _coreLifecycleEpoch);

            ConfigurationProfile? profile = GetActiveConfiguration();
            string? executable = _coreDiscovery.FindExecutable();
            if (executable is null)
            {
                UpdateCoreState(CoreState.Missing, "未找到 Mihomo 核心，请在设置中安装或选择 mihomo.exe");
                await RevokeSystemProxyForCoreLossAsync(
                    operationLease,
                    cancellationToken: cancellationToken);
                return;
            }

            if (profile is null)
            {
                UpdateCoreState(CoreState.Failed, "请先导入一个 Mihomo 配置");
                await RevokeSystemProxyForCoreLossAsync(
                    operationLease,
                    cancellationToken: cancellationToken);
                return;
            }

            UpdateCoreState(CoreState.Validating, null);
            string runtimeConfigPath = Path.Combine(_paths.RuntimeRoot, "mihomo", "active-config.yaml");
            await RuntimeConfigBuilder.BuildForCoreStartAsync(
                profile.Path,
                runtimeConfigPath,
                _settings,
                externalUiPath: _paths.ExternalUiRoot,
                cancellationToken: cancellationToken);

            string runtimeDirectory = Path.Combine(_paths.RuntimeRoot, "mihomo");
            ServiceCorePayload servicePayload = new(
                runtimeConfigPath,
                runtimeDirectory,
                _settings.ControllerPort,
                string.Empty);
            UpdateCoreState(CoreState.Starting, null);
            ServiceResponse? serviceResponse = null;
            try
            {
                serviceResponse = await _localDevice.StartCoreAsync(servicePayload, cancellationToken);
            }
            catch (ServiceUnavailableException exception) when (exception.DispatchState == ServiceDispatchState.NotDispatched)
            {
            }
            catch (ServiceRequestUnknownException exception)
            {
                serviceResponse = await ReconcileUnknownServiceStartAsync(exception).ConfigureAwait(false);
                if (serviceResponse is null)
                {
                    return;
                }
            }
            catch (ServiceProtocolVersionMismatchException)
            {
                // An incompatible service must fail explicitly; reconciling it
                // as an unknown outcome would disguise a deployment error.
                throw;
            }
            catch (IOException exception)
            {
                serviceResponse = await ReconcileUnknownServiceStartAsync(exception).ConfigureAwait(false);
                if (serviceResponse is null)
                {
                    return;
                }
            }

            if (serviceResponse is not null)
            {
                if (!serviceResponse.Succeeded)
                {
                    throw new InvalidOperationException(serviceResponse.Error ?? "ClashTray 服务无法启动 Mihomo。");
                }

                _usingServiceCore = true;
            }
            else
            {
                _usingServiceCore = false;
                if (!await _processManager.ValidateAsync(
                    executable,
                    runtimeConfigPath,
                    runtimeDirectory,
                    safePaths: _paths.ExternalUiRoot,
                    cancellationToken: cancellationToken))
                {
                    UpdateCoreState(CoreState.Failed, "Mihomo 配置验证失败");
                    return;
                }

                await _processManager.StartAsync(
                    executable,
                    runtimeConfigPath,
                    runtimeDirectory,
                    safePaths: _paths.ExternalUiRoot,
                    cancellationToken: cancellationToken);
            }

            bool coreStarted = true;
            SetController(CreateApiClient());
            SetCoreRunningPendingHealth(serviceResponse?.Tun ?? TunState.Unavailable);
            try
            {
                await RefreshCoreHealthWithRetryAsync(cancellationToken);
            }
            catch (OperationCanceledException exception)
            {
                if (coreStarted && !_runtimeCts.IsCancellationRequested)
                {
                    MarkCoreHealthUnconfirmed("启动取消后同步", exception);
                    StartPolling();
                    StartOptionalRefreshInBackground(_api);
                }

                throw;
            }
            catch (Exception exception)
            {
                MarkCoreHealthUnconfirmed("启动", exception);
            }

            await ApplyProgramOverridesAsync(
                coreRunning: CoreHealthConfirmed,
                cancellationToken: cancellationToken,
                operationLease: operationLease);
            StartPolling();
            StartOptionalRefreshInBackground(_api);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RevokeSystemProxyForCoreLossAsync(
                operationLease,
                cancellationToken: cancellationToken);
            UpdateCoreState(CoreState.Failed, ErrorSanitizer.Sanitize(exception));
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The start request was already dispatched; the status probe only refines the failure message and must never throw.")]
    private async Task<ServiceResponse?> ReconcileUnknownServiceStartAsync(Exception startException)
    {
        // Once StartCore was dispatched, conservatively retain ownership even
        // when the response was lost. This prevents a local fallback from
        // starting a second core and guarantees shutdown will still issue the
        // matching service StopCore request.
        _usingServiceCore = true;
        ServiceResponse? status = null;
        Exception? statusException = null;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _runtimeCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            status = await _localDevice.GetStatusAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            statusException = exception;
        }

        if (status?.Core == CoreState.Running)
        {
            _stateStore.Update(snapshot => snapshot with
            {
                Tun = AdoptServiceTunState(status.Tun)
            });
            return status with { Succeeded = true, Error = null };
        }

        string statusDetail = status is null
            ? $"状态查询失败：{ErrorSanitizer.Sanitize(statusException ?? startException)}"
            : $"服务当前报告 {status.Core}";
        UpdateCoreState(
            CoreState.Failed,
            $"ClashTray 服务启动请求已发送，但结果尚未确认（{statusDetail}）。程序将继续后台核对，并在退出时保守停止服务核心。");
        StartPolling();
        return null;
    }

    public Task StopCoreAsync(CancellationToken cancellationToken = default) =>
        AdmitCoreLifecycleAsync(
            "核心",
            (operationLease, token) => StopCoreCoreAsync(operationLease, token),
            cancellationToken);

    private async Task StopCoreCoreAsync(
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        try
        {
            Interlocked.Increment(ref _coreLifecycleEpoch);
            UpdateCoreState(CoreState.Stopping, null);
            InvalidateCoreHealth();
            SetController(null);
            await _logs.StopLogStreamAsync();
            if (_usingServiceCore)
            {
                try
                {
                    ServiceResponse response = await _localDevice.StopCoreAsync(cancellationToken);
                    if (!response.Succeeded)
                    {
                        _stateStore.Update(snapshot => snapshot with { Tun = AdoptServiceTunState(response.Tun) });
                        if (response.Core == CoreState.Stopped)
                        {
                            _usingServiceCore = false;
                        }

                        if (response.Core == CoreState.Running)
                        {
                            SetController(CreateApiClient());
                        }

                        UpdateCoreState(response.Core, response.Error ?? "ClashTray 服务无法停止 Mihomo。");
                        throw new InvalidOperationException(
                            response.Error ?? "ClashTray 服务无法停止 Mihomo。");
                    }

                    _stateStore.Update(snapshot => snapshot with { Tun = AdoptServiceTunState(response.Tun) });
                }
                catch (TimeoutException exception)
                {
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务不可用，停止结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务不可用，停止结果无法确认。",
                        exception);
                }
                catch (ServiceUnavailableException exception)
                {
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务不可用，停止结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务不可用，停止结果无法确认。",
                        exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务访问被拒绝，停止结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务访问被拒绝，停止结果无法确认。",
                        exception);
                }
                catch (ServiceRequestUnknownException exception)
                {
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务停止结果无法确认，请检查服务状态后重试：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务停止结果无法确认，请检查服务状态后重试。",
                        exception);
                }
                catch (IOException exception)
                {
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务通信失败，停止结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务通信失败，停止结果无法确认。",
                        exception);
                }

                _usingServiceCore = false;
            }
            else
            {
                await _processManager.StopAsync(cancellationToken);
                _confirmedTunState = TunState.Off;
                _stateStore.Update(snapshot => snapshot with { Tun = TunState.Off });
            }

            UpdateCoreState(CoreState.Stopped, null);
        }
        finally
        {
            await RevokeSystemProxyForCoreLossAsync(
                operationLease,
                cancellationToken: cancellationToken);
        }
    }

    public Task RestartCoreAsync(CancellationToken cancellationToken = default) =>
        AdmitCoreLifecycleAsync(
            "核心",
            RestartCoreCoreAsync,
            cancellationToken);

    private async Task RestartCoreCoreAsync(
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        UpdateCoreState(CoreState.Restarting, null);
        await StopCoreCoreAsync(operationLease, cancellationToken);
        await StartCoreCoreAsync(operationLease, cancellationToken);
    }

    private bool IsCoreHealthy() =>
        Snapshot.Core.State == CoreState.Running && CoreHealthConfirmed;

    public async Task RefreshDataAsync(CancellationToken cancellationToken = default)
    {
        EndpointSession? remoteSession = _remoteRefresh.CaptureActiveRemoteSession(
            EndpointCommand.ObserveStatus,
            "刷新远程端点数据期间会话已切换，请重试。");
        if (remoteSession is not null)
        {
            EndpointSessionStatusEventArgs remoteStatus = _endpointSessions.Status;
            if (!await _remoteRefresh.RefreshSnapshotAsync(
                    remoteSession,
                    remoteStatus,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "远程端点数据刷新结果无法确认，请重试。");
            }

            return;
        }

        if (_api is not null)
        {
            await RefreshFromApiWithRetryAsync(cancellationToken);
        }
    }

    private async Task RefreshFromApiAsync(
        CancellationToken cancellationToken,
        bool includeRulesAndProviders = true)
    {
        MihomoApiClient? api = _api;
        if (api is null)
        {
            return;
        }

        bool coreHealthWasUnconfirmed = !CoreHealthConfirmed;
        await RefreshCoreHealthAsync(api, cancellationToken);
        await _dataRefresh.RefreshOptionalDataAsync(
            api,
            cancellationToken,
            includeRulesAndProviders || coreHealthWasUnconfirmed);
    }

    private async Task RefreshCoreHealthAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        long lifecycleEpoch = Volatile.Read(ref _coreLifecycleEpoch);
        long processGeneration = _processManager.Generation;
        long controllerGeneration = ControllerGeneration;
        using JsonDocument version = await api.GetVersionAsync(cancellationToken);
        string? versionText = MihomoDataParser.ParseVersion(version);
        using JsonDocument configurationState = await api.GetConfigurationAsync(force: false, cancellationToken);
        ProxyMode? mode = MihomoDataParser.ParseMode(configurationState);
        bool? tunEnabled = MihomoDataParser.ParseTunEnabled(configurationState);

        TunState observedTun = ResolveConfirmedTunState(tunEnabled);
        bool committed = _stateStore.TryUpdate(
            snapshot => snapshot.Core.State == CoreState.Running
                && IsCurrentCoreBinding(
                    api,
                    controllerGeneration,
                    lifecycleEpoch,
                    processGeneration),
            snapshot => snapshot with
            {
                Core = snapshot.Core with
                {
                    State = CoreState.Running,
                    Version = versionText ?? snapshot.Core.Version,
                    Mode = mode ?? snapshot.Core.Mode,
                    ErrorMessage = null
                },
                Logs = _logs.Snapshot(),
                Tun = observedTun,
                ErrorMessage = null
            },
            out _);
        if (!committed)
        {
            return;
        }

        ConfirmCoreHealth(lifecycleEpoch, processGeneration, controllerGeneration);
        if (!CoreHealthConfirmed)
        {
            return;
        }

        Publish();
        _logs.EnsureLogStreamStarted();
    }

    private TunState ResolveConfirmedTunState(bool? controllerEnabled)
    {
        if (controllerEnabled is false)
        {
            return _confirmedTunState is TunState.Off or TunState.Unavailable
                ? _confirmedTunState
                : TunState.Unknown;
        }

        // The service is the only writer. A controller boolean by itself is
        // deliberately represented as Unknown until the service has also
        // confirmed the Windows network probe for the same process.
        if (controllerEnabled is true && _confirmedTunState == TunState.On)
        {
            return TunState.On;
        }

        return _confirmedTunState == TunState.Unavailable
            ? TunState.Unavailable
            : TunState.Unknown;
    }

    private TunState AdoptServiceTunState(TunState state)
    {
        _confirmedTunState = state switch
        {
            TunState.On => TunState.On,
            TunState.Off => TunState.Off,
            _ => state
        };
        return state;
    }

    private async Task RefreshFromApiWithRetryAsync(CancellationToken cancellationToken)
    {
        await RefreshCoreHealthWithRetryAsync(cancellationToken);
        await ApplyProgramOverridesWithLeaseAsync(coreRunning: true, cancellationToken);
        MihomoApiClient? api = _api;
        if (api is not null)
        {
            await _dataRefresh.RefreshOptionalDataAsync(api, cancellationToken);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The retry loop classifies every non-cancellation failure as retryable until the attempt budget is exhausted.")]
    private async Task RefreshCoreHealthWithRetryAsync(CancellationToken cancellationToken)
    {
        MihomoApiClient? api = _api;
        if (api is null)
        {
            return;
        }

        Exception? lastException = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                await RefreshCoreHealthAsync(api, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (attempt < 4)
            {
                lastException = exception;
                LogControllerFailure("核心健康检查", "/version 或 /configs", exception, attempt + 1, stopwatch.Elapsed);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken);
            }
            catch (Exception exception)
            {
                lastException = exception;
                LogControllerFailure("核心健康检查", "/version 或 /configs", exception, attempt + 1, stopwatch.Elapsed);
            }
        }

        throw lastException ?? new HttpRequestException("Mihomo 控制器暂未就绪。");
    }

    private void StartPolling()
    {
        if (_pollingTask is { IsCompleted: false })
        {
            return;
        }

        _pollingTask = Task.Run(RunPollingAsync, CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The background polling loop must survive transient endpoint failures; failures degrade to the last confirmed snapshot.")]
    private async Task RunPollingAsync()
    {
        TimeSpan retryDelay = TimeSpan.FromSeconds(2);
        int retryCount = 0;
        while (ShouldContinuePolling())
        {
            try
            {
                await Task.Delay(retryDelay, _runtimeCts.Token);
                if (!ShouldContinuePolling())
                {
                    break;
                }

                if (_usingServiceCore && _api is null)
                {
                    ServiceResponse pendingStatus = await _localDevice.GetStatusAsync(_runtimeCts.Token)
                        .ConfigureAwait(false);
                    _stateStore.Update(snapshot => snapshot with
                    {
                        Tun = AdoptServiceTunState(pendingStatus.Tun)
                    });
                    if (pendingStatus.Core != CoreState.Running)
                    {
                        const string pendingMessage =
                            "服务核心启动结果仍未确认，正在等待后台状态收敛。";
                        _stateStore.Update(snapshot => snapshot with
                        {
                            Core = snapshot.Core with
                            {
                                State = CoreState.Failed,
                                ErrorMessage = pendingMessage
                            },
                            ErrorMessage = pendingMessage
                        });
                        Publish();
                        retryDelay = IncreaseRetryDelay(retryDelay);
                        continue;
                    }

                    SetController(CreateApiClient());
                    SetCoreRunningPendingHealth(pendingStatus.Tun);
                }

                await RefreshFromApiAsync(
                    _runtimeCts.Token,
                    includeRulesAndProviders: false);
                await ApplyProgramOverridesWithLeaseAsync(
                    coreRunning: true,
                    cancellationToken: _runtimeCts.Token);
                retryDelay = TimeSpan.FromSeconds(2);
                retryCount = 0;
            }
            catch (OperationCanceledException) when (_runtimeCts.IsCancellationRequested)
            {
                break;
            }
            catch (RuntimeQuiescingException) when (_runtimeCts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                retryCount++;
                if (!_usingServiceCore)
                {
                    if (_processManager.State == CoreState.Running)
                    {
                        await RevokeSystemProxyForCoreLossWithLeaseAsync();
                        MarkCoreHealthUnconfirmed("轮询", exception, retryCount);
                        retryDelay = IncreaseRetryDelay(retryDelay);
                        continue;
                    }

                    // Polling can be the first observer of an exited process when
                    // the exit event was missed; commit the CoreLost fact instead
                    // of silently abandoning the loop on a stale Running snapshot.
                    bool unexpectedCoreLost = Snapshot.Core.State is CoreState.Running or CoreState.Starting;
                    if (unexpectedCoreLost)
                    {
                        SetController(null);
                    }

                    await _logs.StopLogStreamAsync();
                    UpdateCoreState(CoreState.Failed, "Mihomo 进程已退出", unexpectedCoreLost);
                    break;
                }

                ServiceResponse? serviceStatus = null;
                Exception? serviceException = null;
                try
                {
                    serviceStatus = await _localDevice.GetStatusAsync(_runtimeCts.Token);
                }
                catch (OperationCanceledException) when (_runtimeCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception statusException)
                {
                    serviceException = statusException;
                }

                if (serviceStatus is null)
                {
                    SetController(null);
                    await _logs.StopLogStreamAsync();
                    await RevokeSystemProxyForCoreLossWithLeaseAsync();
                    _confirmedTunState = TunState.Unavailable;
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                    MarkCoreHealthUnconfirmed("服务重连", serviceException ?? exception, retryCount);
                    retryDelay = IncreaseRetryDelay(retryDelay);
                    continue;
                }

                if (serviceStatus.Core == CoreState.Running)
                {
                    SetController(CreateApiClient());
                    await RevokeSystemProxyForCoreLossWithLeaseAsync();
                    _stateStore.Update(snapshot => snapshot with { Tun = AdoptServiceTunState(serviceStatus.Tun) });
                    MarkCoreHealthUnconfirmed("控制器重连", exception, retryCount);
                    retryDelay = IncreaseRetryDelay(retryDelay);
                    continue;
                }

                SetController(null);
                await _logs.StopLogStreamAsync();
                await RevokeSystemProxyForCoreLossWithLeaseAsync();
                _stateStore.Update(snapshot => snapshot with { Tun = AdoptServiceTunState(serviceStatus.Tun) });
                if (serviceStatus.Core is CoreState.Stopped or CoreState.Failed)
                {
                    UpdateCoreState(
                        serviceStatus.Core,
                        serviceStatus.Core == CoreState.Failed ? "Mihomo 服务进程已停止" : null);
                    break;
                }

                UpdateCoreState(serviceStatus.Core, "核心状态暂时无法确认，正在等待服务完成状态同步。");
                retryDelay = IncreaseRetryDelay(retryDelay);
            }
        }
    }

    private bool ShouldContinuePolling() =>
        !_runtimeCts.IsCancellationRequested
        && (_usingServiceCore || (_api is not null && _processManager.State == CoreState.Running));

    private static TimeSpan IncreaseRetryDelay(TimeSpan current) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Max(2, current.TotalSeconds * 2)));

    private void StartOptionalRefreshInBackground(MihomoApiClient? api)
    {
        if (api is null || _dataRefreshTask is { IsCompleted: false })
        {
            return;
        }

        _dataRefreshTask = Task.Run(
            () => RunOptionalRefreshAsync(api),
            CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional data refresh is non-fatal; failures only leave the affected snapshot section stale.")]
    private async Task RunOptionalRefreshAsync(MihomoApiClient api)
    {
        try
        {
            await _dataRefresh.RefreshOptionalDataAsync(api, _runtimeCts.Token);
        }
        catch (OperationCanceledException) when (_runtimeCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogControllerFailure("后台数据刷新", "/metrics", exception, 0);
            _stateStore.Update(snapshot => snapshot with { Logs = _logs.Snapshot() });
            Publish();
        }
    }

    private void SetCoreRunningPendingHealth(TunState tunState)
    {
        InvalidateCoreHealth();
        TunState observedTun = AdoptServiceTunState(tunState);
        _stateStore.Update(snapshot => snapshot with
        {
            Core = snapshot.Core with
            {
                State = CoreState.Running,
                ErrorMessage = null,
                TrafficAvailable = false,
                MemoryAvailable = false
            },
            Tun = observedTun,
            ErrorMessage = null
        });
        Publish();
    }

    private void MarkCoreHealthUnconfirmed(string phase, Exception exception, int retryCount = 0)
    {
        InvalidateCoreHealth();
        string message = $"核心状态暂时无法确认（{phase}：{DescribeControllerError(exception)}）。";
        LogControllerFailure(phase, "/version 或 /configs", exception, retryCount);
        _stateStore.Update(snapshot => snapshot with
        {
            Core = snapshot.Core with { State = CoreState.Running, ErrorMessage = message },
            ErrorMessage = message,
            Logs = _logs.Snapshot()
        });
        Publish();
    }

    private void LogControllerFailure(
        string phase,
        string path,
        Exception exception,
        int retryCount,
        TimeSpan? elapsed = null)
    {
        string status = exception is HttpRequestException { StatusCode: { } statusCode }
            ? $"HTTP {(int)statusCode}"
            : "HTTP 未确认";
        string duration = elapsed is null ? "未测量" : $"{elapsed.Value.TotalMilliseconds:0}ms";
        string hosting = _usingServiceCore ? "service" : "local";
        string message = $"{phase}失败：托管方式={hosting}，路径={path}，{status}，耗时={duration}，重试={retryCount}，错误类型={DescribeControllerError(exception)}。";
        _logs.AddApplicationLog(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "warning", message));
    }

    private static string DescribeControllerError(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } statusCode } => $"HTTP {(int)statusCode}",
        MihomoStreamException streamException => $"{streamException.Path} {streamException.Kind}",
        TimeoutException => "首条记录超时",
        OperationCanceledException => "已取消",
        _ => exception.GetType().Name
    };

    private void OnProcessStateChanged(object? sender, CoreState state)
    {
        bool unexpectedCoreLost = state == CoreState.Failed
            && Snapshot.Core.State is CoreState.Running or CoreState.Starting
            && !_runtimeCts.IsCancellationRequested;
        if (unexpectedCoreLost)
        {
            // Invalidate the controller binding before publishing CoreLost. Any
            // health response already in flight now fails the generation check.
            SetController(null);
        }

        UpdateCoreState(
            state,
            state == CoreState.Failed ? "Mihomo 进程已退出" : null,
            unexpectedCoreLost);
    }

    private void UpdateCoreState(CoreState state, string? error, bool unexpectedCoreLost = false)
    {
        if (state != CoreState.Running)
        {
            InvalidateCoreHealth();
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            _logs.AddApplicationLog(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "error", error));
        }

        _stateStore.Update(snapshot => snapshot with
        {
            Core = snapshot.Core with { State = state, ErrorMessage = error },
            ErrorMessage = error,
            Logs = _logs.Snapshot()
        });
        Publish();
        if (unexpectedCoreLost)
        {
            QueueSystemProxyRecovery(new CoreLossContext(
                Volatile.Read(ref _coreLifecycleEpoch),
                _processManager.Generation,
                ControllerGeneration,
                Volatile.Read(ref _proxyOwnershipRevision),
                Volatile.Read(ref _proxyIntentRevision),
                CoreHealthConfirmed));
        }
    }

    private bool CoreHealthConfirmed =>
        Volatile.Read(ref _confirmedCoreLifecycleEpoch)
            == Volatile.Read(ref _coreLifecycleEpoch)
        && Volatile.Read(ref _confirmedCoreProcessGeneration)
            == _processManager.Generation
        && Volatile.Read(ref _confirmedControllerGeneration)
            == ControllerGeneration
        && _controllerSessions.Current is not null;

    private void ConfirmCoreHealth(
        long lifecycleEpoch,
        long processGeneration,
        long controllerGeneration)
    {
        Volatile.Write(ref _confirmedCoreProcessGeneration, processGeneration);
        Volatile.Write(ref _confirmedControllerGeneration, controllerGeneration);
        Volatile.Write(ref _confirmedCoreLifecycleEpoch, lifecycleEpoch);
    }

    private void InvalidateCoreHealth()
    {
        Volatile.Write(ref _confirmedCoreLifecycleEpoch, long.MinValue);
        Volatile.Write(ref _confirmedCoreProcessGeneration, long.MinValue);
        Volatile.Write(ref _confirmedControllerGeneration, long.MinValue);
    }
}
