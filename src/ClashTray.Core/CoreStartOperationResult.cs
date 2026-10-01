namespace ClashTray.Core;

public enum CoreStartOutcome
{
    Started,
    AlreadyRunning,
    Busy,
    CoreMissing,
    ConfigurationMissing,
    InvalidConfiguration,
    PortConflict,
    ControllerCandidatesExhausted,
    TimedOut,
    Cancelled,
    Failed
}

/// <summary>
/// Result for one specific start request. Started is emitted only after this
/// request has a healthy, confirmed runtime binding while the lifecycle lock
/// is still held.
/// </summary>
public sealed record CoreStartOperationResult(
    Guid OperationId,
    CoreStartOutcome Outcome,
    int? ConfirmedControllerPort = null,
    string? ErrorMessage = null)
{
    public bool Succeeded => Outcome == CoreStartOutcome.Started;
}
