using ClashTray.Core;

namespace ClashTray.App;

internal sealed record CoreStartFeedback(string ResourceKey, int? ConfirmedPort, string? ErrorDetail)
{
    public static CoreStartFeedback Create(CoreStartOperationResult result)
    {
        string resourceKey = result.Outcome switch
        {
            CoreStartOutcome.Started => "ControllerPortOneTimeStartSucceeded",
            CoreStartOutcome.AlreadyRunning => "ControllerPortOneTimeStartAlreadyRunning",
            CoreStartOutcome.Busy => "ControllerPortOneTimeStartBusy",
            CoreStartOutcome.CoreMissing => "ControllerPortOneTimeStartCoreMissing",
            CoreStartOutcome.ConfigurationMissing => "ControllerPortOneTimeStartConfigurationMissing",
            CoreStartOutcome.InvalidConfiguration => "ControllerPortOneTimeStartInvalidConfiguration",
            CoreStartOutcome.PortConflict => "ControllerPortOneTimeStartPortConflict",
            CoreStartOutcome.ControllerCandidatesExhausted => "ControllerPortOneTimeStartCandidatesExhausted",
            CoreStartOutcome.TimedOut => "ControllerPortOneTimeStartTimedOut",
            CoreStartOutcome.Cancelled => "ControllerPortOneTimeStartCancelled",
            _ => "ControllerPortOneTimeStartFailed"
        };
        return new CoreStartFeedback(resourceKey,
            result.Outcome == CoreStartOutcome.Started ? result.ConfirmedControllerPort : null,
            result.Outcome is CoreStartOutcome.Started or CoreStartOutcome.AlreadyRunning
                ? null : ErrorSanitizer.SanitizeNullable(result.ErrorMessage));
    }
}
