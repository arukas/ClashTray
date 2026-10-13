namespace ClashTray.Core;

internal readonly record struct BoundedCleanupStepResult(
    Exception? Failure,
    Task? IncompleteOperation)
{
    public bool IsSettled => IncompleteOperation is null;
}

/// <summary>
/// Waits until a bounded phase deadline while giving the action a separate
/// cancellation deadline. An incomplete action is returned to its owner so
/// dependent resources stay alive until that action settles.
/// </summary>
internal static class BoundedCleanupStepRunner
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Shutdown must record failures and preserve ownership when a cleanup action does not settle before its deadline.")]
    public static Task<BoundedCleanupStepResult> RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken shutdownDeadline) =>
        RunAsync(operation, shutdownDeadline, shutdownDeadline);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Shutdown must record failures and preserve ownership when a cleanup action does not settle before its deadline.")]
    public static async Task<BoundedCleanupStepResult> RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken waitDeadline,
        CancellationToken operationCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operationCancellationToken.IsCancellationRequested)
        {
            return new BoundedCleanupStepResult(
                new OperationCanceledException(operationCancellationToken),
                null);
        }

        Task operationTask;
        try
        {
            operationTask = operation(operationCancellationToken)
                ?? throw new InvalidOperationException("A cleanup action returned no task.");
        }
        catch (Exception exception)
        {
            return new BoundedCleanupStepResult(exception, null);
        }

        if (operationTask.IsCompleted)
        {
            try
            {
                await operationTask.ConfigureAwait(false);
                return new BoundedCleanupStepResult(null, null);
            }
            catch (Exception exception)
            {
                return new BoundedCleanupStepResult(exception, null);
            }
        }

        try
        {
            await operationTask.WaitAsync(waitDeadline).ConfigureAwait(false);
            return new BoundedCleanupStepResult(null, null);
        }
        catch (Exception exception)
        {
            if (!operationTask.IsCompleted)
            {
                return new BoundedCleanupStepResult(exception, operationTask);
            }

            try
            {
                await operationTask.ConfigureAwait(false);
                return new BoundedCleanupStepResult(null, null);
            }
            catch (Exception operationException)
            {
                return new BoundedCleanupStepResult(operationException, null);
            }
        }
    }
}
