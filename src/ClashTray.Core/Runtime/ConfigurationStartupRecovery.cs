using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed record ConfigurationRecoveryState(
    ConfigurationSwitchJournal? Journal,
    bool ContentRestored,
    bool SelectionRestored,
    string? Message);

internal sealed record ConfigurationRecoveryCoreOperations(
    Func<CancellationToken, Task> Start,
    Func<CancellationToken, Task> Stop,
    Func<CancellationToken, Task> Restart,
    Func<bool> IsHealthy);

// Owns the startup replay of configuration content/selection and the journal's
// retention decision. Runtime supplies admitted lifecycle operations; this
// component never acquires a second operation gate or publishes UI state.
internal sealed class ConfigurationStartupRecovery(
    ConfigurationStore configurations,
    ConfigurationSwitchJournalStore journals,
    SettingsCommitCoordinator settings,
    ConfigurationRecoveryCoreOperations core)
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Failed replay retains its evidence and suppresses automatic network enables while service observation continues.")]
    public async Task<ConfigurationRecoveryState> PrepareAsync(CancellationToken cancellationToken)
    {
        ConfigurationSwitchJournalLoadResult loaded = await journals.LoadAsync(cancellationToken).ConfigureAwait(false);
        ConfigurationSwitchJournal? journal = loaded.Journal;
        string? message = loaded.Message;
        bool contentRestored = true;
        bool selectionRestored = true;
        if (journal is { Stage: ConfigurationSwitchStage.Committed })
        {
            try { await ClearAsync(journal).ConfigureAwait(false); }
            catch (Exception exception)
            { message = $"配置切换已完成，但无法清理恢复记录：{ErrorSanitizer.Sanitize(exception)}"; }
            return new(null, true, true, message);
        }

        if (journal is null) { return new(null, true, true, message); }
        if (journal.ContentBackupId is Guid backupId)
        {
            try
            {
                contentRestored = await configurations.RestorePersistentBackupAsync(
                    backupId, journal.CandidateConfigurationId, cancellationToken).ConfigureAwait(false);
                if (!contentRestored) { message = "配置切换恢复记录缺少旧配置备份，无法恢复订阅内容。"; }
            }
            catch (Exception exception)
            {
                contentRestored = false;
                message = $"订阅配置备份恢复失败：{ErrorSanitizer.Sanitize(exception)}";
            }
        }

        try
        {
            IReadOnlyList<ConfigurationProfile> stored = await configurations.ListAsync(cancellationToken).ConfigureAwait(false);
            string? selectionMessage = await RestoreSelectionAsync(journal, stored, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(selectionMessage)) { message = selectionMessage; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            selectionRestored = false;
            settings.RecordFailure(exception);
            message = $"配置切换恢复未完成，已保留恢复记录：{ErrorSanitizer.Sanitize(exception)}";
        }

        return new(journal, contentRestored, selectionRestored, message);
    }

    public async Task<ConfigurationRecoveryState> CompleteAsync(
        ConfigurationRecoveryState state, CoreState observedCore, CancellationToken cancellationToken)
    {
        if (state.Journal is not { } journal || !state.SelectionRestored) { return state; }
        if (!state.ContentRestored)
        {
            if (observedCore == CoreState.Running) { await core.Stop(cancellationToken).ConfigureAwait(false); }
            return state with { Message = "配置切换恢复记录仍未完成，旧订阅内容无法确认，核心已保持停止。" };
        }

        if (journal.PreviousCoreWasRunning)
        {
            await (observedCore == CoreState.Running ? core.Restart(cancellationToken) : core.Start(cancellationToken)).ConfigureAwait(false);
            if (!core.IsHealthy())
            { return state with { Message = "配置切换恢复记录仍未完成，旧核心未能通过健康检查。" }; }
        }

        await ClearAsync(journal).ConfigureAwait(false);
        return state with { Journal = null };
    }

    public async Task<ConfigurationRecoveryState> DeferAsync(ConfigurationRecoveryState state)
    {
        if (state.Journal is not { } journal) { return state; }
        if (!journal.PreviousCoreWasRunning && state.ContentRestored && state.SelectionRestored)
        {
            await ClearAsync(journal).ConfigureAwait(false);
            return state with { Journal = null };
        }
        return state with { Message = "配置切换恢复记录仍未完成，服务状态暂时无法确认。" };
    }

    private async Task<string?> RestoreSelectionAsync(
        ConfigurationSwitchJournal journal, IReadOnlyList<ConfigurationProfile> stored, CancellationToken cancellationToken)
    {
        // A persisted different-id candidate is already committed. Same-id
        // refreshes still restore their content backup before this decision.
        if (!string.IsNullOrWhiteSpace(journal.CandidateConfigurationId)
            && !string.Equals(journal.PreviousConfigurationId, journal.CandidateConfigurationId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(settings.Current.ActiveConfigurationId, journal.CandidateConfigurationId, StringComparison.OrdinalIgnoreCase))
        { return null; }

        string? previous = journal.PreviousConfigurationId;
        string? message = null;
        if (previous is not null && !stored.Any(configuration => string.Equals(configuration.Id, previous, StringComparison.OrdinalIgnoreCase)))
        {
            previous = null;
            message = "配置切换恢复记录指向的旧配置已不存在，已恢复为未选择配置。";
        }
        await settings.SaveSelectionAsync(previous, cancellationToken).ConfigureAwait(false);
        return message;
    }

    private async Task ClearAsync(ConfigurationSwitchJournal journal)
    {
        await journals.ClearAsync().ConfigureAwait(false);
        if (journal.ContentBackupId is Guid backupId)
        { await configurations.ClearPersistentBackupAsync(backupId).ConfigureAwait(false); }
    }
}
