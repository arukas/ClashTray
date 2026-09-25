using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed partial class ClashTrayRuntime
{

    public async Task<ConfigurationProfile> ImportLocalConfigurationAsync(string path, string? name = null, CancellationToken cancellationToken = default)
    {
        ConfigurationProfile profile = await _configurationStore.ImportLocalAsync(path, name, cancellationToken);
        await SetActiveConfigurationAsync(profile.Id, cancellationToken);
        return profile;
    }

    public async Task<ConfigurationProfile> ImportSubscriptionAsync(Uri uri, string? name = null, CancellationToken cancellationToken = default)
    {
        await _subscriptionOperationLock.WaitAsync(cancellationToken);
        try
        {
            UpdateSubscriptionState(SubscriptionState.Downloading, null);
            try
            {
                ConfigurationProfile profile = await _configurationStore.ImportSubscriptionAsync(uri, name, cancellationToken);
                UpdateSubscriptionState(SubscriptionState.Succeeded, null);
                await SetActiveConfigurationAsync(profile.Id, cancellationToken);
                return profile;
            }
            catch (Exception exception)
            {
                UpdateSubscriptionState(SubscriptionState.Failed, ErrorSanitizer.Sanitize(exception));
                throw;
            }
        }
        finally
        {
            _subscriptionOperationLock.Release();
        }
    }

    public async Task RefreshSubscriptionAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _subscriptionOperationLock.WaitAsync(cancellationToken);
        try
        {
            using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
            {
                await _subscriptionRefresh.RefreshCoreLockedAsync(profile, operationLease, cancellationToken);
            }
        }
        finally
        {
            _subscriptionOperationLock.Release();
        }
    }

    public async Task ReloadConfigurationAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.SubscriptionUri is not null)
        {
            await RefreshSubscriptionAsync(profile, cancellationToken);
            return;
        }

        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            await _configurationStore.ReloadAsync(profile, cancellationToken);
            bool isActive = profile.IsActive
                || string.Equals(profile.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase);
            if (isActive)
            {
                await ExecuteConfigurationSwitchAsync(
                    ConfigurationSwitchRequest.Create(
                        ConfigurationSwitchSource.Manual,
                        profile.Id,
                        restartCore: Snapshot.Core.State == CoreState.Running,
                        forceApply: true),
                    operationLease,
                    cancellationToken);
            }
            else
            {
                await RefreshConfigurationSnapshotAsync(cancellationToken);
            }
        }
    }

    public async Task SetActiveConfigurationAsync(string id, CancellationToken cancellationToken = default)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            ConfigurationSwitchResult result = await ExecuteConfigurationSwitchAsync(
                ConfigurationSwitchRequest.Create(ConfigurationSwitchSource.Manual, id),
                operationLease,
                cancellationToken);
            if (result.Outcome == ConfigurationSwitchOutcome.NoOp)
            {
                await RefreshConfigurationSnapshotAsync(cancellationToken);
            }

            if (_networkSwitchRuntimeController.IsInitialized)
            {
                _networkSwitchRuntimeController.SetManualOverrideForCurrentNetwork(id);
            }
        }
    }

    private async Task<ConfigurationSwitchResult> ExecuteConfigurationSwitchAsync(
        ConfigurationSwitchRequest request,
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        _configurationSwitchOperations.ActiveLease = operationLease;
        try
        {
            ConfigurationSwitchResult result = await _configurationSwitchCoordinator.ExecuteAsync(
                request,
                _configurationSwitchOperations,
                cancellationToken);
            ThrowIfConfigurationSwitchFailed(result);
            return result;
        }
        finally
        {
            _configurationSwitchOperations.ActiveLease = null;
        }
    }

    private async Task<ConfigurationSwitchResult> ExecuteConfigurationSwitchWithLeaseAsync(
        ConfigurationSwitchRequest request,
        CancellationToken cancellationToken)
    {
        using OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteConfigurationSwitchAsync(request, operationLease, cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfConfigurationSwitchFailed(ConfigurationSwitchResult result)
    {
        switch (result.Outcome)
        {
            case ConfigurationSwitchOutcome.NoOp:
            case ConfigurationSwitchOutcome.Committed:
                return;
            case ConfigurationSwitchOutcome.Rejected when result.ErrorCode == ErrorCode.ConfigurationSwitchTargetNotFound:
                throw new FileNotFoundException("Configuration profile not found.");
            case ConfigurationSwitchOutcome.Rejected when result.ErrorCode == ErrorCode.ConfigurationSwitchRecoveryRequired:
                throw new InvalidOperationException("上一个配置切换尚未完成恢复，请先重启 ClashTray 后再试。");
            case ConfigurationSwitchOutcome.RolledBack when result.Failure is OperationCanceledException cancellation:
                throw cancellation;
            case ConfigurationSwitchOutcome.RolledBack:
                throw new InvalidOperationException("配置切换失败，已恢复旧配置。", result.Failure);
            case ConfigurationSwitchOutcome.RollbackFailed:
                List<Exception> failures = new List<Exception>();
                if (result.Failure is not null)
                {
                    failures.Add(result.Failure);
                }

                if (result.RollbackFailure is not null)
                {
                    failures.Add(result.RollbackFailure);
                }

                throw new InvalidOperationException(
                    "配置切换失败，且旧配置恢复失败。",
                    new AggregateException(failures));
            default:
                throw new InvalidOperationException($"配置切换失败：{result.ErrorCode}。");
        }
    }

    private async Task RefreshConfigurationSnapshotAsync(
        CancellationToken cancellationToken,
        bool publish = true)
    {
        IReadOnlyList<ConfigurationProfile> configurations = await _configurationStore.ListAsync(cancellationToken);
        _stateStore.Update(snapshot => snapshot with
        {
            Configurations = configurations.Select(configuration => configuration with
            {
                IsActive = string.Equals(configuration.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase)
            }).ToArray(),
            Core = snapshot.Core with
            {
                ConfigurationName = configurations.FirstOrDefault(configuration =>
                    string.Equals(configuration.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase))?.Name
            }
        });
        if (publish)
        {
            Publish();
        }
    }

    private async Task PromoteConfigurationInMemoryAsync(
        ConfigurationProfile candidate,
        CancellationToken cancellationToken)
    {
        _settings = _settings with { ActiveConfigurationId = candidate.Id };
        IReadOnlyList<ConfigurationProfile> configurations = await _configurationStore.ListAsync(cancellationToken);
        _stateStore.Update(snapshot => snapshot with
        {
            Configurations = configurations.Select(configuration => configuration with
            {
                IsActive = string.Equals(configuration.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)
            }).ToArray(),
            Core = snapshot.Core with { ConfigurationName = candidate.Name }
        });
    }

    private async Task CommitConfigurationSelectionAsync(
        ConfigurationProfile candidate,
        CancellationToken cancellationToken)
    {
        await _settingsStore.SaveAsync(_settings, cancellationToken);
        await RefreshConfigurationSnapshotAsync(cancellationToken);
    }

    private async Task RestoreConfigurationSelectionAsync(
        ConfigurationSwitchRuntimeState previousState,
        CancellationToken cancellationToken)
    {
        _settings = previousState.PreviousSettings
            ?? _settings with { ActiveConfigurationId = previousState.ActiveConfigurationId };
        await _settingsStore.SaveAsync(_settings, cancellationToken);
        await RefreshConfigurationSnapshotAsync(cancellationToken, publish: false);
    }

    private async Task<string?> RestoreConfigurationFromJournalAsync(
        ConfigurationSwitchJournal journal,
        IReadOnlyList<ConfigurationProfile> configurations,
        CancellationToken cancellationToken)
    {
        string? previousConfigurationId = journal.PreviousConfigurationId;
        string? message = null;
        if (previousConfigurationId is not null
            && !configurations.Any(configuration =>
                string.Equals(configuration.Id, previousConfigurationId, StringComparison.OrdinalIgnoreCase)))
        {
            previousConfigurationId = null;
            message = "配置切换恢复记录指向的旧配置已不存在，已恢复为未选择配置。";
        }

        _settings = _settings with { ActiveConfigurationId = previousConfigurationId };
        await _settingsStore.SaveAsync(_settings, cancellationToken);
        return message;
    }

    private async Task ClearRecoveredConfigurationSwitchArtifactsAsync(
        ConfigurationSwitchJournal journal)
    {
        await _configurationSwitchJournalStore.ClearAsync();
        if (journal.ContentBackupId is Guid backupId)
        {
            await _configurationStore.ClearPersistentBackupAsync(backupId);
        }
    }

    private async Task<bool> RecoverCoreFromJournalAsync(
        ConfigurationSwitchJournal journal,
        CoreState observedCoreState,
        CancellationToken cancellationToken)
    {
        if (!journal.PreviousCoreWasRunning)
        {
            return true;
        }

        if (observedCoreState == CoreState.Running)
        {
            await RestartCoreAsync(cancellationToken);
        }
        else
        {
            await StartCoreAsync(cancellationToken);
        }

        return IsCoreHealthy();
    }

    private async Task<(bool Completed, string? Message)> CompleteConfigurationSwitchRecoveryAsync(
        ConfigurationSwitchJournal journal,
        CoreState observedCoreState,
        bool contentRestored,
        CancellationToken cancellationToken)
    {
        if (!contentRestored)
        {
            if (observedCoreState == CoreState.Running)
            {
                await StopCoreAsync(cancellationToken);
            }

            return (false, "配置切换恢复记录仍未完成，旧订阅内容无法确认，核心已保持停止。");
        }

        if (journal.PreviousCoreWasRunning
            && !await RecoverCoreFromJournalAsync(journal, observedCoreState, cancellationToken))
        {
            return (false, "配置切换恢复记录仍未完成，旧核心未能通过健康检查。");
        }

        await ClearRecoveredConfigurationSwitchArtifactsAsync(journal);
        return (true, null);
    }

    public async Task DeleteConfigurationAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            bool wasActive = profile.IsActive
                || string.Equals(profile.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase);
            if (wasActive && Snapshot.Core.State == CoreState.Running)
            {
                await StopCoreCoreAsync(operationLease, cancellationToken);
            }

            await _configurationStore.DeleteAsync(profile, cancellationToken);
            IReadOnlyList<ConfigurationProfile> configurations = await _configurationStore.ListAsync(cancellationToken);
            if (wasActive)
            {
                _settings = _settings with { ActiveConfigurationId = null };
                await _settingsStore.SaveAsync(_settings, cancellationToken);
            }

            _stateStore.Update(snapshot => snapshot with
            {
                Configurations = configurations.Select(configuration => configuration with
                {
                    IsActive = configuration.Id == _settings.ActiveConfigurationId
                }).ToArray(),
                Core = wasActive ? snapshot.Core with { ConfigurationName = null } : snapshot.Core
            });
            Publish();
        }
    }

    private ConfigurationProfile? GetActiveConfiguration() =>
        Snapshot.Configurations.FirstOrDefault(configuration => configuration.IsActive)
        ?? Snapshot.Configurations.FirstOrDefault(configuration => configuration.Id == _settings.ActiveConfigurationId);

    private void UpdateSubscriptionState(SubscriptionState state, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            _logs.AddApplicationLog(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "error", error));
        }

        _stateStore.Update(snapshot => snapshot with { Subscription = state, ErrorMessage = error, Logs = _logs.Snapshot() });
        Publish();
    }

    private void OnScheduledSubscriptionRefreshFailed(ConfigurationProfile profile, Exception exception)
    {
        UpdateSubscriptionState(SubscriptionState.Failed, $"订阅 {profile.Name} 定时刷新失败：{ErrorSanitizer.Sanitize(exception)}");
    }

    private void OnScheduledSubscriptionCycleFailed(Exception exception)
    {
        UpdateSubscriptionState(SubscriptionState.Failed, $"定时订阅任务失败：{ErrorSanitizer.Sanitize(exception)}");
    }

    private sealed class RuntimeConfigurationSwitchOperations : IConfigurationSwitchOperations
    {
        private readonly ClashTrayRuntime _runtime;
        private OperationGate.Lease? _activeLease;

        public RuntimeConfigurationSwitchOperations(ClashTrayRuntime runtime)
        {
            _runtime = runtime;
        }

        internal OperationGate.Lease? ActiveLease
        {
            get => _activeLease;
            set => _activeLease = value;
        }

        private OperationGate.Lease RequireLease() =>
            _activeLease ?? throw new InvalidOperationException("配置切换未在操作锁内执行。");

        public string? CurrentConfigurationId => _runtime._settings.ActiveConfigurationId;

        public async Task<ConfigurationProfile?> ResolveCandidateAsync(
            string id,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ConfigurationProfile> configurations =
                await _runtime._configurationStore.ListAsync(cancellationToken);
            return configurations.FirstOrDefault(configuration =>
                string.Equals(configuration.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public Task ValidateCandidateAsync(
            ConfigurationProfile candidate,
            CancellationToken cancellationToken) =>
            _runtime._configurationStore.ValidateCandidateAsync(candidate, cancellationToken);

        public Task<ConfigurationSwitchRuntimeState> CaptureStateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ConfigurationSwitchRuntimeState(
                _runtime._settings.ActiveConfigurationId,
                _runtime.Snapshot.Core.State == CoreState.Running,
                _runtime._settings.SystemProxyEnabled,
                _runtime.Snapshot.SystemProxy,
                _runtime._settings.TunEnabled,
                _runtime.Snapshot.Tun,
                _runtime.ControllerGeneration,
                _runtime._settings));
        }

        public async Task ApplyAsync(
            ConfigurationSwitchContext context,
            CancellationToken cancellationToken)
        {
            bool restartCore = context.Request.RestartCore && context.PreviousState.CoreWasRunning;
            if (restartCore)
            {
                await _runtime.StopCoreCoreAsync(RequireLease(), cancellationToken);
                await context.SetStageAsync(
                    ConfigurationSwitchStage.NetworkStateSafeguarded,
                    CancellationToken.None);
            }

            await _runtime.PromoteConfigurationInMemoryAsync(context.Candidate, cancellationToken);
            await context.SetStageAsync(
                ConfigurationSwitchStage.RuntimePromoted,
                CancellationToken.None);

            if (restartCore)
            {
                await _runtime.StartCoreCoreAsync(RequireLease(), cancellationToken);
                if (!_runtime.IsCoreHealthy())
                {
                    throw new InvalidOperationException("切换后的 Mihomo 核心健康检查失败。");
                }

                await context.SetStageAsync(
                    ConfigurationSwitchStage.CoreRestarted,
                    CancellationToken.None);
            }

            await _runtime.CommitConfigurationSelectionAsync(
                context.Candidate,
                CancellationToken.None);
        }

        public async Task RollbackAsync(
            ConfigurationSwitchContext context,
            Exception failure,
            CancellationToken cancellationToken)
        {
            await _runtime.RestoreConfigurationSelectionAsync(
                context.PreviousState,
                cancellationToken);
            if (context.PreviousState.CoreWasRunning)
            {
                await _runtime.RestartCoreCoreAsync(RequireLease(), cancellationToken);
                if (!_runtime.IsCoreHealthy())
                {
                    throw new InvalidOperationException("旧配置核心恢复后的健康检查失败。");
                }
            }

            _runtime.Publish();
        }
    }
}
