using System.Diagnostics.CodeAnalysis;

namespace ClashTray.Core;

internal sealed record ShutdownStepExecution(ShutdownCleanupStepResult Result, Exception? Failure, bool CanContinue);

// Owns unsettled cleanup work. Waiting deadlines and operation cancellation
// stay distinct; ownership can be transferred until all late tasks settle.
internal sealed class ShutdownCleanupCohort(Func<string, CancellationToken, Task>? stepHook)
{
    private readonly List<Task> _pending = [];
    public IReadOnlyList<Task> Pending => _pending;

    public async Task<ShutdownStepExecution> RunAsync(string name, Func<CancellationToken, Task> operation,
        CancellationToken waitDeadline, CancellationToken? operationCancellationToken = null,
        bool continueAfterIncomplete = false)
    {
        CancellationToken operationToken = operationCancellationToken ?? waitDeadline;
        async Task ExecuteAsync(CancellationToken token)
        {
            if (stepHook is not null) { await stepHook(name, token).ConfigureAwait(false); }
            await operation(token).ConfigureAwait(false);
        }
        BoundedCleanupStepResult run = await BoundedCleanupStepRunner.RunAsync(ExecuteAsync, waitDeadline, operationToken).ConfigureAwait(false);
        bool expired = waitDeadline.IsCancellationRequested || operationToken.IsCancellationRequested;
        ShutdownCleanupStatus status;
        string? detail = null;
        if (run.IncompleteOperation is not null || expired && run.Failure is OperationCanceledException)
        {
            status = ShutdownCleanupStatus.TimedOutUnknown;
            detail = $"{name} 未在对应退出阶段期限内完成，结果未确认。";
        }
        else if (run.Failure is not null)
        {
            status = ShutdownCleanupStatus.Failed;
            detail = $"{name} 失败：{ErrorSanitizer.Sanitize(run.Failure)}";
        }
        else { status = ShutdownCleanupStatus.Completed; }
        Exception? failure = expired && run.Failure is OperationCanceledException
            ? new TimeoutException($"{name} 达到退出阶段期限。", run.Failure) : run.Failure;
        bool canContinue = !expired || run.Failure is not OperationCanceledException;
        if (run.IncompleteOperation is Task pending)
        {
            _pending.Add(pending);
            canContinue = continueAfterIncomplete;
        }
        return new(new(name, status, detail), failure, canContinue);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Late cleanup faults are observed and recorded while ownership is retained for unsettled work.")]
    public async Task<bool> ObserveCompletedAsync(Action<Exception> recordFailure)
    {
        bool unsettled = false;
        foreach (Task pending in _pending)
        {
            if (!pending.IsCompleted) { unsettled = true; continue; }
            try { await pending.ConfigureAwait(false); }
            catch (Exception exception) { recordFailure(exception); }
        }
        if (!unsettled) { _pending.Clear(); }
        return !unsettled;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A timed-out cleanup task must be observed before its operation gate and deadline tokens are released.")]
    public static async Task RetainOwnershipUntilSettledAsync(Task pendingOperation, OperationGate.Lease? lease,
        CancellationTokenSource shutdownDeadline, CancellationTokenSource preNetworkDeadline)
    {
        try { await pendingOperation.ConfigureAwait(false); }
        catch (Exception) { /* The initiating shutdown recorded the timeout; observe the late completion. */ }
        finally
        {
            lease?.Dispose();
            shutdownDeadline.Dispose();
            preNetworkDeadline.Dispose();
        }
    }
}
