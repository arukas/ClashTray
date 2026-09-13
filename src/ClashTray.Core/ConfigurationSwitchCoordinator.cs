using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record ConfigurationSwitchRequest(
    Guid OperationId,
    ConfigurationSwitchSource Source,
    string TargetConfigurationId,
    long? ExpectedNetworkRevision = null)
{
    public static ConfigurationSwitchRequest Create(
        ConfigurationSwitchSource source,
        string targetConfigurationId,
        long? expectedNetworkRevision = null) =>
        new(Guid.NewGuid(), source, targetConfigurationId, expectedNetworkRevision);

    internal void Validate()
    {
        if (OperationId == Guid.Empty)
        {
            throw new ArgumentException("Configuration switch operation ID is required.", nameof(OperationId));
        }

        if (string.IsNullOrWhiteSpace(TargetConfigurationId))
        {
            throw new ArgumentException("Configuration switch target ID is required.", nameof(TargetConfigurationId));
        }

        if (ExpectedNetworkRevision is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedNetworkRevision));
        }
    }
}

public sealed record ConfigurationSwitchRuntimeState(
    string? ActiveConfigurationId,
    bool CoreWasRunning,
    bool SystemProxyPreference,
    SystemProxyState SystemProxyState,
    bool TunPreference,
    TunState TunState,
    long ControllerGeneration);

public enum ConfigurationSwitchOutcome
{
    NoOp,
    Committed,
    Rejected,
    RolledBack,
    RollbackFailed
}

public sealed record ConfigurationSwitchResult(
    Guid OperationId,
    ConfigurationSwitchOutcome Outcome,
    ConfigurationSwitchStage Stage,
    ErrorCode ErrorCode,
    Exception? Failure = null,
    Exception? RollbackFailure = null);

public interface IConfigurationSwitchOperations
{
    public string? CurrentConfigurationId { get; }

    public Task<ConfigurationProfile?> ResolveCandidateAsync(
        string id,
        CancellationToken cancellationToken);

    public Task ValidateCandidateAsync(
        ConfigurationProfile candidate,
        CancellationToken cancellationToken);

    public Task<ConfigurationSwitchRuntimeState> CaptureStateAsync(
        CancellationToken cancellationToken);

    public Task ApplyAsync(
        ConfigurationSwitchContext context,
        CancellationToken cancellationToken);

    public Task RollbackAsync(
        ConfigurationSwitchContext context,
        Exception failure,
        CancellationToken cancellationToken);
}

public sealed class ConfigurationSwitchContext
{
    private readonly Func<ConfigurationSwitchJournal, CancellationToken, Task> _persistJournalAsync;

    internal ConfigurationSwitchContext(
        ConfigurationSwitchRequest request,
        ConfigurationProfile candidate,
        ConfigurationSwitchRuntimeState previousState,
        ConfigurationSwitchJournal journal,
        Func<ConfigurationSwitchJournal, CancellationToken, Task> persistJournalAsync)
    {
        Request = request;
        Candidate = candidate;
        PreviousState = previousState;
        Journal = journal;
        _persistJournalAsync = persistJournalAsync;
    }

    public ConfigurationSwitchRequest Request { get; }

    public ConfigurationProfile Candidate { get; }

    public ConfigurationSwitchRuntimeState PreviousState { get; }

    public ConfigurationSwitchJournal Journal { get; private set; }

    public async Task SetStageAsync(
        ConfigurationSwitchStage stage,
        CancellationToken cancellationToken = default)
    {
        Journal = Journal.WithStage(stage);
        await _persistJournalAsync(Journal, cancellationToken);
    }
}

public sealed class ConfigurationSwitchCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly ConfigurationSwitchJournalStore _journalStore;

    public ConfigurationSwitchCoordinator(ConfigurationSwitchJournalStore journalStore)
    {
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A configuration switch must catch every operation failure to guarantee the rollback boundary.")]
    public async Task<ConfigurationSwitchResult> ExecuteAsync(
        ConfigurationSwitchRequest request,
        IConfigurationSwitchOperations operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operations);
        request.Validate();
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(
                    operations.CurrentConfigurationId,
                    request.TargetConfigurationId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new ConfigurationSwitchResult(
                    request.OperationId,
                    ConfigurationSwitchOutcome.NoOp,
                    ConfigurationSwitchStage.Committed,
                    ErrorCode.None);
            }

            ConfigurationProfile? candidate = await operations.ResolveCandidateAsync(
                request.TargetConfigurationId,
                cancellationToken);
            if (candidate is null)
            {
                return new ConfigurationSwitchResult(
                    request.OperationId,
                    ConfigurationSwitchOutcome.Rejected,
                    ConfigurationSwitchStage.Prepared,
                    ErrorCode.ConfigurationSwitchTargetNotFound);
            }

            await operations.ValidateCandidateAsync(candidate, cancellationToken);
            ConfigurationSwitchRuntimeState previousState =
                await operations.CaptureStateAsync(cancellationToken);
            ConfigurationSwitchJournal journal = ConfigurationSwitchJournal.Create(
                request.OperationId,
                request.Source,
                previousState.ActiveConfigurationId,
                candidate.Id,
                previousState.CoreWasRunning,
                previousState.SystemProxyPreference,
                previousState.SystemProxyState,
                previousState.TunPreference,
                previousState.TunState,
                previousState.ControllerGeneration);
            ConfigurationSwitchContext context = new(
                request,
                candidate,
                previousState,
                journal,
                (currentJournal, token) => _journalStore.SaveAsync(currentJournal, token));

            await context.SetStageAsync(
                ConfigurationSwitchStage.CandidateValidated,
                cancellationToken);
            try
            {
                await operations.ApplyAsync(context, cancellationToken);
            }
            catch (Exception failure)
            {
                await context.SetStageAsync(
                    ConfigurationSwitchStage.RollingBack,
                    CancellationToken.None);
                try
                {
                    await operations.RollbackAsync(context, failure, CancellationToken.None);
                    await _journalStore.ClearAsync();
                    return new ConfigurationSwitchResult(
                        request.OperationId,
                        ConfigurationSwitchOutcome.RolledBack,
                        ConfigurationSwitchStage.RollingBack,
                        ErrorCode.ConfigurationSwitchFailed,
                        failure);
                }
                catch (Exception rollbackFailure)
                {
                    try
                    {
                        await context.SetStageAsync(
                            ConfigurationSwitchStage.RollbackFailed,
                            CancellationToken.None);
                    }
                    catch
                    {
                    }

                    return new ConfigurationSwitchResult(
                        request.OperationId,
                        ConfigurationSwitchOutcome.RollbackFailed,
                        ConfigurationSwitchStage.RollbackFailed,
                        ErrorCode.ConfigurationSwitchRollbackFailed,
                        failure,
                        rollbackFailure);
                }
            }

            await context.SetStageAsync(
                ConfigurationSwitchStage.Committed,
                CancellationToken.None);
            await _journalStore.ClearAsync();
            return new ConfigurationSwitchResult(
                request.OperationId,
                ConfigurationSwitchOutcome.Committed,
                ConfigurationSwitchStage.Committed,
                ErrorCode.None);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _operationLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
