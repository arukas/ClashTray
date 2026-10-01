using ClashTray.Contracts;

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
    string? ErrorMessage = null,
    ServiceErrorCode ErrorCode = ServiceErrorCode.None,
    ServiceDispatchState DispatchState = ServiceDispatchState.Completed)
{
    public bool Succeeded => Outcome == CoreStartOutcome.Started;
}

internal sealed record CoreStartFailure(
    CoreStartOutcome Outcome,
    string ErrorMessage,
    ServiceErrorCode ErrorCode = ServiceErrorCode.None,
    ServiceDispatchState DispatchState = ServiceDispatchState.Completed)
{
    public CoreStartOperationResult ForOperation(Guid operationId) => new(operationId, Outcome,
        ErrorMessage: ErrorSanitizer.Sanitize(ErrorMessage), ErrorCode: ErrorCode, DispatchState: DispatchState);

    public static CoreStartFailure FromException(Exception exception)
    {
        ServiceErrorCode code = ClassifyException(exception);
        ServiceDispatchState dispatch = exception switch
        {
            ServiceCommandException service => service.DispatchState,
            ServiceRequestUnknownException unknown => unknown.DispatchState,
            _ => ServiceDispatchState.Completed
        };
        return new CoreStartFailure(OutcomeForCode(code), ErrorSanitizer.Sanitize(exception), code, dispatch);
    }

    public static ServiceErrorCode ClassifyException(Exception exception) => exception switch
    {
        ServiceCommandException service => service.ErrorCode,
        OperationBusyException => ServiceErrorCode.OperationBusy,
        TimeoutException => ServiceErrorCode.OperationTimedOut,
        OperationCanceledException => ServiceErrorCode.OperationCancelled,
        ManagedCoreOwnershipException => ServiceErrorCode.ControllerOwnershipUnconfirmed,
        InvalidDataException => ServiceErrorCode.InvalidConfiguration,
        _ => ServiceErrorCode.None
    };

    public static CoreStartFailure UnconfirmedServiceStart(string error, CancellationToken caller, CancellationToken deadline) =>
        new(caller.IsCancellationRequested ? CoreStartOutcome.Cancelled
                : deadline.IsCancellationRequested ? CoreStartOutcome.TimedOut : CoreStartOutcome.Failed,
            error,
            caller.IsCancellationRequested ? ServiceErrorCode.OperationCancelled
                : deadline.IsCancellationRequested ? ServiceErrorCode.OperationTimedOut : ServiceErrorCode.None,
            ServiceDispatchState.DispatchedAwaitingResult);

    private static CoreStartOutcome OutcomeForCode(ServiceErrorCode code) => code switch
    {
        ServiceErrorCode.OperationBusy => CoreStartOutcome.Busy,
        ServiceErrorCode.ControllerPortConflict or ServiceErrorCode.ProxyPortConflict => CoreStartOutcome.PortConflict,
        ServiceErrorCode.ControllerCandidatesExhausted => CoreStartOutcome.ControllerCandidatesExhausted,
        ServiceErrorCode.InvalidConfiguration => CoreStartOutcome.InvalidConfiguration,
        ServiceErrorCode.OperationTimedOut => CoreStartOutcome.TimedOut,
        ServiceErrorCode.OperationCancelled => CoreStartOutcome.Cancelled,
        _ => CoreStartOutcome.Failed
    };
}
