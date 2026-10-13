using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed record ConfigurationSwitchEffects(
    Func<OperationGate.Lease, CancellationToken, Task> StopCore,
    Func<OperationGate.Lease, CancellationToken, Task> StartCore,
    Func<OperationGate.Lease, CancellationToken, Task> RestartCore,
    Func<ConfigurationProfile, CancellationToken, Task> Promote,
    Func<ConfigurationProfile, CancellationToken, Task> Commit,
    Func<ConfigurationSwitchRuntimeState, CancellationToken, Task> Restore,
    Func<bool> IsCoreHealthy,
    Action Publish);

// An operation-scoped adapter: its lease is immutable and belongs to exactly
// one coordinator invocation. It cannot borrow another invocation's lease.
internal sealed class ConfigurationSwitchOperations(
    OperationGate.Lease lease, ConfigurationStore store, Func<AppSettings> settings,
    Func<CancellationToken, Task<ConfigurationSwitchRuntimeState>> captureState,
    ConfigurationSwitchEffects effects) : IConfigurationSwitchOperations
{
    public string? CurrentConfigurationId => settings().ActiveConfigurationId;
    public async Task<ConfigurationProfile?> ResolveCandidateAsync(string id, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConfigurationProfile> configurations = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        return configurations.FirstOrDefault(configuration => string.Equals(configuration.Id, id, StringComparison.OrdinalIgnoreCase));
    }
    public Task ValidateCandidateAsync(ConfigurationProfile candidate, CancellationToken cancellationToken) =>
        store.ValidateCandidateAsync(candidate, cancellationToken);
    public Task<ConfigurationSwitchRuntimeState> CaptureStateAsync(CancellationToken cancellationToken) => captureState(cancellationToken);

    public async Task ApplyAsync(ConfigurationSwitchContext context, CancellationToken cancellationToken)
    {
        bool restartCore = context.Request.RestartCore && context.PreviousState.CoreWasRunning;
        if (restartCore)
        {
            await effects.StopCore(lease, cancellationToken).ConfigureAwait(false);
            await context.SetStageAsync(ConfigurationSwitchStage.NetworkStateSafeguarded, CancellationToken.None).ConfigureAwait(false);
        }
        await effects.Promote(context.Candidate, cancellationToken).ConfigureAwait(false);
        await context.SetStageAsync(ConfigurationSwitchStage.RuntimePromoted, CancellationToken.None).ConfigureAwait(false);
        if (restartCore)
        {
            await effects.StartCore(lease, cancellationToken).ConfigureAwait(false);
            if (!effects.IsCoreHealthy()) { throw new InvalidOperationException("切换后的 Mihomo 核心健康检查失败。"); }
            await context.SetStageAsync(ConfigurationSwitchStage.CoreRestarted, CancellationToken.None).ConfigureAwait(false);
        }
        await effects.Commit(context.Candidate, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task RollbackAsync(ConfigurationSwitchContext context, Exception failure, CancellationToken cancellationToken)
    {
        await effects.Restore(context.PreviousState, cancellationToken).ConfigureAwait(false);
        if (context.PreviousState.CoreWasRunning)
        {
            await effects.RestartCore(lease, cancellationToken).ConfigureAwait(false);
            if (!effects.IsCoreHealthy()) { throw new InvalidOperationException("旧配置核心恢复后的健康检查失败。"); }
        }
        effects.Publish();
    }
}
