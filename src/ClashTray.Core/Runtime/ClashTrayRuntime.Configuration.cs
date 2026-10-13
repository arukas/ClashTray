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
        await RecoverPendingSettingsAsync(cancellationToken);
        ConfigurationSwitchResult result = await _configurationSwitchCoordinator.ExecuteAsync(
            request, CreateConfigurationSwitchOperations(operationLease), cancellationToken);
        ThrowIfConfigurationSwitchFailed(result);
        return result;
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
        await SaveConfigurationSelectionAsync(candidate.Id, cancellationToken);
        await RefreshConfigurationSnapshotAsync(cancellationToken);
    }

    private async Task RestoreConfigurationSelectionAsync(
        ConfigurationSwitchRuntimeState previousState,
        CancellationToken cancellationToken)
    {
        await SaveConfigurationSelectionAsync(previousState.ActiveConfigurationId, cancellationToken);
        await RefreshConfigurationSnapshotAsync(cancellationToken, publish: false);
    }

    private async Task<string?> RestoreConfigurationFromJournalAsync(
        ConfigurationSwitchJournal journal,
        IReadOnlyList<ConfigurationProfile> configurations,
        CancellationToken cancellationToken)
    {
        // The selection commit is persisted before the journal is cleared, so
        // a crash in between leaves the persisted selection on the candidate
        // while the journal still names the previous configuration; rolling
        // back would desync settings from the core already running the
        // candidate. Same-id subscription refreshes (previous == candidate)
        // must keep restoring their content backup and are not skipped here.
        if (!string.IsNullOrWhiteSpace(journal.CandidateConfigurationId)
            && !string.Equals(journal.PreviousConfigurationId, journal.CandidateConfigurationId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_settings.ActiveConfigurationId, journal.CandidateConfigurationId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? previousConfigurationId = journal.PreviousConfigurationId;
        string? message = null;
        if (previousConfigurationId is not null
            && !configurations.Any(configuration =>
                string.Equals(configuration.Id, previousConfigurationId, StringComparison.OrdinalIgnoreCase)))
        {
            previousConfigurationId = null;
            message = "配置切换恢复记录指向的旧配置已不存在，已恢复为未选择配置。";
        }

        await SaveConfigurationSelectionAsync(previousConfigurationId, cancellationToken);
        return message;
    }

    private Task SaveConfigurationSelectionAsync(string? id, CancellationToken cancellationToken) =>
        _settingsCommits.SaveSelectionAsync(id, cancellationToken);

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
            await RecoverPendingSettingsAsync(cancellationToken);
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
                await SaveConfigurationSelectionAsync(null, cancellationToken);
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

    private ConfigurationSwitchOperations CreateConfigurationSwitchOperations(OperationGate.Lease lease) => new(
        lease, _configurationStore, () => _settings, CaptureConfigurationSwitchStateAsync,
        new ConfigurationSwitchEffects(StopCoreCoreAsync,
            (operationLease, token) => StartCoreCoreAsync(operationLease, token), RestartCoreCoreAsync,
            PromoteConfigurationInMemoryAsync, CommitConfigurationSelectionAsync,
            RestoreConfigurationSelectionAsync, IsCoreHealthy, Publish));

    private Task<ConfigurationSwitchRuntimeState> CaptureConfigurationSwitchStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeSnapshot snapshot = Snapshot;
        AppSettings settings = _settings;
        return Task.FromResult(new ConfigurationSwitchRuntimeState(settings.ActiveConfigurationId,
            snapshot.Core.State == CoreState.Running, settings.SystemProxyEnabled, snapshot.SystemProxy,
            settings.TunEnabled, snapshot.Tun, ControllerGeneration, settings));
    }
}
