using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Owns the subscription refresh pipeline: download, validation, persistent
/// backup, configuration switch execution, rollback and journal cleanup. The
/// runtime keeps admission (subscription and operation gates) and delegates the
/// refresh body here once both locks are held.
/// </summary>
internal sealed class SubscriptionRefreshCoordinator
{
    private readonly ConfigurationStore _configurationStore;
    private readonly IConfigurationSwitchOperations _configurationSwitchOperations;
    private readonly ConfigurationSwitchJournalStore _configurationSwitchJournalStore;
    private readonly RuntimeLogCoordinator _logs;
    private readonly RuntimeStateStore _stateStore;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly Action<SubscriptionState, string?> _updateSubscriptionState;
    private readonly Func<ConfigurationSwitchRequest, OperationGate.Lease, CancellationToken, Task<ConfigurationSwitchResult>> _executeConfigurationSwitch;
    private readonly Func<CancellationToken, Task> _refreshConfigurationSnapshot;
    private readonly Action _publish;

    public SubscriptionRefreshCoordinator(
        ConfigurationStore configurationStore,
        IConfigurationSwitchOperations configurationSwitchOperations,
        ConfigurationSwitchJournalStore configurationSwitchJournalStore,
        RuntimeLogCoordinator logs,
        RuntimeStateStore stateStore,
        Func<AppSettings> settingsAccessor,
        Action<SubscriptionState, string?> updateSubscriptionState,
        Func<ConfigurationSwitchRequest, OperationGate.Lease, CancellationToken, Task<ConfigurationSwitchResult>> executeConfigurationSwitch,
        Func<CancellationToken, Task> refreshConfigurationSnapshot,
        Action publish)
    {
        ArgumentNullException.ThrowIfNull(configurationStore);
        ArgumentNullException.ThrowIfNull(configurationSwitchOperations);
        ArgumentNullException.ThrowIfNull(configurationSwitchJournalStore);
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(settingsAccessor);
        ArgumentNullException.ThrowIfNull(updateSubscriptionState);
        ArgumentNullException.ThrowIfNull(executeConfigurationSwitch);
        ArgumentNullException.ThrowIfNull(refreshConfigurationSnapshot);
        ArgumentNullException.ThrowIfNull(publish);
        _configurationStore = configurationStore;
        _configurationSwitchOperations = configurationSwitchOperations;
        _configurationSwitchJournalStore = configurationSwitchJournalStore;
        _logs = logs;
        _stateStore = stateStore;
        _settingsAccessor = settingsAccessor;
        _updateSubscriptionState = updateSubscriptionState;
        _executeConfigurationSwitch = executeConfigurationSwitch;
        _refreshConfigurationSnapshot = refreshConfigurationSnapshot;
        _publish = publish;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Subscription refresh runs unattended; every failure is converted into a typed per-profile result and state update.")]
    public async Task RefreshCoreLockedAsync(
        ConfigurationProfile profile,
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        if (profile.SubscriptionUri is null)
        {
            return;
        }

        bool shouldRemainActive = profile.IsActive
            || string.Equals(profile.Id, _settingsAccessor().ActiveConfigurationId, StringComparison.OrdinalIgnoreCase);
        bool activeSelectionChanged = !string.Equals(
            _settingsAccessor().ActiveConfigurationId,
            profile.Id,
            StringComparison.OrdinalIgnoreCase);
        ConfigurationProfileBackup? contentBackup = null;
        ConfigurationSwitchRuntimeState? previousState = null;
        Guid operationId = Guid.NewGuid();
        Guid? persistentBackupId = null;
        bool contentChanged = false;
        bool switchCommitted = false;
        _updateSubscriptionState(SubscriptionState.Downloading, null);
        try
        {
            if (shouldRemainActive)
            {
                previousState = await _configurationSwitchOperations.CaptureStateAsync(cancellationToken);
                contentBackup = await _configurationStore.CaptureBackupAsync(profile, cancellationToken);
                persistentBackupId = Guid.NewGuid();
                await _configurationStore.SavePersistentBackupAsync(
                    persistentBackupId.Value,
                    profile,
                    contentBackup,
                    cancellationToken);
                ConfigurationSwitchJournal preparedJournal = ConfigurationSwitchJournal.Create(
                    operationId,
                    ConfigurationSwitchSource.SubscriptionRefresh,
                    previousState.ActiveConfigurationId,
                    profile.Id,
                    previousState.CoreWasRunning,
                    previousState.SystemProxyPreference,
                    previousState.SystemProxyState,
                    previousState.TunPreference,
                    previousState.TunState,
                    previousState.ControllerGeneration)
                    .WithContentBackup(persistentBackupId);
                await _configurationSwitchJournalStore.SaveAsync(preparedJournal, cancellationToken);
            }

            _updateSubscriptionState(SubscriptionState.Validating, null);
            ConfigurationImportResult update = await _configurationStore.ImportSubscriptionWithResultAsync(
                profile.SubscriptionUri,
                profile.Name,
                cancellationToken);
            contentChanged = update.ContentChanged;
            _updateSubscriptionState(SubscriptionState.Applying, null);
            if (shouldRemainActive)
            {
                ConfigurationSwitchRequest request = new(
                    operationId,
                    ConfigurationSwitchSource.SubscriptionRefresh,
                    profile.Id,
                    RestartCore: update.ContentChanged || activeSelectionChanged,
                    ForceApply: update.ContentChanged);
                ConfigurationSwitchResult result = await _executeConfigurationSwitch(
                    request,
                    operationLease,
                    cancellationToken);
                switchCommitted = result.Outcome is
                    ConfigurationSwitchOutcome.NoOp
                    or ConfigurationSwitchOutcome.Committed;
                if (switchCommitted)
                {
                    Guid backupId = persistentBackupId
                        ?? throw new InvalidOperationException("订阅备份标识丢失。");
                    await ClearConfigurationSwitchArtifactsAsync(operationId, backupId);
                    persistentBackupId = null;
                }
                if (result.Outcome == ConfigurationSwitchOutcome.NoOp)
                {
                    await _refreshConfigurationSnapshot(cancellationToken);
                }
            }
            else
            {
                await _refreshConfigurationSnapshot(cancellationToken);
            }

            if (shouldRemainActive && !update.ContentChanged && !activeSelectionChanged)
            {
                _logs.AddApplicationLog(new LogEntry(
                    DateTimeOffset.UtcNow,
                    "ClashTray",
                    "info",
                    "订阅内容 SHA-256 未变化，已跳过 Mihomo 重启。"));
                _stateStore.Update(snapshot => snapshot with { Logs = _logs.Snapshot() });
                _publish();
            }

            _updateSubscriptionState(SubscriptionState.Succeeded, null);
        }
        catch (Exception exception)
        {
            Exception finalException = exception;
            if (persistentBackupId is Guid backupId && !switchCommitted)
            {
                try
                {
                    if (contentBackup is not null && contentChanged)
                    {
                        await ConfigurationStore.RestoreBackupAsync(contentBackup, CancellationToken.None);
                        await _refreshConfigurationSnapshot(CancellationToken.None);
                    }

                    ConfigurationSwitchJournalLoadResult currentJournal =
                        await _configurationSwitchJournalStore.LoadAsync(CancellationToken.None);
                    bool keepRecoveryJournal = currentJournal.Journal is { } journal
                        && journal.OperationId == operationId
                        && journal.Stage == ConfigurationSwitchStage.RollbackFailed;
                    if (!keepRecoveryJournal)
                    {
                        await ClearConfigurationSwitchArtifactsAsync(operationId, backupId);
                        persistentBackupId = null;
                    }
                }
                catch (Exception restoreException)
                {
                    Exception recoveryException = restoreException;
                    try
                    {
                        await EnsureRecoveryJournalAsync(
                            operationId,
                            profile,
                            previousState,
                            backupId);
                    }
                    catch (Exception journalException)
                    {
                        recoveryException = new AggregateException(
                            restoreException,
                            journalException);
                    }
                    finalException = new InvalidOperationException(
                        "订阅切换失败，且旧配置文件恢复失败。",
                        new AggregateException(exception, recoveryException));
                }
            }

            _updateSubscriptionState(SubscriptionState.Failed, ErrorSanitizer.Sanitize(finalException));
            throw finalException;
        }
    }

    private async Task ClearConfigurationSwitchArtifactsAsync(
        Guid operationId,
        Guid backupId)
    {
        ConfigurationSwitchJournalLoadResult currentJournal =
            await _configurationSwitchJournalStore.LoadAsync(CancellationToken.None);
        if (currentJournal.Journal is null || currentJournal.Journal.OperationId == operationId)
        {
            await _configurationSwitchJournalStore.ClearAsync();
        }

        await _configurationStore.ClearPersistentBackupAsync(backupId);
    }

    private async Task EnsureRecoveryJournalAsync(
        Guid operationId,
        ConfigurationProfile profile,
        ConfigurationSwitchRuntimeState? previousState,
        Guid backupId)
    {
        if (previousState is null)
        {
            return;
        }

        ConfigurationSwitchJournalLoadResult currentJournal =
            await _configurationSwitchJournalStore.LoadAsync(CancellationToken.None);
        if (currentJournal.Journal is { } existingJournal
            && existingJournal.OperationId != operationId)
        {
            return;
        }

        ConfigurationSwitchJournal journal = currentJournal.Journal ?? ConfigurationSwitchJournal.Create(
            operationId,
            ConfigurationSwitchSource.SubscriptionRefresh,
            previousState.ActiveConfigurationId,
            profile.Id,
            previousState.CoreWasRunning,
            previousState.SystemProxyPreference,
            previousState.SystemProxyState,
            previousState.TunPreference,
            previousState.TunState,
            previousState.ControllerGeneration);
        await _configurationSwitchJournalStore.SaveAsync(
            journal.WithContentBackup(backupId).WithStage(ConfigurationSwitchStage.RollbackFailed),
            CancellationToken.None);
    }
}
