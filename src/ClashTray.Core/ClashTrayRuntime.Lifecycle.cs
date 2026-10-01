using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
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
                if (serviceStatus.RuntimeBinding is null)
                {
                    UpdateCoreState(
                        CoreState.Failed,
                        "ClashTray 服务报告核心正在运行，但没有提供经确认的进程与控制器绑定；已阻止 API 写操作和系统代理。");
                    StartPolling();
                }
                else
                {
                    SetRuntimeBinding(serviceStatus.RuntimeBinding);
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
                        coreRunning: IsCoreHealthy(),
                        cancellationToken: cancellationToken);
                    StartPolling();
                    StartOptionalRefreshInBackground(_api);
                }

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
            (operationLease, token) => StartCoreCoreAsync(operationLease, token, useAvailableControllerPortOnce: false),
            cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This public one-time UI operation returns a typed failed outcome for any unexpected startup exception so the caller cannot mistake normal Task completion for success.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The acquired lifecycle lease is disposed by the using scope on every non-null path; a null lease means no ownership was acquired.")]
    public async Task<CoreStartOperationResult> StartCoreUsingAvailableControllerPortOnceAsync(
        CancellationToken cancellationToken = default)
    {
        Guid operationId = Guid.NewGuid();
        if (cancellationToken.IsCancellationRequested)
        {
            return new CoreStartOperationResult(operationId, CoreStartOutcome.Cancelled);
        }

        OperationGate.Lease? lease = _operationLock.TryAcquire();
        if (lease is null)
        {
            return new CoreStartOperationResult(operationId, CoreStartOutcome.Busy);
        }

        using (lease)
        {
            if (Snapshot.Core.State == CoreState.Running)
            {
                return new CoreStartOperationResult(
                    operationId,
                    CoreStartOutcome.AlreadyRunning,
                    ActiveRuntimeBinding?.ControllerPort);
            }

            CoreStartFailure? failure;
            try
            {
                failure = await StartCoreCoreAsync(lease, cancellationToken, useAvailableControllerPortOnce: true)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CoreStartOperationResult(operationId, CoreStartOutcome.Cancelled,
                    ErrorCode: ServiceErrorCode.OperationCancelled,
                    DispatchState: _usingServiceCore ? ServiceDispatchState.DispatchedAwaitingResult : ServiceDispatchState.Completed);
            }
            catch (Exception exception)
            {
                return CoreStartFailure.FromException(exception).ForOperation(operationId);
            }

            if (failure is not null)
            {
                return failure.ForOperation(operationId);
            }

            CoreRuntimeBinding? binding = ActiveRuntimeBinding;
            if (Snapshot.Core.State == CoreState.Running
                && CoreHealthConfirmed
                && binding is { ControllerReady: true, ControllerPort: >= 1 and <= 65535 })
            {
                return new CoreStartOperationResult(
                    operationId,
                    CoreStartOutcome.Started,
                    binding.ControllerPort);
            }

            return new CoreStartFailure(CoreStartOutcome.Failed,
                Snapshot.Core.ErrorMessage ?? "核心启动操作结束时未确认运行绑定。").ForOperation(operationId);
        }
    }

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
    private async Task<CoreStartFailure?> StartCoreCoreAsync(
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken,
        bool useAvailableControllerPortOnce = false)
    {
        using CancellationTokenSource coreStartupDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        coreStartupDeadline.CancelAfter(_coreStartupBudget);
        CancellationToken coreStartOperationToken = coreStartupDeadline.Token;
        bool localCoreStarted = false;
        try
        {
            if (Snapshot.Core.State == CoreState.Running)
            {
                return null;
            }

            Interlocked.Increment(ref _coreLifecycleEpoch);
            SetController(null);
            SetRuntimeBinding(null);

            ConfigurationProfile? profile = GetActiveConfiguration();
            string? executable = _coreDiscovery.FindExecutable();
            if (executable is null)
            {
                UpdateCoreState(CoreState.Missing, "未找到 Mihomo 核心，请在设置中安装或选择 mihomo.exe");
                await RevokeSystemProxyForCoreLossAsync(
                    operationLease,
                    cancellationToken: cancellationToken);
                return new CoreStartFailure(CoreStartOutcome.CoreMissing, Snapshot.Core.ErrorMessage!);
            }

            if (profile is null)
            {
                UpdateCoreState(CoreState.Failed, "请先导入一个 Mihomo 配置");
                await RevokeSystemProxyForCoreLossAsync(
                    operationLease,
                    cancellationToken: cancellationToken);
                return new CoreStartFailure(CoreStartOutcome.ConfigurationMissing, Snapshot.Core.ErrorMessage!);
            }

            UpdateCoreState(CoreState.Validating, null);
            string runtimeConfigPath = Path.Combine(_paths.RuntimeRoot, "mihomo", "active-config.yaml");
            AppSettings runtimeSettings = _settings with
            {
                ControllerPort = _settings.ControllerPort,
                TunEnabled = false
            };
            await RuntimeConfigBuilder.BuildForCoreStartAsync(
                profile.Path,
                runtimeConfigPath,
                runtimeSettings,
                externalUiPath: _paths.ExternalUiRoot,
                cancellationToken: coreStartupDeadline.Token);

            MihomoEffectiveListenerPlan listenerPlan = await MihomoListenerPlanAnalyzer.AnalyzeEffectiveFileAsync(
                runtimeConfigPath,
                coreStartupDeadline.Token).ConfigureAwait(false);
            if (!listenerPlan.ProxyPlanComplete)
            {
                throw new ServiceCommandException(ServiceErrorCode.InvalidConfiguration,
                    listenerPlan.ProxyPlanWarning ?? "无法确认有效 Mihomo 代理监听计划。");
            }

            List<LocalPortBinding> fixedListeners = listenerPlan.AllBindings.ToList();
            HashSet<int> attemptedControllerPorts = [];
            ControllerPortAllocationResult controllerAllocation = AllocateControllerPort(
                _settings,
                useAvailableControllerPortOnce,
                fixedListeners,
                attemptedControllerPorts,
                ControllerPortAllocator.MaximumFallbackCandidates,
                coreStartupDeadline.Token);
            int inspectedFallbackCandidates = controllerAllocation.FallbackCandidatesExamined;
            int runtimeControllerPort = RequireControllerPort(controllerAllocation);
            if (runtimeControllerPort != runtimeSettings.ControllerPort)
            {
                runtimeSettings = runtimeSettings with { ControllerPort = runtimeControllerPort };
                await RuntimeConfigBuilder.BuildForCoreStartAsync(
                    profile.Path,
                    runtimeConfigPath,
                    runtimeSettings,
                    externalUiPath: _paths.ExternalUiRoot,
                    cancellationToken: coreStartupDeadline.Token).ConfigureAwait(false);
                listenerPlan = await MihomoListenerPlanAnalyzer.AnalyzeEffectiveFileAsync(
                    runtimeConfigPath,
                    coreStartupDeadline.Token).ConfigureAwait(false);
                if (!listenerPlan.ProxyPlanComplete)
                {
                    throw new ServiceCommandException(ServiceErrorCode.InvalidConfiguration,
                        listenerPlan.ProxyPlanWarning ?? "无法确认最终 Mihomo 代理监听计划。");
                }

                fixedListeners = listenerPlan.AllBindings.ToList();
            }

            coreStartupDeadline.Token.ThrowIfCancellationRequested();
            string runtimeDirectory = Path.Combine(_paths.RuntimeRoot, "mihomo");
            ServiceCorePayload servicePayload = new(
                runtimeConfigPath,
                runtimeDirectory,
                _settings.ControllerPort,
                string.Empty,
                _settings.ControllerPortConflictPolicy,
                useAvailableControllerPortOnce,
                _settings.HttpPort,
                _settings.SocksPort,
                _settings.MixedPort,
                _settings.AllowLan,
                _settings.Ipv6);
            UpdateCoreState(CoreState.Starting, null);
            ServiceResponse? serviceResponse = null;
            try
            {
                serviceResponse = await _localDevice.StartCoreAsync(servicePayload, coreStartupDeadline.Token);
            }
            catch (ServiceUnavailableException exception) when (exception.DispatchState == ServiceDispatchState.NotDispatched)
            {
            }
            catch (ServiceRequestUnknownException exception)
            {
                serviceResponse = await ReconcileUnknownServiceStartAsync(exception).ConfigureAwait(false);
                if (serviceResponse is null)
                {
                    return CoreStartFailure.UnconfirmedServiceStart(Snapshot.Core.ErrorMessage!,
                        cancellationToken, coreStartupDeadline.Token);
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
                    return CoreStartFailure.UnconfirmedServiceStart(Snapshot.Core.ErrorMessage!,
                        cancellationToken, coreStartupDeadline.Token);
                }
            }

            if (serviceResponse is not null)
            {
                if (!serviceResponse.Succeeded)
                {
                    throw new ServiceCommandException(serviceResponse.ErrorCode,
                        serviceResponse.Error ?? "ClashTray 服务无法启动 Mihomo。", serviceResponse.DispatchState);
                }

                _usingServiceCore = true;
                CoreRuntimeBinding binding = serviceResponse.RuntimeBinding
                    ?? throw new InvalidOperationException("ClashTray 服务未返回经确认的 Mihomo 运行地址，已拒绝连接控制器。");
                if (binding.PreferredControllerPort != _settings.ControllerPort)
                {
                    throw new InvalidOperationException("ClashTray 服务返回的首选控制器端口与本次启动设置不一致。");
                }

                SetRuntimeBinding(binding);
            }
            else
            {
                _usingServiceCore = false;
                bool localBindingReady = false;
                for (int attempt = 0; attempt < ControllerPortAllocator.MaximumStartAttempts; attempt++)
                {
                    coreStartOperationToken.ThrowIfCancellationRequested();
                    if (attempt > 0)
                    {
                        runtimeSettings = _settings with
                        {
                            ControllerPort = runtimeControllerPort,
                            TunEnabled = false
                        };
                        await RuntimeConfigBuilder.BuildForCoreStartAsync(
                            profile.Path,
                            runtimeConfigPath,
                            runtimeSettings,
                            externalUiPath: _paths.ExternalUiRoot,
                            cancellationToken: coreStartOperationToken).ConfigureAwait(false);
                        listenerPlan = await MihomoListenerPlanAnalyzer.AnalyzeEffectiveFileAsync(
                            runtimeConfigPath,
                            coreStartOperationToken).ConfigureAwait(false);
                        if (!listenerPlan.ProxyPlanComplete)
                        {
                            throw new ServiceCommandException(ServiceErrorCode.InvalidConfiguration,
                                listenerPlan.ProxyPlanWarning ?? "无法确认最终 Mihomo 代理监听计划。");
                        }
                    }

                    if (!await _processManager.ValidateAsync(
                        executable,
                        runtimeConfigPath,
                        runtimeDirectory,
                        safePaths: _paths.ExternalUiRoot,
                        cancellationToken: coreStartOperationToken))
                    {
                        UpdateCoreState(CoreState.Failed, "Mihomo 配置验证失败");
                        return new CoreStartFailure(CoreStartOutcome.InvalidConfiguration,
                            Snapshot.Core.ErrorMessage!, ServiceErrorCode.InvalidConfiguration);
                    }

                    await _processManager.StartAsync(
                        executable,
                        runtimeConfigPath,
                        runtimeDirectory,
                        safePaths: _paths.ExternalUiRoot,
                        cancellationToken: coreStartOperationToken);

                    localCoreStarted = true;
                    LocalCoreProcessIdentity identity = _processManager.CaptureRunningProcessIdentity()
                        ?? throw new InvalidOperationException("Mihomo 启动后立即退出，无法确认受管进程身份。");
                    long generation = _processManager.Generation;
                    Guid instanceId = _processManager.InstanceId;
                    MihomoApiClient localApi = CreateLocalApiClient(runtimeControllerPort, identity, generation, instanceId);
                    SetController(localApi);
                    CoreRuntimeBinding binding;
                    try
                    {
                        binding = await WaitForLocalRuntimeBindingAsync(
                            _settings,
                            runtimeControllerPort,
                            identity,
                            generation,
                            instanceId,
                            listenerPlan,
                            coreStartOperationToken).ConfigureAwait(false);
                    }
                    catch (ServiceCommandException exception)
                        when (exception.ErrorCode == ServiceErrorCode.ControllerOwnershipUnconfirmed
                            && attempt + 1 < ControllerPortAllocator.MaximumStartAttempts)
                    {
                        await StopUnreadyLocalCoreAsync().ConfigureAwait(false);
                        localCoreStarted = false;
                        ControllerPortAllocationResult nextAllocation = AllocateControllerPort(
                            _settings,
                            useAvailableControllerPortOnce,
                            fixedListeners,
                            attemptedControllerPorts,
                            ControllerPortAllocator.MaximumFallbackCandidates - inspectedFallbackCandidates,
                            coreStartOperationToken);
                        inspectedFallbackCandidates += nextAllocation.FallbackCandidatesExamined;
                        runtimeControllerPort = RequireControllerPort(nextAllocation);
                        continue;
                    }

                    if (!_processManager.TryMarkReady(generation))
                    {
                        throw new InvalidOperationException("Mihomo 启动代际已变化，未提交运行状态。");
                    }

                    SetRuntimeBinding(binding);
                    localBindingReady = true;
                    break;
                }

                if (!localBindingReady)
                {
                    throw new ServiceCommandException(ServiceErrorCode.ControllerOwnershipUnconfirmed,
                        "Mihomo 控制器端口在预检后持续被占用，已达到 3 次启动上限。");
                }
            }

            bool coreStarted = true;
            SetController(CreateApiClient());
            SetCoreRunningPendingHealth(serviceResponse?.Tun ?? TunState.Unavailable);
            CoreStartFailure? healthFailure = null;
            try
            {
                await RefreshCoreHealthWithRetryAsync(coreStartOperationToken);
            }
            catch (OperationCanceledException exception)
            {
                if (localCoreStarted && !_usingServiceCore)
                {
                    await StopUnreadyLocalCoreAsync().ConfigureAwait(false);
                }
                else if (coreStarted && !_runtimeCts.IsCancellationRequested)
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
                healthFailure = CoreStartFailure.FromException(exception);
            }

            await ApplyProgramOverridesAsync(
                coreRunning: IsCoreHealthy(),
                cancellationToken: coreStartOperationToken,
                operationLease: operationLease);
            coreStartOperationToken.ThrowIfCancellationRequested();
            StartPolling();
            StartOptionalRefreshInBackground(_api);
            return healthFailure;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && coreStartupDeadline.IsCancellationRequested)
        {
            if (localCoreStarted && !_usingServiceCore)
            {
                await StopUnreadyLocalCoreAsync().ConfigureAwait(false);
                localCoreStarted = false;
            }

            string failureMessage = $"Mihomo 核心启动事务超过 {_coreStartupBudget.TotalSeconds:0} 秒。";
            try
            {
                using CancellationTokenSource cleanupDeadline = new(TimeSpan.FromSeconds(10));
                await RevokeSystemProxyForCoreLossAsync(
                    operationLease,
                    cancellationToken: cleanupDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception restoreException)
            {
                failureMessage += $" 系统代理恢复未确认：{ErrorSanitizer.Sanitize(restoreException)}";
            }

            UpdateCoreState(CoreState.Failed, failureMessage);
            return new CoreStartFailure(CoreStartOutcome.TimedOut, failureMessage, ServiceErrorCode.OperationTimedOut);
        }
        catch (OperationCanceledException)
        {
            if (localCoreStarted && !_usingServiceCore)
            {
                await StopUnreadyLocalCoreAsync().ConfigureAwait(false);
            }

            if (Snapshot.Core.State is CoreState.Validating or CoreState.Starting)
            {
                UpdateCoreState(CoreState.Failed, "Mihomo 核心启动已取消，运行结果未确认。");
            }

            throw;
        }
        catch (Exception exception)
        {
            if (localCoreStarted && !_usingServiceCore)
            {
                await StopUnreadyLocalCoreAsync().ConfigureAwait(false);
            }

            await RevokeSystemProxyForCoreLossAsync(
                operationLease,
                cancellationToken: cancellationToken);
            UpdateCoreState(CoreState.Failed, ErrorSanitizer.Sanitize(exception));
            return CoreStartFailure.FromException(exception);
        }
    }

    private static ControllerPortAllocationResult AllocateControllerPort(
        AppSettings settings,
        bool useAvailablePortOnce,
        IReadOnlyList<LocalPortBinding> fixedListeners,
        HashSet<int> attemptedPorts,
        int maximumFallbackCandidates,
        CancellationToken cancellationToken) => ControllerPortAllocator.Allocate(
            settings.ControllerPort,
            settings.ControllerPortConflictPolicy,
            useAvailablePortOnce,
            fixedListeners,
            static () => RandomNumberGenerator.GetInt32(
                ControllerPortAllocator.MinimumFallbackPort,
                ControllerPortAllocator.MaximumFallbackPort + 1),
            attemptedPorts: attemptedPorts,
            maximumFallbackCandidates: maximumFallbackCandidates,
            cancellationToken: cancellationToken);

    private static int RequireControllerPort(ControllerPortAllocationResult allocation)
    {
        if (allocation.Port is int port)
        {
            return port;
        }

        PortPlanConflict? conflict = allocation.Conflict;
        if (conflict is null)
        {
            throw new ServiceCommandException(ServiceErrorCode.ControllerCandidatesExhausted,
                "没有找到可用的高位控制器端口（最多探测 16 个候选）。");
        }

        bool controllerConflict = conflict.Listener.Name == "controller"
            || conflict.ConflictingWith == "controller";
        string message = conflict.IsInternalConflict
            ? $"本地监听配置冲突：{conflict.ConflictingWith} 与 {conflict.Listener.Name} 使用端口 {conflict.Listener.Port}。"
            : $"{conflict.Listener.Name} 监听端口 {conflict.Listener.Port} 无法使用（{conflict.Probe.Status}）。";
        throw new ServiceCommandException(
            controllerConflict ? ServiceErrorCode.ControllerPortConflict : ServiceErrorCode.ProxyPortConflict,
            message);
    }

    private MihomoApiClient CreateLocalApiClient(
        int controllerPort,
        LocalCoreProcessIdentity identity,
        long processGeneration,
        Guid instanceId)
    {
        if (_controllerApiFactory is not null)
        {
            return _controllerApiFactory();
        }

        return new MihomoApiClient(
            _httpClient,
            new Uri($"http://127.0.0.1:{controllerPort}/"),
            string.Empty,
            controllerOwnershipValidator: () =>
                IsLocalProcessCurrent(identity, processGeneration, instanceId)
                && WindowsListenerOwnerTable.IsOwnedBy(
                    IPAddress.Loopback,
                    controllerPort,
                    PortTransport.Tcp,
                    identity));
    }

    private async Task<CoreRuntimeBinding> WaitForLocalRuntimeBindingAsync(
        AppSettings settings,
        int controllerPort,
        LocalCoreProcessIdentity identity,
        long processGeneration,
        Guid instanceId,
        MihomoEffectiveListenerPlan listenerPlan,
        CancellationToken cancellationToken)
    {
        if (_controllerApiFactory is not null)
        {
            return CreateTestRuntimeBinding(settings, controllerPort) with
            {
                ListenerPlanComplete = listenerPlan.IsComplete,
                AdditionalListeners = listenerPlan.AdditionalListenerPlan.ToContractBindings(),
                ListenerPlanWarning = listenerPlan.Warning,
                ListenerBindings = CreateRuntimeListenerBindings(controllerPort, listenerPlan)
            };
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (!IsLocalProcessCurrent(identity, processGeneration, instanceId))
            {
                throw new InvalidOperationException("受管 Mihomo 进程在监听就绪前退出或代际发生变化。");
            }

            ListenerOwnerObservation controllerOwner = WindowsListenerOwnerTable.InspectListener(
                IPAddress.Loopback,
                controllerPort,
                PortTransport.Tcp,
                identity);
            if (controllerOwner.State == ListenerOwnerState.Foreign)
            {
                throw new ServiceCommandException(
                    ServiceErrorCode.ControllerOwnershipUnconfirmed,
                    "控制器端口在预检后被其他进程占用；未向该端口发送控制器请求。");
            }

            if (controllerOwner.State != ListenerOwnerState.Owned)
            {
                lastError = new TimeoutException(
                    controllerOwner.State == ListenerOwnerState.Unknown
                        ? $"控制器 owner 尚不可确认：{controllerOwner.Detail}"
                        : "等待受管 Mihomo 控制器监听就绪。");
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token).ConfigureAwait(false);
                continue;
            }

            try
            {
                MihomoApiClient api = _api
                    ?? throw new InvalidOperationException("本地 Mihomo 控制器尚未连接。");
                using JsonDocument configuration = await api.GetConfigurationAsync(false, timeout.Token)
                    .ConfigureAwait(false);
                if (!WindowsListenerOwnerTable.IsOwnedBy(
                    IPAddress.Loopback,
                    controllerPort,
                    PortTransport.Tcp,
                    identity))
                {
                    throw new ManagedCoreOwnershipException();
                }

                MihomoListenerPorts ports = MihomoDataParser.ParseListenerPorts(configuration);
                if (ports.Http is null || ports.Socks is null || ports.Mixed is null)
                {
                    throw new InvalidOperationException("Mihomo /configs 未返回完整的 HTTP、SOCKS、Mixed 端口。");
                }

                if (settings.HttpPort > 0 && ports.Http != settings.HttpPort
                    || settings.SocksPort > 0 && ports.Socks != settings.SocksPort
                    || settings.MixedPort > 0 && ports.Mixed != settings.MixedPort)
                {
                    throw new InvalidOperationException("Mihomo 有效代理端口与本次启动设置不一致。");
                }

                if (MihomoDataParser.ParseTunEnabled(configuration) is not false)
                {
                    throw new InvalidOperationException("Mihomo 有效配置未确认 TUN 关闭。");
                }

                ListenerReadinessResult listenerReadiness = ListenerReadinessEvaluator.Evaluate(
                    listenerPlan.ProxyBindings,
                    listenerPlan.AdditionalListenerPlan.Bindings,
                    listener => WindowsListenerOwnerTable.InspectListener(
                        listener.Address,
                        listener.Port,
                        listener.Transport,
                        identity,
                        listener.DualMode));
                if (listenerReadiness.Disposition == ListenerReadinessDisposition.ForeignOwner)
                {
                    throw new ServiceCommandException(
                        ServiceErrorCode.ProxyPortConflict,
                        $"{listenerReadiness.ListenerName} 监听 {listenerReadiness.Disposition}：{listenerReadiness.Detail ?? "端口由其他进程占用。"}");
                }

                if (listenerReadiness.Disposition != ListenerReadinessDisposition.Ready)
                {
                    lastError = listenerReadiness.Disposition == ListenerReadinessDisposition.OwnershipUnknown
                        ? new IOException($"{listenerReadiness.ListenerName} owner 尚不可确认：{listenerReadiness.Detail}")
                        : new TimeoutException($"等待 {listenerReadiness.ListenerName} 监听就绪。");
                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token).ConfigureAwait(false);
                    continue;
                }

                bool httpReady = ports.Http is > 0
                    && settings.HttpPort > 0;
                bool socksReady = ports.Socks is > 0
                    && settings.SocksPort > 0;
                bool mixedReady = ports.Mixed is > 0
                    && settings.MixedPort > 0;

                return new CoreRuntimeBinding(
                    settings.ControllerPort,
                    controllerPort,
                    instanceId,
                    _processManager.OwnerInstanceId,
                    identity.ProcessId,
                    identity.StartTimeUtcTicks,
                    processGeneration,
                    ports.Http ?? 0,
                    ports.Socks ?? 0,
                    ports.Mixed ?? 0,
                    ControllerReady: true,
                    httpReady,
                    socksReady,
                    mixedReady,
                    identity.ExecutablePath,
                    listenerPlan.IsComplete,
                    listenerPlan.AdditionalListenerPlan.ToContractBindings(),
                    listenerPlan.Warning,
                    CreateRuntimeListenerBindings(controllerPort, listenerPlan));
            }
            catch (ManagedCoreOwnershipException exception)
            {
                ListenerOwnerObservation controllerAfterRequest = WindowsListenerOwnerTable.InspectListener(
                    IPAddress.Loopback,
                    controllerPort,
                    PortTransport.Tcp,
                    identity);
                if (controllerAfterRequest.State == ListenerOwnerState.Foreign)
                {
                    throw new ServiceCommandException(
                        ServiceErrorCode.ControllerOwnershipUnconfirmed,
                        "控制器端口不属于当前受管核心；已阻止控制器请求。",
                        exception);
                }

                lastError = exception;
            }
            catch (HttpRequestException exception)
            {
                lastError = exception;
            }
            catch (TimeoutException exception)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"本地 Mihomo 在 30 秒内未完成控制器和代理监听确认。{(lastError is null ? string.Empty : $" {ErrorSanitizer.Sanitize(lastError)}")}",
            lastError);
    }

    private static List<RuntimeListenerBinding> CreateRuntimeListenerBindings(
        int controllerPort,
        MihomoEffectiveListenerPlan listenerPlan)
    {
        List<RuntimeListenerBinding> bindings =
        [
            new RuntimeListenerBinding(
                "controller",
                IPAddress.Loopback.ToString(),
                controllerPort,
                RuntimeListenerTransport.Tcp)
        ];
        bindings.AddRange(listenerPlan.ToContractBindings());
        return bindings;
    }

    private static bool AreRuntimeListenerBindingsOwned(CoreRuntimeBinding binding)
    {
        if (binding.ListenerBindings is null || binding.ListenerBindings.Count is < 1 or > 256)
        {
            return false;
        }

        try
        {
            LocalCoreProcessIdentity identity = new(
                binding.ProcessId,
                binding.ProcessStartedUtcTicks,
                binding.ExecutablePath);
            bool controllerDescribed = false;
            foreach (RuntimeListenerBinding listener in binding.ListenerBindings)
            {
                if (string.IsNullOrWhiteSpace(listener.Name)
                    || listener.Name.Length > 64
                    || listener.Port is < 1 or > 65535
                    || !Enum.IsDefined(listener.Transport)
                    || !IPAddress.TryParse(listener.Address, out IPAddress? address)
                    || listener.DualMode && !address.Equals(IPAddress.IPv6Any))
                {
                    return false;
                }

                if (listener.Name.Equals("controller", StringComparison.Ordinal)
                    && address.Equals(IPAddress.Loopback)
                    && listener.Port == binding.ControllerPort
                    && listener.Transport == RuntimeListenerTransport.Tcp)
                {
                    controllerDescribed = true;
                }

                if (WindowsListenerOwnerTable.InspectListener(
                    address,
                    listener.Port,
                    listener.Transport == RuntimeListenerTransport.Tcp ? PortTransport.Tcp : PortTransport.Udp,
                    identity,
                    listener.DualMode).State != ListenerOwnerState.Owned)
                {
                    return false;
                }
            }

            return controllerDescribed;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private bool IsLocalProcessCurrent(
        LocalCoreProcessIdentity identity,
        long processGeneration,
        Guid instanceId)
    {
        try
        {
            return _processManager.State is CoreState.Starting or CoreState.Running
                && _processManager.Generation == processGeneration
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

    private static CoreRuntimeBinding CreateTestRuntimeBinding(AppSettings settings, int controllerPort)
    {
        using Process process = Process.GetCurrentProcess();
        return new CoreRuntimeBinding(
            settings.ControllerPort,
            controllerPort,
            Guid.NewGuid(),
            Guid.NewGuid(),
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            1,
            settings.HttpPort,
            settings.SocksPort,
            settings.MixedPort,
            ControllerReady: true,
            HttpReady: settings.HttpPort > 0,
            SocksReady: settings.SocksPort > 0,
            MixedReady: settings.MixedPort > 0,
            Environment.ProcessPath ?? string.Empty);
    }

    private async Task StopUnreadyLocalCoreAsync()
    {
        SetController(null);
        SetRuntimeBinding(null);
        try
        {
            if (_processManager.State is not CoreState.Stopped)
            {
                await _processManager.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            _logs.AddApplicationLog(new LogEntry(
                DateTimeOffset.UtcNow,
                "ClashTray",
                "error",
                $"启动失败后的本地核心清理未确认：{ErrorSanitizer.Sanitize(exception)}"));
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

        if (status?.Core == CoreState.Running && status.RuntimeBinding is not null)
        {
            SetRuntimeBinding(status.RuntimeBinding);
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
                            SetRuntimeBinding(null);
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
                SetRuntimeBinding(null);
            }
            else
            {
                await _processManager.StopAsync(cancellationToken);
                SetRuntimeBinding(null);
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

    private const string SystemProxyListenerUnavailableMessage =
        "当前 Mixed TCP 监听未确认覆盖 127.0.0.1；系统代理保持关闭。请检查配置的 bind-address。";

    private bool TryGetConfirmedMixedPort(out int port)
    {
        CoreRuntimeBinding? binding = ActiveRuntimeBinding;
        if (Snapshot.Core.State == CoreState.Running
            && CoreHealthConfirmed
            && binding is { MixedReady: true, MixedPort: >= 1 and <= 65535, ListenerBindings.Count: >= 1 and <= 256 }
            && binding.ListenerBindings.Any(listener =>
                string.Equals(listener.Name, "mixed-tcp", StringComparison.Ordinal)
                && listener.Port == binding.MixedPort
                && listener.Transport == RuntimeListenerTransport.Tcp
                && IPAddress.TryParse(listener.Address, out IPAddress? address)
                && (listener.DualMode
                    ? address.Equals(IPAddress.IPv6Any)
                    : address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.Any))))
        {
            port = binding.MixedPort;
            return true;
        }

        port = 0;
        return false;
    }

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
        CoreRuntimeBinding binding = ActiveRuntimeBinding
            ?? throw new ManagedCoreOwnershipException();
        MihomoListenerPorts listenerPorts = MihomoDataParser.ParseListenerPorts(configurationState);
        bool additionalListenerOwnershipConfirmed = _controllerApiFactory is not null
            || Volatile.Read(ref _controllerSessionInjectedForTesting) != 0
            || AreRuntimeListenerBindingsOwned(binding);
        if (_controllerApiFactory is null
            && Volatile.Read(ref _controllerSessionInjectedForTesting) == 0
            && (!binding.ControllerReady
                || listenerPorts.Http != binding.HttpPort
                || listenerPorts.Socks != binding.SocksPort
                || listenerPorts.Mixed != binding.MixedPort
                || !binding.HttpReady
                || !binding.SocksReady
                || binding.MixedReady && binding.MixedPort <= 0
                || !additionalListenerOwnershipConfirmed))
        {
            throw new InvalidOperationException("当前 Mihomo 有效监听与已确认的运行绑定不一致。");
        }

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

                    AdoptServiceController(pendingStatus);
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
                    if (serviceStatus.RuntimeBinding is null)
                    {
                        SetController(null);
                        SetRuntimeBinding(null);
                        await RevokeSystemProxyForCoreLossWithLeaseAsync();
                        MarkCoreHealthUnconfirmed(
                            "服务重连",
                            new ManagedCoreOwnershipException(),
                            retryCount);
                        retryDelay = IncreaseRetryDelay(retryDelay);
                        continue;
                    }

                    AdoptServiceController(serviceStatus);
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

    private void AdoptServiceController(ServiceResponse status)
    {
        CoreRuntimeBinding binding = status.RuntimeBinding ?? throw new ManagedCoreOwnershipException();
        SetRuntimeBinding(binding);
        SetController(CreateApiClient());
    }

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
            SetRuntimeBinding(null);
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
